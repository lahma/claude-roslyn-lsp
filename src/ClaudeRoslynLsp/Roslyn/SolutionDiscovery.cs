using System.Text.Json;

namespace ClaudeRoslynLsp.Roslyn;

/// <summary>How the workspace to open was decided.</summary>
internal enum SolutionDiscoveryOutcome
{
    /// <summary>Nothing was found; Roslyn will run in misc-files mode.</summary>
    None,

    /// <summary><c>CLAUDE_ROSLYN_LSP_SOLUTION</c> named it.</summary>
    Explicit,

    /// <summary><c>.vscode/settings.json</c>'s <c>dotnet.defaultSolution</c> named it.</summary>
    DefaultSolutionSetting,

    /// <summary><c>dotnet.defaultSolution</c> is <c>disable</c>: no solution, on purpose.</summary>
    Disabled,

    /// <summary>One solution file won the scoring.</summary>
    Scored,

    /// <summary>No solution file exists, so the projects are opened directly.</summary>
    ProjectsOnly,

    /// <summary>An explicit setting pointed at something that is not there.</summary>
    Failed,
}

/// <summary>One solution file that was considered, with the arithmetic that ranked it.</summary>
/// <param name="Path">The absolute path.</param>
/// <param name="Score">Its total score; higher wins.</param>
/// <param name="ProjectCount">How many projects it lists.</param>
/// <param name="Depth">How many directories below the root it sits.</param>
/// <param name="Reason">The score, broken down, for <c>doctor</c> and the log.</param>
internal sealed record SolutionCandidate(string Path, int Score, int ProjectCount, int Depth, string Reason);

/// <summary>What discovery decided, and everything it considered while deciding it.</summary>
internal sealed record SolutionDiscoveryResult
{
    /// <summary>How the decision was reached.</summary>
    internal required SolutionDiscoveryOutcome Outcome { get; init; }

    /// <summary>The directory the search started from.</summary>
    internal required string Root { get; init; }

    /// <summary>The chosen <c>.slnx</c>/<c>.sln</c>, or <see langword="null"/>.</summary>
    internal string? SolutionPath { get; init; }

    /// <summary>The projects to open when there is no solution, or when one was named explicitly.</summary>
    internal IReadOnlyList<string> ProjectPaths { get; init; } = [];

    /// <summary>Every solution file that was considered, best first.</summary>
    internal IReadOnlyList<SolutionCandidate> Candidates { get; init; } = [];

    /// <summary>Why nothing could be opened, or <see langword="null"/>.</summary>
    internal string? Failure { get; init; }

    /// <summary>One line saying what happened, for the log and for <c>doctor</c>.</summary>
    internal required string Explanation { get; init; }

    /// <summary>Whether there is something for Roslyn to open.</summary>
    internal bool HasWorkspace => SolutionPath is not null || ProjectPaths.Count > 0;
}

/// <summary>
/// Decides which solution or projects to open, from an explicit setting, from the editor
/// configuration that is probably already in the repository, or by looking.
/// </summary>
/// <remarks>
/// <para>
/// <b>D39 — an explicit setting that does not resolve is a failure, and discovery never runs after
/// one.</b> Both <c>CLAUDE_ROSLYN_LSP_SOLUTION</c> and <c>.vscode/settings.json</c>'s
/// <c>dotnet.defaultSolution</c> are statements of intent. Falling back to a scan when one of them
/// points at a moved file would produce an adapter that opens a <em>different</em> solution and then
/// answers questions about it with total confidence — the failure mode this whole project exists to
/// remove, reintroduced as a convenience.
/// </para>
/// <para>
/// <b>D40 — <c>dotnet.defaultSolution</c> is honoured, because it is already there.</b> Any repository
/// that has been opened in VS Code with the C# extension has been asked this question and has answered
/// it, and the answer is checked in. Reading it costs one file and removes the most common reason
/// anybody would have to set <c>CLAUDE_ROSLYN_LSP_SOLUTION</c> at all. The sentinel <c>disable</c> is
/// honoured too: a repository that told its editor not to load a solution meant it.
/// </para>
/// <para>
/// <b>D41 — scoring, not "the first one found".</b> Name matching the root folder is worth more than
/// everything else combined, because that is what a repository's own solution is called;
/// <c>.slnx</c> beats <c>.sln</c> because a repository carrying both is migrating and the new one is
/// the answer; project count breaks the remaining ties towards the solution that covers more of the
/// tree. Shallower wins before alphabetical order, because a solution at the root is a repository's
/// solution and one three directories down is a sample.
/// </para>
/// <para>
/// <b>D42 — the project fallback keeps every candidate, including tests.</b> An earlier sketch trimmed
/// test projects to save load time. That is exactly backwards for this product: an agent asked to fix
/// a failing test needs the test project loaded, and a symbol that resolves everywhere except in tests
/// is a worse experience than a slower load. The cap of 500 is a guard against opening a monorepo by
/// accident, not a curation policy.
/// </para>
/// </remarks>
internal static class SolutionDiscovery
{
    /// <summary>How many projects the fallback will open before giving up on the idea.</summary>
    internal const int MaxProjectCandidates = 500;

    /// <summary>How deep the solution scan goes. The root is depth 0.</summary>
    internal const int SolutionSearchDepth = 3;

    /// <summary>How deep the project fallback goes — deeper, because <c>src/a/b/C/C.csproj</c> is normal.</summary>
    internal const int ProjectSearchDepth = 8;

    /// <summary>The VS Code setting that names a repository's solution.</summary>
    internal const string DefaultSolutionSetting = "dotnet.defaultSolution";

    /// <summary>The value of <see cref="DefaultSolutionSetting"/> that means "do not load one".</summary>
    internal const string DefaultSolutionDisabled = "disable";

    /// <summary>
    /// Directories the scan never descends into.
    /// </summary>
    /// <remarks>
    /// <c>artifacts/</c> is on the list because the .NET SDK's artifacts output layout copies project
    /// files into it, and a scan that finds them ranks a build output as a candidate workspace.
    /// </remarks>
    internal static readonly string[] SkippedDirectories =
        ["bin", "obj", ".git", "node_modules", ".vs", "artifacts", "TestResults"];

    /// <summary>Runs the whole decision for a directory.</summary>
    /// <param name="root">The workspace root — normally the process's working directory.</param>
    /// <param name="explicitPath">The value of <c>CLAUDE_ROSLYN_LSP_SOLUTION</c>, or null.</param>
    /// <param name="explicitVariable">Which variable supplied it, for the failure message.</param>
    internal static SolutionDiscoveryResult Discover(
        string root,
        string? explicitPath = null,
        string? explicitVariable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var fullRoot = Path.GetFullPath(root);

        if (explicitPath is not null)
        {
            return FromExplicitPath(
                fullRoot,
                explicitPath,
                explicitVariable ?? "CLAUDE_ROSLYN_LSP_SOLUTION",
                SolutionDiscoveryOutcome.Explicit);
        }

        if (ReadDefaultSolution(fullRoot) is { } configured)
        {
            if (string.Equals(configured, DefaultSolutionDisabled, StringComparison.OrdinalIgnoreCase))
            {
                return new SolutionDiscoveryResult
                {
                    Outcome = SolutionDiscoveryOutcome.Disabled,
                    Root = fullRoot,
                    Explanation =
                        $"{DefaultSolutionSetting} is '{DefaultSolutionDisabled}' in .vscode/settings.json, so no " +
                        "solution is opened.",
                };
            }

            return FromExplicitPath(
                fullRoot,
                configured,
                $".vscode/settings.json ({DefaultSolutionSetting})",
                SolutionDiscoveryOutcome.DefaultSolutionSetting);
        }

        var candidates = ScoreCandidates(fullRoot);

        if (candidates.Count > 0)
        {
            var winner = candidates[0];

            return new SolutionDiscoveryResult
            {
                Outcome = SolutionDiscoveryOutcome.Scored,
                Root = fullRoot,
                SolutionPath = winner.Path,
                Candidates = candidates,
                Explanation = candidates.Count == 1
                    ? $"Found one solution: {winner.Path}."
                    : $"Chose {winner.Path} (score {winner.Score}) from {candidates.Count} solutions.",
            };
        }

        var projects = FindProjects(fullRoot);

        if (projects.Count > 0)
        {
            return new SolutionDiscoveryResult
            {
                Outcome = SolutionDiscoveryOutcome.ProjectsOnly,
                Root = fullRoot,
                ProjectPaths = projects,
                Explanation = projects.Count >= MaxProjectCandidates
                    ? $"No solution found; opening the first {MaxProjectCandidates} projects under {fullRoot}. " +
                      "Set CLAUDE_ROSLYN_LSP_SOLUTION to something smaller."
                    : $"No solution found; opening {projects.Count} project(s) under {fullRoot}.",
            };
        }

        return new SolutionDiscoveryResult
        {
            Outcome = SolutionDiscoveryOutcome.None,
            Root = fullRoot,
            Explanation =
                $"No .slnx, .sln or .csproj was found under {fullRoot}. Roslyn will run in misc-files mode, where " +
                "answers come from single files with no project context.",
        };
    }

    /// <summary>
    /// Reads <c>dotnet.defaultSolution</c> from <c>.vscode/settings.json</c> in the root.
    /// </summary>
    /// <remarks>
    /// Parsed as JSON with comments, because that file is JSONC in practice — VS Code writes comments
    /// into it and users leave them there, and a strict parser would report a broken settings file as
    /// "no setting", which is a silent wrong answer. Any parse failure at all is treated as absence:
    /// this is somebody else's file and this adapter has no standing to fail a startup over its
    /// syntax.
    /// </remarks>
    /// <param name="root">The workspace root.</param>
    /// <returns>The setting's value, trimmed, or <see langword="null"/>.</returns>
    internal static string? ReadDefaultSolution(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var path = Path.Combine(root, ".vscode", "settings.json");

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(DefaultSolutionSetting, out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = value.GetString();

            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Counts the projects a solution file lists.
    /// </summary>
    /// <remarks>
    /// By text, not by a solution parser, and that is the whole point: the package budget has no room
    /// for <c>Microsoft.Build</c>, the number is only ever used as a tie-break, and being wrong by one
    /// changes nothing. <c>.sln</c> lists each project on a line beginning <c>Project("{</c>;
    /// <c>.slnx</c> lists them as <c>&lt;Project Path=</c> elements. Solution folders inflate the
    /// <c>.sln</c> count slightly — they use the same keyword — which is accepted for the same reason.
    /// </remarks>
    /// <param name="solutionPath">The solution file.</param>
    internal static int CountProjects(string solutionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);

        string text;

        try
        {
            text = File.ReadAllText(solutionPath);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }

        if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return Count(text, "<Project Path=");
        }

        var projects = 0;

        foreach (var line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith("Project(\"{", StringComparison.Ordinal))
            {
                projects++;
            }
        }

        return projects;
    }

    /// <summary>Scores one solution file. See D41 in the type remarks for the weights.</summary>
    /// <param name="solutionPath">The solution file.</param>
    /// <param name="rootName">The root directory's own name.</param>
    /// <param name="projectCount">How many projects it lists.</param>
    /// <param name="reason">The breakdown, for the report.</param>
    internal static int Score(string solutionPath, string rootName, int projectCount, out string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);

        var name = Path.GetFileNameWithoutExtension(solutionPath);
        var parts = new List<string>();
        var score = 0;

        if (string.Equals(name, rootName, StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
            parts.Add("+100 name matches the folder");
        }

        if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
            parts.Add("+10 .slnx");
        }

        score += projectCount;
        parts.Add($"+{projectCount} project(s)");

        reason = string.Join(", ", parts);
        return score;
    }

    /// <summary>Finds and ranks every solution file within <see cref="SolutionSearchDepth"/>.</summary>
    /// <param name="root">The fully-qualified workspace root.</param>
    internal static IReadOnlyList<SolutionCandidate> ScoreCandidates(string root)
    {
        var rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var candidates = new List<SolutionCandidate>();

        foreach (var (file, depth) in Walk(root, SolutionSearchDepth, ["*.slnx", "*.sln"], int.MaxValue))
        {
            var projects = CountProjects(file);
            var score = Score(file, rootName, projects, out var reason);
            candidates.Add(new SolutionCandidate(file, score, projects, depth, reason));
        }

        candidates.Sort(static (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);

            if (byScore != 0)
            {
                return byScore;
            }

            var byDepth = left.Depth.CompareTo(right.Depth);

            return byDepth != 0 ? byDepth : string.CompareOrdinal(left.Path, right.Path);
        });

        return candidates;
    }

    /// <summary>Finds every project file, capped at <see cref="MaxProjectCandidates"/>.</summary>
    /// <param name="root">The fully-qualified workspace root.</param>
    internal static IReadOnlyList<string> FindProjects(string root)
    {
        var projects = Walk(root, ProjectSearchDepth, ["*.csproj"], MaxProjectCandidates)
            .Select(static found => found.Path)
            .ToList();

        projects.Sort(StringComparer.Ordinal);

        return projects;
    }

    private static SolutionDiscoveryResult FromExplicitPath(
        string root,
        string configured,
        string source,
        SolutionDiscoveryOutcome outcome)
    {
        string full;

        try
        {
            full = Path.IsPathRooted(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(Path.Combine(root, configured));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new SolutionDiscoveryResult
            {
                Outcome = SolutionDiscoveryOutcome.Failed,
                Root = root,
                Failure = $"{source} is '{configured}', which is not a usable path: {exception.Message}",
                Explanation = $"{source} could not be resolved.",
            };
        }

        if (!File.Exists(full))
        {
            return new SolutionDiscoveryResult
            {
                Outcome = SolutionDiscoveryOutcome.Failed,
                Root = root,
                Failure =
                    $"{source} points at '{full}', which does not exist. Discovery is deliberately not attempted " +
                    "after an explicit setting: opening a different solution and answering confidently about it is " +
                    "worse than not starting.",
                Explanation = $"{source} points at a file that is not there.",
            };
        }

        var isProject = full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

        return new SolutionDiscoveryResult
        {
            Outcome = outcome,
            Root = root,
            SolutionPath = isProject ? null : full,
            ProjectPaths = isProject ? [full] : [],
            Explanation = $"{source} selected {full}.",
        };
    }

    /// <summary>
    /// Breadth-first walk to a bounded depth, skipping the directories in
    /// <see cref="SkippedDirectories"/>.
    /// </summary>
    /// <remarks>
    /// Breadth-first rather than <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>
    /// for two reasons: the recursive overload has no way to skip a directory, so a
    /// <c>node_modules</c> in a mixed repository is walked in full before it is filtered; and the
    /// depth bound is what stops a discovery run in a home directory from becoming a filesystem scan.
    /// </remarks>
    private static IEnumerable<(string Path, int Depth)> Walk(
        string root,
        int maxDepth,
        string[] patterns,
        int limit)
    {
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));
        var found = 0;

        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();

            string[] files;
            string[] children;

            try
            {
                files = patterns
                    .SelectMany(pattern => Directory.GetFiles(directory, pattern))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                children = depth < maxDepth ? Directory.GetDirectories(directory) : [];
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(files, StringComparer.Ordinal);

            foreach (var file in files)
            {
                yield return (file, depth);

                if (++found >= limit)
                {
                    yield break;
                }
            }

            Array.Sort(children, StringComparer.Ordinal);

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);

                if (!SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(value, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
