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

    private static ReadinessGate CreateGate(out TestTimeProvider time, out List<string> notices)
    {
        var clock = new TestTimeProvider();
        var lines = new List<string>();

        time = clock;
        notices = lines;

        return new ReadinessGate(clock, Timeout, NullLogger.Instance, lines.Add);
    }
}
