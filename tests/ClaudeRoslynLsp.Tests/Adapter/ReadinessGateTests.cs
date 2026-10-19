using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Protocol;
using ClaudeRoslynLsp.Tests.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

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

    [Fact]
    public void RequestsAreHeldWhileTheWorkspaceLoadsAndReleasedInArrivalOrder()
    {
        var released = new List<string>();

        using var gate = CreateGate(out _, out _);
        gate.Start();
        gate.MarkRoslynInitialized();

        for (var index = 0; index < 5; index++)
        {
            var method = $"request-{index}";
            Assert.True(gate.TryHold(JsonRpcId.FromNumber(index), method, outcome =>
            {
                Assert.Equal(GateOutcome.Pass, outcome);
                released.Add(method);
            }));
        }

        Assert.Equal(5, gate.HeldCount);
        Assert.Empty(released);

        gate.MarkProjectsLoaded();

        Assert.Equal(["request-0", "request-1", "request-2", "request-3", "request-4"], released);
        Assert.Equal(ReadinessState.ProjectsLoaded, gate.State);
        Assert.Equal(0, gate.HeldCount);
    }

    [Fact]
    public void OnceOpenARequestGoesStraightThroughWithoutBeingQueued()
    {
        using var gate = CreateGate(out _, out _);
        gate.Start();
        gate.MarkProjectsLoaded();

        var outcome = default(GateOutcome?);

        Assert.False(gate.TryHold(JsonRpcId.FromNumber(1), "textDocument/definition", x => outcome = x));
        Assert.Equal(GateOutcome.Pass, outcome);
    }

    /// <summary>
    /// The budget is a floor, not a promise: after it, a partially loaded workspace answering some
    /// questions beats a server that has stopped answering at all.
    /// </summary>
    [Fact]
    public void TheLoadBudgetFlipsTheGateOpenAndPassesEverythingHeld()
    {
        using var gate = CreateGate(out var time, out var notices);
        gate.Start();
        gate.MarkRoslynInitialized();

        var released = 0;
        gate.TryHold(JsonRpcId.FromNumber(1), "textDocument/definition", outcome =>
        {
            Assert.Equal(GateOutcome.Pass, outcome);
            released++;
        });

        time.Advance(Timeout - TimeSpan.FromSeconds(1));
        Assert.Equal(0, released);
        Assert.Equal(ReadinessState.RoslynInitialized, gate.State);

        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(1, released);
        Assert.Equal(ReadinessState.LoadTimedOut, gate.State);
        Assert.Contains(notices, x => x.Contains("did not finish loading", StringComparison.Ordinal));
    }

    /// <summary>
    /// A cancellation arriving while a request is held has never reached Roslyn, so it cannot be
    /// mapped through — the gate answers it, with LSP's own <c>RequestCancelled</c>.
    /// </summary>
    [Fact]
    public void CancellingAHeldRequestDequeuesItAndLeavesTheRestInOrder()
    {
        var released = new List<string>();

        using var gate = CreateGate(out _, out _);
        gate.Start();
        gate.MarkRoslynInitialized();

        gate.TryHold(JsonRpcId.FromNumber(1), "first", _ => released.Add("first"));
        gate.TryHold(JsonRpcId.FromNumber(2), "second", outcome =>
        {
            Assert.Equal(GateOutcome.Cancelled, outcome);
            released.Add("second-cancelled");
        });
        gate.TryHold(JsonRpcId.FromNumber(3), "third", _ => released.Add("third"));

        Assert.True(gate.TryCancel(JsonRpcId.FromNumber(2)));
        Assert.Equal(2, gate.HeldCount);
        Assert.Equal(["second-cancelled"], released);

        gate.MarkProjectsLoaded();

        Assert.Equal(["second-cancelled", "first", "third"], released);
    }

    [Fact]
    public void CancellingSomethingThatIsNotHeldReportsSo()
    {
        using var gate = CreateGate(out _, out _);
        gate.Start();

        Assert.False(gate.TryCancel(JsonRpcId.FromNumber(99)));
    }

    /// <summary>
    /// A backend that will not start is a state the user has to be able to read. The refusal names
    /// <c>doctor</c>, because that is where the resolution chain is.
    /// </summary>
    [Fact]
    public void AFailedBackendRefusesEverythingHeldWithAnActionableMessage()
    {
        using var gate = CreateGate(out _, out _);
        gate.Start();

        var outcome = default(GateOutcome?);
        gate.TryHold(JsonRpcId.FromNumber(1), "textDocument/definition", x => outcome = x);

        gate.MarkFailed("the backend could not be started");

        Assert.Equal(GateOutcome.Failed, outcome);
        Assert.Equal(ReadinessState.Failed, gate.State);
        Assert.Contains("doctor", gate.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("the backend could not be started", gate.FailureMessage, StringComparison.Ordinal);

        // And a request arriving afterwards is refused at once rather than queued forever.
        var later = default(GateOutcome?);
        Assert.False(gate.TryHold(JsonRpcId.FromNumber(2), "textDocument/hover", x => later = x));
        Assert.Equal(GateOutcome.Failed, later);
    }

    [Fact]
    public void ALoadedWorkspaceCannotBeUnLoadedByALaterFailure()
    {
        using var gate = CreateGate(out _, out _);
        gate.Start();
        gate.MarkProjectsLoaded();
        gate.MarkFailed("the Roslyn backend exited");

        Assert.Equal(ReadinessState.ProjectsLoaded, gate.State);
    }

    /// <summary>
    /// Notifications flow from the moment Roslyn is initialised, not from the moment the workspace is
    /// loaded: a <c>didOpen</c> withheld until then would leave Roslyn analysing the file on disk.
    /// </summary>
    [Fact]
    public void NotificationsAreAllowedFromRoslynInitializedOnwards()
    {
        using var gate = CreateGate(out _, out _);

        Assert.False(gate.NotificationsAllowed);

        gate.Start();
        Assert.False(gate.NotificationsAllowed);

        gate.MarkRoslynInitialized();
        Assert.True(gate.NotificationsAllowed);

        gate.MarkProjectsLoaded();
        Assert.True(gate.NotificationsAllowed);
    }

    /// <summary>
    /// Ten seconds of silence on a slow load looks like a hang, so the client is told — once per
    /// interval, only while something is actually waiting, and naming the workspace.
    /// </summary>
    [Fact]
    public void AClientHoldingRequestsIsToldEveryTenSeconds()
    {
        using var gate = CreateGate(out var time, out var notices);
        gate.WorkspaceDescription = "Quartz.slnx";
        gate.Start();
        gate.MarkRoslynInitialized();

        // Nothing held: nothing said.
        time.Advance(TimeSpan.FromSeconds(25));
        Assert.Empty(notices);

        gate.TryHold(JsonRpcId.FromNumber(1), "textDocument/definition", _ => { });

        time.Advance(ReadinessGate.NoticeInterval);
        var first = Assert.Single(notices);

        Assert.Contains("holding 1 request(s)", first, StringComparison.Ordinal);
        Assert.Contains("Quartz.slnx", first, StringComparison.Ordinal);
        // The notice reports the moment the timer was scheduled for, not the end of the jump: the
        // third tick is due at 30 s and that is when the line was written.
        Assert.Contains("30s elapsed", first, StringComparison.Ordinal);

        time.Advance(ReadinessGate.NoticeInterval);
        Assert.Equal(2, notices.Count);

        // And it stops the moment the workspace is up.
        gate.MarkProjectsLoaded();
        time.Advance(ReadinessGate.NoticeInterval * 3);
        Assert.Equal(2, notices.Count);
    }

    /// <summary>
    /// D96: a dormant gate holds with no clock at all. A gate built on MarkRestarting would open by
    /// itself after the load budget and pass everything it held to a backend that is not there, and it
    /// would narrate a load nobody has started every ten seconds while it waited.
    /// </summary>
    [Fact]
    public void ADormantGateHoldsWithoutATimeoutOrANotice()
    {
        using var gate = CreateGate(out var time, out var notices);
        gate.Start();
        gate.MarkProjectsLoaded();

        var asked = 0;
        gate.HeldWhileDormant += () => asked++;

        Assert.True(gate.MarkDormant());
        Assert.Equal(ReadinessState.Dormant, gate.State);
        Assert.False(gate.IsOpen);
        Assert.False(gate.NotificationsAllowed);

        var released = 0;
        Assert.True(gate.TryHold(JsonRpcId.FromNumber(1), "textDocument/definition", _ => released++));

        // Holding is what asks for the backend back.
        Assert.Equal(1, asked);

        time.Advance(Timeout * 30);

        Assert.Equal(0, released);
        Assert.Equal(ReadinessState.Dormant, gate.State);
        Assert.Equal(1, gate.HeldCount);
        Assert.Empty(notices);
    }

    /// <summary>A gate that is not open has nothing to go dormant from, and the recycle must not go ahead.</summary>
    [Fact]
    public void OnlyAnOpenGateCanGoDormant()
    {
        using var gate = CreateGate(out _, out _);
        gate.Start();
        gate.MarkRoslynInitialized();

        Assert.False(gate.MarkDormant());
        Assert.Equal(ReadinessState.RoslynInitialized, gate.State);

        gate.MarkFailed("no backend");

        Assert.False(gate.MarkDormant());
        Assert.Equal(ReadinessState.Failed, gate.State);
    }

    /// <summary>
    /// The wake starts the load clock — from the wake, not from the recycle — and the reloaded
    /// workspace's readiness releases what was held and raises <c>Opened</c> again, which is what
    /// re-arms the diagnostics and watch bridges.
    /// </summary>
    [Fact]
    public void WakingArmsTheLoadBudgetAndReopeningRaisesOpenedAgain()
    {
        using var gate = CreateGate(out var time, out var notices);
        gate.Start();

        var opened = new List<ReadinessState>();
        gate.Opened += opened.Add;

        gate.MarkProjectsLoaded();
        Assert.Single(opened);

        gate.MarkDormant();

        var outcomes = new List<GateOutcome>();
        gate.TryHold(JsonRpcId.FromNumber(1), "textDocument/definition", outcomes.Add);

        // An hour dormant: none of it counts against the load budget.
        time.Advance(TimeSpan.FromHours(1));

        Assert.True(gate.MarkWaking());
        Assert.Equal(ReadinessState.Starting, gate.State);

        time.Advance(Timeout - TimeSpan.FromSeconds(1));
        Assert.Empty(outcomes);

        // And the ten-second notice is back, because now something is genuinely loading.
        Assert.NotEmpty(notices);

        gate.MarkRoslynInitialized();
        gate.MarkProjectsLoaded();

        Assert.Equal([GateOutcome.Pass], outcomes);
        Assert.Equal(2, opened.Count);
        Assert.Equal(ReadinessState.ProjectsLoaded, opened[1]);
    }

    /// <summary>The wake's budget runs out like a first load's does.</summary>
    [Fact]
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

        Assert.Equal(GateOutcome.Pass, outcome);
        Assert.Equal(ReadinessState.LoadTimedOut, gate.State);
    }

    /// <summary>
    /// A recycle abandoned before Roslyn heard anything reopens the gate, releases what arrived, and
    /// does not raise <c>Opened</c> — nothing downstream was told it had closed.
    /// </summary>
    [Fact]
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

        Assert.Equal(ReadinessState.ProjectsLoaded, gate.State);
        Assert.Equal([GateOutcome.Pass], outcomes);
        Assert.Equal(0, opened);
    }

    /// <summary>
    /// A late readiness notification from a backend that is being stopped must not open a dormant
    /// gate into a server that is gone; only the wake leaves that state.
    /// </summary>
    [Fact]
    public void AStrayReadinessNotificationDoesNotOpenADormantGate()
    {
        using var gate = CreateGate(out _, out _);
        gate.Start();
        gate.MarkProjectsLoaded();
        gate.MarkDormant();

        var released = 0;
        gate.TryHold(JsonRpcId.FromNumber(1), "textDocument/definition", _ => released++);

        gate.MarkProjectsLoaded();

        Assert.Equal(ReadinessState.Dormant, gate.State);
        Assert.Equal(0, released);
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
