using System.Globalization;

using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>How far the backend has got.</summary>
internal enum ReadinessState
{
    /// <summary>Nothing has been connected yet. Even notifications have nowhere to go.</summary>
    Starting,

    /// <summary>Roslyn answered <c>initialize</c>. Notifications flow; requests are still held.</summary>
    RoslynInitialized,

    /// <summary><c>workspace/projectInitializationComplete</c> arrived. Everything flows.</summary>
    ProjectsLoaded,

    /// <summary>The load budget ran out. Everything flows, and the answers may be incomplete.</summary>
    LoadTimedOut,

    /// <summary>There is no usable backend. Requests are refused with an explanation.</summary>
    Failed,
}

/// <summary>What happened to a request that was held.</summary>
internal enum GateOutcome
{
    /// <summary>The gate opened; forward the request now.</summary>
    Pass,

    /// <summary>The client cancelled it while it was held; answer <c>-32800</c>.</summary>
    Cancelled,

    /// <summary>There is no backend; answer <c>-32603</c> with a <c>doctor</c> hint.</summary>
    Failed,
}

/// <summary>
/// Holds requests in arrival order until the Roslyn workspace has actually finished loading, then
/// releases them in that same order.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the single most load-bearing piece of the adapter</b>, and the reason is C27: a
/// request issued before <c>workspace/projectInitializationComplete</c> is answered by Roslyn with
/// an <em>empty successful result</em>, never with <c>ContentModified</c> or any other error. So a
/// client that does not gate silently reports "no definition found" for the first several seconds of
/// every session — the worst possible failure, because it is indistinguishable from a correct answer
/// and teaches the agent that the language server is useless.
/// </para>
/// <para>
/// <b>Block, do not bounce.</b> The obvious alternative — answer <c>-32801 ContentModified</c> and
/// let the client retry — does not survive contact with Claude Code, which retries about three times
/// inside three and a half seconds. A two-project solution takes 2.5-3.3 s to load and a cold or
/// contended one takes 6-9 s (C31), so the retry budget is exhausted before the workspace exists.
/// Holding the request costs the client nothing it was not already going to spend, and it turns a
/// wrong answer into a slow one.
/// </para>
/// <para>
/// <b>The timeout is a floor, not a promise.</b> After
/// <c>CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS</c> the gate flips to
/// <see cref="ReadinessState.LoadTimedOut"/> and passes everything through unheld. A partially
/// loaded workspace answering some questions is worth more than a server that has stopped answering
/// at all, and the log says which state the answers came from.
/// </para>
/// <para>
/// Releases run <em>synchronously, in queue order</em>, rather than by completing a task per held
/// request. Continuation scheduling gives no ordering guarantee, and a client that sent three
/// requests is entitled to have them reach Roslyn in the order it sent them.
/// </para>
/// <para>
/// Driven by <see cref="TimeProvider"/> so the timeout and the ten-second progress notice are
/// testable without a test that actually waits two minutes.
/// </para>
/// </remarks>
internal sealed partial class ReadinessGate : IDisposable
{
    /// <summary>How often a client holding requests is told that it is still waiting.</summary>
    /// <remarks>
    /// Ten seconds is chosen against the human on the other end, not the protocol: long enough that
    /// a normal load never produces a line, short enough that a slow one does not look like a hang.
    /// It goes out as <c>window/logMessage</c>, which is the only channel Claude Code renders.
    /// </remarks>
    internal static readonly TimeSpan NoticeInterval = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private readonly Queue<HeldRequest> _held = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;
    private readonly Action<string> _notifyClient;

    private ITimer? _timeoutTimer;
    private ITimer? _noticeTimer;
    private long _startedAt;
    private ReadinessState _state = ReadinessState.Starting;
    private string? _failureReason;

    /// <summary>Creates a gate that has not started its clock.</summary>
    /// <param name="time">The clock, so the timeout and the notice are testable.</param>
    /// <param name="timeout">How long requests may be held before they are passed through anyway.</param>
    /// <param name="logger">The stderr log.</param>
    /// <param name="notifyClient">Sends a line the client will show its user.</param>
    internal ReadinessGate(TimeProvider time, TimeSpan timeout, ILogger logger, Action<string> notifyClient)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(notifyClient);

        _time = time;
        _timeout = timeout;
        _logger = logger;
        _notifyClient = notifyClient;
    }

    /// <summary>What the workspace is called in the holding notice.</summary>
    internal string WorkspaceDescription { get; set; } = "the workspace";

    /// <summary>The current state.</summary>
    internal ReadinessState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>Why the backend is unusable, when it is.</summary>
    internal string? FailureReason
    {
        get
        {
            lock (_lock)
            {
                return _failureReason;
            }
        }
    }

    /// <summary>How many requests are waiting right now.</summary>
    internal int HeldCount
    {
        get
        {
            lock (_lock)
            {
                return _held.Count;
            }
        }
    }

    /// <summary>
    /// Whether notifications may be forwarded. True from the moment Roslyn has answered
    /// <c>initialize</c>: a notification sent before that is simply lost, because Roslyn refuses
    /// everything until it is initialised.
    /// </summary>
    internal bool NotificationsAllowed
    {
        get
        {
            lock (_lock)
            {
                return _state is ReadinessState.RoslynInitialized
                    or ReadinessState.ProjectsLoaded
                    or ReadinessState.LoadTimedOut;
            }
        }
    }

    /// <summary>Starts the load clock, the timeout and the periodic notice.</summary>
    /// <remarks>
    /// Separate from the constructor because the clock starts when the backend starts, which is when
    /// the client sends <c>initialized</c> — not when the session object was built.
    /// </remarks>
    internal void Start()
    {
        lock (_lock)
        {
            if (_timeoutTimer is not null)
            {
                return;
            }

            _startedAt = _time.GetTimestamp();
            _timeoutTimer = _time.CreateTimer(_ => OnTimeout(), null, _timeout, Timeout.InfiniteTimeSpan);
            _noticeTimer = _time.CreateTimer(_ => OnNotice(), null, NoticeInterval, NoticeInterval);
        }
    }

    /// <summary>
    /// Holds one request, or reports that it may go straight through.
    /// </summary>
    /// <param name="id">The client's id, so <c>$/cancelRequest</c> can find it again.</param>
    /// <param name="method">The method, for the log line and the notice.</param>
    /// <param name="release">
    /// Invoked exactly once with the outcome — either now, synchronously, when the gate is already
    /// open or already failed, or later on the thread that opens it.
    /// </param>
    /// <returns>True when the request was queued; false when <paramref name="release"/> already ran.</returns>
    internal bool TryHold(JsonRpcId id, string method, Action<GateOutcome> release)
    {
        ArgumentNullException.ThrowIfNull(release);

        GateOutcome immediate;

        lock (_lock)
        {
            switch (_state)
            {
                case ReadinessState.ProjectsLoaded:
                case ReadinessState.LoadTimedOut:
                    immediate = GateOutcome.Pass;
                    break;

                case ReadinessState.Failed:
                    immediate = GateOutcome.Failed;
                    break;

                default:
                    _held.Enqueue(new HeldRequest(id, method, release));
                    Log.Held(_logger, method, id, _held.Count);
                    return true;
            }
        }

        release(immediate);
        return false;
    }

    /// <summary>Removes a held request because the client cancelled it, and answers it as cancelled.</summary>
    /// <param name="id">The id named by <c>$/cancelRequest</c>.</param>
    /// <returns>True when a held request was found and answered.</returns>
    internal bool TryCancel(JsonRpcId id)
    {
        HeldRequest? cancelled = null;

        lock (_lock)
        {
            if (_held.Count == 0)
            {
                return false;
            }

            // Rebuilt rather than filtered in place: a Queue<T> has no removal, and preserving the
            // order of everything that stays is the property this whole class exists for.
            var remaining = new Queue<HeldRequest>(_held.Count);

            while (_held.TryDequeue(out var candidate))
            {
                if (cancelled is null && candidate.Id == id)
                {
                    cancelled = candidate;
                }
                else
                {
                    remaining.Enqueue(candidate);
                }
            }

            while (remaining.TryDequeue(out var kept))
            {
                _held.Enqueue(kept);
            }
        }

        if (cancelled is null)
        {
            return false;
        }

        Log.Cancelled(_logger, cancelled.Method, id);
        cancelled.Release(GateOutcome.Cancelled);
        return true;
    }

    /// <summary>Records that Roslyn answered <c>initialize</c>. Notifications start flowing.</summary>
    internal void MarkRoslynInitialized()
    {
        lock (_lock)
        {
            if (_state == ReadinessState.Starting)
            {
                _state = ReadinessState.RoslynInitialized;
            }
        }
    }

    /// <summary>
    /// Records that the workspace finished loading, and releases everything held.
    /// </summary>
    /// <remarks>
    /// Also the answer for the misc-files case (C28): with no solution configured there is nothing
    /// to wait for, and holding requests for a workspace that will never load would be a hang.
    /// </remarks>
    internal void MarkProjectsLoaded() => Open(ReadinessState.ProjectsLoaded, GateOutcome.Pass);

    /// <summary>Records that there is no usable backend, and refuses everything held.</summary>
    /// <param name="reason">What went wrong, in a sentence the user can act on.</param>
    internal void MarkFailed(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        lock (_lock)
        {
            _failureReason = reason;
        }

        Open(ReadinessState.Failed, GateOutcome.Failed);
    }

    /// <summary>The message an unusable backend answers a request with.</summary>
    internal string FailureMessage =>
        $"{ServerVersion.Name} has no working Roslyn backend: {FailureReason ?? "the backend did not start"}. "
        + $"Run `{ServerVersion.Name} doctor` to see the resolution chain.";

    /// <inheritdoc />
    public void Dispose()
    {
        _timeoutTimer?.Dispose();
        _noticeTimer?.Dispose();
    }

    /// <summary>Moves to a terminal state and drains the queue with one outcome.</summary>
    /// <param name="state">The state to move to.</param>
    /// <param name="outcome">What every held request becomes.</param>
    private void Open(ReadinessState state, GateOutcome outcome)
    {
        HeldRequest[] drained;
        TimeSpan elapsed;

        lock (_lock)
        {
            if (_state is ReadinessState.ProjectsLoaded or ReadinessState.LoadTimedOut or ReadinessState.Failed)
            {
                // Already open. Roslyn sends projectInitializationComplete once per load, and a
                // reload after the timeout must not un-fail a failed session.
                return;
            }

            _state = state;
            elapsed = _startedAt == 0 ? TimeSpan.Zero : _time.GetElapsedTime(_startedAt);

            drained = new HeldRequest[_held.Count];
            var index = 0;

            while (_held.TryDequeue(out var request))
            {
                drained[index++] = request;
            }

            _timeoutTimer?.Dispose();
            _noticeTimer?.Dispose();
            _timeoutTimer = null;
            _noticeTimer = null;
        }

        Log.Opened(_logger, state, drained.Length, elapsed.TotalSeconds);

        // Outside the lock, in order. A release posts to an outbound queue, which never blocks, so
        // the sequence the client sees is the sequence it sent.
        foreach (var request in drained)
        {
            request.Release(outcome);
        }
    }

    /// <summary>The load budget ran out.</summary>
    private void OnTimeout()
    {
        Log.TimedOut(_logger, _timeout.TotalSeconds, HeldCount);
        _notifyClient(
            $"The workspace did not finish loading within {_timeout.TotalSeconds:0} s; answers may be "
            + "incomplete until it does. Set CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS higher if this "
            + "solution is simply large.");

        Open(ReadinessState.LoadTimedOut, GateOutcome.Pass);
    }

    /// <summary>Tells the client it is still waiting, and why.</summary>
    private void OnNotice()
    {
        int count;
        TimeSpan elapsed;

        lock (_lock)
        {
            if (_state is ReadinessState.ProjectsLoaded or ReadinessState.LoadTimedOut or ReadinessState.Failed)
            {
                return;
            }

            count = _held.Count;
            elapsed = _startedAt == 0 ? TimeSpan.Zero : _time.GetElapsedTime(_startedAt);
        }

        if (count == 0)
        {
            return;
        }

        _notifyClient(string.Format(
            CultureInfo.InvariantCulture,
            "holding {0} request(s) until {1} finishes loading, {2:0}s elapsed",
            count,
            WorkspaceDescription,
            elapsed.TotalSeconds));
    }

    /// <summary>One request waiting for the workspace.</summary>
    private sealed record HeldRequest(JsonRpcId Id, string Method, Action<GateOutcome> Release);

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 400,
            Level = LogLevel.Debug,
            Message = "Holding {Method} (id {Id}) until the workspace is loaded; {Count} held.")]
        internal static partial void Held(ILogger logger, string method, JsonRpcId id, int count);

        [LoggerMessage(
            EventId = 401,
            Level = LogLevel.Information,
            Message = "Readiness gate open ({State}) after {Elapsed:0.0} s; released {Count} held request(s).")]
        internal static partial void Opened(ILogger logger, ReadinessState state, int count, double elapsed);

        [LoggerMessage(
            EventId = 402,
            Level = LogLevel.Warning,
            Message = "The workspace did not load within {Timeout:0} s; passing {Count} held request(s) " +
                      "through to a workspace that may still be incomplete.")]
        internal static partial void TimedOut(ILogger logger, double timeout, int count);

        [LoggerMessage(
            EventId = 403,
            Level = LogLevel.Debug,
            Message = "The client cancelled {Method} (id {Id}) while it was held.")]
        internal static partial void Cancelled(ILogger logger, string method, JsonRpcId id);
    }
}
