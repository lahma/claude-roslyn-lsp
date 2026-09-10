using System.Text.Json;

using ClaudeRoslynLsp.Cli;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Lsp;

/// <summary>
/// The <c>lsp</c> verb, as it stands before the mediation layer exists: a correct but empty LSP
/// server. It completes the handshake, refuses everything else politely, and shuts down by the book.
/// </summary>
/// <remarks>
/// <para>
/// It is not a placeholder in the "returns 42" sense, and that is the point. Framing, id echoing,
/// the <c>shutdown</c>/<c>exit</c> exit-code rule and the answer-every-request rule are the parts of
/// an LSP server that are load-bearing before any feature is: get them wrong and the client hangs or
/// restarts in a loop, with no error anywhere to explain it. Having them settled and tested from the
/// first commit means every later work package is adding behaviour to something known-good rather
/// than debugging the transport and the feature at the same time. <c>SmokeTest</c> drives exactly
/// this exchange against the published Native AOT binary on every release RID.
/// </para>
/// <para>
/// WP2 replaces the body of the dispatch loop with the real mediation — an endpoint pair, an id map,
/// raw pass-through and the readiness gate — and keeps the framing underneath it.
/// </para>
/// </remarks>
internal sealed partial class LspStubServer : IDisposable
{
    private readonly LspFrameReader _reader;
    private readonly LspFrameWriter _writer;
    private readonly ILogger _logger;

    /// <summary>
    /// Whether <c>shutdown</c> has been answered. It decides the process exit code, per the
    /// specification: <c>exit</c> after <c>shutdown</c> is success, <c>exit</c> without one is a
    /// failure the client is entitled to notice.
    /// </summary>
    private bool _shutdownRequested;

    /// <summary>Creates a stub server over an already-connected pair of streams.</summary>
    /// <param name="input">The inbound stream, framed LSP.</param>
    /// <param name="output">The outbound stream, framed LSP.</param>
    /// <param name="logger">Where diagnostics go. Never the output stream.</param>
    internal LspStubServer(Stream input, Stream output, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _reader = new LspFrameReader(input);
        _writer = new LspFrameWriter(output);
        _logger = logger;
    }

    /// <summary>Runs the server on the process's own standard streams and returns the exit code.</summary>
    internal static async Task<int> RunStdioAsync()
    {
        var options = ClaudeRoslynLspOptions.FromEnvironment();

        using var loggerFactory = CliRuntime.CreateLoggerFactory(options.LogLevel);
        var logger = loggerFactory.CreateLogger<LspStubServer>();

        Log.Starting(logger, ServerVersion.Name, ServerVersion.Value);

        using var server = new LspStubServer(CliRuntime.StandardInput, CliRuntime.StandardOutput, logger);

        return await server.RunAsync().ConfigureAwait(false);
    }

    /// <summary>Reads and answers messages until <c>exit</c>, end of stream, or a framing failure.</summary>
    /// <param name="cancellationToken">Cancels the read loop.</param>
    /// <returns>The process exit code.</returns>
    internal async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (true)
            {
                var body = await _reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false);

                if (body is null)
                {
                    // The client closed the stream without saying goodbye. Treated exactly like
                    // `exit`: an editor that was killed is the ordinary cause, and the specification's
                    // rule about whether shutdown came first is what says whether that was orderly.
                    _logger.LogInformation("The client closed the connection.");
                    return ExitCode;
                }

                if (await DispatchAsync(body, cancellationToken).ConfigureAwait(false) is { } exitCode)
                {
                    return exitCode;
                }
            }
        }
        catch (LspProtocolException exception)
        {
            // Unrecoverable by construction: once framing is lost there is no way to find the start
            // of the next message, so continuing would answer garbage rather than nothing.
            _logger.LogError(exception, "The inbound stream is not well-framed LSP; stopping.");
            return CliDispatcher.ExitFailure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExitCode;
        }
    }

    /// <summary>Releases the writer's serialisation gate. The streams belong to the caller.</summary>
    public void Dispose() => _writer.Dispose();

    /// <summary>The exit code the specification prescribes for wherever the loop happens to end.</summary>
    private int ExitCode => _shutdownRequested ? CliDispatcher.ExitSuccess : CliDispatcher.ExitFailure;

    /// <summary>
    /// Answers one message, and returns a process exit code when that message was the last one.
    /// </summary>
    /// <param name="body">The raw UTF-8 message body.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task<int?> DispatchAsync(byte[] body, CancellationToken cancellationToken)
    {
        IncomingMessage? message;

        try
        {
            message = JsonSerializer.Deserialize(body, LspJsonContext.Default.IncomingMessage);
        }
        catch (JsonException exception)
        {
            // Well framed, badly written. The frame boundary is still known, so this is answerable
            // and the connection survives it - under a null id, because there was no readable one.
            _logger.LogWarning(exception, "Discarding a message whose body is not valid JSON.");
            await WriteErrorAsync(JsonRpc.Null, JsonRpc.ParseError, "The message body is not valid JSON.",
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (message?.Method is not { } method)
        {
            // A message with no method is a response, and this server issues no requests to be
            // answered. Ignored rather than refused: replying to a response is not a thing.
            _logger.LogDebug("Ignoring a message with no method member.");
            return null;
        }

        switch (method)
        {
            case "initialize" when message.IsRequest:
                await WriteInitializeAsync(message.Id, cancellationToken).ConfigureAwait(false);
                return null;

            case "shutdown" when message.IsRequest:
                _shutdownRequested = true;
                await _writer.WriteFrameAsync(
                    new NullResultResponse { Id = message.Id },
                    LspJsonContext.Default.NullResultResponse,
                    cancellationToken).ConfigureAwait(false);
                return null;

            case "exit":
                var exitCode = ExitCode;

                Log.Exiting(
                    _logger,
                    exitCode,
                    _shutdownRequested ? "shutdown was requested first" : "no shutdown was requested");

                return exitCode;

            default:
                if (!message.IsRequest)
                {
                    // Notifications are, by definition, unanswerable - including the ones this build
                    // has nothing to do with, such as `initialized` and the document lifecycle.
                    Log.IgnoringNotification(_logger, method);
                    return null;
                }

                await WriteErrorAsync(
                    message.Id,
                    JsonRpc.MethodNotFound,
                    $"'{method}' is not implemented in this version of {ServerVersion.Name}. This build " +
                    "answers the LSP handshake only; the Roslyn backend arrives in a later release.",
                    cancellationToken).ConfigureAwait(false);
                return null;
        }
    }

    /// <summary>Answers <c>initialize</c> with the static capability document and this server's identity.</summary>
    /// <param name="id">The request id, echoed exactly.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private ValueTask WriteInitializeAsync(JsonElement id, CancellationToken cancellationToken) =>
        _writer.WriteFrameAsync(
            new InitializeResponse
            {
                Id = id,
                Result = new InitializeResult
                {
                    Capabilities = new ServerCapabilities
                    {
                        TextDocumentSync = new TextDocumentSyncOptions
                        {
                            OpenClose = true,
                            Change = 1,
                            Save = new SaveOptions { IncludeText = false },
                        },
                    },
                    ServerInfo = new ServerInfo
                    {
                        Name = ServerVersion.Name,
                        Version = ServerVersion.Value,
                    },
                },
            },
            LspJsonContext.Default.InitializeResponse,
            cancellationToken);

    /// <summary>Writes a JSON-RPC error response.</summary>
    /// <param name="id">The id being answered, or <see cref="JsonRpc.Null"/> when none could be read.</param>
    /// <param name="code">The JSON-RPC error code.</param>
    /// <param name="message">The human-readable explanation.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private ValueTask WriteErrorAsync(JsonElement id, int code, string message, CancellationToken cancellationToken) =>
        _writer.WriteFrameAsync(
            new ErrorResponse
            {
                Id = id,
                Error = new ResponseError { Code = code, Message = message },
            },
            LspJsonContext.Default.ErrorResponse,
            cancellationToken);

    /// <summary>
    /// Source-generated log methods for the records that carry arguments.
    /// </summary>
    /// <remarks>
    /// A plain <c>logger.LogDebug("… {Method} …", method)</c> allocates a <c>params object?[]</c> and
    /// evaluates its arguments whether or not the level is enabled, which CA1873 refuses under
    /// <c>TreatWarningsAsErrors</c>. <c>[LoggerMessage]</c> generates the level check and a
    /// strongly-typed state object instead, so nothing is paid for a log line that is switched off —
    /// and nothing is reflected over, which matters for Native AOT. The records that take no
    /// arguments stay ordinary calls: there is nothing there to evaluate.
    /// </remarks>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1,
            Level = LogLevel.Information,
            Message = "{Server} {Version} starting in LSP mode. This build answers the handshake only; " +
                      "the Roslyn backend arrives in a later release.")]
        internal static partial void Starting(ILogger logger, string server, string version);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Exiting with code {ExitCode} ({Reason}).")]
        internal static partial void Exiting(ILogger logger, int exitCode, string reason);

        [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Ignoring the {Method} notification.")]
        internal static partial void IgnoringNotification(ILogger logger, string method);
    }
}
