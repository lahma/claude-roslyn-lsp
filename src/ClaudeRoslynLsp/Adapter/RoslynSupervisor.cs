using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>What the supervisor decided to do about a backend that went away.</summary>
internal enum RestartDecision
{
    /// <summary>Relaunch, re-initialise, replay the mirror and re-open the workspace.</summary>
    Restart,

    /// <summary>The budget is spent. The session moves to <see cref="ReadinessState.Failed"/>.</summary>
    GiveUp,

    /// <summary>The adapter is shutting down; the exit was expected.</summary>
    Expected,
}

/// <summary>
/// Decides whether a Roslyn that just died gets another go.
/// </summary>
/// <remarks>
/// <para>
/// <b>A crash is absorbed, not forwarded (D19).</b> Claude Code's own restart budget is three, and
/// it is spent by restarting <em>this</em> process — which throws away the client session, the
/// document mirror, the readiness state and every answer in flight, and leaves the model looking at
/// a tool that stopped working. Roslyn dying is not that kind of failure: everything needed to
/// rebuild the backend's state is held here, so the client should never learn it happened. What it
/// sees instead is a few seconds of held requests, exactly as it saw at startup.
/// </para>
/// <para>
/// <b>Two restarts per ten minutes, and then it stops.</b> The limit is not about resource use, it
/// is about the difference between an accident and a pattern. A Roslyn killed by the OOM killer, or
/// by a user's <c>taskkill</c>, or by a driver update yanking a file, comes back and works — that is
/// worth two attempts. A Roslyn that dies on the same solution three times in ten minutes is
/// deterministic, and relaunching it a fourth time turns one broken session into an infinite loop
/// that also holds every request in it. So the third failure moves the gate to
/// <see cref="ReadinessState.Failed"/>, where every request is answered with a code and a
/// <c>doctor</c> hint — a state a user can read and act on.
/// </para>
/// <para>
/// <b>Every step says so on <c>window/logMessage</c>.</b> A session that silently went quiet for
/// eight seconds and then answered again is indistinguishable from a slow one; a session that said
/// "the Roslyn backend exited, restarting (1 of 2)" is a bug report somebody can file.
/// </para>
/// </remarks>
internal sealed partial class RoslynSupervisor
{
    /// <summary>How many relaunches are allowed inside <see cref="RestartWindow"/>.</summary>
    internal const int MaxRestarts = 2;

    /// <summary>The window the restart budget is counted over.</summary>
    internal static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(10);

    private readonly Lock _lock = new();
    private readonly List<DateTimeOffset> _restarts = [];
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>Creates a supervisor with a fresh budget.</summary>
    /// <param name="time">The clock the window is measured on.</param>
    /// <param name="logger">The stderr log.</param>
    internal RoslynSupervisor(TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _time = time;
        _logger = logger;
    }

    /// <summary>How many relaunches have happened inside the current window.</summary>
    internal int RestartsInWindow
    {
        get
        {
            lock (_lock)
            {
                Expire();
                return _restarts.Count;
            }
        }
    }

    /// <summary>
    /// Decides what to do about a backend that has gone, and spends a restart when it says so.
    /// </summary>
    /// <param name="stopping">Whether the adapter itself is shutting the backend down.</param>
    /// <param name="attempt">Which attempt this would be, 1-based, for the log line.</param>
    internal RestartDecision Decide(bool stopping, out int attempt)
    {
        attempt = 0;

        if (stopping)
        {
            return RestartDecision.Expected;
        }

        lock (_lock)
        {
            Expire();

            if (_restarts.Count >= MaxRestarts)
            {
                Log.GivingUp(_logger, _restarts.Count, RestartWindow.TotalMinutes);
                return RestartDecision.GiveUp;
            }

            _restarts.Add(_time.GetUtcNow());
            attempt = _restarts.Count;
        }

        Log.Restarting(_logger, attempt, MaxRestarts);
        return RestartDecision.Restart;
    }

    /// <summary>The sentence the client is told when the budget runs out.</summary>
    internal static string GiveUpReason =>
        $"the Roslyn backend exited {MaxRestarts + 1} times within {RestartWindow.TotalMinutes:0} minutes";

    /// <summary>Drops restarts that have fallen out of the window. Called under the lock.</summary>
    private void Expire()
    {
        var cutoff = _time.GetUtcNow() - RestartWindow;
        _restarts.RemoveAll(x => x < cutoff);
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1500,
            Level = LogLevel.Warning,
            Message = "The Roslyn backend exited unexpectedly; relaunching (attempt {Attempt} of {Limit}).")]
        internal static partial void Restarting(ILogger logger, int attempt, int limit);

        [LoggerMessage(
            EventId = 1501,
            Level = LogLevel.Error,
            Message = "The Roslyn backend has exited {Count} time(s) within {Minutes:0} minutes; it will " +
                      "not be relaunched again. Every request will now be refused with an explanation " +
                      "rather than answered from a workspace that is not there.")]
        internal static partial void GivingUp(ILogger logger, int count, double minutes);
    }
}
