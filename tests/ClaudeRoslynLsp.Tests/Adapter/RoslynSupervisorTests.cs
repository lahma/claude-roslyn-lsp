using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Tests.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// The budget that separates an accident from a pattern.
/// </summary>
/// <remarks>
/// On a hand-driven clock, because the window is ten minutes and a test that waited for it is a test
/// nobody runs.
/// </remarks>
public class RoslynSupervisorTests
{
    /// <summary>An expected exit — the adapter shutting the backend down — spends nothing.</summary>
    [Fact]
    public void AnExpectedExitIsNotARestart()
    {
        var supervisor = new RoslynSupervisor(new TestTimeProvider(), NullLogger.Instance);

        Assert.Equal(RestartDecision.Expected, supervisor.Decide(stopping: true, out _));
        Assert.Equal(0, supervisor.RestartsInWindow);
    }

    /// <summary>Two goes, and the third failure gives up.</summary>
    [Fact]
    public void TwoRestartsAreAllowedAndTheThirdFailureGivesUp()
    {
        var supervisor = new RoslynSupervisor(new TestTimeProvider(), NullLogger.Instance);

        Assert.Equal(RestartDecision.Restart, supervisor.Decide(stopping: false, out var first));
        Assert.Equal(1, first);

        Assert.Equal(RestartDecision.Restart, supervisor.Decide(stopping: false, out var second));
        Assert.Equal(2, second);

        Assert.Equal(RestartDecision.GiveUp, supervisor.Decide(stopping: false, out var third));
        Assert.Equal(0, third);
    }

    /// <summary>
    /// The budget is a rate, not a total: a backend that died twice this morning still gets another
    /// go this afternoon.
    /// </summary>
    [Fact]
    public void TheBudgetRefillsAsTheWindowSlides()
    {
        var time = new TestTimeProvider();
        var supervisor = new RoslynSupervisor(time, NullLogger.Instance);

        Assert.Equal(RestartDecision.Restart, supervisor.Decide(stopping: false, out _));
        Assert.Equal(RestartDecision.Restart, supervisor.Decide(stopping: false, out _));
        Assert.Equal(RestartDecision.GiveUp, supervisor.Decide(stopping: false, out _));

        time.Advance(RoslynSupervisor.RestartWindow + TimeSpan.FromSeconds(1));

        Assert.Equal(0, supervisor.RestartsInWindow);
        Assert.Equal(RestartDecision.Restart, supervisor.Decide(stopping: false, out var afterWindow));
        Assert.Equal(1, afterWindow);
    }

    /// <summary>
    /// Only the oldest restart falls out of a partly-slid window, so a slow crash loop cannot buy
    /// itself an extra go by spacing itself out just inside the limit.
    /// </summary>
    [Fact]
    public void OnlyRestartsOlderThanTheWindowExpire()
    {
        var time = new TestTimeProvider();
        var supervisor = new RoslynSupervisor(time, NullLogger.Instance);

        Assert.Equal(RestartDecision.Restart, supervisor.Decide(stopping: false, out _));

        time.Advance(TimeSpan.FromMinutes(9));

        Assert.Equal(RestartDecision.Restart, supervisor.Decide(stopping: false, out _));
        Assert.Equal(2, supervisor.RestartsInWindow);
        Assert.Equal(RestartDecision.GiveUp, supervisor.Decide(stopping: false, out _));

        // Two minutes later the first one is out of the window and the second is not.
        time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(1, supervisor.RestartsInWindow);
        Assert.Equal(RestartDecision.Restart, supervisor.Decide(stopping: false, out _));
    }

    /// <summary>The sentence the client is given names the numbers, because a bug report needs them.</summary>
    [Fact]
    public void TheGiveUpReasonNamesTheBudget()
    {
        Assert.Contains("3 times", RoslynSupervisor.GiveUpReason, StringComparison.Ordinal);
        Assert.Contains("10 minutes", RoslynSupervisor.GiveUpReason, StringComparison.Ordinal);
    }
}
