using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Tests.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

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
    [Test]
    public void AnExpectedExitIsNotARestart()
    {
        var supervisor = new RoslynSupervisor(new TestTimeProvider(), NullLogger.Instance);

        Assert.That(supervisor.Decide(stopping: true, out _), Is.EqualTo(RestartDecision.Expected));
        Assert.That(supervisor.RestartsInWindow, Is.EqualTo(0));
    }

    /// <summary>Two goes, and the third failure gives up.</summary>
    [Test]
    public void TwoRestartsAreAllowedAndTheThirdFailureGivesUp()
    {
        var supervisor = new RoslynSupervisor(new TestTimeProvider(), NullLogger.Instance);

        Assert.That(supervisor.Decide(stopping: false, out var first), Is.EqualTo(RestartDecision.Restart));
        Assert.That(first, Is.EqualTo(1));

        Assert.That(supervisor.Decide(stopping: false, out var second), Is.EqualTo(RestartDecision.Restart));
        Assert.That(second, Is.EqualTo(2));

        Assert.That(supervisor.Decide(stopping: false, out var third), Is.EqualTo(RestartDecision.GiveUp));
        Assert.That(third, Is.EqualTo(0));
    }

    /// <summary>
    /// The budget is a rate, not a total: a backend that died twice this morning still gets another
    /// go this afternoon.
    /// </summary>
    [Test]
    public void TheBudgetRefillsAsTheWindowSlides()
    {
        var time = new TestTimeProvider();
        var supervisor = new RoslynSupervisor(time, NullLogger.Instance);

        Assert.That(supervisor.Decide(stopping: false, out _), Is.EqualTo(RestartDecision.Restart));
        Assert.That(supervisor.Decide(stopping: false, out _), Is.EqualTo(RestartDecision.Restart));
        Assert.That(supervisor.Decide(stopping: false, out _), Is.EqualTo(RestartDecision.GiveUp));

        time.Advance(RoslynSupervisor.RestartWindow + TimeSpan.FromSeconds(1));

        Assert.That(supervisor.RestartsInWindow, Is.EqualTo(0));
        Assert.That(supervisor.Decide(stopping: false, out var afterWindow), Is.EqualTo(RestartDecision.Restart));
        Assert.That(afterWindow, Is.EqualTo(1));
    }

    /// <summary>
    /// Only the oldest restart falls out of a partly-slid window, so a slow crash loop cannot buy
    /// itself an extra go by spacing itself out just inside the limit.
    /// </summary>
    [Test]
    public void OnlyRestartsOlderThanTheWindowExpire()
    {
        var time = new TestTimeProvider();
        var supervisor = new RoslynSupervisor(time, NullLogger.Instance);

        Assert.That(supervisor.Decide(stopping: false, out _), Is.EqualTo(RestartDecision.Restart));

        time.Advance(TimeSpan.FromMinutes(9));

        Assert.That(supervisor.Decide(stopping: false, out _), Is.EqualTo(RestartDecision.Restart));
        Assert.That(supervisor.RestartsInWindow, Is.EqualTo(2));
        Assert.That(supervisor.Decide(stopping: false, out _), Is.EqualTo(RestartDecision.GiveUp));

        // Two minutes later the first one is out of the window and the second is not.
        time.Advance(TimeSpan.FromMinutes(2));

        Assert.That(supervisor.RestartsInWindow, Is.EqualTo(1));
        Assert.That(supervisor.Decide(stopping: false, out _), Is.EqualTo(RestartDecision.Restart));
    }

    /// <summary>The sentence the client is given names the numbers, because a bug report needs them.</summary>
    [Test]
    public void TheGiveUpReasonNamesTheBudget()
    {
        Assert.That(RoslynSupervisor.GiveUpReason, Contains.Substring("3 times").Using(StringComparison.Ordinal));
        Assert.That(RoslynSupervisor.GiveUpReason, Contains.Substring("10 minutes").Using(StringComparison.Ordinal));
    }
}
