using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Roslyn;
using ClaudeRoslynLsp.Testing;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Mcp;

/// <summary>
/// Which Roslyn the <c>mcp</c> verb runs against, and how the smoke test gets one without a
/// download.
/// </summary>
/// <remarks>
/// <para>
/// The mirror image of <c>LspAdapterServer.SelectFactory</c>, and deliberately the same vocabulary
/// (D80): <c>--smoke</c> is the scripted backend inside this process,
/// <c>CLAUDE_ROSLYN_LSP_FAKE_BACKEND=child</c> is the same script in a child process, and anything
/// else is the real launcher. One value is new here — <c>none</c>, which registers
/// <see cref="NotWiredRoslynEngine"/> and is what proves D69's claim that the handshake and
/// <c>tools/list</c> do not depend on a backend at all. That leg used to be the default, which made
/// it accidental; the eager start (D76) would have turned it into a 70 MB download on five release
/// runners, so it is now spelled out.
/// </para>
/// <para>
/// Both fakes stay behind an explicit opt-in for the reason the <c>lsp</c> verb states: a backend
/// that answers navigation from a script is indistinguishable from a working one until somebody
/// trusts an answer.
/// </para>
/// </remarks>
internal static partial class McpBackendSelection
{
    /// <summary>The flag that swaps the real backend for the scripted one, in this process.</summary>
    internal const string SmokeFlag = "--smoke";

    /// <summary>The value of <see cref="LspAdapterServer.FakeBackendVariable"/> that wires nothing at all.</summary>
    internal const string NoBackend = "none";

    /// <summary>
    /// Builds the engine factory for one <c>mcp</c> run, or <see langword="null"/> for no backend.
    /// </summary>
    /// <param name="smoke">Whether <c>--smoke</c> was passed.</param>
    internal static Func<IServiceProvider, IRoslynEngine>? Resolve(bool smoke)
    {
        var fake = Environment.GetEnvironmentVariable(LspAdapterServer.FakeBackendVariable);

        if (string.Equals(fake, NoBackend, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return services => Build(services, smoke, fake);
    }

    /// <summary>Starts the backend, when the engine is one that has to be started.</summary>
    /// <param name="engine">The engine the container resolved.</param>
    internal static void Start(IRoslynEngine engine)
    {
        if (engine is OwnedRoslynEngine owned)
        {
            owned.Start();
        }
    }

    private static OwnedRoslynEngine Build(IServiceProvider services, bool smoke, string? fake)
    {
        var options = services.GetRequiredService<ClaudeRoslynLspOptions>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ClaudeRoslynLsp.Mcp.Engine");
        var root = McpServerSetup.ResolveWorkspaceRoot();

        if (smoke)
        {
            return new OwnedRoslynEngine(
                new InProcessFakeRoslynFactory(logger),
                options,
                () => WorkspaceSelection.None("the scripted backend needs no solution"),
                root,
                TimeProvider.System,
                logger,
                static () => new RoslynBackendIdentity("scripted-fake", Environment.ProcessId));
        }

        if (string.Equals(fake, "child", StringComparison.OrdinalIgnoreCase))
        {
            return new OwnedRoslynEngine(
                new ChildProcessFakeRoslynFactory(logger),
                options,
                () => WorkspaceSelection.None("the scripted backend needs no solution"),
                root,
                TimeProvider.System,
                logger,
                static () => new RoslynBackendIdentity("scripted-fake", null));
        }

        var factory = new LaunchedRoslynFactory(options, AdapterPaths.Resolve(options), logger);

        return new OwnedRoslynEngine(
            factory,
            options,
            () => SelectWorkspace(options, logger),
            root,
            TimeProvider.System,
            logger,
            () => new RoslynBackendIdentity(
                factory.Resolution?.Version ?? RoslynServerManifest.Version,
                factory.ProcessId));
    }

    /// <summary>
    /// Runs solution discovery and reduces it to what the opener needs.
    /// </summary>
    /// <remarks>
    /// The same decision the <c>lsp</c> verb makes, from the same class (D39-D42), against the
    /// process's working directory — which is what every MCP client sets to the workspace (D68).
    /// What is different is where the explanation goes: there is no <c>window/logMessage</c> here, so
    /// it goes to stderr and, through <c>getWorkspaceStatus</c>, to the model.
    /// </remarks>
    private static WorkspaceSelection SelectWorkspace(ClaudeRoslynLspOptions options, ILogger logger)
    {
        SolutionDiscoveryResult result;

        try
        {
            result = SolutionDiscovery.Discover(
                Environment.CurrentDirectory,
                options.Solution,
                options.SolutionVariable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException)
        {
            Log.DiscoveryFailed(logger, exception);
            return WorkspaceSelection.None($"the workspace could not be scanned ({exception.Message})");
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            var outcome = result.Outcome.ToString();
            Log.Discovered(logger, outcome, result.Explanation);
        }

        return new WorkspaceSelection(
            result.SolutionPath,
            result.ProjectPaths,
            result.Explanation,
            result.Outcome == SolutionDiscoveryOutcome.Failed);
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 5200, Level = LogLevel.Information, Message = "Workspace ({Outcome}): {Explanation}")]
        internal static partial void Discovered(ILogger logger, string outcome, string explanation);

        [LoggerMessage(
            EventId = 5201,
            Level = LogLevel.Warning,
            Message = "Solution discovery could not read the workspace; the MCP tools will answer from " +
                      "misc-files mode, which is wrong for anything that crosses a file.")]
        internal static partial void DiscoveryFailed(ILogger logger, Exception exception);
    }
}
