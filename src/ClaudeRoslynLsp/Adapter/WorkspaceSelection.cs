namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// What the session decided to open, reduced to the three things the opener needs.
/// </summary>
/// <remarks>
/// <para>
/// The decision itself — an explicit setting, <c>.vscode/settings.json</c>'s
/// <c>dotnet.defaultSolution</c>, the scored walk, the project fallback (D39-D42) — belongs to
/// <c>Roslyn/SolutionDiscovery</c>, and this record is the only thing that crosses from there into
/// the mediation. Keeping the boundary this narrow is what lets the adapter's own tests drive a
/// session with a made-up workspace and no filesystem at all.
/// </para>
/// <para>
/// <see cref="Explanation"/> is not decoration. Which solution was opened, and why that one, is the
/// first question of every report about a repository with more than one — so it is logged to stderr
/// <em>and</em> sent to the client once, through the only channel a client renders.
/// </para>
/// </remarks>
/// <param name="SolutionPath">The <c>.sln</c>/<c>.slnx</c>/<c>.slnf</c> to open, or null.</param>
/// <param name="ProjectPaths">The project files to open when there is no solution.</param>
/// <param name="Explanation">One line saying what was chosen and why.</param>
/// <param name="Failed">
/// True when an explicit setting named something that is not there. Discovery deliberately does not
/// run after one (D39): opening a <em>different</em> solution and answering about it confidently is
/// the failure this project exists to remove.
/// </param>
internal sealed record WorkspaceSelection(
    string? SolutionPath,
    IReadOnlyList<string> ProjectPaths,
    string Explanation,
    bool Failed = false)
{
    /// <summary>Nothing was configured and nothing was found.</summary>
    internal static WorkspaceSelection None(string explanation) => new(null, [], explanation);

    /// <summary>
    /// The v1 shape: one configured path, which is a solution or a project depending on its
    /// extension.
    /// </summary>
    /// <remarks>
    /// Kept because it is what the adapter's own tests drive the session with, and because a caller
    /// that has a path and no discovery result should not have to fabricate one.
    /// </remarks>
    /// <param name="path">The configured path, or null.</param>
    internal static WorkspaceSelection FromPath(string? path)
    {
        if (path is not { Length: > 0 })
        {
            return None("no solution is configured");
        }

        return WorkspaceOpener.IsSolutionExtension(Path.GetExtension(path))
            ? new WorkspaceSelection(path, [], $"opening the configured solution {path}")
            : new WorkspaceSelection(null, [path], $"opening the configured project {path}");
    }

    /// <summary>Whether there is something for Roslyn to open.</summary>
    internal bool OpensSomething => SolutionPath is not null || ProjectPaths.Count > 0;
}
