using System.Text.Json;

using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>What opening the workspace turned into.</summary>
/// <param name="Notification">The <c>solution/open</c> or <c>project/open</c> to send, if any.</param>
/// <param name="Description">A short name for the workspace, used in the readiness notice.</param>
/// <param name="OpensSomething">
/// False when nothing is configured, which means there is nothing to wait for and the readiness gate
/// must open at once.
/// </param>
internal sealed record WorkspaceOpen(byte[]? Notification, string Description, bool OpensSomething);

/// <summary>
/// Turns "which solution" into Roslyn's custom <c>solution/open</c> or <c>project/open</c>.
/// </summary>
/// <remarks>
/// <para>
/// Roslyn's <c>--autoLoadProjects</c> keys off <c>workspaceFolders</c>, which Claude Code does not
/// send, so nothing loads unless the adapter says so explicitly. Both methods are Roslyn's own
/// additions to LSP (C39) and neither answers: they are notifications, and the acknowledgement is
/// <c>workspace/projectInitializationComplete</c> arriving 2.5-9 s later (C31).
/// </para>
/// <para>
/// <b>Nothing configured is a supported state, not an error.</b> With no solution Roslyn serves any
/// opened file in misc-files mode, where it compiles the file alone and reports diagnostics against
/// a project that is not there — IDE0005 on the usings the real project needs (C28). That is worse
/// than useless for diagnostics but perfectly serviceable for a syntax-level answer, so the adapter
/// says so in one clear log line and opens the gate immediately rather than holding requests for a
/// load that will never happen.
/// </para>
/// <para>
/// <b>The decision arrives through a delegate, and it is announced exactly once.</b> Which candidate
/// won and what it scored (D41) is the first question of any report about a repository with more
/// than one solution, so the explanation goes to stderr <em>and</em> to the client's
/// <c>window/logMessage</c> — which is the only channel Claude Code renders. Once, on the first
/// build, because a supervisor that relaunches Roslyn (D57) opens the same workspace again and a
/// second identical line would read as a second solution.
/// </para>
/// </remarks>
internal sealed partial class WorkspaceOpener
{
    private static readonly string[] SolutionExtensions = [".sln", ".slnx", ".slnf"];

    private readonly Func<WorkspaceSelection> _select;
    private readonly ILogger _logger;
    private readonly Action<string>? _announce;
    private int _announced;

    /// <summary>Creates an opener over a selection source.</summary>
    /// <param name="select">Returns what to open. Called once per backend start.</param>
    /// <param name="logger">The stderr log.</param>
    /// <param name="announce">
    /// Sends the chosen workspace to the client, or null when nothing should be sent. Invoked at
    /// most once for the life of the session.
    /// </param>
    internal WorkspaceOpener(Func<WorkspaceSelection> select, ILogger logger, Action<string>? announce = null)
    {
        ArgumentNullException.ThrowIfNull(select);
        ArgumentNullException.ThrowIfNull(logger);

        _select = select;
        _logger = logger;
        _announce = announce;
    }

    /// <summary>Creates an opener over a single configured path.</summary>
    /// <param name="resolvePath">Returns the configured solution or project path, or null.</param>
    /// <param name="logger">The stderr log.</param>
    internal WorkspaceOpener(Func<string?> resolvePath, ILogger logger)
        : this(BuildSelector(resolvePath), logger)
    {
    }

    /// <summary>Whether an extension names a solution rather than a project.</summary>
    /// <param name="extension">The extension, with its dot.</param>
    internal static bool IsSolutionExtension(string? extension) =>
        extension is not null && SolutionExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the notification that opens the configured workspace.</summary>
    internal WorkspaceOpen Build()
    {
        var selection = _select();

        Announce(selection.Explanation, selection.Failed);

        if (!selection.OpensSomething)
        {
            Log.NothingConfigured(_logger, selection.Explanation);
            return new WorkspaceOpen(null, "no solution", OpensSomething: false);
        }

        if (selection.SolutionPath is { Length: > 0 } solution)
        {
            return BuildSolution(solution);
        }

        return BuildProjects(selection.ProjectPaths);
    }

    /// <summary>Renders <c>solution/open</c> for one solution file.</summary>
    private WorkspaceOpen BuildSolution(string path)
    {
        if (!TryFullPath(path, out var fullPath))
        {
            return new WorkspaceOpen(null, path, OpensSomething: false);
        }

        Log.OpeningSolution(_logger, fullPath);

        return new WorkspaceOpen(
            JsonSerializer.SerializeToUtf8Bytes(
                new SolutionOpenNotification
                {
                    Params = new SolutionOpenParams { Solution = new Uri(fullPath).AbsoluteUri },
                },
                LspJsonContext.Default.SolutionOpenNotification),
            Path.GetFileName(fullPath),
            OpensSomething: true);
    }

    /// <summary>Renders one <c>project/open</c> carrying every project file.</summary>
    /// <remarks>
    /// One notification for the whole set, not one each: Roslyn treats each <c>project/open</c> as a
    /// separate load and would report <c>projectInitializationComplete</c> after the first one,
    /// opening the readiness gate on a workspace that is a fraction of the way loaded.
    /// </remarks>
    private WorkspaceOpen BuildProjects(IReadOnlyList<string> paths)
    {
        var uris = new List<string>(paths.Count);

        foreach (var path in paths)
        {
            if (TryFullPath(path, out var fullPath))
            {
                uris.Add(new Uri(fullPath).AbsoluteUri);
            }
        }

        if (uris.Count == 0)
        {
            return new WorkspaceOpen(null, "no solution", OpensSomething: false);
        }

        Log.OpeningProjects(_logger, uris.Count);

        return new WorkspaceOpen(
            JsonSerializer.SerializeToUtf8Bytes(
                new ProjectOpenNotification { Params = new ProjectOpenParams { Projects = uris } },
                LspJsonContext.Default.ProjectOpenNotification),
            uris.Count == 1 ? Path.GetFileName(paths[0]) : $"{uris.Count} projects",
            OpensSomething: true);
    }

    /// <summary>Tells the client, once, which workspace this session is answering about.</summary>
    private void Announce(string explanation, bool failed)
    {
        if (_announce is null || Interlocked.Exchange(ref _announced, 1) != 0)
        {
            return;
        }

        _announce(explanation);

        if (failed)
        {
            Log.SelectionFailed(_logger, explanation);
        }
    }

    /// <summary>
    /// Expands a path, treating an unusable one as "nothing to open" rather than as a crash.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="File.Exists(string)"/>: a path that is merely absent is still
    /// worth sending, because Roslyn's own error for it names the file and reaches the user's log,
    /// whereas a silent refusal here would look exactly like a solution that loaded and turned out
    /// to be empty.
    /// </remarks>
    private bool TryFullPath(string path, out string fullPath)
    {
        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException or IOException)
        {
            Log.Unusable(_logger, path, exception.Message);
            fullPath = string.Empty;
            return false;
        }
    }

    /// <summary>Adapts the single-path constructor onto the selection one.</summary>
    private static Func<WorkspaceSelection> BuildSelector(Func<string?> resolvePath)
    {
        ArgumentNullException.ThrowIfNull(resolvePath);
        return () => WorkspaceSelection.FromPath(resolvePath());
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 900,
            Level = LogLevel.Warning,
            Message = "No solution will be opened ({Reason}), so Roslyn will serve opened files in " +
                      "misc-files mode: navigation works within a file, and diagnostics will be wrong " +
                      "because the file belongs to no project. Set CLAUDE_ROSLYN_LSP_SOLUTION to the " +
                      ".sln, .slnx or .csproj to load.")]
        internal static partial void NothingConfigured(ILogger logger, string reason);

        [LoggerMessage(EventId = 901, Level = LogLevel.Information, Message = "Opening solution {Path}.")]
        internal static partial void OpeningSolution(ILogger logger, string path);

        [LoggerMessage(EventId = 902, Level = LogLevel.Information, Message = "Opening {Count} project(s).")]
        internal static partial void OpeningProjects(ILogger logger, int count);

        [LoggerMessage(
            EventId = 903,
            Level = LogLevel.Error,
            Message = "The configured solution path '{Path}' cannot be used ({Reason}); continuing in " +
                      "misc-files mode.")]
        internal static partial void Unusable(ILogger logger, string path, string reason);

        [LoggerMessage(
            EventId = 904,
            Level = LogLevel.Error,
            Message = "The workspace could not be chosen: {Reason}")]
        internal static partial void SelectionFailed(ILogger logger, string reason);
    }
}
