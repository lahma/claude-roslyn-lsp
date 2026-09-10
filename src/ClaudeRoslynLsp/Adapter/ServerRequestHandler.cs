using System.Buffers;
using System.Text.Json;

using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>What the adapter did with one message Roslyn sent it.</summary>
internal enum ServerMessageOutcome
{
    /// <summary>Answered with a JSON <c>null</c> result.</summary>
    AnsweredNull,

    /// <summary>Answered with a result the adapter computed.</summary>
    AnsweredWithResult,

    /// <summary>Refused with <c>-32601</c>, so Roslyn does not block on it.</summary>
    MethodNotFound,

    /// <summary>Passed on to the client, with the id rewritten if it had one.</summary>
    ForwardedToClient,

    /// <summary>Consumed here: it changed adapter state, or it went into the log.</summary>
    Consumed,

    /// <summary>Deliberately discarded.</summary>
    Dropped,
}

/// <summary>
/// The table for everything Roslyn asks or tells the adapter — the half of the mediation that Claude
/// Code cannot do for itself.
/// </summary>
/// <remarks>
/// <para>
/// Claude Code answers exactly one of these methods (<c>workspace/configuration</c>, and only when a
/// <c>settings</c> block happens to be in the plugin configuration) and refuses two of them with
/// <c>-32601</c> outright (<c>client/registerCapability</c> and
/// <c>window/workDoneProgress/create</c>). Roslyn sends about a hundred and forty registrations, two
/// configuration requests covering eighty sections, a progress stream and twenty-odd log lines
/// during a single startup (C31, C32). Relaying those would produce a hundred and forty errors and
/// a server with no watchers, no diagnostic sources and no progress.
/// </para>
/// <para>
/// So this table answers them. The rule behind every row is the same: <b>Roslyn must never be left
/// waiting on the adapter</b>, because a server-to-client request that goes unanswered stalls the
/// handler that issued it, and several of those handlers are on the solution-load path. Even a
/// method nobody has heard of gets <c>-32601</c> with a log line rather than silence.
/// </para>
/// </remarks>
internal sealed partial class ServerRequestHandler
{
    private readonly RegistrationTracker _registrations;
    private readonly ConfigurationResponder _configuration;
    private readonly ProgressTracker _progress;
    private readonly ReadinessGate _gate;
    private readonly ILogger _logger;

    /// <summary>Creates the handler over the adapter's shared state.</summary>
    /// <param name="registrations">Where dynamic registrations are recorded.</param>
    /// <param name="configuration">What answers <c>workspace/configuration</c>.</param>
    /// <param name="progress">What consumes the work-done progress stream.</param>
    /// <param name="gate">What <c>projectInitializationComplete</c> opens.</param>
    /// <param name="logger">The stderr log.</param>
    internal ServerRequestHandler(
        RegistrationTracker registrations,
        ConfigurationResponder configuration,
        ProgressTracker progress,
        ReadinessGate gate,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(logger);

        _registrations = registrations;
        _configuration = configuration;
        _progress = progress;
        _gate = gate;
        _logger = logger;
    }

    /// <summary>
    /// Raised when Roslyn asks for a category of results to be recomputed.
    /// </summary>
    /// <remarks>
    /// The refresh itself is WP4's — it is what re-pulls diagnostics for every open document after a
    /// build changed the world. The event exists now so that WP4 subscribes rather than reopening
    /// this table, and so that the answer Roslyn gets (an immediate <c>null</c>) is already correct:
    /// a refresh request that is not acknowledged blocks the handler that sent it.
    /// </remarks>
    internal event Action<string>? RefreshRequested;

    /// <summary>Whether the client can apply a workspace edit, which decides how one is answered.</summary>
    internal bool ClientAppliesEdits { get; set; }

    /// <summary>The workspace root, which is what <c>workspace/workspaceFolders</c> answers with.</summary>
    internal IReadOnlyList<WorkspaceFolder> WorkspaceFolders { get; set; } = [];

    /// <summary>
    /// Handles one message from Roslyn.
    /// </summary>
    /// <param name="body">The raw message.</param>
    /// <param name="info">Its scan.</param>
    /// <param name="answer">Sends a reply back to Roslyn.</param>
    /// <param name="forward">Sends the message on to the client.</param>
    /// <returns>What was done, which is what the table's tests assert on.</returns>
    internal ServerMessageOutcome Handle(
        ReadOnlySpan<byte> body,
        in LspMessageInfo info,
        Action<byte[]> answer,
        Action<byte[]> forward)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(forward);

        var method = info.Method ?? string.Empty;
        var idToken = info.IdToken(body);
        var isRequest = info.Kind == LspMessageKind.Request;

        switch (method)
        {
            case "client/registerCapability":
                _registrations.Register(Params(body, LspJsonContext.Default.RegistrationParams));
                return AnswerNull(isRequest, idToken, answer);

            case "client/unregisterCapability":
                _registrations.Unregister(Params(body, LspJsonContext.Default.UnregistrationParams));
                return AnswerNull(isRequest, idToken, answer);

            case "workspace/configuration":
                if (!isRequest)
                {
                    return ServerMessageOutcome.Dropped;
                }

                answer(_configuration.BuildResponse(idToken, Params(body, LspJsonContext.Default.ConfigurationParams)));
                return ServerMessageOutcome.AnsweredWithResult;

            case "window/workDoneProgress/create":
                _progress.Create(Params(body, LspJsonContext.Default.WorkDoneProgressCreateParams));
                return AnswerNull(isRequest, idToken, answer);

            // Never forwarded: Claude Code refuses the create request that would have to precede it,
            // so a forwarded stream would be reported against a token the client says does not exist.
            case "$/progress":
                _progress.Report(Params(body, LspJsonContext.Default.ProgressParams));
                return ServerMessageOutcome.Consumed;

            // The one notification the whole adapter is waiting for (C31). No params, ever.
            case "workspace/projectInitializationComplete":
                Log.ProjectsLoaded(_logger);
                _gate.MarkProjectsLoaded();
                return ServerMessageOutcome.Consumed;

            case "workspace/diagnostic/refresh":
            case "workspace/codeLens/refresh":
            case "workspace/inlayHint/refresh":
            case "workspace/semanticTokens/refresh":
                RefreshRequested?.Invoke(method);
                return AnswerNull(isRequest, idToken, answer);

            // Shown to the user by the client, so it crosses unchanged.
            case "window/showMessage":
                forward(body.ToArray());
                return ServerMessageOutcome.ForwardedToClient;

            case "window/logMessage":
                forward(PrefixLogMessage(body));
                return ServerMessageOutcome.ForwardedToClient;

            // A modal question with buttons, in a client that has no user sitting in front of it.
            // Answered null (= dismissed) and logged, so the text is not lost.
            case "window/showMessageRequest":
            case "window/_roslyn_showToast":
                LogServerPrompt(body, method);
                return AnswerNull(isRequest, idToken, answer);

            case "workspace/applyEdit":
                return ApplyEdit(body, idToken, isRequest, answer, forward);

            // Roslyn's telemetry is Roslyn's business, and a client that logs it would put the
            // user's solution structure into a transcript nobody asked to publish.
            case "telemetry/event":
                return ServerMessageOutcome.Dropped;

            case "workspace/workspaceFolders":
                if (!isRequest)
                {
                    return ServerMessageOutcome.Dropped;
                }

                answer(JsonRpcErrors.RawResult(idToken, BuildWorkspaceFolders()));
                return ServerMessageOutcome.AnsweredWithResult;

            default:
                if (!isRequest)
                {
                    Log.UnknownNotification(_logger, method);
                    return ServerMessageOutcome.Dropped;
                }

                Log.UnknownRequest(_logger, method);
                answer(JsonRpcErrors.Error(
                    idToken,
                    JsonRpcErrors.MethodNotFound,
                    $"{ServerVersion.Name} does not implement the client-side method '{method}'."));

                return ServerMessageOutcome.MethodNotFound;
        }
    }

    /// <summary>
    /// Forwards <c>workspace/applyEdit</c> only to a client that said it applies edits.
    /// </summary>
    /// <remarks>
    /// A refusal has to be a well-formed <c>{applied:false}</c> rather than an error: Roslyn treats
    /// an error as a failed operation and may log it as a fault, whereas <c>applied:false</c> is the
    /// protocol's own way of saying "the client declined", which is exactly what happened. The v1
    /// adapter never applies edits itself — that is the MCP half's job (D21), where it is the LSP
    /// client and owns the files.
    /// </remarks>
    private ServerMessageOutcome ApplyEdit(
        ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> idToken,
        bool isRequest,
        Action<byte[]> answer,
        Action<byte[]> forward)
    {
        if (!isRequest)
        {
            return ServerMessageOutcome.Dropped;
        }

        if (ClientAppliesEdits)
        {
            forward(body.ToArray());
            return ServerMessageOutcome.ForwardedToClient;
        }

        Log.EditRefused(_logger);

        answer(JsonRpcErrors.RawResult(
            idToken,
            JsonSerializer.SerializeToUtf8Bytes(
                new ApplyWorkspaceEditResult
                {
                    Applied = false,
                    FailureReason =
                        $"The editor connected to {ServerVersion.Name} did not declare "
                        + "workspace.applyEdit, so there is nothing here that can write the file.",
                },
                LspJsonContext.Default.ApplyWorkspaceEditResult)));

        return ServerMessageOutcome.AnsweredWithResult;
    }

    /// <summary>Rewrites a <c>window/logMessage</c> with the prefix that says whose line it is.</summary>
    /// <remarks>
    /// Roslyn produces twenty-odd of these during startup and they are its only log channel at the
    /// default level (C6). Unprefixed they would appear in the user's transcript as though the
    /// adapter had said them, and the difference matters the moment something is wrong.
    /// </remarks>
    private static byte[] PrefixLogMessage(ReadOnlySpan<byte> body)
    {
        var parameters = Params(body, LspJsonContext.Default.LogMessageParams);

        if (parameters is null)
        {
            return body.ToArray();
        }

        return JsonSerializer.SerializeToUtf8Bytes(
            new LogMessageNotification
            {
                Params = new LogMessageParams
                {
                    Type = parameters.Type,
                    Message = "[roslyn] " + (parameters.Message ?? string.Empty),
                },
            },
            LspJsonContext.Default.LogMessageNotification);
    }

    /// <summary>Answers a request with null, or does nothing when it was a notification.</summary>
    private static ServerMessageOutcome AnswerNull(bool isRequest, ReadOnlySpan<byte> idToken, Action<byte[]> answer)
    {
        if (!isRequest)
        {
            return ServerMessageOutcome.Consumed;
        }

        answer(JsonRpcErrors.NullResult(idToken));
        return ServerMessageOutcome.AnsweredNull;
    }

    /// <summary>Renders the workspace folders as a JSON array, or <c>null</c> when there are none.</summary>
    private ReadOnlySpan<byte> BuildWorkspaceFolders()
    {
        if (WorkspaceFolders.Count == 0)
        {
            return "null"u8;
        }

        var buffer = new ArrayBufferWriter<byte>(64 * WorkspaceFolders.Count);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();

            foreach (var folder in WorkspaceFolders)
            {
                writer.WriteStartObject();
                writer.WriteString("uri"u8, folder.Uri);
                writer.WriteString("name"u8, folder.Name);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Puts a dismissed prompt's text into the log rather than losing it.</summary>
    private void LogServerPrompt(ReadOnlySpan<byte> body, string method)
    {
        var parameters = Params(body, LspJsonContext.Default.LogMessageParams);
        Log.Prompt(_logger, method, parameters?.Message ?? "(no message)");
    }

    /// <summary>Deserialises a message's <c>params</c>, treating anything unreadable as absent.</summary>
    private static T? Params<T>(ReadOnlySpan<byte> body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            var envelope = JsonSerializer.Deserialize(body, LspJsonContext.Default.RawParamsNotification);

            return envelope?.Params.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? envelope.Params.Deserialize(typeInfo)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1000,
            Level = LogLevel.Information,
            Message = "Roslyn reported workspace/projectInitializationComplete; the workspace is loaded.")]
        internal static partial void ProjectsLoaded(ILogger logger);

        [LoggerMessage(
            EventId = 1001,
            Level = LogLevel.Warning,
            Message = "Roslyn sent the request '{Method}', which this adapter does not implement; it was " +
                      "answered -32601 so Roslyn is not left waiting on it.")]
        internal static partial void UnknownRequest(ILogger logger, string method);

        [LoggerMessage(
            EventId = 1002,
            Level = LogLevel.Debug,
            Message = "Dropped the unhandled Roslyn notification '{Method}'.")]
        internal static partial void UnknownNotification(ILogger logger, string method);

        [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "[roslyn {Method}] {Message}")]
        internal static partial void Prompt(ILogger logger, string method, string message);

        [LoggerMessage(
            EventId = 1004,
            Level = LogLevel.Warning,
            Message = "Roslyn asked for a workspace edit to be applied, but the connected client does not " +
                      "declare workspace.applyEdit; it was declined rather than silently dropped.")]
        internal static partial void EditRefused(ILogger logger);
    }
}
