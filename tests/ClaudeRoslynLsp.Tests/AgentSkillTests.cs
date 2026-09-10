using System.Reflection;
using System.Text.RegularExpressions;

using ModelContextProtocol.Server;

using Xunit;

namespace ClaudeRoslynLsp.Tests;

/// <summary>
/// The shipped Agent Skill (<c>.claude/skills/claude-roslyn-lsp/SKILL.md</c>) against the tool
/// surface it describes.
/// </summary>
/// <remarks>
/// <para>
/// The skill is the primary lever of this project, not an add-on: the problem it exists to fix is
/// behavioural — a model reaching for grep and sed on C# — and no schema can carry "rename this
/// symbol rather than editing its declaration", because each schema sees one tool. Nothing loads that
/// file at build time, so it is exactly the kind of document that keeps advertising a tool surface
/// that has moved on. This test is what makes the skill one of the places a new tool has to be added.
/// </para>
/// <para>
/// It checks both directions. A name the skill uses that no tool answers to sends a model at
/// something that does not exist; a tool the skill never mentions is a tool the workflow silently
/// drops.
/// </para>
/// <para>
/// <b>Calibration, and what is different while the inventory is empty.</b> A reference is a
/// backticked camelCase token whose leading lowercase run is one of the verbs tool names use. Those
/// verbs normally come from the inventory itself — but this build registers no tools yet, and a verb
/// set derived from an empty inventory would match nothing and make the whole scan pass vacuously.
/// So the verbs are the union of the live inventory and <see cref="PlannedVerbs"/>, the verbs of the
/// designed tool table. That keeps the "names a tool that does not exist" direction sharp <em>today</em>:
/// a playbook written now against a tool that has not been built fails, which is the correct answer.
/// The reverse direction is genuinely vacuous until there are tools, and becomes an assertion the
/// moment the first one lands, with no change needed here.
/// </para>
/// </remarks>
public class AgentSkillTests
{
    /// <summary>The file that identifies the repository root when walking up from the test assembly.</summary>
    private const string RootMarker = "claude-roslyn-lsp.slnx";

    /// <summary>
    /// The canonical skill location. <c>.claude/skills/</c> is what Claude Code loads as a project
    /// skill from a checkout, and what <c>npx skills add</c> and <c>gh skill install</c> read when
    /// installing into another agent; every other tool is pointed at this path from AGENTS.md and
    /// README.md rather than given a second copy.
    /// </summary>
    private const string SkillDirectory = ".claude/skills/claude-roslyn-lsp";

    /// <summary>
    /// The verbs the designed tool table uses. Only load-bearing while no tool is registered; once
    /// the inventory is non-empty its own verbs are unioned in and this list stops mattering.
    /// </summary>
    private static readonly string[] PlannedVerbs =
        ["apply", "find", "fix", "format", "get", "rename", "resolve"];

    /// <summary>A backticked span on one line — the way the skill spells every identifier.</summary>
    private static readonly Regex BacktickedSpan = new("`([^`\n]+)`", RegexOptions.Compiled);

    /// <summary>A camelCase identifier: a lowercase run, then a capital, then anything.</summary>
    private static readonly Regex CamelCaseIdentifier = new("^([a-z]+)[A-Z][A-Za-z0-9]*$", RegexOptions.Compiled);

    /// <summary>The Agent Skills spec's <c>name</c> rule: lowercase alphanumerics, single hyphens.</summary>
    private static readonly Regex SkillName = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    [Fact]
    public void EveryToolTheSkillNamesExists()
    {
        var toolNames = ToolNames();
        var unknown = ReferencedToolNames().Where(name => !toolNames.Contains(name)).ToList();

        Assert.True(
            unknown.Count == 0,
            $"{SkillDirectory}/SKILL.md names tools this server does not have: {string.Join(", ", unknown)}. "
            + "Rename them, or drop the guidance that used them.");
    }

    [Fact]
    public void EveryToolIsNamedInTheSkill()
    {
        var referenced = ReferencedToolNames();

        var missing = ToolNames()
            .Where(name => !referenced.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{SkillDirectory}/SKILL.md never mentions {string.Join(", ", missing)}. A tool the skill leaves out "
            + "is a tool the workflow it teaches silently drops — place it in a playbook, in backticks.");
    }

    /// <summary>
    /// The frontmatter is the only part of a skill that is always in context, and the two fields the
    /// Agent Skills spec requires are what every client keys discovery off. <c>name</c> has to equal
    /// the directory name: clients that take the command from the directory and clients that take it
    /// from the field would otherwise disagree about what this skill is called.
    /// </summary>
    [Fact]
    public void FrontmatterFollowsTheAgentSkillsSpec()
    {
        var lines = File.ReadAllLines(SkillFile());

        Assert.Equal("---", lines[0]);

        var end = Array.IndexOf(lines, "---", 1);
        Assert.True(end > 0, "SKILL.md has no closing frontmatter delimiter.");

        var frontmatter = lines[1..end];

        var name = Value(frontmatter, "name");
        Assert.Equal(SkillDirectory[(SkillDirectory.LastIndexOf('/') + 1)..], name);
        Assert.Matches(SkillName, name);
        Assert.True(name.Length <= 64, $"name is {name.Length} characters; the spec allows 64.");

        var description = Value(frontmatter, "description");
        Assert.False(string.IsNullOrWhiteSpace(description), "description is required and drives invocation.");
        Assert.True(
            description.Length <= 1024,
            $"description is {description.Length} characters; the spec allows 1024.");

        Assert.Equal("MIT", Value(frontmatter, "license"));
    }

    /// <summary>
    /// The MCP tool names, reflected off the product assembly rather than read from any list.
    /// </summary>
    /// <remarks>
    /// Empty in this work package. There is deliberately no "must not be empty" self-check here yet:
    /// it would fail on a scaffold that has not registered a tool, which is the state this repository
    /// is in by design. WP5 lands the tools, and the first of them makes both directions of the
    /// cross-check real.
    /// </remarks>
    private static HashSet<string> ToolNames() =>
        typeof(ServerVersion).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(candidate => candidate.Attribute is not null)
            .Select(candidate => candidate.Attribute!.Name ?? candidate.Method.Name)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Every backticked token in the skill that is shaped like one of this server's tool names.
    /// </summary>
    private static HashSet<string> ReferencedToolNames()
    {
        var verbs = ToolNames()
            .Select(name => CamelCaseIdentifier.Match(name).Groups[1].Value)
            .Concat(PlannedVerbs)
            .Where(verb => verb.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        // Self-check: the scan is only as good as its verb set, so an empty one would mean it matches
        // nothing and passes for the wrong reason.
        Assert.NotEmpty(verbs);

        var referenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match span in BacktickedSpan.Matches(File.ReadAllText(SkillFile())))
        {
            var token = span.Groups[1].Value;
            var identifier = CamelCaseIdentifier.Match(token);

            if (identifier.Success && verbs.Contains(identifier.Groups[1].Value))
            {
                referenced.Add(token);
            }
        }

        return referenced;
    }

    /// <summary>Reads one frontmatter key, folding a <c>&gt;-</c> block scalar back onto one line.</summary>
    private static string Value(string[] frontmatter, string key)
    {
        var index = Array.FindIndex(frontmatter, line => line.StartsWith(key + ":", StringComparison.Ordinal));
        Assert.True(index >= 0, $"SKILL.md frontmatter has no '{key}' key.");

        var head = frontmatter[index][(key.Length + 1)..].Trim();
        List<string> folded = head is ">-" or ">" or "|" or "|-" ? [] : [head];

        for (var next = index + 1; next < frontmatter.Length && frontmatter[next].StartsWith(' '); next++)
        {
            folded.Add(frontmatter[next].Trim());
        }

        return string.Join(' ', folded).Trim();
    }

    private static string SkillFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RootMarker)))
            {
                var file = Path.Combine(directory.FullName, SkillDirectory, "SKILL.md");
                Assert.True(File.Exists(file), $"The shipped skill is missing: {SkillDirectory}/SKILL.md.");
                return file;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not find {RootMarker} above {AppContext.BaseDirectory}.");
    }
}
