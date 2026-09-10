using System.Diagnostics;
using System.Text.Json;

using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// The Roslyn-facing half of the adapter: the reader, the single writer, and the authored handshake.
/// </summary>
/// <remarks>
/// The handshake is the interesting part and it is deliberately not symmetric with the client's. The
/// adapter declares what <em>it</em> can do for Roslyn — answer configuration, accept dynamic
/// registrations, accept watched-file registrations, accept work-done progress — rather than
/// relaying what Claude Code declared, because Claude Code declares almost none of those and refuses
/// several of them outright. Forwarding the client's document would therefore switch off exactly the
/// machinery this adapter exists to supply. See <see cref="RoslynInitializeParams"/> for the list
/// and the argument for each entry.
/// </remarks>
internal sealed class ServerEndpoint : IAsyncDisposable
{
    private readonly LspFrameReader _reader;
    private readonly OutboundQueue _outbound;

    /// <summary>Creates the endpoint over an established connection.</summary>
    /// <param name="connection">The connection to Roslyn.</param>
    /// <param name="logger">The stderr log.</param>
    internal ServerEndpoint(RoslynConnection connection, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Connection = connection;
        _reader = new LspFrameReader(connection.Input);
        _outbound = new OutboundQueue(connection.Output, "Roslyn", logger);
    }

    /// <summary>The connection this endpoint speaks over.</summary>
    internal RoslynConnection Connection { get; }

    /// <summary>Reads the next message body, or null at a clean end of stream.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken) =>
        _reader.ReadFrameAsync(cancellationToken);

    /// <summary>Queues an already-encoded message for Roslyn.</summary>
    /// <param name="body">The UTF-8 body.</param>
    internal void Post(ReadOnlyMemory<byte> body) => _outbound.Post(body);

    /// <summary>Stops accepting outbound messages and waits for the queue to drain.</summary>
    internal async Task DrainAsync()
    {
        _outbound.Complete();
        await _outbound.Completion.ConfigureAwait(false);
    }

    /// <summary>Builds the <c>initialize</c> the adapter sends Roslyn.</summary>
    /// <param name="idToken">The id it is issued under, minted by the id map.</param>
    /// <param name="rootUri">The workspace root, taken from the client.</param>
    /// <param name="workspaceFolders">The client's folders, when it sent any.</param>
    /// <param name="watchFiles">
    /// Whether the adapter will actually deliver watched-file events. False drops the capability
    /// from the document entirely, so Roslyn registers nothing (C34) rather than registering 140
    /// watchers and waiting to be told about changes that will never arrive.
    /// </param>
    internal static byte[] BuildInitialize(
        ReadOnlySpan<byte> idToken,
        string? rootUri,
        IReadOnlyList<WorkspaceFolder>? workspaceFolders,
        bool watchFiles = true)
    {
        var folders = workspaceFolders;

        if (folders is null && rootUri is { Length: > 0 })
        {
            // Roslyn's --autoLoadProjects keys off workspaceFolders, and a client that sent only a
            // rootUri would otherwise leave it with none. Synthesising one costs nothing and keeps
            // the two spellings of "the workspace" in agreement.
            folders = [new WorkspaceFolder { Uri = rootUri, Name = NameOf(rootUri) }];
        }

        var parameters = new RoslynInitializeParams
        {
            // The adapter's own pid, not the client's. Roslyn watches it and exits when it goes, so
            // naming the client here would leave Roslyn alive after the adapter died — an orphaned
            // process holding a whole solution in memory with nobody to answer.
            ProcessId = Environment.ProcessId,
            ClientInfo = new ServerInfo { Name = ServerVersion.Name, Version = ServerVersion.Value },
            RootUri = rootUri,
            WorkspaceFolders = folders,
            Capabilities = new RoslynClientCapabilities
            {
                Workspace = new RoslynWorkspaceCapabilities
                {
                    DidChangeWatchedFiles = watchFiles ? new WatchedFilesCapability() : null,
                },
            },
        };

        return JsonRpcErrors.Request(
            idToken,
            "initialize",
            JsonSerializer.SerializeToUtf8Bytes(parameters, LspJsonContext.Default.RoslynInitializeParams));
    }

    /// <summary>The <c>initialized</c> notification that ends the handshake.</summary>
    internal static byte[] BuildInitialized() => JsonRpcErrors.Notification("initialized", "{}"u8);

    /// <summary>The <c>shutdown</c> request, under an id the caller minted.</summary>
    /// <param name="idToken">The id it is issued under.</param>
    internal static byte[] BuildShutdown(ReadOnlySpan<byte> idToken) =>
        JsonRpcErrors.Request(idToken, "shutdown", default);

    /// <summary>The <c>exit</c> notification.</summary>
    internal static byte[] BuildExit() => JsonRpcErrors.Notification("exit", default);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _outbound.DisposeAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The last path segment of a URI, for a workspace folder's display name.</summary>
    private static string NameOf(string uri)
    {
        Debug.Assert(uri.Length > 0, "Callers check for an empty URI first.");

        var trimmed = uri.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');

        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
    }
}
