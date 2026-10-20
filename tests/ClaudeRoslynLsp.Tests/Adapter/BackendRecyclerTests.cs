using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Tests.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// The idle policy on its own: when a backend is recycled, and every reason it is not (D96, D100,
/// D101, D106).
/// </summary>
/// <remarks>
/// <para>
/// Against a hand-rolled target and a hand-rolled clock, because every property here is about
/// <em>time</em> — a baseline taken a minute after the load, a window of twenty minutes counted from
/// the last request — and a test that waited for any of it in real time would never be run.
/// </para>
/// <para>
/// The clock is moved a minute at a time and the policy's background work is awaited after each
/// step, because a decision is made in the timer callback but carried out on its own task: moving
/// twenty minutes in one jump would fire twenty decisions before the first had finished, which tests
/// the jump rather than the policy.
/// </para>
/// </remarks>
public class BackendRecyclerTests
{
    private const int ProcessId = 4242;
    private const long Megabyte = 1024 * 1024;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// The baseline is the settled size a minute after the load, not the size at the instant the gate
    /// opened, when the first pulls and the analyzers' first pass are still landing.
    /// </summary>
    [Fact]
    public void TheBaselineIsTakenAMinuteAfterTheWorkspaceLoads()
    {
        var harness = new Harness();
        harness.Load(400);

        harness.Time.Advance(BackendRecycler.BaselineDelay - TimeSpan.FromSeconds(1));
        Assert.Null(harness.Recycler.Status.BaselineBytes);

        harness.Memory = 650 * Megabyte;
        harness.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(650 * Megabyte, harness.Recycler.Status.BaselineBytes);
    }

    /// <summary>Both arms of <c>max(baseline × factor, baseline + 512 MB)</c>.</summary>
    [Theory]
    [InlineData(300, 2.0, 812)]
    [InlineData(400, 2.0, 912)]
    [InlineData(512, 2.0, 1024)]
    [InlineData(1000, 2.0, 2000)]
    [InlineData(1000, 3.5, 3500)]
    public void TheThresholdIsTheLargerOfTheFactorAndTheFloor(int baseline, double factor, int threshold)
    {
        Assert.Equal(threshold * Megabyte, RecyclePolicy.ThresholdFor(baseline * Megabyte, factor));
    }

    /// <summary>The whole rule, end to end: grown, idle for the window, and recycled once.</summary>
    [Fact]
    public async Task AGrownBackendIsRecycledWhenTheIdleWindowHasPassed()
    {
        var harness = new Harness();
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);

        harness.Memory = 1500 * Megabyte;

        await harness.AdvanceMinutesAsync(18);
        Assert.Empty(harness.Target.Decisions);

        await harness.AdvanceMinutesAsync(2);

        var decision = Assert.Single(harness.Target.Decisions);

        Assert.Equal(RecycleReason.Growth, decision.Reason);
        Assert.Equal(1500 * Megabyte, decision.MemoryBytes);
        Assert.Equal(400 * Megabyte, decision.BaselineBytes);
        Assert.Equal(912 * Megabyte, decision.ThresholdBytes);
        Assert.True(decision.Idle >= TimeSpan.FromMinutes(20));

        var status = harness.Recycler.Status;

        Assert.Equal(BackendPhase.Dormant, status.Phase);
        Assert.Equal("dormant", status.StateName);
        Assert.Equal(1, status.Recycles);
        Assert.NotNull(status.LastRecycleAt);
        Assert.Null(status.ProcessId);

        // Dormant is dormant: nothing else is decided until somebody asks for the backend.
        await harness.AdvanceMinutesAsync(30);
        Assert.Single(harness.Target.Decisions);
    }

    /// <summary>A request inside the window starts the window again.</summary>
    [Fact]
    public async Task ActivityInsideTheWindowPostponesTheRecycle()
    {
        var harness = new Harness();
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 1500 * Megabyte;

        await harness.AdvanceMinutesAsync(15);
        harness.Recycler.RecordActivity();

        await harness.AdvanceMinutesAsync(19);
        Assert.Empty(harness.Target.Decisions);

        await harness.AdvanceMinutesAsync(2);
        Assert.Single(harness.Target.Decisions);
    }

    /// <summary>Idle is not enough: a backend near its load size is cheaper kept than reloaded.</summary>
    [Fact]
    public async Task ABackendUnderItsThresholdIsKeptHoweverLongItIsIdle()
    {
        var harness = new Harness();
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 900 * Megabyte;

        await harness.AdvanceMinutesAsync(120);

        Assert.Empty(harness.Target.Decisions);
        Assert.Equal(900 * Megabyte, harness.Recycler.Status.LatestBytes);
    }

    /// <summary>D106: the idle shutdown needs no growth at all.</summary>
    [Fact]
    public async Task TheIdleShutdownRecyclesWhateverTheSize()
    {
        var harness = new Harness(new RecyclePolicy(TimeSpan.Zero, 2.0, TimeSpan.FromMinutes(45)));
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);

        await harness.AdvanceMinutesAsync(43);
        Assert.Empty(harness.Target.Decisions);

        await harness.AdvanceMinutesAsync(2);

        Assert.Equal(RecycleReason.IdleShutdown, Assert.Single(harness.Target.Decisions).Reason);

        // No compaction step either: that belongs to the growth recycle, which is off here.
        Assert.Equal(0, harness.Compactor.Calls);
    }

    /// <summary>
    /// Never during a load, a reload, a held request or an in-flight one: the owner says no, and the
    /// policy does nothing, however grown and however idle.
    /// </summary>
    [Fact]
    public async Task NothingIsRecycledWhileTheOwnerSaysItCannotBe()
    {
        var harness = new Harness();
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 3000 * Megabyte;
        harness.Target.CanRecycle = false;

        await harness.AdvanceMinutesAsync(60);
        Assert.Empty(harness.Target.Decisions);

        // And as soon as it can, it is — there was no activity, so the window has long passed. The
        // idle period's compaction comes first, one look earlier (D101).
        harness.Target.CanRecycle = true;
        await harness.AdvanceMinutesAsync(2);

        Assert.Single(harness.Target.Decisions);
    }

    /// <summary>A backend that is still loading is never measured against, let alone recycled.</summary>
    [Fact]
    public async Task ABackendThatHasNotFinishedLoadingIsNeverRecycled()
    {
        var harness = new Harness();
        harness.Recycler.OnLaunching();
        harness.Recycler.OnLaunched(ProcessId);
        harness.Memory = 3000 * Megabyte;

        await harness.AdvanceMinutesAsync(90);

        Assert.Empty(harness.Target.Decisions);
        Assert.Null(harness.Recycler.Status.BaselineBytes);
        Assert.Equal(BackendPhase.Starting, harness.Recycler.Status.Phase);
    }

    /// <summary>
    /// D101: once per idle period, the compactor is asked first, and what it gave back is what the
    /// recycle decision sees.
    /// </summary>
    [Fact]
    public async Task TheCompactorIsAskedOncePerIdlePeriodAndWhatItFreesIsSeen()
    {
        var harness = new Harness();
        harness.Compactor.Requested = true;
        harness.Compactor.Frees = () => harness.Memory = 700 * Megabyte;

        // Loaded at minute 0, so the idle period's fifth minute is the fifth look.
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 1500 * Megabyte;

        await harness.AdvanceMinutesAsync(3);
        Assert.Equal(0, harness.Compactor.Calls);

        harness.Time.Advance(TimeSpan.FromMinutes(1));
        await harness.SettleCompactionAsync();

        Assert.Equal(1, harness.Compactor.Calls);
        Assert.Equal(700 * Megabyte, harness.Recycler.Status.LatestBytes);

        // Grown no longer, so the window passes without a recycle — and without a second compaction.
        await harness.AdvanceMinutesAsync(30);

        Assert.Empty(harness.Target.Decisions);
        Assert.Equal(1, harness.Compactor.Calls);

        // A new idle period gets a new compaction, five minutes into it.
        harness.Recycler.RecordActivity();
        harness.Memory = 1500 * Megabyte;

        await harness.AdvanceMinutesAsync(5);
        Assert.Equal(1, harness.Compactor.Calls);

        harness.Time.Advance(TimeSpan.FromMinutes(1));
        await harness.SettleCompactionAsync();

        Assert.Equal(2, harness.Compactor.Calls);
    }

    /// <summary>With the default compactor the policy is exactly the plain recycle.</summary>
    [Fact]
    public async Task TheDefaultCompactorChangesNothing()
    {
        var harness = new Harness(compactor: NoBackendCompactor.Instance);
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 1500 * Megabyte;

        await harness.AdvanceMinutesAsync(20);

        Assert.Single(harness.Target.Decisions);
    }

    /// <summary>Both windows at zero is "off", including the compaction step.</summary>
    [Fact]
    public async Task ZeroWindowsDisableThePolicy()
    {
        var harness = new Harness(RecyclePolicy.Off);
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 9000 * Megabyte;

        await harness.AdvanceMinutesAsync(300);

        Assert.Empty(harness.Target.Decisions);
        Assert.Equal(0, harness.Compactor.Calls);
        Assert.False(RecyclePolicy.Off.Enabled);
    }

    /// <summary>
    /// A request that arrives while the backend is being stopped is remembered, and relaunches it the
    /// moment it is gone; one that arrives later relaunches it at once. Either way, once.
    /// </summary>
    [Fact]
    public async Task ActivityDuringOrAfterARecycleWakesTheBackendOnce()
    {
        var harness = new Harness();
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 1500 * Megabyte;

        harness.Target.DuringRecycle = () => harness.Recycler.RecordActivity();

        await harness.AdvanceMinutesAsync(20);

        Assert.Single(harness.Target.Decisions);
        Assert.Equal(1, harness.Target.Wakes);
        Assert.Equal(BackendPhase.Starting, harness.Recycler.Status.Phase);

        // A second recycle, this time with the request after it: one wake per request burst.
        harness.Target.DuringRecycle = null;
        harness.Load(400, alreadyLaunching: true);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 1500 * Megabyte;

        await harness.AdvanceMinutesAsync(20);
        Assert.Equal(2, harness.Target.Decisions.Count);
        Assert.Equal(1, harness.Target.Wakes);

        harness.Recycler.RecordActivity();
        harness.Recycler.RecordActivity();

        Assert.Equal(2, harness.Target.Wakes);
        Assert.Equal(2, harness.Recycler.Status.Recycles);
    }

    /// <summary>An owner that abandons the stop leaves the backend running, and nothing is counted.</summary>
    [Fact]
    public async Task AnAbandonedRecycleCountsForNothing()
    {
        var harness = new Harness();
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 1500 * Megabyte;
        harness.Target.Stops = false;

        await harness.AdvanceMinutesAsync(19);

        Assert.Single(harness.Target.Decisions);

        var status = harness.Recycler.Status;

        Assert.Equal(BackendPhase.Running, status.Phase);
        Assert.Equal(0, status.Recycles);
        Assert.Equal(ProcessId, status.ProcessId);
    }

    /// <summary>
    /// A target that throws once its gate is dormant is believed about its state, not assumed to be
    /// running: a policy that went back to running over a dormant gate would never wake it
    /// (finding 2).
    /// </summary>
    [Fact]
    public async Task ATargetThatThrowsOnceItIsDormantLeavesThePolicyDormant()
    {
        var harness = new Harness();
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 1500 * Megabyte;
        harness.Target.Throws = true;
        harness.Target.IsDormant = true;

        await harness.AdvanceMinutesAsync(20);

        Assert.Single(harness.Target.Decisions);
        Assert.Equal(BackendPhase.Dormant, harness.Recycler.Status.Phase);
        Assert.Equal(1, harness.Recycler.Status.Recycles);

        harness.Recycler.RecordActivity();
        Assert.Equal(1, harness.Target.Wakes);
    }

    /// <summary>And one that throws before it stopped anything is still running.</summary>
    [Fact]
    public async Task ATargetThatThrowsBeforeStoppingAnythingIsStillRunning()
    {
        var harness = new Harness();
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 1500 * Megabyte;
        harness.Target.Throws = true;

        await harness.AdvanceMinutesAsync(20);

        Assert.NotEmpty(harness.Target.Decisions);
        Assert.Equal(BackendPhase.Running, harness.Recycler.Status.Phase);
        Assert.Equal(0, harness.Recycler.Status.Recycles);
    }

    /// <summary>
    /// A wake whose relaunch failed is dormant again, so the next activity wakes it once more — and a
    /// failure reported for a backend that is not waking changes nothing (finding 5).
    /// </summary>
    [Fact]
    public async Task AFailedWakeIsDormantAgainAndTheNextActivityWakesItOnceMore()
    {
        var harness = new Harness();
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 1500 * Megabyte;

        await harness.AdvanceMinutesAsync(20);
        Assert.Equal(BackendPhase.Dormant, harness.Recycler.Status.Phase);

        harness.Recycler.RecordActivity();
        Assert.Equal(1, harness.Target.Wakes);
        Assert.Equal(BackendPhase.Starting, harness.Recycler.Status.Phase);

        harness.Recycler.OnWakeFailed();

        Assert.Equal(BackendPhase.Dormant, harness.Recycler.Status.Phase);
        Assert.Null(harness.Recycler.Status.ProcessId);

        harness.Recycler.RecordActivity();
        Assert.Equal(2, harness.Target.Wakes);

        harness.Load(400, alreadyLaunching: true);
        harness.Recycler.OnWakeFailed();

        Assert.Equal(BackendPhase.Running, harness.Recycler.Status.Phase);
    }

    /// <summary>
    /// A gate that opened on its budget is served from, but not measured: the baseline waits for the
    /// load to finish, and is taken once (finding 6, D100).
    /// </summary>
    [Fact]
    public void NoBaselineIsTakenFromAWorkspaceThatHasNotLoaded()
    {
        var harness = new Harness();
        harness.Recycler.OnLaunching();
        harness.Recycler.OnLaunched(ProcessId);
        harness.Memory = 150 * Megabyte;
        harness.Recycler.OnWorkspaceReady(loaded: false);

        Assert.Equal(BackendPhase.Running, harness.Recycler.Status.Phase);

        harness.Time.Advance(BackendRecycler.BaselineDelay * 3);
        Assert.Null(harness.Recycler.Status.BaselineBytes);

        harness.Memory = 600 * Megabyte;
        harness.Recycler.OnWorkspaceLoaded();
        harness.Time.Advance(BackendRecycler.BaselineDelay);

        Assert.Equal(600 * Megabyte, harness.Recycler.Status.BaselineBytes);

        harness.Memory = 900 * Megabyte;
        harness.Recycler.OnWorkspaceLoaded();
        harness.Time.Advance(BackendRecycler.BaselineDelay);

        Assert.Equal(600 * Megabyte, harness.Recycler.Status.BaselineBytes);
    }

    /// <summary>
    /// Which process is measured and whether it may be compacted, from what the server announces
    /// (C45) and what this process launched (finding 7, D101).
    /// </summary>
    [Theory]
    [InlineData(4242, null, 4242, false, false)]
    [InlineData(null, 999, 999, true, false)]
    [InlineData(4242, 4242, 4242, true, false)]
    [InlineData(4242, 999, 999, false, true)]
    [InlineData(null, null, null, false, false)]
    public void OnlyTheLaunchedChildIsEverCompacted(
        int? announced,
        int? launched,
        int? measured,
        bool compactable,
        bool mismatch)
    {
        Assert.Equal((measured, compactable, mismatch), BackendRecycler.ChooseProcess(announced, launched));
    }

    /// <summary>A process that may not be compacted is still measured, and still recycled when it grows.</summary>
    [Fact]
    public async Task AProcessThatMayNotBeCompactedIsMeasuredButNeverCompacted()
    {
        var harness = new Harness();
        harness.Compactor.Requested = true;

        harness.Recycler.OnLaunching();
        harness.Recycler.OnLaunched(ProcessId, compactable: false);
        harness.Memory = 400 * Megabyte;
        harness.Recycler.OnWorkspaceReady();
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 1500 * Megabyte;

        await harness.AdvanceMinutesAsync(20);

        Assert.Equal(0, harness.Compactor.Calls);
        Assert.Single(harness.Target.Decisions);
    }

    /// <summary>Every policy read from the environment means what the variables say.</summary>
    [Fact]
    public void ThePolicyIsReadFromTheOptions()
    {
        var policy = RecyclePolicy.From(ClaudeRoslynLspOptions.FromEnvironment(static _ => null));

        Assert.Equal(TimeSpan.FromMinutes(20), policy.IdleRecycle);
        Assert.Equal(2.0, policy.GrowthFactor);
        Assert.Equal(TimeSpan.Zero, policy.IdleShutdown);
        Assert.Equal(TimeSpan.FromMinutes(5), policy.CompactAfter);
        Assert.True(policy.Enabled);

        // Half the window when that is the earlier of the two.
        Assert.Equal(TimeSpan.FromMinutes(3), (policy with { IdleRecycle = TimeSpan.FromMinutes(6) }).CompactAfter);
    }

    /// <summary>One policy over a scripted owner, a scripted process and the hand-moved clock.</summary>
    private sealed class Harness
    {
        internal Harness(RecyclePolicy? policy = null, IBackendCompactor? compactor = null)
        {
            Compactor = new CountingCompactor();
            Recycler = new BackendRecycler(
                Time,
                policy ?? new RecyclePolicy(TimeSpan.FromMinutes(20), 2.0, TimeSpan.Zero),
                Target,
                pid => pid == ProcessId ? Memory : null,
                compactor ?? Compactor,
                NullLogger.Instance);

            Target.Recycler = Recycler;
            Recycler.Start();
        }

        internal TestTimeProvider Time { get; } = new();

        internal ScriptedTarget Target { get; } = new();

        internal CountingCompactor Compactor { get; }

        internal BackendRecycler Recycler { get; }

        /// <summary>What the scripted process holds right now.</summary>
        internal long? Memory { get; set; }

        /// <summary>Takes the backend through its launch and its load, with a given size.</summary>
        internal void Load(int megabytes, bool alreadyLaunching = false)
        {
            if (!alreadyLaunching)
            {
                Recycler.OnLaunching();
            }

            Recycler.OnLaunched(ProcessId);
            Memory = megabytes * Megabyte;
            Recycler.OnWorkspaceReady();
        }

        /// <summary>Moves the clock a minute at a time, letting each minute's work finish.</summary>
        internal async Task AdvanceMinutesAsync(int minutes)
        {
            for (var minute = 0; minute < minutes; minute++)
            {
                Time.Advance(TimeSpan.FromMinutes(1));
                await Recycler.Work.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
            }
        }

        /// <summary>
        /// Lets a compaction that has just started finish: its settle is on the same clock, so the
        /// test has to wait for the delay to be armed (C62) and then move past it.
        /// </summary>
        internal async Task SettleCompactionAsync()
        {
            await Time.WaitForTimerAsync(BackendRecycler.CompactionSettle, Cancellation);
            Time.Advance(BackendRecycler.CompactionSettle);
            await Recycler.Work.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        }
    }

    /// <summary>An owner that records what it was asked to do.</summary>
    private sealed class ScriptedTarget : IRecycleTarget
    {
        internal BackendRecycler? Recycler { get; set; }

        public bool CanRecycle { get; set; } = true;

        /// <summary>Whether a recycle actually stops the backend, or is abandoned.</summary>
        internal bool Stops { get; set; } = true;

        /// <summary>Runs while a recycle is "stopping", to simulate a request racing it.</summary>
        internal Action? DuringRecycle { get; set; }

        /// <summary>Whether a recycle throws instead of answering.</summary>
        internal bool Throws { get; set; }

        /// <inheritdoc />
        public bool IsDormant { get; set; }

        internal List<RecycleDecision> Decisions { get; } = [];

        internal int Wakes { get; private set; }

        public Task<bool> RecycleAsync(RecycleDecision decision)
        {
            lock (Decisions)
            {
                Decisions.Add(decision);
            }

            DuringRecycle?.Invoke();

            return Throws
                ? Task.FromException<bool>(new InvalidOperationException("The scripted stop failed part way."))
                : Task.FromResult(Stops);
        }

        /// <summary>What an owner's wake does to the policy: a new backend is being launched.</summary>
        public void Wake()
        {
            Wakes++;
            Recycler?.OnLaunching();
        }
    }

    /// <summary>
    /// A compactor that counts, and that only claims to have compacted — which is what makes the
    /// policy wait for a settle — when a test asks it to.
    /// </summary>
    private sealed class CountingCompactor : IBackendCompactor
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        internal bool Requested { get; set; }

        internal Action? Frees { get; set; }

        public Task<bool> TryCompactAsync(int processId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);

            if (Requested)
            {
                Frees?.Invoke();
            }

            return Task.FromResult(Requested);
        }
    }
}
