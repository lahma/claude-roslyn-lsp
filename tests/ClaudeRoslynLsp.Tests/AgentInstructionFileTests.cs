using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests;

/// <summary>
/// The agent instruction files — the root <c>AGENTS.md</c> and the co-located ones it indexes — held
/// to the size budget the root file states, to the routing that makes the co-located ones reachable,
/// and to the decision table that says where each numbered decision is recorded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a test.</b> Both ways of crossing the budget are silent for the reader who matters. OpenAI
/// Codex stops reading at <c>project_doc_max_bytes</c> (32 KiB by default, across the whole chain from
/// the root to the working directory) with nothing but a log line, and one agent host refused the
/// root file outright when it reached 167 k characters against a 150 k limit — which is what split it
/// into an index and one file per area. Relocation is the sanctioned way to fit, and relocation is
/// exactly what breaks a link or strands a rule nothing points at, so the routing is checked beside
/// the budget.
/// </para>
/// <para>
/// <b>Bytes as they are on disk.</b> <c>.gitattributes</c> checks every <c>*.md</c> out with LF on
/// every operating system, so the size measured here is the size every checkout has.
/// </para>
/// <para>
/// <b>The decisions.</b> Code comments, log messages, <c>doctor</c> output and tests cite decisions
/// by number alone (<c>D57</c>), and the root file's table is the only way from a number to its
/// argument. So every number is recorded exactly once, in the file that table names, and every number
/// the code cites has to exist: a citation that leads nowhere is how an argument that was moved
/// rather than deleted gets lost.
/// </para>
/// </remarks>
public class AgentInstructionFileTests
{
    /// <summary>What the root <c>AGENTS.md</c> may weigh: smaller, because it is loaded by every session.</summary>
    private const int RootBudget = 24 * 1024;

    /// <summary>What a co-located <c>AGENTS.md</c> may weigh — Codex's <c>project_doc_max_bytes</c> default.</summary>
    private const int CoLocatedBudget = 32 * 1024;

    /// <summary>The whole content of the <c>CLAUDE.md</c> beside every co-located file.</summary>
    private const string ClaudeImport = "@AGENTS.md";

    /// <summary>Directories under the root that hold build output or somebody else's files.</summary>
    private static readonly string[] SkippedDirectories = ["bin", "obj", "artifacts", "node_modules", "TestResults"];

    /// <summary>The directories whose C# sources may cite a decision.</summary>
    private static readonly string[] SourceDirectories = ["src", "tests", "build"];

    private static readonly Regex LinkTarget = new(@"\]\(([^)\s]+)\)", RegexOptions.CultureInvariant);

    private static readonly Regex Definition = new(@"^\*\*D(\d+) — ", RegexOptions.CultureInvariant);

    private static readonly Regex DecisionRange = new(@"D(\d+)(?:\s*[–-]\s*D(\d+))?", RegexOptions.CultureInvariant);

    private static readonly Regex TableRow = new(@"^\| (D[^|]*) \| \[[^\]]*\]\(([^)]+)\) \|$", RegexOptions.CultureInvariant);

    private static readonly Regex Citation = new(@"\bD(\d{1,3})\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// No instruction file crosses the budget the root file states; a failure lists the headroom of
    /// every file, because the question at that moment is where the material can go instead.
    /// </summary>
    [Test]
    public void EveryInstructionFileFitsTheBudgetTheRootFileStates()
    {
        var files = InstructionFiles();
        var over = files.Where(file => Size(file) > Budget(file)).ToList();

        Assert.That(
            over.Count == 0, Is.True,
            $"{string.Join(", ", over.Select(Relative))} cross(es) the budget the root AGENTS.md states, "
            + "and an agent that truncates there loses the tail without a word. Do not cut an argument "
            + "to fit: move it to the AGENTS.md of the area it governs and route to it. Headroom:"
            + Environment.NewLine + Report(files));
    }

    /// <summary>
    /// The constants above are the numbers the root file argues for in prose, so neither can drift
    /// into being the only place the number is written down.
    /// </summary>
    [Test]
    public void TheRootFileStatesTheBudgetsThisTestEnforces()
    {
        var text = File.ReadAllText(RootFile());

        foreach (var budget in new[] { RootBudget, CoLocatedBudget })
        {
            var stated = $"under **{budget / 1024} KiB**";
            Assert.That(
                text.Contains(stated, StringComparison.Ordinal), Is.True,
                $"The root AGENTS.md no longer says \"{stated}\", but this test enforces {budget} bytes. "
                + "The prose is where the number is argued and the constant is what fails; they have to agree.");
        }
    }

    /// <summary>
    /// Every co-located file is named by the root index and has the one-line <c>CLAUDE.md</c> beside
    /// it, and the index names no file that does not exist.
    /// </summary>
    /// <remarks>
    /// The two routes serve different readers. Most agents only open a nested file because the index
    /// names it; Claude Code reads <c>CLAUDE.md</c> and never <c>AGENTS.md</c>, and loads a nested
    /// <c>CLAUDE.md</c> when it reads a file in that directory. A file missing from either is invisible
    /// to everybody on that side, which is the same outcome as truncation and just as quiet.
    /// </remarks>
    [Test]
    public void EveryCoLocatedFileIsIndexedAndLoadedByClaudeCode()
    {
        var root = RootFile();
        var coLocated = InstructionFiles().Where(file => file != root).Select(Relative).ToHashSet(StringComparer.Ordinal);
        var indexed = LinksIn(root)
            .Select(link => Resolve(root, link).Path)
            .Where(path => Path.GetFileName(path) == "AGENTS.md" && path != root)
            .Select(Relative)
            .ToHashSet(StringComparer.Ordinal);

        var complaints = new List<string>();
        complaints.AddRange(coLocated.Except(indexed).Select(path => $"{path} exists but the root index does not name it."));
        complaints.AddRange(indexed.Except(coLocated).Select(path => $"the root index names {path}, which does not exist."));

        foreach (var path in coLocated)
        {
            var claude = RepositoryLayout.Path_([.. path.Split('/')[..^1], "CLAUDE.md"]);
            if (!File.Exists(claude) || File.ReadAllText(claude).TrimEnd() != ClaudeImport)
            {
                complaints.Add($"{path} has no CLAUDE.md beside it whose whole content is '{ClaudeImport}'.");
            }
        }

        Assert.That(
            complaints.Count == 0, Is.True,
            "The routing to the co-located instruction files is broken:" + Environment.NewLine
            + string.Join(Environment.NewLine, complaints));
    }

    /// <summary>
    /// Every relative link and heading anchor in an instruction file resolves.
    /// </summary>
    [Test]
    public void EveryLinkAndAnchorBetweenInstructionFilesResolves()
    {
        var broken = new List<string>();

        foreach (var document in InstructionFiles())
        {
            foreach (var link in LinksIn(document))
            {
                var (path, anchor) = Resolve(document, link);

                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    broken.Add($"{Relative(document)} -> {link} (no such file)");
                }
                else if (anchor is not null && path.EndsWith(".md", StringComparison.Ordinal) && !HeadingAnchors(path).Contains(anchor))
                {
                    broken.Add($"{Relative(document)} -> {link} (no heading with that anchor)");
                }
            }
        }

        Assert.That(
            broken.Count == 0, Is.True,
            "Links between the instruction files that do not resolve — the way a relocated rule becomes "
            + "unreachable:" + Environment.NewLine + string.Join(Environment.NewLine, broken));
    }

    /// <summary>
    /// Every decision is recorded exactly once, the numbers run from D1 without a gap, and the root
    /// file's table names the file that records each one.
    /// </summary>
    [Test]
    public void EveryDecisionIsRecordedOnceWhereTheRootTableSays()
    {
        var recorded = new Dictionary<int, List<string>>();
        foreach (var file in InstructionFiles())
        {
            foreach (var line in OutsideFences(file))
            {
                var match = Definition.Match(line);
                if (match.Success)
                {
                    var number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    recorded.TryAdd(number, []);
                    recorded[number].Add(Relative(file));
                }
            }
        }

        var root = RootFile();
        var listed = new Dictionary<int, List<string>>();
        foreach (var line in OutsideFences(root))
        {
            var row = TableRow.Match(line);
            if (!row.Success)
            {
                continue;
            }

            var file = Relative(Resolve(root, row.Groups[2].Value).Path);
            foreach (var number in Numbers(row.Groups[1].Value))
            {
                listed.TryAdd(number, []);
                listed[number].Add(file);
            }
        }

        Assert.That(recorded.Count > 0, Is.True, "No decision paragraph ('**D<n> — ') was found at all.");

        var complaints = new List<string>();
        for (var number = 1; number <= recorded.Keys.Max(); number++)
        {
            recorded.TryGetValue(number, out var where);
            listed.TryGetValue(number, out var table);

            if (where is null)
            {
                complaints.Add($"D{number} is recorded nowhere, but numbers run to D{recorded.Keys.Max()}.");
            }
            else if (where.Count > 1)
            {
                complaints.Add($"D{number} is recorded {where.Count} times: {string.Join(", ", where)}.");
            }
            else if (table is null || table.Count != 1 || table[0] != where[0])
            {
                complaints.Add($"D{number} is recorded in {where[0]}, but the root table says {(table is null ? "nothing" : string.Join(", ", table))}.");
            }
        }

        complaints.AddRange(listed.Keys.Where(number => !recorded.ContainsKey(number)).Select(number => $"The root table lists D{number}, which is recorded nowhere."));

        Assert.That(
            complaints.Count == 0, Is.True,
            "The decisions and the root AGENTS.md's table of where they live disagree:" + Environment.NewLine
            + string.Join(Environment.NewLine, complaints));
    }

    /// <summary>
    /// Every decision a source file cites — in a comment, a log message or <c>doctor</c> output — is
    /// recorded in an instruction file.
    /// </summary>
    [Test]
    public void EveryDecisionTheSourceCitesIsRecorded()
    {
        var recorded = InstructionFiles()
            .SelectMany(OutsideFences)
            .Select(line => Definition.Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var sources = SourceDirectories
            .SelectMany(directory => Files(RepositoryLayout.Path_(directory), "*.cs"))
            .ToList();
        Assert.That(sources.Count > 0, Is.True, "No C# source was found to check citations in.");

        var dangling = sources
            .SelectMany(source => File.ReadLines(source).Select((line, index) => (source, line, index)))
            .SelectMany(entry => Citation.Matches(entry.line)
                .Where(match => !recorded.Contains(match.Groups[1].Value))
                .Select(match => $"{Relative(entry.source)}:{entry.index + 1} cites {match.Value}"))
            .ToList();

        Assert.That(
            dangling.Count == 0, Is.True,
            "Citations of decisions no instruction file records:" + Environment.NewLine
            + string.Join(Environment.NewLine, dangling));
    }

    // ------------------------------------------------------------------ helpers

    private static string RootFile() => RepositoryLayout.Path_("AGENTS.md");

    /// <summary>Every <c>AGENTS.md</c> in the repository, the root one first.</summary>
    private static List<string> InstructionFiles()
    {
        var files = Files(RepositoryLayout.Root, "AGENTS.md").OrderBy(Relative, StringComparer.Ordinal).ToList();
        Assert.That(files.Contains(RootFile()), Is.True, "There is no AGENTS.md at the repository root.");
        return [RootFile(), .. files.Where(file => file != RootFile())];
    }

    /// <summary>
    /// Files under a directory, skipping build output and every dot-directory — <c>.git</c>, and the
    /// <c>.alta/worktrees</c> a main checkout holds full copies of this repository in.
    /// </summary>
    private static IEnumerable<string> Files(string directory, string pattern)
    {
        foreach (var file in Directory.EnumerateFiles(directory, pattern))
        {
            yield return file;
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in Files(child, pattern))
            {
                yield return file;
            }
        }
    }

    private static int Budget(string file) => file == RootFile() ? RootBudget : CoLocatedBudget;

    private static int Size(string file) => (int)new FileInfo(file).Length;

    private static string Report(List<string> files)
    {
        var report = new StringBuilder();
        foreach (var file in files.OrderByDescending(file => (double)Size(file) / Budget(file)))
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  {Relative(file),-52} {Size(file),7} of {Budget(file),6} bytes, {Budget(file) - Size(file),7} free");
        }

        return report.ToString();
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(RepositoryLayout.Root, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>The lines of a markdown file that are not inside a fenced code block.</summary>
    private static IEnumerable<string> OutsideFences(string path)
    {
        var fenced = false;
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
            }
            else if (!fenced)
            {
                yield return line;
            }
        }
    }

    /// <summary>The relative link targets of a markdown file, outside code; anything with a scheme is skipped.</summary>
    private static IEnumerable<string> LinksIn(string path) =>
        OutsideFences(path)
            .SelectMany(line => LinkTarget.Matches(line).Select(match => match.Groups[1].Value))
            .Where(target => !target.Contains("://", StringComparison.Ordinal) && !target.StartsWith("mailto:", StringComparison.Ordinal));

    /// <summary>A link target as a full path and an optional anchor; an anchor alone points into the document itself.</summary>
    private static (string Path, string? Anchor) Resolve(string document, string link)
    {
        var hash = link.IndexOf('#', StringComparison.Ordinal);
        var target = hash < 0 ? link : link[..hash];
        var anchor = hash < 0 ? null : link[(hash + 1)..];
        var path = target.Length == 0
            ? document
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(document)!, target.Replace('/', Path.DirectorySeparatorChar)));
        return (path, anchor);
    }

    /// <summary>The GitHub anchors a markdown file's headings produce.</summary>
    private static HashSet<string> HeadingAnchors(string path) =>
        OutsideFences(path)
            .Where(line => line.StartsWith('#'))
            .Select(line => Anchor(line.TrimStart('#').Trim()))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// GitHub's rule: lower-case, keep letters, digits, hyphens and underscores, turn each space into
    /// a hyphen, drop everything else — so an em dash between two spaces leaves two hyphens.
    /// </summary>
    private static string Anchor(string heading)
    {
        var anchor = new StringBuilder(heading.Length);
        foreach (var character in heading)
        {
            if (char.IsLetterOrDigit(character))
            {
                anchor.Append(char.ToLowerInvariant(character));
            }
            else if (character == ' ')
            {
                anchor.Append('-');
            }
            else if (character is '-' or '_')
            {
                anchor.Append(character);
            }
        }

        return anchor.ToString();
    }

    /// <summary>The numbers a table cell lists, as single decisions (<c>D43</c>) and ranges (<c>D44–D48</c>).</summary>
    private static IEnumerable<int> Numbers(string cell)
    {
        foreach (Match match in DecisionRange.Matches(cell))
        {
            var first = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var last = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : first;
            for (var number = first; number <= last; number++)
            {
                yield return number;
            }
        }
    }
}
