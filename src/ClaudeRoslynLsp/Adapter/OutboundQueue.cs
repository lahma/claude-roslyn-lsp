using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// The single writer for one side of the adapter: everything bound for a peer is posted here and
/// leaves in the order it was posted.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LspFrameWriter"/> already guarantees that two frames cannot interleave on the wire.
/// What it cannot guarantee is <em>order</em>: a semaphore hands the stream to whichever waiter the
/// runtime picks, so two answers released together by the readiness gate could reach the client in
/// either sequence. A channel with one reader makes posting order the wire order, and posting is a
/// non-blocking call — which is what lets the gate release a queue of held requests synchronously,
/// inside the sequence it recorded them in.
/// </para>
/// <para>
/// Unbounded on purpose. The alternative is back-pressure onto a read loop, and a read loop that
/// stops reading is a peer whose pipe fills and whose next write blocks forever — a deadlock that
/// looks exactly like a hung language server. The messages queued here are already bounded by the
/// number of requests in flight, which the peer itself limits.
/// </para>
/// </remarks>
internal sealed partial class OutboundQueue : IAsyncDisposable
{
    private readonly Channel<ReadOnlyMemory<byte>> _channel =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

    private readonly LspFrameWriter _writer;
    private readonly ILogger _logger;
    private readonly string _peer;
    private readonly Task _pump;

    /// <summary>Creates the queue and starts its pump.</summary>
    /// <param name="stream">The outbound stream.</param>
    /// <param name="peer">Which peer this writes to, for log lines.</param>
    /// <param name="logger">Where write failures are reported. Never the stream itself.</param>
    internal OutboundQueue(Stream stream, string peer, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _writer = new LspFrameWriter(stream);
        _logger = logger;
        _peer = peer;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Completes once every posted message has been written, or writing has failed.</summary>
    internal Task Completion => _pump;

    /// <summary>Queues one already-encoded message body.</summary>
    /// <param name="body">The UTF-8 body. It is framed on the way out.</param>
    /// <returns>False once the queue has been closed, which happens only during shutdown.</returns>
    internal bool Post(ReadOnlyMemory<byte> body) => _channel.Writer.TryWrite(body);

    /// <summary>Serialises <paramref name="message"/> through its source-generated contract and queues it.</summary>
    /// <typeparam name="T">The message type, which must be declared in <see cref="LspJsonContext"/>.</typeparam>
    /// <param name="message">The message.</param>
    /// <param name="typeInfo">The contract, passed explicitly so nothing can fall back to reflection.</param>
    internal bool Post<T>(T message, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return Post(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message, typeInfo));
    }

    /// <summary>Stops accepting messages; the pump drains what is already queued and then finishes.</summary>
    internal void Complete() => _channel.Writer.TryComplete();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Complete();

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.PumpFailed(_logger, _peer, exception);
        }

        _writer.Dispose();
    }

    /// <summary>Writes queued messages until the queue is completed or the stream refuses.</summary>
    private async Task PumpAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var body))
                {
                    await _writer.WriteFrameAsync(body).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                              or OperationCanceledException)
        {
            // The peer went away mid-write. Normal at shutdown and unrecoverable at any other time:
            // there is no second channel to report a broken channel on, so this is logged and the
            // session's own end-of-stream handling decides what it means.
            Log.PeerClosed(_logger, _peer, exception);
        }
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 300,
            Level = LogLevel.Debug,
            Message = "The {Peer} connection closed while a message was being written.")]
        internal static partial void PeerClosed(ILogger logger, string peer, Exception exception);

        [LoggerMessage(EventId = 301, Level = LogLevel.Warning, Message = "Writing to {Peer} failed.")]
        internal static partial void PumpFailed(ILogger logger, string peer, Exception exception);
    }
}
