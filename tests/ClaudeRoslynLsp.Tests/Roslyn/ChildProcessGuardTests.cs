using System.Diagnostics;

using ClaudeRoslynLsp.Roslyn;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Roslyn;

/// <summary>
/// Covers D35: on Windows a child in the job object dies with the job, and everywhere else the guard
/// is an inert object that costs nothing.
/// </summary>
/// <remarks>
/// The Windows case is tested with a real child rather than by asserting the P/Invokes were called,
/// because the interesting failure is not "the call was made" but "the call was made with a buffer
/// the wrong size": <c>SetInformationJobObject</c> answers <c>ERROR_BAD_LENGTH</c> to the basic
/// structure and the job then silently keeps nothing alive. Only a process that actually dies proves
/// the difference.
/// </remarks>
public class ChildProcessGuardTests
{
    [Fact]
    public void TheGuardIsOnlyActiveOnWindows()
    {
        using var guard = ChildProcessGuard.Create(NullLogger.Instance);

        Assert.Equal(OperatingSystem.IsWindows(), guard.IsActive);
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        var guard = ChildProcessGuard.Create(NullLogger.Instance);

        guard.Dispose();
        guard.Dispose();
    }

    [Fact]
    public void AChildOutsideTheGuardOutlivesIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The job object exists only on Windows; elsewhere Roslyn's own processId watchdog covers this.");
        }

        using var process = StartLongLivedChild();

        try
        {
            using (ChildProcessGuard.Create(NullLogger.Instance))
            {
                // Deliberately not added to the guard.
            }

            // The control case for the test below: nothing about creating and closing a job object
            // kills a process that was never in it.
            Assert.False(process.WaitForExit(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            Kill(process);
        }
    }

    [Fact]
    public void AChildInTheGuardDiesWhenTheGuardIsDisposed()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The job object exists only on Windows; elsewhere Roslyn's own processId watchdog covers this.");
        }

        using var process = StartLongLivedChild();

        try
        {
            var guard = ChildProcessGuard.Create(NullLogger.Instance);
            Assert.True(guard.IsActive);

            guard.Add(process, NullLogger.Instance);
            guard.Dispose();

            Assert.True(
                process.WaitForExit(TimeSpan.FromSeconds(10)),
                "The child survived the job object being closed, so KILL_ON_JOB_CLOSE did not take effect.");
        }
        finally
        {
            Kill(process);
        }
    }

    /// <summary>
    /// A process that has already exited is nothing to guard, and saying so must not throw: the real
    /// launcher hits this whenever Roslyn fails to start.
    /// </summary>
    [Fact]
    public void AddingAnExitedProcessIsHarmless()
    {
        using var guard = ChildProcessGuard.Create(NullLogger.Instance);
        using var process = StartLongLivedChild();

        Kill(process);
        process.WaitForExit(TimeSpan.FromSeconds(10));

        guard.Add(process, NullLogger.Instance);
    }

    /// <summary>
    /// <c>ping -n</c> rather than <c>timeout</c>: <c>timeout</c> refuses to run at all when stdin is
    /// redirected, which it is here.
    /// </summary>
    private static Process StartLongLivedChild()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "ping", "-n", "60", "127.0.0.1" } }
            : new ProcessStartInfo("sh") { ArgumentList = { "-c", "sleep 60" } };

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        var process = Process.Start(startInfo);
        Assert.NotNull(process);

        return process!;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
