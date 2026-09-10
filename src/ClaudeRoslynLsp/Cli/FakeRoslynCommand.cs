using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Testing;

namespace ClaudeRoslynLsp.Cli;

/// <summary>
/// <c>claude-roslyn-lsp fake-roslyn</c> — the scripted stand-in for the real Roslyn language server,
/// speaking the same <c>Content-Length</c>-framed dialect on stdio.
/// </summary>
/// <remarks>
/// <para>
/// Hidden from the usage text: it is a test double, not a user-facing verb. <c>SmokeTest</c> points
/// the adapter at this same binary (<c>CLAUDE_ROSLYN_LSP_FAKE_BACKEND=child</c>) so that every
/// release RID can prove the whole path — spawn a child, wire three redirected handles, frame both
/// ways, gate a request on readiness, shut down in order — on its own architecture, without
/// downloading seventy megabytes of real Roslyn onto five runners.
/// </para>
/// <para>
/// The scripted server itself is <see cref="FakeRoslynServer"/>, shared with the mediation tests, so
/// what the smoke test exercises and what the unit tests exercise are the same code answering the
/// same script.
/// </para>
/// </remarks>
internal static class FakeRoslynCommand
{
    /// <summary>
    /// How long after <c>solution/open</c> this fake reports the workspace loaded.
    /// </summary>
    /// <remarks>
    /// Long enough that a request sent immediately after <c>initialized</c> is genuinely held by the
    /// readiness gate rather than racing it, and short enough that the smoke test costs nothing. The
    /// real server takes 2.5-9 s (C31); what is being proved here is the mechanism, not the duration.
    /// </remarks>
    internal static readonly TimeSpan ProjectLoadDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>Runs the scripted backend on the process's own standard streams.</summary>
    internal static async Task<int> RunAsync()
    {
        var options = ClaudeRoslynLspOptions.FromEnvironment();

        using var loggerFactory = CliRuntime.CreateLoggerFactory(options.LogLevel);
        var logger = loggerFactory.CreateLogger("ClaudeRoslynLsp.Testing.FakeRoslyn");

        var server = new FakeRoslynServer(
            FakeRoslynScript.Roslyn512Startup(projectLoadDelay: ProjectLoadDelay),
            logger);

        await server.RunAsync(CliRuntime.StandardInput, CliRuntime.StandardOutput).ConfigureAwait(false);

        // The same exit-code rule the real server follows, because the adapter's shutdown path is
        // what this verb exists to exercise.
        return server.ShutdownRequested ? CliDispatcher.ExitSuccess : CliDispatcher.ExitFailure;
    }
}
