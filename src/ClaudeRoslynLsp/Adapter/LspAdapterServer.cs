using ClaudeRoslynLsp.Cli;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Roslyn;
using ClaudeRoslynLsp.Testing;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// The <c>lsp</c> verb: builds a session over the process's own standard streams and runs it.
/// </summary>
/// <remarks>
/// Everything that is a decision lives in <see cref="AdapterSession"/>; what is decided here is only
/// which backend the session talks to and which workspace it opens, and both are settled by the
/// environment rather than by a conditional inside the mediation.
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
    /// Both fake paths stay behind an explicit opt-in now that the real launcher is the default. A
    /// backend that answers navigation from a script is indistinguishable from a working one until
    /// somebody trusts an answer, so it must never be reachable by accident.
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

        // Built before the session so the session can hand it a way to talk to the client: an
        // acquisition that downloads 70 MB has to be able to say so, and the only channel a client
        // renders is window/logMessage.
        var messages = new PendingClientMessages();
        var factory = SelectFactory(smoke, options, logger, messages.Send);

        Log.Starting(logger, ServerVersion.Name, ServerVersion.Value, factory.Description);

        await using var session = new AdapterSession(
            CliRuntime.StandardInput,
            CliRuntime.StandardOutput,
            factory,
            options,
            () => options.Solution,
            TimeProvider.System,
            logger,
            () => SelectWorkspace(options, logger));

        messages.Attach(session);

        return await session.RunAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Runs solution discovery and reduces it to what the opener needs.
    /// </summary>
    /// <remarks>
    /// The decision itself is <see cref="SolutionDiscovery"/>'s (D39-D42): an explicit setting that
    /// does not resolve is a failure rather than a fall-through, <c>.vscode/settings.json</c>'s
    /// <c>dotnet.defaultSolution</c> is honoured because it is already checked in, candidates are
    /// scored rather than taken in walk order, and the project fallback keeps test projects. What is
    /// added here is the sentence the client is told, which carries the winner <em>and its score</em>
    /// — because "which solution did it open" is the first question of every report about a
    /// repository with more than one.
    /// </remarks>
    /// <param name="options">The environment configuration.</param>
    /// <param name="logger">The stderr log.</param>
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
            // Discovery walks a filesystem somebody else owns. A directory that vanished mid-walk
            // must degrade to misc-files mode, not take the server down.
            Log.DiscoveryFailed(logger, exception);
            return WorkspaceSelection.None($"the workspace could not be scanned ({exception.Message})");
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            var outcome = result.Outcome.ToString();
            Log.Discovered(logger, outcome, result.Explanation);
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var candidate in result.Candidates)
            {
                Log.Candidate(logger, candidate.Path, candidate.Score, candidate.Reason);
            }
        }

        var explanation = result.Candidates.Count > 1 && result.SolutionPath is { } chosen
            ? $"{result.Explanation} (chosen from {result.Candidates.Count} candidates; "
              + $"{Path.GetFileName(chosen)} scored {result.Candidates[0].Score})"
            : result.Explanation;

        return new WorkspaceSelection(
            result.SolutionPath,
            result.ProjectPaths,
            explanation,
            result.Outcome == SolutionDiscoveryOutcome.Failed);
    }

    /// <summary>Picks the backend this run talks to.</summary>
    /// <param name="smoke">Whether <c>--smoke</c> was passed.</param>
    /// <param name="options">The environment configuration.</param>
    /// <param name="logger">The stderr log.</param>
    /// <param name="tellClient">Sends a message to the client, once there is a session to send it through.</param>
    private static IRoslynConnectionFactory SelectFactory(
        bool smoke,
        ClaudeRoslynLspOptions options,
        ILogger logger,
        Action<int, string> tellClient)
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

        return new LaunchedRoslynFactory(options, AdapterPaths.Resolve(options), logger, tellClient);
    }

    /// <summary>
    /// Holds messages the factory produced before the session existed, then forwards them.
    /// </summary>
    /// <remarks>
    /// The ordering problem is real and small: the factory is constructed first because the session
    /// takes it as a constructor argument, but the factory's first act — resolving and possibly
    /// downloading Roslyn — happens after <c>initialized</c>, by which time the session is there. A
    /// buffer of a handful of lines removes the ordering question entirely rather than relying on
    /// that timing staying true.
    /// </remarks>
    private sealed class PendingClientMessages
    {
        private readonly Lock _lock = new();
        private readonly List<(int Type, string Message)> _buffered = [];
        private AdapterSession? _session;

        /// <summary>Sends a message, or holds it until there is a session.</summary>
        /// <param name="type">The <c>window/logMessage</c> severity.</param>
        /// <param name="message">The text.</param>
        internal void Send(int type, string message)
        {
            AdapterSession? session;

            lock (_lock)
            {
                session = _session;

                if (session is null)
                {
                    _buffered.Add((type, message));
                    return;
                }
            }

            session.TellClient(type, message);
        }

        /// <summary>Attaches the session and flushes whatever was buffered.</summary>
        /// <param name="session">The running session.</param>
        internal void Attach(AdapterSession session)
        {
            (int Type, string Message)[] buffered;

            lock (_lock)
            {
                _session = session;
                buffered = [.. _buffered];
                _buffered.Clear();
            }

            foreach (var (type, message) in buffered)
            {
                session.TellClient(type, message);
            }
        }
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 200,
            Level = LogLevel.Information,
            Message = "{Server} {Version} starting in LSP mode, backed by {Backend}.")]
        internal static partial void Starting(ILogger logger, string server, string version, string backend);

        [LoggerMessage(EventId = 201, Level = LogLevel.Information, Message = "Workspace ({Outcome}): {Explanation}")]
        internal static partial void Discovered(ILogger logger, string outcome, string explanation);

        [LoggerMessage(EventId = 202, Level = LogLevel.Debug, Message = "Candidate {Path} scored {Score}: {Reason}")]
        internal static partial void Candidate(ILogger logger, string path, int score, string reason);

        [LoggerMessage(
            EventId = 203,
            Level = LogLevel.Warning,
            Message = "Solution discovery could not read the workspace; continuing in misc-files mode.")]
        internal static partial void DiscoveryFailed(ILogger logger, Exception exception);
    }
}
