using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter.Sharing;

/// <summary>
/// One other <c>claude-roslyn-lsp</c> process attached to this one's Roslyn: its frames, its single
/// writer, the requests it has outstanding and the documents it has open.
/// </summary>
/// <remarks>
/// <para>
/// The same two primitives every peer in this repository is served with — an
/// <see cref="LspFrameReader"/> and an <see cref="OutboundQueue"/> — because an attached client is an
/// LSP peer in every respect that matters, and the ordering guarantee the queue exists for (answers
/// leave in the order they were produced) applies to it exactly as it does to Claude Code.
/// </para>
/// <para>
/// The outstanding-request table is per client rather than shared: <c>$/cancelRequest</c> names an
/// id in the sender's own numbering, so a table shared between two attached clients would let one of
/// them cancel the other's request by counting to the same number.
/// </para>
/// </remarks>
internal sealed class SharedClient : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<JsonRpcId, CancellationTokenSource> _inFlight = [];
    private readonly LspFrameReader _reader;
    private readonly OutboundQueue _outbound;
    private readonly Func<ValueTask> _dispose;

    private int _disposed;

    /// <summary>Creates a client over an established transport.</summary>
    /// <param name="id">This host's own number for the client, used in log lines.</param>
    /// <param name="input">What the host reads.</param>
    /// <param name="output">What the host writes.</param>
    /// <param name="dispose">Releases the transport.</param>
    /// <param name="logger">The stderr log.</param>
    internal SharedClient(int id, Stream input, Stream output, Func<ValueTask> dispose, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(dispose);
        ArgumentNullException.ThrowIfNull(logger);

        Id = id;
        _reader = new LspFrameReader(input);
        _outbound = new OutboundQueue(output, $"attached client {id}", logger);
        _dispose = dispose;
    }

    /// <summary>This host's number for the client.</summary>
    internal int Id { get; }

    /// <summary>The documents this client has open, so they can be released when it goes.</summary>
    internal HashSet<string> Documents { get; } = new(StringComparer.Ordinal);

    /// <summary>The task reading this client's messages.</summary>
    internal Task? Pump { get; set; }

    /// <summary>Reads the next message, or null at a clean end of stream.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken) =>
        _reader.ReadFrameAsync(cancellationToken);

    /// <summary>Queues one already-encoded message for this client.</summary>
    /// <param name="body">The UTF-8 body.</param>
    internal void Post(ReadOnlyMemory<byte> body) => _outbound.Post(body);

    /// <summary>Records a request as outstanding and returns what cancels it.</summary>
    /// <param name="id">The client's own id for the request.</param>
    /// <param name="stopping">The host's shutdown token, which cancels everything at once.</param>
    internal CancellationTokenSource Register(JsonRpcId id, CancellationToken stopping)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);

        lock (_lock)
        {
            // A client that reuses an id it has an answer outstanding for is out of contract; the
            // last one wins for cancellation and both still get their own answer, because the answer
            // is built from the id token rather than looked up here.
            _inFlight[id] = cancellation;
        }

        return cancellation;
    }

    /// <summary>Forgets a request that has been answered.</summary>
    /// <param name="id">The client's own id for the request.</param>
    internal void Complete(JsonRpcId id)
    {
        lock (_lock)
        {
            _inFlight.Remove(id);
        }
    }

    /// <summary>Cancels one outstanding request, which also cancels it at Roslyn.</summary>
    /// <param name="id">The id named by <c>$/cancelRequest</c>.</param>
    internal void Cancel(JsonRpcId id)
    {
        CancellationTokenSource? cancellation;

        lock (_lock)
        {
            _inFlight.Remove(id, out cancellation);
        }

        TryCancel(cancellation);
    }

    /// <summary>Cancels everything this client had outstanding, because it has gone.</summary>
    internal void CancelAll()
    {
        CancellationTokenSource[] outstanding;

        lock (_lock)
        {
            outstanding = [.. _inFlight.Values];
            _inFlight.Clear();
        }

        foreach (var cancellation in outstanding)
        {
            TryCancel(cancellation);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancelAll();

        await _outbound.DisposeAsync().ConfigureAwait(false);
        await _dispose().ConfigureAwait(false);
    }

    /// <summary>Cancels a source that may already have been disposed by the request that owned it.</summary>
    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The request completed between the lookup and the cancel, which is the outcome wanted.
        }
    }
}
