using ClaudeRoslynLsp.Cli;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Testing;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// The <c>lsp</c> verb: builds a session over the process's own standard streams and runs it.
/// </summary>
/// <remarks>
/// Everything that is a decision lives in <see cref="AdapterSession"/>; what is decided here is only
/// which backend the session talks to, and that is settled by the environment rather than by a
/// conditional inside the mediation.
/// </remarks>
internal static partial class LspAdapterServer
{
    /// <summary>
    /// The flag that swaps the real backend for the scripted one, in this process.
    /// </summary>
    /// <remarks>
    /// Hidden from the usage text on purpose: it is what <c>SmokeTest</c> drives so that every
    /// release RID can prove framing, gating and shutdown against a published Native AOT binary
    /// without downloading seventy megabytes of Roslyn onto five runners. A user has no reason to
    /// type it, and a documented flag is one somebody eventually runs by accident.
    /// </remarks>
    internal const string SmokeFlag = "--smoke";

    /// <summary>
    /// The variable that spawns the scripted backend as a <em>child process</em> instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set to <c>child</c>, this makes the adapter launch <c>&lt;self&gt; fake-roslyn</c> and speak
    /// to it over its stdio. That is a different thing from <see cref="SmokeFlag"/> and worth having
    /// separately: the in-process fake proves the mediation, and this proves the <em>plumbing</em> —
    /// that a child can be started on this RID, that its handles are wired the right way round, and
    /// that nothing in the AOT binary writes a banner onto a stream that is now a protocol channel
    /// (C7 is exactly that failure, observed on the real server).
    /// </para>
    /// <para>
    /// It lives here rather than in a launcher because WP3 owns process launch and this must not
    /// pre-empt its design. When WP4 wires the real <c>IRoslynLauncher</c> in, this stays as the
    /// smoke path and nothing about it has to move.
    /// </para>
    /// </remarks>
    internal const string FakeBackendVariable = "CLAUDE_ROSLYN_LSP_FAKE_BACKEND";

    /// <summary>Runs the adapter on the process's own standard streams and returns the exit code.</summary>
    /// <param name="arguments">Whatever followed the verb.</param>
    internal static async Task<int> RunStdioAsync(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var smoke = false;

        foreach (var argument in arguments)
        {
            if (string.Equals(argument, SmokeFlag, StringComparison.Ordinal))
            {
                smoke = true;
                continue;
            }

            CliRuntime.WriteError($"{ServerVersion.Name}: `lsp` does not take the argument '{argument}'.");
            return CliDispatcher.ExitUsage;
        }

        var options = ClaudeRoslynLspOptions.FromEnvironment();

        using var loggerFactory = CliRuntime.CreateLoggerFactory(options.LogLevel);
        var logger = loggerFactory.CreateLogger("ClaudeRoslynLsp.Adapter");

        var factory = SelectFactory(smoke, options, logger);

        Log.Starting(logger, ServerVersion.Name, ServerVersion.Value, factory.Description);

        await using var session = new AdapterSession(
            CliRuntime.StandardInput,
            CliRuntime.StandardOutput,
            factory,
            options,
            () => options.Solution,
            TimeProvider.System,
            logger);

        return await session.RunAsync().ConfigureAwait(false);
    }

    /// <summary>Picks the backend this run talks to.</summary>
    /// <param name="smoke">Whether <c>--smoke</c> was passed.</param>
    /// <param name="options">The environment configuration.</param>
    /// <param name="logger">The stderr log.</param>
    private static IRoslynConnectionFactory SelectFactory(
        bool smoke,
        ClaudeRoslynLspOptions options,
        ILogger logger)
    {
        if (smoke)
        {
            return new InProcessFakeRoslynFactory(logger);
        }

        if (string.Equals(
                Environment.GetEnvironmentVariable(FakeBackendVariable),
                "child",
                StringComparison.OrdinalIgnoreCase))
        {
            return new ChildProcessFakeRoslynFactory(logger);
        }

        // WP3 builds the acquisition and launch chain and WP4 wires it in here. Until then this is
        // an honest refusal rather than a stub that pretends: the session answers the handshake,
        // holds nothing, and refuses every request with a doctor hint — which is a state a user can
        // act on, unlike a server that answers everything with an empty result.
        return new UnavailableRoslynFactory(options, logger);
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 200,
            Level = LogLevel.Information,
            Message = "{Server} {Version} starting in LSP mode, backed by {Backend}.")]
        internal static partial void Starting(ILogger logger, string server, string version, string backend);
    }
}
