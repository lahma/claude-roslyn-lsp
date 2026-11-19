using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Tests.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

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

    private static CancellationToken Cancellation => TestContext.CurrentContext.CancellationToken;

    /// <summary>
    /// The baseline is the settled size a minute after the load, not the size at the instant the gate
    /// opened, when the first pulls and the analyzers' first pass are still landing.
    /// </summary>
    [Test]
    public void TheBaselineIsTakenAMinuteAfterTheWorkspaceLoads()
    {
        var harness = new Harness();
        harness.Load(400);

        harness.Time.Advance(BackendRecycler.BaselineDelay - TimeSpan.FromSeconds(1));
        Assert.That(harness.Recycler.Status.BaselineBytes, Is.Null);

        harness.Memory = 650 * Megabyte;
        harness.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.That(harness.Recycler.Status.BaselineBytes, Is.EqualTo(650 * Megabyte));
    }

    /// <summary>Both arms of <c>max(baseline × factor, baseline + 512 MB)</c>.</summary>
    [TestCase(300, 2.0, 812)]
    [TestCase(400, 2.0, 912)]
    [TestCase(512, 2.0, 1024)]
    [TestCase(1000, 2.0, 2000)]
    [TestCase(1000, 3.5, 3500)]
    public void TheThresholdIsTheLargerOfTheFactorAndTheFloor(int baseline, double factor, int threshold)
    {
        Assert.That(RecyclePolicy.ThresholdFor(baseline * Megabyte, factor), Is.EqualTo(threshold * Megabyte));
    }

    /// <summary>The whole rule, end to end: grown, idle for the window, and recycled once.</summary>
    [Test]
    public async Task AGrownBackendIsRecycledWhenTheIdleWindowHasPassed()
    {
        var harness = new Harness();
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);

        harness.Memory = 1500 * Megabyte;

        await harness.AdvanceMinutesAsync(18);
        Assert.That(harness.Target.Decisions, Is.Empty);

        await harness.AdvanceMinutesAsync(2);

        Assert.That(harness.Target.Decisions, Has.Exactly(1).Items);
        var decision = harness.Target.Decisions.Single();

        Assert.That(decision.Reason, Is.EqualTo(RecycleReason.Growth));
        Assert.That(decision.MemoryBytes, Is.EqualTo(1500 * Megabyte));
        Assert.That(decision.BaselineBytes, Is.EqualTo(400 * Megabyte));
        Assert.That(decision.ThresholdBytes, Is.EqualTo(912 * Megabyte));
        Assert.That(decision.Idle >= TimeSpan.FromMinutes(20), Is.True);

        var status = harness.Recycler.Status;

        Assert.That(status.Phase, Is.EqualTo(BackendPhase.Dormant));
        Assert.That(status.StateName, Is.EqualTo("dormant"));
        Assert.That(status.Recycles, Is.EqualTo(1));
        Assert.That(status.LastRecycleAt, Is.Not.Null);
        Assert.That(status.ProcessId, Is.Null);

        // Dormant is dormant: nothing else is decided until somebody asks for the backend.
        await harness.AdvanceMinutesAsync(30);
        Assert.That(harness.Target.Decisions, Has.Exactly(1).Items);
    }

    /// <summary>A request inside the window starts the window again.</summary>
    [Test]
    public async Task ActivityInsideTheWindowPostponesTheRecycle()
    {
        var harness = new Harness();
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 1500 * Megabyte;

        await harness.AdvanceMinutesAsync(15);
        harness.Recycler.RecordActivity();

        await harness.AdvanceMinutesAsync(19);
        Assert.That(harness.Target.Decisions, Is.Empty);

        await harness.AdvanceMinutesAsync(2);
        Assert.That(harness.Target.Decisions, Has.Exactly(1).Items);
    }

    /// <summary>Idle is not enough: a backend near its load size is cheaper kept than reloaded.</summary>
    [Test]
    public async Task ABackendUnderItsThresholdIsKeptHoweverLongItIsIdle()
    {
        var harness = new Harness();
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 900 * Megabyte;

        await harness.AdvanceMinutesAsync(120);

        Assert.That(harness.Target.Decisions, Is.Empty);
        Assert.That(harness.Recycler.Status.LatestBytes, Is.EqualTo(900 * Megabyte));
    }

    /// <summary>D106: the idle shutdown needs no growth at all.</summary>
    [Test]
    public async Task TheIdleShutdownRecyclesWhateverTheSize()
    {
        var harness = new Harness(new RecyclePolicy(TimeSpan.Zero, 2.0, TimeSpan.FromMinutes(45)));
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);

        await harness.AdvanceMinutesAsync(43);
        Assert.That(harness.Target.Decisions, Is.Empty);

        await harness.AdvanceMinutesAsync(2);

        Assert.That(harness.Target.Decisions, Has.Exactly(1).Items);
        Assert.That(harness.Target.Decisions.Single().Reason, Is.EqualTo(RecycleReason.IdleShutdown));

        // No compaction step either: that belongs to the growth recycle, which is off here.
        Assert.That(harness.Compactor.Calls, Is.EqualTo(0));
    }

    /// <summary>
    /// Never during a load, a reload, a held request or an in-flight one: the owner says no, and the
    /// policy does nothing, however grown and however idle.
    /// </summary>
    [Test]
    public async Task NothingIsRecycledWhileTheOwnerSaysItCannotBe()
    {
        var harness = new Harness();
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 3000 * Megabyte;
        harness.Target.CanRecycle = false;

        await harness.AdvanceMinutesAsync(60);
        Assert.That(harness.Target.Decisions, Is.Empty);

        // And as soon as it can, it is — there was no activity, so the window has long passed. The
        // idle period's compaction comes first, one look earlier (D101).
        harness.Target.CanRecycle = true;
        await harness.AdvanceMinutesAsync(2);

        Assert.That(harness.Target.Decisions, Has.Exactly(1).Items);
    }

    /// <summary>A backend that is still loading is never measured against, let alone recycled.</summary>
    [Test]
    public async Task ABackendThatHasNotFinishedLoadingIsNeverRecycled()
    {
        var harness = new Harness();
        harness.Recycler.OnLaunching();
        harness.Recycler.OnLaunched(ProcessId);
        harness.Memory = 3000 * Megabyte;

        await harness.AdvanceMinutesAsync(90);

        Assert.That(harness.Target.Decisions, Is.Empty);
        Assert.That(harness.Recycler.Status.BaselineBytes, Is.Null);
        Assert.That(harness.Recycler.Status.Phase, Is.EqualTo(BackendPhase.Starting));
    }

    /// <summary>
    /// D101: once per idle period, the compactor is asked first, and what it gave back is what the
    /// recycle decision sees.
    /// </summary>
    [Test]
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
        Assert.That(harness.Compactor.Calls, Is.EqualTo(0));

        harness.Time.Advance(TimeSpan.FromMinutes(1));
        await harness.SettleCompactionAsync();

        Assert.That(harness.Compactor.Calls, Is.EqualTo(1));
        Assert.That(harness.Recycler.Status.LatestBytes, Is.EqualTo(700 * Megabyte));

        // Grown no longer, so the window passes without a recycle — and without a second compaction.
        await harness.AdvanceMinutesAsync(30);

        Assert.That(harness.Target.Decisions, Is.Empty);
        Assert.That(harness.Compactor.Calls, Is.EqualTo(1));

        // A new idle period gets a new compaction, five minutes into it.
        harness.Recycler.RecordActivity();
        harness.Memory = 1500 * Megabyte;

        await harness.AdvanceMinutesAsync(5);
        Assert.That(harness.Compactor.Calls, Is.EqualTo(1));

        harness.Time.Advance(TimeSpan.FromMinutes(1));
        await harness.SettleCompactionAsync();

        Assert.That(harness.Compactor.Calls, Is.EqualTo(2));
    }

    /// <summary>With the default compactor the policy is exactly the plain recycle.</summary>
    [Test]
    public async Task TheDefaultCompactorChangesNothing()
    {
        var harness = new Harness(compactor: NoBackendCompactor.Instance);
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 1500 * Megabyte;

        await harness.AdvanceMinutesAsync(20);

        Assert.That(harness.Target.Decisions, Has.Exactly(1).Items);
    }

    /// <summary>Both windows at zero is "off", including the compaction step.</summary>
    [Test]
    public async Task ZeroWindowsDisableThePolicy()
    {
        var harness = new Harness(RecyclePolicy.Off);
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 9000 * Megabyte;

        await harness.AdvanceMinutesAsync(300);

        Assert.That(harness.Target.Decisions, Is.Empty);
        Assert.That(harness.Compactor.Calls, Is.EqualTo(0));
        Assert.That(RecyclePolicy.Off.Enabled, Is.False);
    }

    /// <summary>
    /// A request that arrives while the backend is being stopped is remembered, and relaunches it the
    /// moment it is gone; one that arrives later relaunches it at once. Either way, once.
    /// </summary>
    [Test]
    public async Task ActivityDuringOrAfterARecycleWakesTheBackendOnce()
    {
        var harness = new Harness();
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 1500 * Megabyte;

        harness.Target.DuringRecycle = () => harness.Recycler.RecordActivity();

        await harness.AdvanceMinutesAsync(20);

        Assert.That(harness.Target.Decisions, Has.Exactly(1).Items);
        Assert.That(harness.Target.Wakes, Is.EqualTo(1));
        Assert.That(harness.Recycler.Status.Phase, Is.EqualTo(BackendPhase.Starting));

        // A second recycle, this time with the request after it: one wake per request burst.
        harness.Target.DuringRecycle = null;
        harness.Load(400, alreadyLaunching: true);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 1500 * Megabyte;

        await harness.AdvanceMinutesAsync(20);
        Assert.That(harness.Target.Decisions.Count, Is.EqualTo(2));
        Assert.That(harness.Target.Wakes, Is.EqualTo(1));

        harness.Recycler.RecordActivity();
        harness.Recycler.RecordActivity();

        Assert.That(harness.Target.Wakes, Is.EqualTo(2));
        Assert.That(harness.Recycler.Status.Recycles, Is.EqualTo(2));
    }

    /// <summary>An owner that abandons the stop leaves the backend running, and nothing is counted.</summary>
    [Test]
    public async Task AnAbandonedRecycleCountsForNothing()
    {
        var harness = new Harness();
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 1500 * Megabyte;
        harness.Target.Stops = false;

        await harness.AdvanceMinutesAsync(19);

        Assert.That(harness.Target.Decisions, Has.Exactly(1).Items);

        var status = harness.Recycler.Status;

        Assert.That(status.Phase, Is.EqualTo(BackendPhase.Running));
        Assert.That(status.Recycles, Is.EqualTo(0));
        Assert.That(status.ProcessId, Is.EqualTo(ProcessId));
    }

    /// <summary>
    /// A target that throws once its gate is dormant is believed about its state, not assumed to be
    /// running: a policy that went back to running over a dormant gate would never wake it
    /// (finding 2).
    /// </summary>
    [Test]
    public async Task ATargetThatThrowsOnceItIsDormantLeavesThePolicyDormant()
    {
        var harness = new Harness();
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 1500 * Megabyte;
        harness.Target.Throws = true;
        harness.Target.IsDormant = true;

        await harness.AdvanceMinutesAsync(20);

        Assert.That(harness.Target.Decisions, Has.Exactly(1).Items);
        Assert.That(harness.Recycler.Status.Phase, Is.EqualTo(BackendPhase.Dormant));
        Assert.That(harness.Recycler.Status.Recycles, Is.EqualTo(1));

        harness.Recycler.RecordActivity();
        Assert.That(harness.Target.Wakes, Is.EqualTo(1));
    }

    /// <summary>And one that throws before it stopped anything is still running.</summary>
    [Test]
    public async Task ATargetThatThrowsBeforeStoppingAnythingIsStillRunning()
    {
        var harness = new Harness();
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 1500 * Megabyte;
        harness.Target.Throws = true;

        await harness.AdvanceMinutesAsync(20);

        Assert.That(harness.Target.Decisions, Is.Not.Empty);
        Assert.That(harness.Recycler.Status.Phase, Is.EqualTo(BackendPhase.Running));
        Assert.That(harness.Recycler.Status.Recycles, Is.EqualTo(0));
    }

    /// <summary>
    /// A wake whose relaunch failed is dormant again, so the next activity wakes it once more — and a
    /// failure reported for a backend that is not waking changes nothing (finding 5).
    /// </summary>
    [Test]
    public async Task AFailedWakeIsDormantAgainAndTheNextActivityWakesItOnceMore()
    {
        var harness = new Harness();
        harness.Load(400);
        harness.Time.Advance(BackendRecycler.BaselineDelay);
        harness.Memory = 1500 * Megabyte;

        await harness.AdvanceMinutesAsync(20);
        Assert.That(harness.Recycler.Status.Phase, Is.EqualTo(BackendPhase.Dormant));

        harness.Recycler.RecordActivity();
        Assert.That(harness.Target.Wakes, Is.EqualTo(1));
        Assert.That(harness.Recycler.Status.Phase, Is.EqualTo(BackendPhase.Starting));

        harness.Recycler.OnWakeFailed();

        Assert.That(harness.Recycler.Status.Phase, Is.EqualTo(BackendPhase.Dormant));
        Assert.That(harness.Recycler.Status.ProcessId, Is.Null);

        harness.Recycler.RecordActivity();
        Assert.That(harness.Target.Wakes, Is.EqualTo(2));

        harness.Load(400, alreadyLaunching: true);
        harness.Recycler.OnWakeFailed();

        Assert.That(harness.Recycler.Status.Phase, Is.EqualTo(BackendPhase.Running));
    }

    /// <summary>
    /// A gate that opened on its budget is served from, but not measured: the baseline waits for the
    /// load to finish, and is taken once (finding 6, D100).
    /// </summary>
    [Test]
    public void NoBaselineIsTakenFromAWorkspaceThatHasNotLoaded()
    {
        var harness = new Harness();
        harness.Recycler.OnLaunching();
        harness.Recycler.OnLaunched(ProcessId);
        harness.Memory = 150 * Megabyte;
        harness.Recycler.OnWorkspaceReady(loaded: false);

        Assert.That(harness.Recycler.Status.Phase, Is.EqualTo(BackendPhase.Running));

        harness.Time.Advance(BackendRecycler.BaselineDelay * 3);
        Assert.That(harness.Recycler.Status.BaselineBytes, Is.Null);

        harness.Memory = 600 * Megabyte;
        harness.Recycler.OnWorkspaceLoaded();
        harness.Time.Advance(BackendRecycler.BaselineDelay);

        Assert.That(harness.Recycler.Status.BaselineBytes, Is.EqualTo(600 * Megabyte));

        harness.Memory = 900 * Megabyte;
        harness.Recycler.OnWorkspaceLoaded();
        harness.Time.Advance(BackendRecycler.BaselineDelay);

        Assert.That(harness.Recycler.Status.BaselineBytes, Is.EqualTo(600 * Megabyte));
    }

    /// <summary>
    /// Which process is measured and whether it may be compacted, from what the server announces
    /// (C45) and what this process launched (finding 7, D101).
    /// </summary>
    [TestCase(4242, null, 4242, false, false)]
    [TestCase(null, 999, 999, true, false)]
    [TestCase(4242, 4242, 4242, true, false)]
    [TestCase(4242, 999, 999, false, true)]
    [TestCase(null, null, null, false, false)]
    public void OnlyTheLaunchedChildIsEverCompacted(
        int? announced,
        int? launched,
        int? measured,
        bool compactable,
        bool mismatch)
    {
        Assert.That(BackendRecycler.ChooseProcess(announced, launched), Is.EqualTo((measured, compactable, mismatch)));
    }

    /// <summary>A process that may not be compacted is still measured, and still recycled when it grows.</summary>
    [Test]
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

        Assert.That(harness.Compactor.Calls, Is.EqualTo(0));
        Assert.That(harness.Target.Decisions, Has.Exactly(1).Items);
    }

    /// <summary>Every policy read from the environment means what the variables say.</summary>
    [Test]
    public void ThePolicyIsReadFromTheOptions()
    {
        var policy = RecyclePolicy.From(ClaudeRoslynLspOptions.FromEnvironment(static _ => null));

        Assert.That(policy.IdleRecycle, Is.EqualTo(TimeSpan.FromMinutes(20)));
        Assert.That(policy.GrowthFactor, Is.EqualTo(2.0));
        Assert.That(policy.IdleShutdown, Is.EqualTo(TimeSpan.Zero));
        Assert.That(policy.CompactAfter, Is.EqualTo(TimeSpan.FromMinutes(5)));
        Assert.That(policy.Enabled, Is.True);

        // Half the window when that is the earlier of the two.
        Assert.That((policy with { IdleRecycle = TimeSpan.FromMinutes(6) }).CompactAfter, Is.EqualTo(TimeSpan.FromMinutes(3)));
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
