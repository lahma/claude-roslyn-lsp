namespace ClaudeRoslynLsp.Mcp.Engine;

/// <summary>
/// The project list <c>getWorkspaceStatus</c> reports, read off the solution rather than asked for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Roslyn is never asked, because there is nothing to ask (D78).</b> Its custom method list (C39)
/// has no "describe the workspace" request, and the only thing that names a project is a log line
/// that the default child log level does not emit (D37). Reading the solution file is the honest
/// alternative: it is exactly the set that was handed to <c>solution/open</c>, it costs one file
/// read plus one per project, and it is available before the load finishes — which is when a model
/// most wants to know what it is waiting for.
/// </para>
/// <para>
/// <b>The target frameworks are a text scan, and it is a best effort on purpose.</b> Evaluating a
/// project file properly means MSBuild, which hard rule 1 and the package budget both refuse; a
/// project that gets its <c>TargetFramework</c> from a <c>Directory.Build.props</c> or from a
/// condition therefore reports none. That is the same trade D41 already made for the candidate
/// score, and the same reasoning applies: an empty list reads as "not known", where a wrong list
/// would read as a fact. What Roslyn does report — how many projects it is loading, and which ones
/// finished — comes from <see cref="WorkspaceLoadTracker"/> and is layered on top.
/// </para>
/// </remarks>
internal static class WorkspaceProjects
{
    /// <summary>The most projects that will be listed, so a monorepo cannot fill a tool result.</summary>
    internal const int MaxProjects = 500;

    private static readonly string[] ProjectExtensions = [".csproj", ".vbproj", ".fsproj"];

    /// <summary>
    /// Lists the projects of a workspace: the solution's, when there is one, else the explicit set.
    /// </summary>
    /// <param name="solutionPath">The <c>.sln</c>/<c>.slnx</c>/<c>.slnf</c> that was opened, or null.</param>
    /// <param name="projectPaths">The project files that were opened, when there is no solution.</param>
    internal static IReadOnlyList<WorkspaceProject> Read(string? solutionPath, IReadOnlyList<string> projectPaths)
    {
        ArgumentNullException.ThrowIfNull(projectPaths);

        var paths = solutionPath is { Length: > 0 } solution
            ? ReadSolution(solution)
            : projectPaths;

        var projects = new List<WorkspaceProject>(Math.Min(paths.Count, MaxProjects));

        foreach (var path in paths.Take(MaxProjects))
        {
            projects.Add(new WorkspaceProject(
                Path.GetFileNameWithoutExtension(path),
                path,
                ReadTargetFrameworks(path)));
        }

        projects.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));

        return projects;
    }

    /// <summary>
    /// The project files a solution names, as absolute paths.
    /// </summary>
    /// <remarks>
    /// Counted by text for the same reason <c>SolutionDiscovery.CountProjects</c> counts by text
    /// (D41): <c>.sln</c> puts each project on a line beginning <c>Project("{</c> with the relative
    /// path as the second quoted field, and <c>.slnx</c> writes <c>&lt;Project Path="..." /&gt;</c>.
    /// Solution folders appear in the first form and are filtered by extension.
    /// </remarks>
    /// <param name="solutionPath">The solution file.</param>
    internal static IReadOnlyList<string> ReadSolution(string solutionPath)
    {
        ArgumentNullException.ThrowIfNull(solutionPath);

        string text;
        string directory;

        try
        {
            text = File.ReadAllText(solutionPath);
            directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? Environment.CurrentDirectory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            return [];
        }

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relative in ExtractPaths(text))
        {
            if (!ProjectExtensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string full;

            try
            {
                full = Path.GetFullPath(Path.Combine(directory, relative.Replace('\\', Path.DirectorySeparatorChar)));
            }
            catch (Exception exception) when (exception is ArgumentException or PathTooLongException
                                                  or NotSupportedException)
            {
                continue;
            }

            if (seen.Add(full))
            {
                found.Add(full);
            }
        }

        return found;
    }

    /// <summary>
    /// The target frameworks a project file states outright, or an empty list when it states none.
    /// </summary>
    /// <param name="projectPath">The project file.</param>
    internal static IReadOnlyList<string> ReadTargetFrameworks(string projectPath)
    {
        ArgumentNullException.ThrowIfNull(projectPath);

        string text;

        try
        {
            text = File.ReadAllText(projectPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            return [];
        }

        if (Element(text, "TargetFrameworks") is { Length: > 0 } many)
        {
            return [.. many.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        return Element(text, "TargetFramework") is { Length: > 0 } one ? [one] : [];
    }

    /// <summary>The text content of the first <c>&lt;name&gt;...&lt;/name&gt;</c> element.</summary>
    private static string? Element(string text, string name)
    {
        var open = "<" + name + ">";
        var close = "</" + name + ">";

        var start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);

        if (start < 0)
        {
            return null;
        }

        start += open.Length;
        var end = text.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);

        if (end <= start)
        {
            return null;
        }

        var value = text[start..end].Trim();

        // A value that is an MSBuild expression is not a target framework, it is a promise to
        // evaluate one, and this class does not evaluate.
        return value.Contains('$', StringComparison.Ordinal) ? null : value;
    }

    /// <summary>Every quoted path in a solution file, in both formats.</summary>
    private static IEnumerable<string> ExtractPaths(string text)
    {
        const string SlnxMarker = "<Project Path=";
        const string SlnMarker = "Project(\"{";

        if (text.Contains(SlnxMarker, StringComparison.Ordinal))
        {
            var index = 0;

            while ((index = text.IndexOf(SlnxMarker, index, StringComparison.Ordinal)) >= 0)
            {
                index += SlnxMarker.Length;

                if (QuotedAt(text, index) is { } path)
                {
                    yield return path;
                }
            }

            yield break;
        }

        var line = 0;

        while ((line = text.IndexOf(SlnMarker, line, StringComparison.Ordinal)) >= 0)
        {
            // `Project("{GUID}") = "Name", "Relative\Path.csproj", "{GUID}"` - the path is the
            // fourth quoted run, counting the type guid as the first.
            var cursor = line;
            string? path = null;

            for (var field = 0; field < 4; field++)
            {
                var quote = text.IndexOf('"', cursor);

                if (quote < 0)
                {
                    break;
                }

                path = QuotedAt(text, quote);
                cursor = text.IndexOf('"', quote + 1) + 1;

                if (cursor <= 0)
                {
                    break;
                }
            }

            line += SlnMarker.Length;

            if (path is { Length: > 0 })
            {
                yield return path;
            }
        }
    }

    /// <summary>The contents of the next double-quoted run at or after an index.</summary>
    private static string? QuotedAt(string text, int index)
    {
        var open = text.IndexOf('"', index);

        if (open < 0)
        {
            return null;
        }

        var close = text.IndexOf('"', open + 1);

        return close <= open ? null : text[(open + 1)..close];
    }
}
