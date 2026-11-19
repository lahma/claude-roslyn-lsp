using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Protocol;
using ClaudeRoslynLsp.Tests.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// The gate that turns "an empty answer, immediately" into "the right answer, a few seconds later".
/// </summary>
/// <remarks>
/// Every assertion here corresponds to a way the adapter could silently mislead its client. Order,
/// because a client that sent three requests expects them applied in that order. The timeout flip,
/// because a server that stops answering is worse than one answering from a partial workspace. The
/// cancellation code, because a client that gave up on a request must not be answered as though it
/// had not. And <c>-32603</c> for a failed backend, because that is the only outcome a user can act
/// on.
/// </remarks>
public class ReadinessGateTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    [Test]
    public void RequestsAreHeldWhileTheWorkspaceLoadsAndReleasedInArrivalOrder()
    {
        var released = new List<string>();

        using var gate = CreateGate(out _, out _);
        gate.Start();
        gate.MarkRoslynInitialized();

        for (var index = 0; index < 5; index++)
        {
            var method = $"request-{index}";
            Assert.That(gate.TryHold(JsonRpcId.FromNumber(index), method, outcome =>
            {
                Assert.That(outcome, Is.EqualTo(GateOutcome.Pass));
                released.Add(method);
            }), Is.True);
        }

        Assert.That(gate.HeldCount, Is.EqualTo(5));
        Assert.That(released, Is.Empty);

        gate.MarkProjectsLoaded();

        Assert.That(released, Is.EqualTo(["request-0", "request-1", "request-2", "request-3", "request-4"]));
        Assert.That(gate.State, Is.EqualTo(ReadinessState.ProjectsLoaded));
        Assert.That(gate.HeldCount, Is.EqualTo(0));
    }

    [Test]
    public void OnceOpenARequestGoesStraightThroughWithoutBeingQueued()
    {
        using var gate = CreateGate(out _, out _);
        gate.Start();
        gate.MarkProjectsLoaded();

        var outcome = default(GateOutcome?);

        Assert.That(gate.TryHold(JsonRpcId.FromNumber(1), "textDocument/definition", x => outcome = x), Is.False);
        Assert.That(outcome, Is.EqualTo(GateOutcome.Pass));
    }

    /// <summary>
    /// The budget is a floor, not a promise: after it, a partially loaded workspace answering some
    /// questions beats a server that has stopped answering at all.
    /// </summary>
    [Test]
    public void TheLoadBudgetFlipsTheGateOpenAndPassesEverythingHeld()
    {
        using var gate = CreateGate(out var time, out var notices);
        gate.Start();
        gate.MarkRoslynInitialized();

        var released = 0;
        gate.TryHold(JsonRpcId.FromNumber(1), "textDocument/definition", outcome =>
        {
            Assert.That(outcome, Is.EqualTo(GateOutcome.Pass));
            released++;
        });

        time.Advance(Timeout - TimeSpan.FromSeconds(1));
        Assert.That(released, Is.EqualTo(0));
        Assert.That(gate.State, Is.EqualTo(ReadinessState.RoslynInitialized));

        time.Advance(TimeSpan.FromSeconds(2));

        Assert.That(released, Is.EqualTo(1));
        Assert.That(gate.State, Is.EqualTo(ReadinessState.LoadTimedOut));
        Assert.That(notices, Has.Some.Matches<string>(x => x!.Contains("did not finish loading", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A cancellation arriving while a request is held has never reached Roslyn, so it cannot be
    /// mapped through — the gate answers it, with LSP's own <c>RequestCancelled</c>.
    /// </summary>
    [Test]
    public void CancellingAHeldRequestDequeuesItAndLeavesTheRestInOrder()
    {
        var released = new List<string>();

        using var gate = CreateGate(out _, out _);
        gate.Start();
        gate.MarkRoslynInitialized();

        gate.TryHold(JsonRpcId.FromNumber(1), "first", _ => released.Add("first"));
        gate.TryHold(JsonRpcId.FromNumber(2), "second", outcome =>
        {
            Assert.That(outcome, Is.EqualTo(GateOutcome.Cancelled));
            released.Add("second-cancelled");
        });
        gate.TryHold(JsonRpcId.FromNumber(3), "third", _ => released.Add("third"));

        Assert.That(gate.TryCancel(JsonRpcId.FromNumber(2)), Is.True);
        Assert.That(gate.HeldCount, Is.EqualTo(2));
        Assert.That(released, Is.EqualTo(["second-cancelled"]));

        gate.MarkProjectsLoaded();

        Assert.That(released, Is.EqualTo(["second-cancelled", "first", "third"]));
    }

    [Test]
    public void CancellingSomethingThatIsNotHeldReportsSo()
    {
        using var gate = CreateGate(out _, out _);
        gate.Start();

        Assert.That(gate.TryCancel(JsonRpcId.FromNumber(99)), Is.False);
    }

    /// <summary>
    /// A backend that will not start is a state the user has to be able to read. The refusal names
    /// <c>doctor</c>, because that is where the resolution chain is.
    /// </summary>
    [Test]
    public void AFailedBackendRefusesEverythingHeldWithAnActionableMessage()
    {
        using var gate = CreateGate(out _, out _);
        gate.Start();

        var outcome = default(GateOutcome?);
        gate.TryHold(JsonRpcId.FromNumber(1), "textDocument/definition", x => outcome = x);

        gate.MarkFailed("the backend could not be started");

        Assert.That(outcome, Is.EqualTo(GateOutcome.Failed));
        Assert.That(gate.State, Is.EqualTo(ReadinessState.Failed));
        Assert.That(gate.FailureMessage, Contains.Substring("doctor").Using(StringComparison.Ordinal));
        Assert.That(gate.FailureMessage, Contains.Substring("the backend could not be started").Using(StringComparison.Ordinal));

        // And a request arriving afterwards is refused at once rather than queued forever.
        var later = default(GateOutcome?);
        Assert.That(gate.TryHold(JsonRpcId.FromNumber(2), "textDocument/hover", x => later = x), Is.False);
        Assert.That(later, Is.EqualTo(GateOutcome.Failed));
    }

    [Test]
    public void ALoadedWorkspaceCannotBeUnLoadedByALaterFailure()
    {
        using var gate = CreateGate(out _, out _);
        gate.Start();
        gate.MarkProjectsLoaded();
        gate.MarkFailed("the Roslyn backend exited");

        Assert.That(gate.State, Is.EqualTo(ReadinessState.ProjectsLoaded));
    }

    /// <summary>
    /// Notifications flow from the moment Roslyn is initialised, not from the moment the workspace is
    /// loaded: a <c>didOpen</c> withheld until then would leave Roslyn analysing the file on disk.
    /// </summary>
    [Test]
    public void NotificationsAreAllowedFromRoslynInitializedOnwards()
    {
        using var gate = CreateGate(out _, out _);

        Assert.That(gate.NotificationsAllowed, Is.False);

        gate.Start();
        Assert.That(gate.NotificationsAllowed, Is.False);

        gate.MarkRoslynInitialized();
        Assert.That(gate.NotificationsAllowed, Is.True);

        gate.MarkProjectsLoaded();
        Assert.That(gate.NotificationsAllowed, Is.True);
    }

    /// <summary>
    /// Ten seconds of silence on a slow load looks like a hang, so the client is told — once per
    /// interval, only while something is actually waiting, and naming the workspace.
    /// </summary>
    [Test]
    public void AClientHoldingRequestsIsToldEveryTenSeconds()
    {
        using var gate = CreateGate(out var time, out var notices);
        gate.WorkspaceDescription = "Quartz.slnx";
        gate.Start();
        gate.MarkRoslynInitialized();

        // Nothing held: nothing said.
        time.Advance(TimeSpan.FromSeconds(25));
        Assert.That(notices, Is.Empty);

        gate.TryHold(JsonRpcId.FromNumber(1), "textDocument/definition", _ => { });

        time.Advance(ReadinessGate.NoticeInterval);
        Assert.That(notices, Has.Exactly(1).Items);
        var first = notices.Single();

        Assert.That(first, Contains.Substring("holding 1 request(s)").Using(StringComparison.Ordinal));
        Assert.That(first, Contains.Substring("Quartz.slnx").Using(StringComparison.Ordinal));
        // The notice reports the moment the timer was scheduled for, not the end of the jump: the
        // third tick is due at 30 s and that is when the line was written.
        Assert.That(first, Contains.Substring("30s elapsed").Using(StringComparison.Ordinal));

        time.Advance(ReadinessGate.NoticeInterval);
        Assert.That(notices.Count, Is.EqualTo(2));

        // And it stops the moment the workspace is up.
        gate.MarkProjectsLoaded();
        time.Advance(ReadinessGate.NoticeInterval * 3);
        Assert.That(notices.Count, Is.EqualTo(2));
    }

    /// <summary>
    /// D96: a dormant gate holds with no clock at all. A gate built on MarkRestarting would open by
    /// itself after the load budget and pass everything it held to a backend that is not there, and it
    /// would narrate a load nobody has started every ten seconds while it waited.
    /// </summary>
    [Test]
    public void ADormantGateHoldsWithoutATimeoutOrANotice()
    {
        using var gate = CreateGate(out var time, out var notices);
        gate.Start();
        gate.MarkProjectsLoaded();

        var asked = 0;
        gate.HeldWhileDormant += () => asked++;

        Assert.That(gate.MarkDormant(), Is.True);
        Assert.That(gate.State, Is.EqualTo(ReadinessState.Dormant));
        Assert.That(gate.IsOpen, Is.False);
        Assert.That(gate.NotificationsAllowed, Is.False);

        var released = 0;
        Assert.That(gate.TryHold(JsonRpcId.FromNumber(1), "textDocument/definition", _ => released++), Is.True);

        // Holding is what asks for the backend back.
        Assert.That(asked, Is.EqualTo(1));

        time.Advance(Timeout * 30);

        Assert.That(released, Is.EqualTo(0));
        Assert.That(gate.State, Is.EqualTo(ReadinessState.Dormant));
        Assert.That(gate.HeldCount, Is.EqualTo(1));
        Assert.That(notices, Is.Empty);
    }

    /// <summary>A gate that is not open has nothing to go dormant from, and the recycle must not go ahead.</summary>
    [Test]
    public void OnlyAnOpenGateCanGoDormant()
    {
        using var gate = CreateGate(out _, out _);
        gate.Start();
        gate.MarkRoslynInitialized();

        Assert.That(gate.MarkDormant(), Is.False);
        Assert.That(gate.State, Is.EqualTo(ReadinessState.RoslynInitialized));

        gate.MarkFailed("no backend");

        Assert.That(gate.MarkDormant(), Is.False);
        Assert.That(gate.State, Is.EqualTo(ReadinessState.Failed));
    }

    /// <summary>
    /// The wake starts the load clock — from the wake, not from the recycle — and the reloaded
    /// workspace's readiness releases what was held and raises <c>Opened</c> again, which is what
    /// re-arms the diagnostics and watch bridges.
    /// </summary>
    [Test]
    public void WakingArmsTheLoadBudgetAndReopeningRaisesOpenedAgain()
    {
        using var gate = CreateGate(out var time, out var notices);
        gate.Start();

        var opened = new List<ReadinessState>();
        gate.Opened += opened.Add;

        gate.MarkProjectsLoaded();
        Assert.That(opened, Has.Exactly(1).Items);

        gate.MarkDormant();

        var outcomes = new List<GateOutcome>();
        gate.TryHold(JsonRpcId.FromNumber(1), "textDocument/definition", outcomes.Add);

        // An hour dormant: none of it counts against the load budget.
        time.Advance(TimeSpan.FromHours(1));

        Assert.That(gate.MarkWaking(), Is.True);
        Assert.That(gate.State, Is.EqualTo(ReadinessState.Starting));

        time.Advance(Timeout - TimeSpan.FromSeconds(1));
        Assert.That(outcomes, Is.Empty);

        // And the ten-second notice is back, because now something is genuinely loading.
        Assert.That(notices, Is.Not.Empty);

        gate.MarkRoslynInitialized();
        gate.MarkProjectsLoaded();

        Assert.That(outcomes, Is.EqualTo([GateOutcome.Pass]));
        Assert.That(opened.Count, Is.EqualTo(2));
        Assert.That(opened[1], Is.EqualTo(ReadinessState.ProjectsLoaded));
    }

    /// <summary>The wake's budget runs out like a first load's does.</summary>
    [Test]
    public void AReloadThatOverrunsItsBudgetTimesOutFromTheWake()
    {
        using var gate = CreateGate(out var time, out _);
        gate.Start();
        gate.MarkProjectsLoaded();
        gate.MarkDormant();

        var outcome = default(GateOutcome?);
        gate.TryHold(JsonRpcId.FromNumber(1), "textDocument/definition", x => outcome = x);

        time.Advance(TimeSpan.FromMinutes(30));
        gate.MarkWaking();

        time.Advance(Timeout + TimeSpan.FromSeconds(1));

        Assert.That(outcome, Is.EqualTo(GateOutcome.Pass));
        Assert.That(gate.State, Is.EqualTo(ReadinessState.LoadTimedOut));
    }

    /// <summary>
    /// A recycle abandoned before Roslyn heard anything reopens the gate, releases what arrived, and
    /// does not raise <c>Opened</c> — nothing downstream was told it had closed.
    /// </summary>
    [Test]
    public void CancellingDormancyReleasesWhatArrivedWithoutReopeningTheBridges()
    {
        using var gate = CreateGate(out _, out _);
        gate.Start();
        gate.MarkProjectsLoaded();

        var opened = 0;
        gate.Opened += _ => opened++;

        gate.MarkDormant();

        var outcomes = new List<GateOutcome>();
        gate.TryHold(JsonRpcId.FromNumber(1), "textDocument/definition", outcomes.Add);

        gate.CancelDormant();

        Assert.That(gate.State, Is.EqualTo(ReadinessState.ProjectsLoaded));
        Assert.That(outcomes, Is.EqualTo([GateOutcome.Pass]));
        Assert.That(opened, Is.EqualTo(0));
    }

    /// <summary>
    /// A late readiness notification from a backend that is being stopped must not open a dormant
    /// gate into a server that is gone; only the wake leaves that state.
    /// </summary>
    [Test]
    public void AStrayReadinessNotificationDoesNotOpenADormantGate()
    {
        using var gate = CreateGate(out _, out _);
        gate.Start();
        gate.MarkProjectsLoaded();
        gate.MarkDormant();

        var released = 0;
        gate.TryHold(JsonRpcId.FromNumber(1), "textDocument/definition", _ => released++);

        gate.MarkProjectsLoaded();

        Assert.That(gate.State, Is.EqualTo(ReadinessState.Dormant));
        Assert.That(released, Is.EqualTo(0));
    }

    /// <summary>
    /// A load that finishes after its budget ran out moves the gate to loaded without opening it a
    /// second time, and says so once (finding 6, D100).
    /// </summary>
    [Test]
    public void AWorkspaceThatLoadsAfterItsBudgetIsLoadedLateWithoutOpeningAgain()
    {
        using var gate = CreateGate(out var time, out _);
        gate.Start();
        gate.MarkRoslynInitialized();

        var opened = new List<ReadinessState>();
        var late = 0;
        gate.Opened += opened.Add;
        gate.LoadedLate += () => late++;

        time.Advance(Timeout + TimeSpan.FromSeconds(1));

        Assert.That(gate.State, Is.EqualTo(ReadinessState.LoadTimedOut));
        Assert.That(opened, Is.EqualTo([ReadinessState.LoadTimedOut]));

        gate.MarkProjectsLoaded();

        Assert.That(gate.State, Is.EqualTo(ReadinessState.ProjectsLoaded));
        Assert.That(late, Is.EqualTo(1));
        Assert.That(opened, Has.Exactly(1).Items);

        gate.MarkProjectsLoaded();
        Assert.That(late, Is.EqualTo(1));
    }

    /// <summary>
    /// A wake that fails puts the gate back to sleep: what waited for it is refused with the reason,
    /// and the gate is dormant rather than failed, so the next request is held and asks again
    /// (finding 5, D96).
    /// </summary>
    [Test]
    public void AWakeThatFailsGoesBackToSleepAndRefusesOnlyWhatWaitedForIt()
    {
        using var gate = CreateGate(out var time, out _);
        gate.Start();
        gate.MarkProjectsLoaded();
        gate.MarkDormant();

        var outcomes = new List<GateOutcome>();
        string? message = null;

        gate.TryHold(JsonRpcId.FromNumber(1), "textDocument/definition", outcomes.Add);
        Assert.That(gate.MarkWaking(), Is.True);

        gate.TryHold(JsonRpcId.FromNumber(2), "textDocument/hover", outcome =>
        {
            outcomes.Add(outcome);
            message = gate.FailureMessage;
        });

        Assert.That(gate.ReturnToDormant("the relaunch failed; the next request tries again"), Is.True);

        Assert.That(gate.State, Is.EqualTo(ReadinessState.Dormant));
        Assert.That(outcomes, Is.EqualTo([GateOutcome.Failed, GateOutcome.Failed]));
        Assert.That(message, Contains.Substring("the next request tries again").Using(StringComparison.Ordinal));
        Assert.That(gate.HeldCount, Is.EqualTo(0));
        Assert.That(gate.FailureReason, Is.Null);

        var asked = 0;
        gate.HeldWhileDormant += () => asked++;

        Assert.That(gate.TryHold(JsonRpcId.FromNumber(3), "textDocument/definition", outcomes.Add), Is.True);
        Assert.That(gate.HeldCount, Is.EqualTo(1));
        Assert.That(asked, Is.EqualTo(1));

        // Asleep: no load budget runs, and nothing is refused.
        time.Advance(Timeout * 2);

        Assert.That(gate.State, Is.EqualTo(ReadinessState.Dormant));
        Assert.That(outcomes.Count, Is.EqualTo(2));
    }

    /// <summary>Only a waking gate can go back to sleep; an open or a dormant one is left as it is.</summary>
    [Test]
    public void OnlyAWakingGateCanGoBackToSleep()
    {
        using var gate = CreateGate(out _, out _);
        gate.Start();
        gate.MarkProjectsLoaded();

        Assert.That(gate.ReturnToDormant("not waking"), Is.False);
        Assert.That(gate.State, Is.EqualTo(ReadinessState.ProjectsLoaded));

        gate.MarkDormant();

        Assert.That(gate.ReturnToDormant("not waking"), Is.False);
        Assert.That(gate.State, Is.EqualTo(ReadinessState.Dormant));
        Assert.That(gate.FailureReason, Is.Null);
    }

    private static ReadinessGate CreateGate(out TestTimeProvider time, out List<string> notices)
    {
        var clock = new TestTimeProvider();
        var lines = new List<string>();

        time = clock;
        notices = lines;

        return new ReadinessGate(clock, Timeout, NullLogger.Instance, lines.Add);
    }
}
