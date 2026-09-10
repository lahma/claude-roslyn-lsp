using System.Globalization;

using ClaudeRoslynLsp.Mcp.Engine;

namespace ClaudeRoslynLsp.Mcp.Models;

/// <summary>
/// The <c>status</c> every tool result carries, and the sentence that goes with it when the answer
/// is not a real one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a status rather than an error (D66).</b> A solution takes 2.5-9 seconds to load and can
/// take two minutes on a large one (C31), and Roslyn answers anything asked before then with an
/// <em>empty successful result</em> (C27). Three behaviours were available: block until ready — which
/// is what the LSP half does, because a language client has nowhere else to be; fail — which teaches
/// a model that the server is broken; or answer with a status. The MCP half answers with a status,
/// because an MCP call is a turn of a conversation: a model that is told "still loading, 3 of 8
/// projects" can do something else and come back, and a model that is told nothing for two minutes
/// cannot.
/// </para>
/// <para>
/// Every result record therefore has the same two members in the same place, and a caller can read
/// <c>status</c> before it reads anything else.
/// </para>
/// </remarks>
internal static class ToolStatus
{
    /// <summary>The answer is real.</summary>
    internal const string Ok = "ok";

    /// <summary>The workspace is still loading; nothing was asked of Roslyn.</summary>
    internal const string Loading = "loading";

    /// <summary>The backend could not be reached at all.</summary>
    internal const string Failed = "failed";

    /// <summary>The status a workspace state maps to.</summary>
    /// <param name="state">The state <c>EnsureReadyAsync</c> reported.</param>
    internal static string For(WorkspaceState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.Status switch
        {
            WorkspaceLoadStatus.Ready => Ok,
            WorkspaceLoadStatus.Failed => Failed,
            _ => Loading,
        };
    }

    /// <summary>
    /// What to tell a caller whose question could not be asked, in terms of what it should do next.
    /// </summary>
    /// <param name="state">The state <c>EnsureReadyAsync</c> reported.</param>
    internal static string NoteFor(WorkspaceState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Status == WorkspaceLoadStatus.Failed)
        {
            return state.Message
                ?? "The Roslyn backend could not be started. Run `claude-roslyn-lsp doctor` to find out why.";
        }

        var progress = state.ProjectsTotal > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $" ({state.ProjectsLoaded} of {state.ProjectsTotal} projects loaded)")
            : string.Empty;

        return "The workspace is still loading" + progress
            + ". Nothing was asked of Roslyn, because a question asked now comes back empty rather than wrong-looking. "
            + "Call getWorkspaceStatus or retry in a few seconds.";
    }
}
