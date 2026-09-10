using System.Text.Json;

using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// The Claude-facing half of the adapter: the reader, the single writer, and everything the adapter
/// knows about the client it is talking to.
/// </summary>
/// <remarks>
/// <para>
/// The one behaviour worth naming here is that <c>initialize</c> is answered <b>before</b> anything
/// has been launched, out of a capability document written in this repository. Claude Code holds
/// <c>initialize</c> open indefinitely if it is not answered, and Roslyn takes the better part of a
/// second to answer its own (C31) — so deriving the answer from Roslyn's would make the client's
/// startup wait on the backend's, and forwarding Roslyn's document would promise four features the
/// adapter does not carry (C26).
/// </para>
/// <para>
/// What the client tells us in return is not thrown away. Two facts decide behaviour later: whether
/// it applies workspace edits (which decides whether Roslyn's <c>workspace/applyEdit</c> can be
/// forwarded or has to be refused), and the workspace root (which becomes Roslyn's
/// <c>rootUri</c> and the answer to <c>workspace/workspaceFolders</c>).
/// </para>
/// </remarks>
internal sealed class ClientEndpoint : IAsyncDisposable
{
    private readonly LspFrameReader _reader;
    private readonly OutboundQueue _outbound;

    /// <summary>Creates the endpoint over the client's stream pair.</summary>
    /// <param name="input">The client's messages arrive here.</param>
    /// <param name="output">The adapter's messages leave here.</param>
    /// <param name="logger">The stderr log.</param>
    internal ClientEndpoint(Stream input, Stream output, ILogger logger)
    {
        _reader = new LspFrameReader(input);
        _outbound = new OutboundQueue(output, "the client", logger);
    }

    /// <summary>Whether the client declared <c>workspace.applyEdit</c>.</summary>
    internal bool SupportsApplyEdit { get; private set; }

    /// <summary>Whether the client declared <c>workspace.configuration</c>.</summary>
    /// <remarks>
    /// Recorded but deliberately unused for answering Roslyn: even a client that says yes cannot
    /// address a section name containing a pipe, so the adapter always answers those itself. What
    /// this is for is <c>doctor</c>, where "the client claims to answer configuration and cannot"
    /// is a fact worth being able to state.
    /// </remarks>
    internal bool SupportsConfiguration { get; private set; }

    /// <summary>The workspace root the client named, as a URI.</summary>
    internal string? RootUri { get; private set; }

    /// <summary>The client's workspace folders, when it sent any.</summary>
    internal IReadOnlyList<WorkspaceFolder>? WorkspaceFolders { get; private set; }

    /// <summary>The client's own settings block, when it sent one in <c>initializationOptions</c>.</summary>
    internal JsonElement InitializationOptions { get; private set; }

    /// <summary>Reads the next message body, or null at a clean end of stream.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken) =>
        _reader.ReadFrameAsync(cancellationToken);

    /// <summary>Queues an already-encoded message for the client.</summary>
    /// <param name="body">The UTF-8 body.</param>
    internal void Post(ReadOnlyMemory<byte> body) => _outbound.Post(body);

    /// <summary>Sends a <c>window/logMessage</c> the client will show its user.</summary>
    /// <param name="type">The severity: 1 error, 2 warning, 3 info, 4 log.</param>
    /// <param name="message">The text.</param>
    internal void Log(int type, string message) =>
        _outbound.Post(
            new LogMessageNotification { Params = new LogMessageParams { Type = type, Message = message } },
            LspJsonContext.Default.LogMessageNotification);

    /// <summary>Stops accepting outbound messages and waits for the queue to drain.</summary>
    internal async Task DrainAsync()
    {
        _outbound.Complete();
        await _outbound.Completion.ConfigureAwait(false);
    }

    /// <summary>
    /// Records what the client said about itself and returns the answer to its <c>initialize</c>.
    /// </summary>
    /// <param name="body">The raw request.</param>
    /// <param name="info">Its scan, which carries the id token to echo.</param>
    internal byte[] AcceptInitialize(ReadOnlySpan<byte> body, in LspMessageInfo info)
    {
        ClientInitializeParams? parameters = null;

        try
        {
            var envelope = JsonSerializer.Deserialize(body, LspJsonContext.Default.RawParamsRequest);

            if (envelope?.Params.ValueKind == JsonValueKind.Object)
            {
                parameters = envelope.Params.Deserialize(LspJsonContext.Default.ClientInitializeParams);
            }
        }
        catch (JsonException)
        {
            // A handshake nobody could read is still a handshake that has to be answered: the
            // alternative is a client that waits forever. The adapter proceeds with no root, which
            // degrades to misc-files mode rather than to silence.
        }

        RootUri = parameters?.RootUri ?? PathToUri(parameters?.RootPath);
        WorkspaceFolders = parameters?.WorkspaceFolders;
        InitializationOptions = parameters?.InitializationOptions ?? default;

        if (parameters?.Capabilities is { ValueKind: JsonValueKind.Object } capabilities
            && capabilities.TryGetProperty("workspace", out var workspace)
            && workspace.ValueKind == JsonValueKind.Object)
        {
            SupportsApplyEdit = workspace.TryGetProperty("applyEdit", out var applyEdit)
                                && applyEdit.ValueKind == JsonValueKind.True;

            SupportsConfiguration = workspace.TryGetProperty("configuration", out var configuration)
                                    && configuration.ValueKind == JsonValueKind.True;
        }

        if (RootUri is null && WorkspaceFolders is { Count: > 0 } folders)
        {
            RootUri = folders[0].Uri;
        }

        return JsonSerializer.SerializeToUtf8Bytes(
            new InitializeResponse
            {
                Id = ParseId(body, info),
                Result = new InitializeResult
                {
                    Capabilities = BuildCapabilities(),
                    ServerInfo = new ServerInfo { Name = ServerVersion.Name, Version = ServerVersion.Value },
                },
            },
            LspJsonContext.Default.InitializeResponse);
    }

    /// <summary>The capability document this adapter advertises. Authored, never forwarded (D25).</summary>
    internal static ServerCapabilities BuildCapabilities() =>
        new()
        {
            TextDocumentSync = new TextDocumentSyncOptions
            {
                OpenClose = true,
                Change = 1,
                Save = new SaveOptions { IncludeText = false },
            },
        };

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _outbound.DisposeAsync();

    /// <summary>Re-reads the id as an element, so the typed response can echo it exactly.</summary>
    private static JsonElement ParseId(ReadOnlySpan<byte> body, in LspMessageInfo info)
    {
        if (!info.HasIdToken)
        {
            return JsonRpc.Null;
        }

        var reader = new Utf8JsonReader(info.IdToken(body), isFinalBlock: true, state: default);
        return JsonElement.ParseValue(ref reader).Clone();
    }

    /// <summary>Converts a legacy <c>rootPath</c> into a URI, or returns null.</summary>
    private static string? PathToUri(string? path)
    {
        if (path is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException or UriFormatException)
        {
            return null;
        }
    }
}
