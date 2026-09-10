using System.ComponentModel;

using ClaudeRoslynLsp.Mcp.Models;

using ModelContextProtocol.Server;

namespace ClaudeRoslynLsp.Mcp.Tools;

/// <summary>
/// The tool that answers "is this thing working, and what is it looking at".
/// </summary>
/// <remarks>
/// Its own class because it is the one tool that is <em>useful</em> when the workspace is not ready:
/// every other tool answers a not-ready call with a status object and a note, and this is the tool
/// that note tells the caller to call.
/// </remarks>
[McpServerToolType]
internal sealed class WorkspaceTools
{
    private WorkspaceTools()
    {
    }

    /// <summary>Reports what solution is open, how far it has loaded, and what it cost.</summary>
    /// <param name="context">The injected tool context.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    [McpServerTool(
        Name = "getWorkspaceStatus",
        Title = "Get workspace status",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Reports which solution this server has open, how many of its projects have loaded, any load errors, "
        + "the Roslyn build in use, and the language server's process id and memory. Call this first when another "
        + "tool answers with status 'loading' or 'failed', and when a repository has more than one solution and you "
        + "need to know which one the answers are about. A large solution takes a few seconds to load and can take "
        + "up to two minutes; until it has, every other tool reports 'loading' rather than answering emptily.")]
    public static async Task<WorkspaceStatusResult> GetWorkspaceStatusAsync(
        RoslynToolContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await ToolErrors.ExecuteAsync("getWorkspaceStatus", async () =>
        {
            var state = await context.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

            var solution = state.SolutionPath is { } path ? context.RelativeFromPath(path) : null;

            var projects = state.Projects?
                .Select(project => new ProjectSummary(
                    project.Name,
                    context.RelativeFromPath(project.Path),
                    project.TargetFrameworks))
                .ToArray();

            if (!state.IsReady)
            {
                return NotReadyResults.Workspace(state, solution, projects);
            }

            return new WorkspaceStatusResult(
                ToolStatus.Ok,
                solution,
                state.ProjectsLoaded,
                state.ProjectsTotal,
                projects,
                state.LoadErrors is { Count: > 0 } errors ? errors : null,
                state.RoslynVersion,
                state.ProcessId,
                state.WorkingSetBytes is { } bytes ? (int) (bytes / 1024 / 1024) : null,
                state.LoadErrors is { Count: > 0 }
                    ? "Some projects did not load. Symbols in them will be missing from every answer; "
                        + "a `dotnet restore` or a build often fixes it."
                    : null);
        }).ConfigureAwait(false);
    }
}
