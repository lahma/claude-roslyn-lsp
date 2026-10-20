using System.Diagnostics;

using ClaudeRoslynLsp.Configuration;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>Why a backend was recycled.</summary>
internal enum RecycleReason
{
    /// <summary>It grew past its threshold and the session has been idle for the recycle window.</summary>
    Growth,

    /// <summary>The session has been idle for the idle-shutdown window, whatever the backend's size.</summary>
    IdleShutdown,
}

/// <summary>Where a backend is in its life, as far as the idle policy is concerned.</summary>
internal enum BackendPhase
{
    /// <summary>Being launched or loading the workspace. Never recycled.</summary>
    Starting,

    /// <summary>The workspace is loaded and answering.</summary>
    Running,

    /// <summary>A recycle is stopping it.</summary>
    Recycling,

    /// <summary>Stopped on purpose; the next request relaunches it (D96).</summary>
    Dormant,
}

/// <summary>One recycle decision, carried into the stop so the line that reports it has the numbers.</summary>
/// <param name="Reason">Which rule fired.</param>
/// <param name="Idle">How long the session had been idle.</param>
/// <param name="MemoryBytes">The sample the decision was made on, when there was one.</param>
/// <param name="BaselineBytes">The backend's baseline, when it had one.</param>
/// <param name="ThresholdBytes">The threshold the sample exceeded, for a growth recycle.</param>
internal sealed record RecycleDecision(
    RecycleReason Reason,
    TimeSpan Idle,
    long? MemoryBytes,
    long? BaselineBytes,
    long? ThresholdBytes);

/// <summary>
/// What the session file, <c>doctor</c> and <c>getWorkspaceStatus</c> report about a backend (D102).
/// </summary>
/// <param name="Phase">Where it is in its life.</param>
/// <param name="ProcessId">Roslyn's own process id (C45), or null while there is none.</param>
/// <param name="LastActivityAt">When a client last asked this backend for anything (D95).</param>
/// <param name="BaselineBytes">Its memory once the workspace had settled, for this generation.</param>
/// <param name="LatestBytes">The most recent memory sample, by the policy's metric (D100).</param>
/// <param name="Recycles">How many times it has been recycled in this process's lifetime.</param>
/// <param name="LastRecycleAt">When it last was.</param>
internal sealed record BackendStatus(
    BackendPhase Phase,
    int? ProcessId,
    DateTimeOffset LastActivityAt,
    long? BaselineBytes,
    long? LatestBytes,
    int Recycles,
    DateTimeOffset? LastRecycleAt)
{
    /// <summary>The one state word other code tests for: a backend that is answering.</summary>
    internal const string RunningState = "running";

    /// <summary>The state as the session file spells it: <c>starting</c>, <c>running</c> or <c>dormant</c>.</summary>
    /// <remarks>
    /// Three words for four phases, because the file describes what a request would meet rather
    /// than what this process is doing: during a recycle the gate is already dormant (D96), so a
    /// request arriving then is held and relaunches the backend exactly as it would a minute later.
    /// </remarks>
    internal string StateName => Phase switch
    {
        BackendPhase.Starting => "starting",
        BackendPhase.Running => RunningState,
        _ => "dormant",
    };
}

/// <summary>
/// The idle policy's numbers, read once from the environment (D96, D100, D106).
/// </summary>
/// <param name="IdleRecycle">How long the session must be idle before a grown backend is recycled; zero is off.</param>
/// <param name="GrowthFactor">How far past its baseline it must have grown.</param>
/// <param name="IdleShutdown">How long the session must be idle before the backend is recycled whatever its size; zero is off.</param>
internal sealed record RecyclePolicy(TimeSpan IdleRecycle, double GrowthFactor, TimeSpan IdleShutdown)
{
    /// <summary>The absolute floor every threshold is at least this far above its baseline.</summary>
    /// <remarks>
    /// The factor alone is wrong at both ends. On a small solution doubling a 300 MB baseline is
    /// 300 MB of growth, which a single find-references produces and a reload would only pay back;
    /// the floor makes the threshold 812 MB there. On a large one the factor dominates, which is the
    /// case the recycle exists for.
    /// </remarks>
    internal const long GrowthFloorBytes = 512L * 1024 * 1024;

    /// <summary>The longest the compaction step waits into an idle period (D101).</summary>
    internal static readonly TimeSpan MaxCompactionLead = TimeSpan.FromMinutes(5);

    /// <summary>A policy that never recycles anything.</summary>
    internal static RecyclePolicy Off { get; } = new(TimeSpan.Zero, ClaudeRoslynLspOptions.DefaultRecycleGrowthFactor, TimeSpan.Zero);

    /// <summary>Whether either rule can fire at all.</summary>
    internal bool Enabled => IdleRecycle > TimeSpan.Zero || IdleShutdown > TimeSpan.Zero;

    /// <summary>
    /// How long into an idle period the compactor is asked to give memory back: the earlier of five
    /// minutes and half the recycle window (D101).
    /// </summary>
    internal TimeSpan CompactAfter => IdleRecycle / 2 < MaxCompactionLead ? IdleRecycle / 2 : MaxCompactionLead;

    /// <summary>Reads the policy from the process's configuration.</summary>
    /// <param name="options">The configuration.</param>
    internal static RecyclePolicy From(ClaudeRoslynLspOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new RecyclePolicy(
            TimeSpan.FromMinutes(options.IdleRecycleMinutes),
            options.RecycleGrowthFactor,
            TimeSpan.FromMinutes(options.IdleShutdownMinutes));
    }

    /// <summary>
    /// The size past which an idle backend is recycled: <c>max(baseline × factor, baseline + 512 MB)</c>.
    /// </summary>
    /// <param name="baselineBytes">The backend's baseline.</param>
    /// <param name="factor">The growth factor.</param>
    internal static long ThresholdFor(long baselineBytes, double factor) =>
        Math.Max((long) (baselineBytes * factor), baselineBytes + GrowthFloorBytes);
}

/// <summary>
/// The owner's half of a recycle: whichever object holds the one connection to Roslyn.
/// </summary>
/// <remarks>
/// Both <see cref="AdapterSession"/> and <c>OwnedRoslynEngine</c> implement it, and each implements
/// it with its own lifecycle glue (D75): what is shared is the policy, which is this file, and the
/// protocol stop, which is <see cref="ServerEndpoint.StopByProtocolAsync"/>.
/// </remarks>
internal interface IRecycleTarget
{
    /// <summary>
    /// Whether the backend could be stopped right now without anybody noticing: this process owns
    /// it, the gate is open, nothing is held and nothing is in flight.
    /// </summary>
    bool CanRecycle { get; }

    /// <summary>Stops the backend for a recycle and leaves the gate dormant.</summary>
    /// <param name="decision">What was decided, for the record.</param>
    /// <returns>True when the backend is gone; false when the recycle was abandoned and nothing changed.</returns>
    Task<bool> RecycleAsync(RecycleDecision decision);

    /// <summary>Relaunches a dormant backend through the crash path's reconnect (D57), without spending its budget.</summary>
    void Wake();

    /// <summary>
    /// Whether the backend is gone and the gate dormant, so that the next request wakes it.
    /// </summary>
    /// <remarks>
    /// Asked only when <see cref="RecycleAsync"/> threw. The targets are written never to throw once
    /// the gate is dormant, and this is the belt to those braces: the policy follows the target's real
    /// state rather than assuming the backend is still running, because a dormant gate under a policy
    /// that thinks it is running holds every request for ever and wakes nothing.
    /// </remarks>
    bool IsDormant { get; }
}

/// <summary>
/// Asks a Roslyn child's runtime to compact its heap and give the space back (D101).
/// </summary>
/// <remarks>
/// A compacting gen2 collection reclaims fragmentation, which is most of what an idle child adds over
/// its load size (C66), but it cannot drop state the workspace still references — that is what the
/// recycle is for. The production implementation is <c>Roslyn/DiagnosticsIpcCompactor</c>, which asks
/// the child's runtime over its diagnostics channel; <see cref="NoBackendCompactor"/> stands in for
/// a backend this process did not launch, and the policy is then exactly the plain recycle.
/// </remarks>
internal interface IBackendCompactor
{
    /// <summary>Asks the runtime of one process to compact now.</summary>
    /// <param name="processId">Roslyn's process id (C45).</param>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    /// <returns>True when a compaction was actually requested, so a fresh sample is worth taking.</returns>
    Task<bool> TryCompactAsync(int processId, CancellationToken cancellationToken);
}

/// <summary>
/// The compactor that does nothing: the default for a backend this process did not launch, and for
/// the tests (D101).
/// </summary>
internal sealed class NoBackendCompactor : IBackendCompactor
{
    private NoBackendCompactor()
    {
    }

    /// <summary>The one instance.</summary>
    internal static NoBackendCompactor Instance { get; } = new();

    /// <inheritdoc />
    public Task<bool> TryCompactAsync(int processId, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>
/// How much memory a backend process is holding, measured the way the idle policy measures it.
/// </summary>
internal static class BackendMemory
{
    /// <summary>
    /// The policy's metric: private bytes on Windows, the working set elsewhere (D100).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Windows: the commit charge, not the working set.</b> Windows trims an idle process's
    /// working set as soon as anything else wants the memory, and it was observed doing exactly that
    /// to a grown child — 119 MB of working set over 711 MB of private bytes. A threshold on the
    /// working set would read that child as small, keep it, and leave the commit — which is what
    /// actually stops other processes from getting memory — exactly where it was.
    /// </para>
    /// <para>
    /// <b>Elsewhere: the working set.</b> On Linux <see cref="Process.PrivateMemorySize64"/> is not
    /// the anonymous resident memory a GC heap is; the resident set is the number the kernel's own
    /// out-of-memory accounting uses, and it is what <see cref="Process.WorkingSet64"/> reads.
    /// </para>
    /// </remarks>
    /// <param name="processId">The process to measure.</param>
    /// <returns>The figure in bytes, or null when the process cannot be looked at.</returns>
    internal static long? PolicyBytes(int processId) =>
        Sample(processId) is { } sample
            ? OperatingSystem.IsWindows() ? sample.PrivateBytes : sample.WorkingSetBytes
            : null;

    /// <summary>Both figures for one process, for the reports that show both.</summary>
    /// <param name="processId">The process to measure.</param>
    /// <returns>The sample, or null when the process is gone or cannot be read.</returns>
    internal static BackendMemorySample? Sample(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            process.Refresh();

            return new BackendMemorySample(process.WorkingSet64, process.PrivateMemorySize64);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                              or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Bytes as whole megabytes, for the reports.</summary>
    /// <param name="bytes">The figure, or null.</param>
    internal static int? Megabytes(long? bytes) => bytes is { } value ? (int) (value / (1024 * 1024)) : null;
}

/// <summary>One process's memory: what the OS has resident for it, and what it has committed.</summary>
/// <param name="WorkingSetBytes">The working set.</param>
/// <param name="PrivateBytes">The private bytes (the commit charge on Windows).</param>
internal readonly record struct BackendMemorySample(long WorkingSetBytes, long PrivateBytes);

/// <summary>
/// The idle policy: when a backend that nobody is using has grown enough that a reload is cheaper
/// than keeping it, stop it, and relaunch it on the next request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the adapter, and why lazily (D96).</b> A Roslyn child that edited, built and ran tests all
/// day holds four to six times its load size and gives none of it back, because an idle process
/// allocates nothing and so no collection ever runs; with a dozen sessions open that was most of a
/// 64 GB machine. Nothing in Roslyn knows its client went quiet. This adapter does, and it already
/// rebuilds a backend without the client noticing after a crash (D57) — so it is the one place a
/// fresh process can be swapped in. Lazily, because a quiet session may never ask again, and an
/// eagerly reloaded child would pay the load and most of the growth back for nothing.
/// </para>
/// <para>
/// <b>One clock, owned by whichever process owns the child.</b> The activity stamp (D95) is fed by
/// that process's own client and, when it is hosting, by every attached client through
/// <see cref="Sharing.ISharedEngineHost.Recycler"/>, so the host recycles only when everybody using
/// its Roslyn has been quiet for the window. A process whose engine is attached owns no child and
/// never decides anything.
/// </para>
/// <para>
/// <b>Ordering.</b> Activity and the start of a recycle are serialised on this object's lock: the
/// decision carries the activity serial it was made on, and a recycle whose serial is stale by the
/// time it starts does not start. Activity that arrives once one has started is remembered and wakes
/// the backend the moment it is gone — or, earlier still, makes the owner abandon the stop before it
/// has told Roslyn anything.
/// </para>
/// </remarks>
internal sealed partial class BackendRecycler : IDisposable
{
    /// <summary>How often the policy looks.</summary>
    /// <remarks>
    /// A minute: the windows are tens of minutes, and a memory sample is a system call, so looking
    /// more often would cost something and decide nothing sooner.
    /// </remarks>
    internal static readonly TimeSpan EvaluationInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long after the workspace loads the baseline is taken (D100).</summary>
    /// <remarks>
    /// A minute, because the instant the gate opens is not the settled size: the first diagnostics
    /// pulls, the analyzers' first pass over the open documents and the build hosts winding down all
    /// land in the seconds after <c>projectInitializationComplete</c>, and a baseline taken in the
    /// middle of them is a threshold that moves with the timing of the load.
    /// </remarks>
    internal static readonly TimeSpan BaselineDelay = TimeSpan.FromSeconds(60);

    /// <summary>How long after a compaction the next sample is taken (D101).</summary>
    internal static readonly TimeSpan CompactionSettle = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many wakes in a row may fail before the session gives up for good (D96).
    /// </summary>
    /// <remarks>
    /// Three, for the same reason the crash budget is two in ten minutes (D57): one or two failures are
    /// a loaded box or a cache cleaned while the backend slept, and the next request deserves another
    /// go; a third in a row is a setup that is broken, and a session that keeps retrying it would
    /// answer every request with a launch that fails rather than with the sentence naming
    /// <c>doctor</c>.
    /// </remarks>
    internal const int MaxConsecutiveWakeFailures = 3;

    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly RecyclePolicy _policy;
    private readonly IRecycleTarget _target;
    private readonly Func<int, long?> _sample;
    private readonly IBackendCompactor _compactor;
    private readonly ILogger _logger;

    private ITimer? _evaluation;
    private ITimer? _baselineTimer;
    private Task _work = Task.CompletedTask;
    private BackendPhase _phase = BackendPhase.Starting;
    private DateTimeOffset _lastActivity;
    private long _activitySerial;
    private int _generation;
    private int? _processId;
    private bool _compactable = true;
    private long? _baseline;
    private long? _latest;
    private bool _compacted;
    private bool _busy;
    private bool _wakePending;
    private int _recycles;
    private DateTimeOffset? _lastRecycleAt;
    private DateTimeOffset? _dormantSince;
    private bool _disposed;

    /// <summary>Creates the policy for one owner. Nothing is evaluated until <see cref="Start"/>.</summary>
    /// <param name="time">The clock every window is measured on.</param>
    /// <param name="policy">The numbers.</param>
    /// <param name="target">The owner, which stops and relaunches.</param>
    /// <param name="sample">Measures a process by the policy's metric (D100); injected so tests need no real process.</param>
    /// <param name="compactor">Asks the child to give memory back before a recycle is considered (D101).</param>
    /// <param name="logger">The stderr log.</param>
    internal BackendRecycler(
        TimeProvider time,
        RecyclePolicy policy,
        IRecycleTarget target,
        Func<int, long?> sample,
        IBackendCompactor compactor,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(compactor);
        ArgumentNullException.ThrowIfNull(logger);

        _time = time;
        _policy = policy;
        _target = target;
        _sample = sample;
        _compactor = compactor;
        _logger = logger;
        _lastActivity = time.GetUtcNow();
    }

    /// <summary>
    /// Raised, outside the lock, whenever the backend's phase, process, baseline or recycle count
    /// changes — what the session file and the attached clients are told (D102, D105).
    /// </summary>
    internal event Action<BackendStatus>? StatusChanged;

    /// <summary>The policy in force.</summary>
    internal RecyclePolicy Policy => _policy;

    /// <summary>A snapshot of everything reported about the backend.</summary>
    internal BackendStatus Status
    {
        get
        {
            lock (_lock)
            {
                return Snapshot();
            }
        }
    }

    /// <summary>Whether activity arrived while a recycle was under way, so the backend is wanted back.</summary>
    internal bool WakePending
    {
        get
        {
            lock (_lock)
            {
                return _wakePending;
            }
        }
    }

    /// <summary>The compaction or recycle running now, if any. Tests wait on it.</summary>
    internal Task Work
    {
        get
        {
            lock (_lock)
            {
                return _work;
            }
        }
    }

    /// <summary>
    /// Decides which process the policy measures, and whether it may be compacted, from what the
    /// server says holds the workspace (C45) and what this process actually launched.
    /// </summary>
    /// <remarks>
    /// Only the launched child is ever compacted, and only when the two agree (D101): with
    /// <c>CLAUDE_ROSLYN_LSP_ROSLYN_PATH</c> pointing at a wrapper or at the <c>fake-roslyn</c> verb
    /// (D34) the announced id names a process this adapter never started — 4242, for the script —
    /// and a blocking collection forced in whatever .NET process has it would be a side effect on
    /// somebody else's program. When they differ the launched child is what is measured, because it
    /// is at least ours. With nothing launched (an in-process fake) there is nothing to compact, and
    /// the announced id is measured as before.
    /// </remarks>
    /// <param name="announced">C45's <c>_roslyn_processId</c>, when the server sent one.</param>
    /// <param name="launched">The process this adapter started, when it started one.</param>
    internal static (int? ProcessId, bool Compactable, bool Mismatch) ChooseProcess(int? announced, int? launched) =>
        (announced, launched) switch
        {
            (_, null) => (announced, false, false),
            (null, { } child) => (child, true, false),
            ({ } server, { } child) when server == child => (child, true, false),
            (_, { } child) => (child, false, true),
        };

    /// <summary>Starts the evaluation clock, once.</summary>
    internal void Start()
    {
        lock (_lock)
        {
            if (_disposed || _evaluation is not null || !_policy.Enabled)
            {
                return;
            }

            _evaluation = _time.CreateTimer(
                static state => ((BackendRecycler) state!).OnTick(),
                this,
                EvaluationInterval,
                EvaluationInterval);
        }
    }

    /// <summary>
    /// Records that a client asked for something (D95), and relaunches a dormant backend.
    /// </summary>
    /// <remarks>
    /// Cheap on the hot path — one lock and a timestamp — because it runs for every message a client
    /// sends. A dormant backend is claimed for the wake under the lock, so two requests arriving
    /// together relaunch it once.
    /// </remarks>
    internal void RecordActivity()
    {
        var wake = false;
        TimeSpan dormantFor = default;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var now = _time.GetUtcNow();

            _lastActivity = now;
            _activitySerial++;
            _compacted = false;

            switch (_phase)
            {
                case BackendPhase.Recycling:
                    _wakePending = true;
                    break;

                case BackendPhase.Dormant:
                    _phase = BackendPhase.Starting;
                    dormantFor = _dormantSince is { } since ? now - since : TimeSpan.Zero;
                    wake = true;
                    break;

                default:
                    break;
            }
        }

        if (wake)
        {
            Log.Waking(_logger, dormantFor.TotalMinutes);
            _target.Wake();
        }
    }

    /// <summary>A backend is being connected: the first one, a relaunch after a crash, or a wake.</summary>
    internal void OnLaunching()
    {
        lock (_lock)
        {
            _generation++;
            _phase = BackendPhase.Starting;
            _processId = null;
            _baseline = null;
            _latest = null;
            _baselineTimer?.Dispose();
            _baselineTimer = null;
        }

        RaiseStatusChanged();
    }

    /// <summary>The backend answered <c>initialize</c>, naming the process that holds the workspace.</summary>
    /// <param name="processId">
    /// Roslyn's own process id (C45), or null when this process does not own it — an attached
    /// engine's backend is another process's child, and sampling it here would be measuring somebody
    /// else's decision.
    /// </param>
    /// <param name="compactable">
    /// Whether <paramref name="processId"/> is known to be the child this process launched, which is the
    /// only process the compactor may force a collection in (D101). Measuring a process that turned out
    /// to be somebody else's costs a wrong number; compacting it costs somebody else a blocking gen2
    /// collection.
    /// </param>
    internal void OnLaunched(int? processId, bool compactable = true)
    {
        lock (_lock)
        {
            _processId = processId;
            _compactable = compactable;
        }

        RaiseStatusChanged();
    }

    /// <summary>The gate opened: the backend is answering, and — when it really loaded — the baseline is due.</summary>
    /// <param name="loaded">
    /// False when the gate opened because the load budget ran out. The backend serves from then on,
    /// but a baseline taken in the next minute would be a sample of a workspace still loading — a low
    /// threshold that recycles a slow solution every idle period — so it waits for
    /// <see cref="OnWorkspaceLoaded"/> instead (D100).
    /// </param>
    internal void OnWorkspaceReady(bool loaded = true)
    {
        lock (_lock)
        {
            if (_disposed || _phase is BackendPhase.Recycling or BackendPhase.Dormant)
            {
                return;
            }

            _phase = BackendPhase.Running;

            if (loaded)
            {
                ArmBaseline();
            }
        }

        RaiseStatusChanged();
    }

    /// <summary>
    /// The workspace finished loading after the gate had already opened on its budget: the baseline
    /// that was held back is due now.
    /// </summary>
    internal void OnWorkspaceLoaded()
    {
        lock (_lock)
        {
            if (_disposed || _phase != BackendPhase.Running || _baseline is not null)
            {
                return;
            }

            ArmBaseline();
        }
    }

    /// <summary>
    /// A wake's relaunch failed: the backend is dormant again, and the next request tries once more.
    /// </summary>
    internal void OnWakeFailed()
    {
        lock (_lock)
        {
            if (_disposed || _phase != BackendPhase.Starting)
            {
                return;
            }

            _phase = BackendPhase.Dormant;
            _processId = null;
        }

        RaiseStatusChanged();
    }

    /// <summary>Arms this generation's baseline a minute from now. Called under the lock.</summary>
    private void ArmBaseline()
    {
        var generation = _generation;

        _baselineTimer?.Dispose();
        _baselineTimer = _time.CreateTimer(
            _ => TakeBaseline(generation),
            null,
            BaselineDelay,
            Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _evaluation?.Dispose();
            _baselineTimer?.Dispose();
            _evaluation = null;
            _baselineTimer = null;
        }
    }

    /// <summary>Samples one generation's baseline, unless that generation has gone already.</summary>
    private void TakeBaseline(int generation)
    {
        int? processId;

        lock (_lock)
        {
            if (_disposed || generation != _generation || _baseline is not null)
            {
                return;
            }

            processId = _processId;
        }

        if (processId is not { } id || _sample(id) is not { } bytes)
        {
            return;
        }

        lock (_lock)
        {
            if (generation != _generation)
            {
                return;
            }

            _baseline = bytes;
            _latest = bytes;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var baselineMegabytes = BackendMemory.Megabytes(bytes) ?? 0;
            var thresholdMegabytes = BackendMemory.Megabytes(RecyclePolicy.ThresholdFor(bytes, _policy.GrowthFactor)) ?? 0;

            Log.Baseline(_logger, id, baselineMegabytes, thresholdMegabytes);
        }

        RaiseStatusChanged();
    }

    /// <summary>Looks at the backend once, and starts a compaction or a recycle if one is due.</summary>
    /// <remarks>
    /// The decision is synchronous, inside the timer callback, so it is made at the moment the clock
    /// says rather than whenever a thread pool thread gets round to it; only the slow parts — the
    /// compaction's settle and the stop — run on their own task. The owner's
    /// <see cref="IRecycleTarget.CanRecycle"/> is asked outside this object's lock, because it takes
    /// the owner's own locks and the owner calls in here while holding them.
    /// </remarks>
    private void OnTick()
    {
        if (!_target.CanRecycle)
        {
            return;
        }

        int? processId;
        long? baseline;
        bool compacted;
        bool compactable;
        long serial;
        TimeSpan idle;

        lock (_lock)
        {
            if (_disposed || _busy || _phase != BackendPhase.Running)
            {
                return;
            }

            processId = _processId;
            baseline = _baseline;
            compacted = _compacted;
            compactable = _compactable;
            serial = _activitySerial;
            idle = _time.GetUtcNow() - _lastActivity;
        }

        // D106: the idle shutdown needs no memory figure at all, and when both windows have passed it
        // is the one that says why.
        if (_policy.IdleShutdown > TimeSpan.Zero && idle >= _policy.IdleShutdown)
        {
            var latest = processId is { } shutdownId ? _sample(shutdownId) : null;
            Begin(serial, compact: false, () => RecycleAsync(
                new RecycleDecision(RecycleReason.IdleShutdown, idle, latest, baseline, null),
                serial));

            return;
        }

        if (_policy.IdleRecycle <= TimeSpan.Zero || processId is not { } id || baseline is not { } baselineBytes)
        {
            return;
        }

        if (!compacted && compactable && idle >= _policy.CompactAfter)
        {
            Begin(serial, compact: true, () => CompactAsync(id));
            return;
        }

        if (idle < _policy.IdleRecycle || _sample(id) is not { } current)
        {
            return;
        }

        lock (_lock)
        {
            if (_processId == id)
            {
                _latest = current;
            }
        }

        var threshold = RecyclePolicy.ThresholdFor(baselineBytes, _policy.GrowthFactor);

        if (current <= threshold)
        {
            return;
        }

        Begin(serial, compact: false, () => RecycleAsync(
            new RecycleDecision(RecycleReason.Growth, idle, current, baselineBytes, threshold),
            serial));
    }

    /// <summary>Claims the one slot for slow work, unless activity has moved the serial on.</summary>
    private void Begin(long serial, bool compact, Func<Task> work)
    {
        lock (_lock)
        {
            if (_disposed || _busy || _phase != BackendPhase.Running || _activitySerial != serial)
            {
                return;
            }

            _busy = true;

            if (compact)
            {
                _compacted = true;
            }

            _work = Task.Run(() => RunAsync(work), CancellationToken.None);
        }
    }

    /// <summary>Runs one piece of slow work and releases the slot, whatever happens to it.</summary>
    private async Task RunAsync(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Never fatal: the policy is an optimisation over a session that already works, and the
            // worst a failure here can mean is a child that keeps its memory until the next look.
            Log.WorkFailed(_logger, exception);
        }
        finally
        {
            lock (_lock)
            {
                _busy = false;
            }
        }
    }

    /// <summary>Asks the child to give memory back, then measures what it gave (D101).</summary>
    private async Task CompactAsync(int processId)
    {
        var before = _sample(processId);
        var requested = await _compactor.TryCompactAsync(processId, CancellationToken.None).ConfigureAwait(false);

        if (!requested)
        {
            return;
        }

        await Task.Delay(CompactionSettle, _time).ConfigureAwait(false);

        var after = _sample(processId);

        lock (_lock)
        {
            if (after is not null && _processId == processId)
            {
                _latest = after;
            }
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            var beforeMegabytes = BackendMemory.Megabytes(before) ?? -1;
            var afterMegabytes = BackendMemory.Megabytes(after) ?? -1;

            Log.Compacted(_logger, processId, beforeMegabytes, afterMegabytes);
        }
    }

    /// <summary>Stops the backend for one decision, and leaves it dormant when the owner did.</summary>
    private async Task RecycleAsync(RecycleDecision decision, long serial)
    {
        lock (_lock)
        {
            if (_disposed || _phase != BackendPhase.Running || _activitySerial != serial)
            {
                return;
            }

            _phase = BackendPhase.Recycling;
            _wakePending = false;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            var idleMinutes = decision.Idle.TotalMinutes;
            var memoryMegabytes = BackendMemory.Megabytes(decision.MemoryBytes) ?? -1;
            var baselineMegabytes = BackendMemory.Megabytes(decision.BaselineBytes) ?? -1;
            if (decision.ThresholdBytes is { } threshold)
            {
                var thresholdMegabytes = BackendMemory.Megabytes(threshold) ?? -1;

                Log.Recycling(_logger, idleMinutes, memoryMegabytes, baselineMegabytes, thresholdMegabytes);
            }
            else
            {
                // D106: an idle shutdown has no threshold to report, only how long nobody asked.
                Log.ShuttingDownIdle(_logger, idleMinutes, memoryMegabytes, baselineMegabytes);
            }
        }

        bool stopped;

        try
        {
            stopped = await _target.RecycleAsync(decision).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Not assumed to be "nothing changed". A target that threw after closing its gate has a
            // dormant gate and no backend, and a policy that went back to Running would never wake it:
            // every later request would be held for ever. So it is asked.
            Log.WorkFailed(_logger, exception);
            stopped = _target.IsDormant;
        }

        var wake = false;

        lock (_lock)
        {
            if (!stopped)
            {
                if (_phase == BackendPhase.Recycling)
                {
                    _phase = BackendPhase.Running;
                }

                _wakePending = false;
            }
            else
            {
                var now = _time.GetUtcNow();

                _phase = BackendPhase.Dormant;
                _recycles++;
                _lastRecycleAt = now;
                _dormantSince = now;
                _processId = null;

                if (_wakePending)
                {
                    _wakePending = false;
                    _phase = BackendPhase.Starting;
                    wake = true;
                }
            }
        }

        if (!stopped)
        {
            Log.Abandoned(_logger);
            return;
        }

        RaiseStatusChanged();

        if (wake)
        {
            Log.Waking(_logger, 0);
            _target.Wake();
        }
    }

    /// <summary>Builds the status record. Called under the lock.</summary>
    private BackendStatus Snapshot() =>
        new(_phase, _processId, _lastActivity, _baseline, _latest, _recycles, _lastRecycleAt);

    /// <summary>Tells the subscribers, outside the lock.</summary>
    private void RaiseStatusChanged()
    {
        BackendStatus status;

        lock (_lock)
        {
            status = Snapshot();
        }

        StatusChanged?.Invoke(status);
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1800,
            Level = LogLevel.Information,
            Message = "Recycling the idle Roslyn backend: idle for {IdleMinutes:0} min, holding " +
                      "{MemoryMegabytes} MB against a baseline of {BaselineMegabytes} MB and a threshold of " +
                      "{ThresholdMegabytes} MB. The next request relaunches it (D96).")]
        internal static partial void Recycling(
            ILogger logger,
            double idleMinutes,
            int memoryMegabytes,
            int baselineMegabytes,
            int thresholdMegabytes);

        [LoggerMessage(
            EventId = 1806,
            Level = LogLevel.Information,
            Message = "Shutting the idle Roslyn backend down: nothing has asked it anything for {IdleMinutes:0} " +
                      "min (CLAUDE_ROSLYN_LSP_IDLE_SHUTDOWN_MINUTES); it holds {MemoryMegabytes} MB against a " +
                      "baseline of {BaselineMegabytes} MB (-1 when not measured). The next request relaunches it (D106).")]
        internal static partial void ShuttingDownIdle(
            ILogger logger,
            double idleMinutes,
            int memoryMegabytes,
            int baselineMegabytes);

        [LoggerMessage(
            EventId = 1801,
            Level = LogLevel.Information,
            Message = "Waking the Roslyn backend after {DormantMinutes:0} min dormant: a request arrived, and it " +
                      "is held until the workspace has loaded again.")]
        internal static partial void Waking(ILogger logger, double dormantMinutes);

        [LoggerMessage(
            EventId = 1802,
            Level = LogLevel.Information,
            Message = "Asked Roslyn process {ProcessId} to compact: {BeforeMegabytes} MB before, " +
                      "{AfterMegabytes} MB after (D101).")]
        internal static partial void Compacted(ILogger logger, int processId, int beforeMegabytes, int afterMegabytes);

        [LoggerMessage(
            EventId = 1803,
            Level = LogLevel.Debug,
            Message = "Roslyn process {ProcessId} settled at {BaselineMegabytes} MB after loading; it is " +
                      "recycled when idle above {ThresholdMegabytes} MB.")]
        internal static partial void Baseline(ILogger logger, int processId, int baselineMegabytes, int thresholdMegabytes);

        [LoggerMessage(
            EventId = 1804,
            Level = LogLevel.Debug,
            Message = "An idle recycle was abandoned before Roslyn was stopped: a request arrived, or one " +
                      "was still in flight.")]
        internal static partial void Abandoned(ILogger logger);

        [LoggerMessage(
            EventId = 1805,
            Level = LogLevel.Warning,
            Message = "The idle policy failed; the backend keeps its memory until the next look.")]
        internal static partial void WorkFailed(ILogger logger, Exception exception);
    }
}
