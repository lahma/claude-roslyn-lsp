using System.ComponentModel;

using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Mcp.Models;

using ModelContextProtocol.Server;

namespace ClaudeRoslynLsp.Mcp.Tools;

/// <summary>
/// The on-demand diagnostics tool: what an IDE would be showing, in about a second, without a build.
/// </summary>
[McpServerToolType]
internal sealed class DiagnosticTools
{
    /// <summary>How many diagnostics one call reports by default.</summary>
    private const int DefaultMaxResults = 100;

    private DiagnosticTools()
    {
    }

    /// <summary>Reports diagnostics for a file, a project or the solution.</summary>
    /// <param name="context">The injected tool context.</param>
    /// <param name="scope">file, project or solution.</param>
    /// <param name="path">The file, for file scope.</param>
    /// <param name="project">The project, for project scope.</param>
    /// <param name="minSeverity">The severity floor.</param>
    /// <param name="includeAnalyzers">Whether to include IDE/CA style and quality rules.</param>
    /// <param name="ids">Only report these diagnostic ids.</param>
    /// <param name="maxResults">The cap.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    [McpServerTool(
        Name = "getDiagnostics",
        Title = "Get diagnostics",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Reports C# compiler errors and warnings — and, on request, IDE and CA analyzer diagnostics — for one file, "
        + "one project or the whole solution, with 1-based positions. Call this after editing C# instead of running "
        + "`dotnet build` to see whether it still compiles: a Roslyn design-time pass takes about a second where a "
        + "build takes ten to sixty. It is not a build, though — it does not run source generators the way a build "
        + "does, has no MSBuild errors in it and runs no tests — so run the real build before you claim the solution "
        + "is green. Defaults to compiler diagnostics at warning and above; set includeAnalyzers to see IDE0005, "
        + "CA1822 and their kind.")]
    public static async Task<DiagnosticsResult> GetDiagnosticsAsync(
        RoslynToolContext context,
        [Description("How much to look at: file (default, needs path), project (needs project, or path to infer it), or solution.")]
        string? scope = null,
        [Description("The file to check, workspace-relative with forward slashes. Required for scope: file.")]
        string? path = null,
        [Description("The project to check, by name (Quartz.Core) or by workspace-relative .csproj path. Required for scope: project unless path is given.")]
        string? project = null,
        [Description("The lowest severity to report: error, warning (default), information or hint.")]
        string? minSeverity = null,
        [Description("Include analyzer diagnostics (IDE*, CA*, and any analyzers the project references) alongside compiler ones. Default false, because they are numerous and rarely what you are checking after an edit.")]
        bool includeAnalyzers = false,
        [Description("Only report these diagnostic ids, e.g. [\"CS0029\", \"IDE0005\"]. Setting this overrides includeAnalyzers.")]
        string[]? ids = null,
        [Description("How many diagnostics to report. Default 100.")]
        int? maxResults = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await ToolErrors.ExecuteAsync("getDiagnostics", async () =>
        {
            var requested = DiagnosticQuery.ParseScope(scope);
            var scopeName = requested.ToString().ToLowerInvariant();

            var state = await context.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

            if (!state.IsReady)
            {
                return NotReadyResults.Diagnostics(state, scopeName);
            }

            IReadOnlyList<(string Path, RawDiagnostic Diagnostic)> pulled;
            string note;

            if (requested == DiagnosticScope.File)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new ArgumentException("scope: \"file\" needs a path", nameof(path));
                }

                pulled = await DiagnosticQuery
                    .ForFileAsync(context, context.Guard.FromModelPath(path), cancellationToken)
                    .ConfigureAwait(false);

                note = DiagnosticQuery.DesignTimeNote;
            }
            else
            {
                var directory = requested == DiagnosticScope.Project
                    ? ProjectDirectory(context, state, project, path)
                    : null;

                // An explicit `ids` list overrides includeAnalyzers (D67), so it also decides whether
                // the analyzer scope has to be raised: `ids: ["IDE0005"]` is a caller asking for an
                // analyzer diagnostic by name, and D82 is what makes a closed file answer for one.
                pulled = await DiagnosticQuery
                    .ForWorkspaceAsync(
                        context,
                        directory,
                        includeAnalyzers || ids is { Length: > 0 },
                        cancellationToken)
                    .ConfigureAwait(false);

                note = DiagnosticQuery.DesignTimeNote + DiagnosticQuery.OpenDocumentsNote;
            }

            var (entries, total, counts) = DiagnosticQuery.Reduce(
                pulled,
                minSeverity,
                includeAnalyzers,
                ids,
                Math.Clamp(maxResults ?? DefaultMaxResults, 1, 1000));

            return new DiagnosticsResult(
                ToolStatus.Ok,
                scopeName,
                total,
                counts,
                entries,
                total > entries.Count,
                note);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the directory of the project a query is scoped to: the one named, or the one that owns
    /// the file that was given.
    /// </summary>
    /// <remarks>
    /// Inferring from <c>path</c> is not a convenience feature. The commonest project-scoped question
    /// is "did my change break anything else in this project", which a model asks holding a file
    /// path and not a project name — and requiring it to find the name first is another reason to
    /// reach for a directory listing.
    /// </remarks>
    private static string ProjectDirectory(
        RoslynToolContext context,
        WorkspaceState state,
        string? project,
        string? path)
    {
        var projects = state.Projects ?? [];

        if (!string.IsNullOrWhiteSpace(project))
        {
            var wanted = project.Trim();

            var match = projects.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, wanted, StringComparison.OrdinalIgnoreCase))
                ?? projects.FirstOrDefault(candidate =>
                    context.RelativeFromPath(candidate.Path)
                        .EndsWith(wanted.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                ?? throw ToolErrors.Ambiguous(
                    "getDiagnostics",
                    $"project '{wanted}'",
                    projects.Select(candidate => candidate.Name));

            return DirectoryOf(match.Path);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "scope: \"project\" needs either a project name or a path inside the project",
                nameof(project));
        }

        var full = context.Guard.FromModelPath(path);

        var owner = projects
            .Select(candidate => DirectoryOf(candidate.Path))
            .Where(directory => full.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(directory => directory.Length)
            .FirstOrDefault();

        return owner ?? throw ToolErrors.NotFound("getDiagnostics", $"project owning '{path}'");
    }

    private static string DirectoryOf(string projectPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? projectPath;
        return directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
    }
}
