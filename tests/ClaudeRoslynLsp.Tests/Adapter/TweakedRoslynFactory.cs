using ClaudeRoslynLsp.Adapter;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// <see cref="RestartableFakeRoslynFactory"/> with the three things a real launch can do that the
/// script cannot: fail to connect at all, end its stream with an exception rather than cleanly, and
/// report the process it started.
/// </summary>
/// <remarks>
/// Each of them is per connection attempt, numbered from one, because the claims under test are
/// about a <em>particular</em> launch — the wake after a recycle, not the first load — and a fault
/// injected into every connection would only prove that a session with no backend has none.
/// </remarks>
internal sealed class TweakedRoslynFactory : IRoslynConnectionFactory
{
    private int _attempts;

    /// <summary>Wraps a restartable fake.</summary>
    /// <param name="inner">What actually connects, when an attempt is not told to fail.</param>
    internal TweakedRoslynFactory(RestartableFakeRoslynFactory inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Inner = inner;
    }

    /// <summary>The fake behind this factory, for the backends it started.</summary>
    internal RestartableFakeRoslynFactory Inner { get; }

    /// <summary>Which attempts throw instead of connecting.</summary>
    internal Func<int, bool> FailAttempt { get; set; } = static _ => false;

    /// <summary>Which attempts' streams throw where they would have ended.</summary>
    internal Func<int, bool> ThrowAtEndOfStream { get; set; } = static _ => false;

    /// <summary>The process every connection reports having launched, as the real launcher does (D101).</summary>
    internal int? LaunchedProcessId { get; set; }

    /// <summary>How many connections have been asked for, including the ones that failed.</summary>
    internal int Attempts => Volatile.Read(ref _attempts);

    /// <inheritdoc />
    public string Description => Inner.Description;

    /// <inheritdoc />
    public async Task<RoslynConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _attempts);

        if (FailAttempt(attempt))
        {
            throw new IOException($"connection attempt {attempt} was told to fail");
        }

        var connection = await Inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var input = ThrowAtEndOfStream(attempt) ? new ThrowAtEndStream(connection.Input) : connection.Input;

        return new RoslynConnection(
            input,
            connection.Output,
            connection.Exited,
            connection.Description,
            connection.DisposeAsync)
        {
            Terminate = connection.Terminate,
            LaunchedProcessId = LaunchedProcessId,
        };
    }

    /// <summary>
    /// A stream that throws where the one it wraps ends — a transport that fails as it closes, which
    /// the read loop does not catch and therefore surfaces as a faulted pump.
    /// </summary>
    private sealed class ThrowAtEndStream : Stream
    {
        private readonly Stream _inner;

        internal ThrowAtEndStream(Stream inner) => _inner = inner;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Check(_inner.Read(buffer, offset, count), count);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Check(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false), buffer.Length);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private static int Check(int read, int requested) =>
            read == 0 && requested > 0
                ? throw new InvalidOperationException("The transport failed as it closed.")
                : read;
    }
}
