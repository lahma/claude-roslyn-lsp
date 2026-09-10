using System.IO.Pipes;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter.Sharing;

/// <summary>
/// The backend for a process that did <em>not</em> launch Roslyn: a named-pipe connection to the one
/// that did.
/// </summary>
/// <remarks>
/// <para>
/// <b>D91 — attaching is a connection factory, so nothing above it changes.</b> Both
/// <see cref="AdapterSession"/> and <c>OwnedRoslynEngine</c> already take an
/// <see cref="IRoslynConnectionFactory"/> and neither of them asks what is on the other end. That is
/// the whole reason the shared engine costs one new implementation of a two-member interface rather
/// than a second lifecycle: an attached session sends the same authored <c>initialize</c> (D14),
/// holds requests behind the same readiness gate (D46), mirrors the same documents (D13) and is
/// supervised by the same <see cref="RoslynSupervisor"/> (D57). What it talks to happens to be
/// another copy of this program.
/// </para>
/// <para>
/// <see cref="RoslynConnection.Exited"/> is completed the moment a read reaches end of stream, which
/// is what a host that shut down or died looks like from here. Without it the session's <c>exit</c>
/// would wait out its whole budget for a process that was never this one's child.
/// </para>
/// </remarks>
internal sealed partial class AttachedRoslynFactory : IRoslynConnectionFactory
{
    /// <summary>
    /// How long the connect is given.
    /// </summary>
    /// <remarks>
    /// Short on purpose: the host publishes its session file only after its pipe is accepting (D86),
    /// so a connection that does not happen almost immediately means the file is stale in a way the
    /// pid check could not see — a reused process id, or a host that died between the read and the
    /// connect. Waiting longer would only delay the fallback, which is to become the host.
    /// </remarks>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly SharedSession _session;
    private readonly ILogger _logger;

    /// <summary>Creates a factory over one published session.</summary>
    /// <param name="session">The session file's contents, already validated by the registry.</param>
    /// <param name="logger">The stderr log.</param>
    internal AttachedRoslynFactory(SharedSession session, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(logger);

        _session = session;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Description =>
        $"the shared Roslyn hosted by {ServerVersion.Name} process {_session.ProcessId}";

    /// <inheritdoc />
    public async Task<RoslynConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            _session.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            await pipe.ConnectAsync((int) ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);

            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new IOException(
                $"Could not attach to the shared Roslyn on pipe '{_session.PipeName}' ({exception.Message}).",
                exception);
        }

        Log.Attached(_logger, _session.ProcessId, _session.PipeName);

        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        return new RoslynConnection(
            new EndOfStreamSignal(pipe, exited),
            pipe,
            exited.Task,
            Description,
            async () =>
            {
                exited.TrySetResult();
                await pipe.DisposeAsync().ConfigureAwait(false);
            })
        {
            Attached = true,
            HostProcessId = _session.ProcessId,
        };
    }

    /// <summary>
    /// A read-only view of the pipe that reports the moment it stops carrying anything.
    /// </summary>
    /// <remarks>
    /// A named pipe has no exit code and no <see cref="System.Diagnostics.Process.Exited"/>; the only
    /// observable event is a read returning zero or throwing. Turning that into the same
    /// <see cref="RoslynConnection.Exited"/> task a launched child produces is what lets the
    /// supervisor treat a host that went away exactly like a Roslyn that crashed — which it is, from
    /// here.
    /// </remarks>
    private sealed class EndOfStreamSignal(Stream inner, TaskCompletionSource exited) : Stream
    {
        /// <inheritdoc />
        public override bool CanRead => inner.CanRead;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int read;

            try
            {
                read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                exited.TrySetResult();
                throw;
            }

            if (read == 0)
            {
                exited.TrySetResult();
            }

            return read;
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                exited.TrySetResult();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 5500,
            Level = LogLevel.Information,
            Message = "Attached to the Roslyn hosted by process {ProcessId} on pipe {PipeName}; this " +
                      "process starts no server of its own (D23).")]
        internal static partial void Attached(ILogger logger, int processId, string pipeName);
    }
}
