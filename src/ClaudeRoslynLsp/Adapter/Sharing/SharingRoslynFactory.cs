using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter.Sharing;

/// <summary>
/// The backend selection for one solution: attach to whoever already has a Roslyn for it, and
/// otherwise become the process that does.
/// </summary>
/// <remarks>
/// <para>
/// <b>D92 — the whole of the sharing decision is one connection factory, and every failure in it
/// falls back to the behaviour of every version before it.</b> A home directory that cannot be
/// written, a platform with no named pipes, a host that published itself and then died between the
/// read and the connect: each of them ends in <c>_inner.ConnectAsync</c>, which is a private Roslyn
/// — slower and hungrier, and correct. Sharing is an optimisation over a product that already works,
/// and an optimisation that can fail a session is not worth having.
/// </para>
/// <para>
/// <b>The relaunch case is why the host is remembered rather than re-derived.</b> When Roslyn dies,
/// the supervisor (D57) asks this factory for another connection — and the session file still names
/// this process, because this process is still the host and its pipe is still accepting with the
/// attached clients' requests held by the gate. Attaching to it would be attaching to ourselves. So a
/// factory that is already hosting relaunches the child and keeps serving, which is also what makes
/// a crash invisible to an attached client: it sees a workspace that went briefly quiet, not a
/// backend that went away.
/// </para>
/// <para>
/// <b>The publish order is the race.</b> The lock is held only for the moment between "is there a
/// session" and "there is now, and it is mine" — the pipe is accepting before the file is written
/// (D86) and Roslyn is launched after the lock is released, so the loser of the race attaches
/// immediately and waits behind the winner's readiness gate rather than behind its download.
/// </para>
/// </remarks>
internal sealed partial class SharingRoslynFactory : IRoslynConnectionFactory
{
    /// <summary>How long to wait between two looks at a session file another process is writing.</summary>
    internal static readonly TimeSpan LockPoll = TimeSpan.FromMilliseconds(100);

    /// <summary>How many times to go round before hosting without having seen the lock.</summary>
    /// <remarks>
    /// Fifty, which with <see cref="LockPoll"/> is five seconds. The lock is held for one file write,
    /// so reaching the end of this loop means the holder died holding it — at which point the file
    /// system will hand it over anyway and the loop is only spending time.
    /// </remarks>
    internal const int LockAttempts = 50;

    private readonly Lock _lock = new();
    private readonly IRoslynConnectionFactory _inner;
    private readonly SessionRegistry _registry;
    private readonly Func<string?> _resolveSolution;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private ISharedEngineHost? _owner;
    private SharedRoslynHost? _host;
    private string? _solutionPath;
    private string? _key;
    private int _connectionGeneration;
    private bool _attached;

    /// <summary>Creates the factory around the one that launches a real Roslyn.</summary>
    /// <param name="inner">What connects when this process has to host, usually <c>LaunchedRoslynFactory</c>.</param>
    /// <param name="registry">The session directory under the adapter's home (D27).</param>
    /// <param name="resolveSolution">
    /// The solution being opened, which is what the session key is derived from. A delegate rather
    /// than a path because discovery (D39-D42) walks a filesystem and this factory is built before
    /// the client has said anything; it is called once, at the first connect, and a workspace with no
    /// solution simply is not shared — there is nothing for two processes to agree they are looking
    /// at.
    /// </param>
    /// <param name="time">The clock, so the lock loop is testable.</param>
    /// <param name="logger">The stderr log.</param>
    internal SharingRoslynFactory(
        IRoslynConnectionFactory inner,
        SessionRegistry registry,
        Func<string?> resolveSolution,
        TimeProvider time,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(resolveSolution);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _registry = registry;
        _resolveSolution = resolveSolution;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Tells the factory which object would host, once that object exists.
    /// </summary>
    /// <remarks>
    /// A property rather than a constructor argument because the dependency genuinely runs both ways:
    /// the session takes the factory, and the factory needs the session. The ordering is safe by
    /// construction — nothing calls <see cref="ConnectAsync"/> until the owner has been built and
    /// started — and an owner that is somehow still missing degrades to a private Roslyn rather than
    /// to a null reference.
    /// </remarks>
    /// <param name="owner">The session or engine that will hold the backend.</param>
    internal void Own(ISharedEngineHost owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        lock (_lock)
        {
            _owner = owner;
        }
    }

    /// <summary>The session key this factory rendezvouses on, once a solution has been resolved.</summary>
    internal string? Key
    {
        get
        {
            lock (_lock)
            {
                return _key;
            }
        }
    }

    /// <summary>The host this process is running, or null when it attached or has not decided yet.</summary>
    internal SharedRoslynHost? Host
    {
        get
        {
            lock (_lock)
            {
                return _host;
            }
        }
    }

    /// <summary>Whether the last connection attached to somebody else's Roslyn.</summary>
    internal bool IsAttached
    {
        get
        {
            lock (_lock)
            {
                return _attached;
            }
        }
    }

    /// <inheritdoc />
    public string Description
    {
        get
        {
            lock (_lock)
            {
                return _attached
                    ? "a shared Roslyn hosted by another claude-roslyn-lsp process"
                    : _host is not null
                        ? $"{_inner.Description}, shared with other claude-roslyn-lsp processes"
                        : _inner.Description;
            }
        }
    }

    /// <inheritdoc />
    public async Task<RoslynConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!TryResolveKey())
        {
            // Misc-files mode (C28): there is no solution, so there is nothing two processes could
            // agree they are both looking at.
            Log.NoSolution(_logger);

            return await _inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Host is not null)
        {
            // A relaunch (D57). The pipe is still accepting and the attached clients' requests are
            // held by the gate this process owns, so all that is missing is the child.
            Log.Relaunching(_logger, Key!);
            return await HostAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var attempt = 0; attempt < LockAttempts; attempt++)
        {
            if (await TryAttachAsync(cancellationToken).ConfigureAwait(false) is { } attached)
            {
                return attached;
            }

            using var guard = _registry.TryAcquireLock(Key!);

            if (guard is null)
            {
                // Another process is deciding right now. It publishes in the time one file write
                // takes, so looking again shortly is cheaper than any coordination would be.
                await Task.Delay(LockPoll, _time, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (_registry.TryRead(Key!) is not null)
            {
                // It published between the read above and the lock. Round again, and attach.
                continue;
            }

            if (!TryStartHosting())
            {
                // Sharing is off the table for this process — no owner, no pipes, or an unwritable
                // home. A private Roslyn is what every version before this one did.
                Log.NotSharing(_logger, Key!);
                return await _inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }

            break;
        }

        return await HostAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops hosting, if this process was, and withdraws its session file.</summary>
    internal async ValueTask StopHostingAsync()
    {
        SharedRoslynHost? host;

        lock (_lock)
        {
            host = _host;
            _host = null;
        }

        if (host is null)
        {
            return;
        }

        // The file first: a process reading it while the pipe is being torn down would connect and
        // then immediately see the connection close, which costs it a whole attach cycle.
        if (Key is { } key)
        {
            _registry.Delete(key);
        }

        await host.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Connects to a published session, or reports that there is nothing to attach to.</summary>
    private async Task<RoslynConnection?> TryAttachAsync(CancellationToken cancellationToken)
    {
        var session = _registry.TryRead(Key!);

        if (session is null || IsOurs(session))
        {
            return null;
        }

        try
        {
            var connection = await new AttachedRoslynFactory(session, _logger)
                .ConnectAsync(cancellationToken)
                .ConfigureAwait(false);

            lock (_lock)
            {
                _attached = true;
            }

            return connection;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The pid was alive and the pipe was not: a reused process id, or a host that went away
            // between the two. Deleting the file is what turns that into "this process hosts".
            Log.AttachFailed(_logger, session.ProcessId, exception.Message);
            _registry.Delete(Key!);

            return null;
        }
    }

    /// <summary>Resolves the solution once, and with it the key two processes rendezvous on.</summary>
    private bool TryResolveKey()
    {
        lock (_lock)
        {
            if (_key is not null)
            {
                return true;
            }
        }

        var path = _resolveSolution();

        if (path is not { Length: > 0 })
        {
            return false;
        }

        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException
                                              or NotSupportedException)
        {
            return false;
        }

        lock (_lock)
        {
            _solutionPath = path;
            _key ??= SessionRegistry.KeyFor(path);
        }

        return true;
    }

    /// <summary>Whether a published session is the one this factory is serving.</summary>
    private bool IsOurs(SharedSession session)
    {
        lock (_lock)
        {
            return _host is { } host && string.Equals(session.PipeName, host.PipeName, StringComparison.Ordinal);
        }
    }

    /// <summary>Starts the pipe server and publishes it. Failure means "keep Roslyn private".</summary>
    private bool TryStartHosting()
    {
        ISharedEngineHost? owner;

        lock (_lock)
        {
            owner = _owner;
        }

        if (owner is null)
        {
            Log.NoOwner(_logger);
            return false;
        }

        var pipeName = SharedRoslynHost.CreatePipeName();
        var host = new SharedRoslynHost(owner, pipeName, _logger);

        try
        {
            host.Listen();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or PlatformNotSupportedException or NotSupportedException)
        {
            Log.CannotListen(_logger, pipeName, exception.Message);
            _ = host.DisposeAsync().AsTask();

            return false;
        }

        var published = _registry.Write(
            Key!,
            new SharedSession(
                Environment.ProcessId,
                ServerVersion.Value,
                SharedSession.PipeTransport,
                pipeName,
                _solutionPath ?? string.Empty,
                _time.GetUtcNow()));

        if (!published)
        {
            _ = host.DisposeAsync().AsTask();
            return false;
        }

        lock (_lock)
        {
            _host = host;
            _attached = false;
        }

        return true;
    }

    /// <summary>Launches the real Roslyn and wraps it so that losing it also stops the sharing.</summary>
    private async Task<RoslynConnection> HostAsync(CancellationToken cancellationToken)
    {
        RoslynConnection inner;

        try
        {
            inner = await _inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Nothing was launched, so there is nothing to share. Attached clients see the pipe close
            // and go and try the same launch themselves, which fails the same way and for the same
            // reason — which is the right outcome: both processes report the same failure.
            await StopHostingAsync().ConfigureAwait(false);
            throw;
        }

        var generation = Interlocked.Increment(ref _connectionGeneration);

        return new RoslynConnection(
            inner.Input,
            inner.Output,
            inner.Exited,
            Description,
            async () =>
            {
                await inner.DisposeAsync().ConfigureAwait(false);

                // Only the connection this factory is currently serving takes the host down with it.
                // A superseded attempt being disposed after a relaunch must not withdraw a session
                // file that names a pipe still carrying two clients.
                if (Volatile.Read(ref _connectionGeneration) == generation)
                {
                    await StopHostingAsync().ConfigureAwait(false);
                }
            });
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 5600,
            Level = LogLevel.Information,
            Message = "This process already hosts the shared Roslyn for session {Key}; relaunching the " +
                      "backend and keeping the attached clients.")]
        internal static partial void Relaunching(ILogger logger, string key);

        [LoggerMessage(
            EventId = 5601,
            Level = LogLevel.Warning,
            Message = "A session file named process {ProcessId} but it could not be attached to " +
                      "({Reason}); it has been removed and this process will host instead.")]
        internal static partial void AttachFailed(ILogger logger, int processId, string reason);

        [LoggerMessage(
            EventId = 5602,
            Level = LogLevel.Warning,
            Message = "Nothing has claimed the shared-engine seam, so this Roslyn will not be shared.")]
        internal static partial void NoOwner(ILogger logger);

        [LoggerMessage(
            EventId = 5603,
            Level = LogLevel.Warning,
            Message = "The shared-engine pipe {PipeName} could not be created ({Reason}); this process " +
                      "keeps its own Roslyn.")]
        internal static partial void CannotListen(ILogger logger, string pipeName, string reason);

        [LoggerMessage(
            EventId = 5605,
            Level = LogLevel.Information,
            Message = "No solution is configured, so this Roslyn is not shared; it serves opened files " +
                      "in misc-files mode for this process alone.")]
        internal static partial void NoSolution(ILogger logger);

        [LoggerMessage(
            EventId = 5604,
            Level = LogLevel.Information,
            Message = "Session {Key} will not be shared; this process launches a Roslyn of its own.")]
        internal static partial void NotSharing(ILogger logger, string key);
    }
}
