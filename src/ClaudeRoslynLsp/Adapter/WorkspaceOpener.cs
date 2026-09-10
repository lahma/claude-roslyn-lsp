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
/// The path arrives through a delegate rather than being discovered here. Solution discovery —
/// scoring candidates, the depth-3 walk, the 500-project cap — is WP3's, and this class is the seam
/// it plugs into: v1 reads <c>CLAUDE_ROSLYN_LSP_SOLUTION</c> and nothing else.
/// </para>
/// </remarks>
internal sealed partial class WorkspaceOpener
{
    private static readonly string[] SolutionExtensions = [".sln", ".slnx", ".slnf"];

    private readonly Func<string?> _resolvePath;
    private readonly ILogger _logger;

    /// <summary>Creates an opener over a path source.</summary>
    /// <param name="resolvePath">
    /// Returns the configured solution or project path, or null when none is configured. WP3
    /// replaces the environment-variable implementation with real discovery.
    /// </param>
    /// <param name="logger">The stderr log.</param>
    internal WorkspaceOpener(Func<string?> resolvePath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(resolvePath);
        ArgumentNullException.ThrowIfNull(logger);

        _resolvePath = resolvePath;
        _logger = logger;
    }

    /// <summary>Builds the notification that opens the configured workspace.</summary>
    internal WorkspaceOpen Build()
    {
        var path = _resolvePath();

        if (path is not { Length: > 0 })
        {
            Log.NothingConfigured(_logger);
            return new WorkspaceOpen(null, "no solution", OpensSomething: false);
        }

        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException or IOException)
        {
            Log.Unusable(_logger, path, exception.Message);
            return new WorkspaceOpen(null, path, OpensSomething: false);
        }

        // Not File.Exists: a path that is merely absent is still worth sending, because Roslyn's own
        // error for it names the file and reaches the user's log, whereas a silent refusal here
        // would look exactly like a solution that loaded and turned out to be empty.
        var extension = Path.GetExtension(fullPath);
        var name = Path.GetFileName(fullPath);
        var uri = new Uri(fullPath).AbsoluteUri;

        if (SolutionExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            Log.OpeningSolution(_logger, fullPath);

            return new WorkspaceOpen(
                JsonSerializer.SerializeToUtf8Bytes(
                    new SolutionOpenNotification { Params = new SolutionOpenParams { Solution = uri } },
                    LspJsonContext.Default.SolutionOpenNotification),
                name,
                OpensSomething: true);
        }

        Log.OpeningProject(_logger, fullPath);

        return new WorkspaceOpen(
            JsonSerializer.SerializeToUtf8Bytes(
                new ProjectOpenNotification { Params = new ProjectOpenParams { Projects = [uri] } },
                LspJsonContext.Default.ProjectOpenNotification),
            name,
            OpensSomething: true);
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 900,
            Level = LogLevel.Warning,
            Message = "No solution is configured, so Roslyn will serve opened files in misc-files mode: " +
                      "navigation works within a file, and diagnostics will be wrong because the file " +
                      "belongs to no project. Set CLAUDE_ROSLYN_LSP_SOLUTION to the .sln, .slnx or .csproj " +
                      "to load.")]
        internal static partial void NothingConfigured(ILogger logger);

        [LoggerMessage(EventId = 901, Level = LogLevel.Information, Message = "Opening solution {Path}.")]
        internal static partial void OpeningSolution(ILogger logger, string path);

        [LoggerMessage(EventId = 902, Level = LogLevel.Information, Message = "Opening project {Path}.")]
        internal static partial void OpeningProject(ILogger logger, string path);

        [LoggerMessage(
            EventId = 903,
            Level = LogLevel.Error,
            Message = "The configured solution path '{Path}' cannot be used ({Reason}); continuing in " +
                      "misc-files mode.")]
        internal static partial void Unusable(ILogger logger, string path, string reason);
    }
}
