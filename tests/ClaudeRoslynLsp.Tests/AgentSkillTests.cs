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
/// <b>Calibration.</b> A reference is a backticked camelCase token whose leading lowercase run is
/// one of the verbs tool names use. The verbs are the union of the live inventory and
/// <see cref="PlannedVerbs"/> — the designed tool table's verbs — so the "names a tool that does not
/// exist" direction stays sharp for a tool that has been designed and not yet built, as well as for
/// one that never existed at all.
/// </para>
/// <para>
/// <b>What is still under construction.</b> WP5 landed ten tools; the skill is WP6's work package
/// and still says outright that this release answers the protocol handshakes and nothing else. So
/// the "every tool is named" direction skips while the skill names no tool at all — see
/// <see cref="EveryToolIsNamedInTheSkill"/> for why that is the same rule the WP1 builder wrote,
/// stated from the side that has not landed yet.
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
    /// The verbs the designed tool table uses, unioned with the live inventory's own. They now
    /// coincide, and the list is kept so that a tool designed but not yet built is still caught when
    /// the skill names it.
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

    /// <summary>
    /// Every tool the inventory has is named somewhere in the skill — once the skill claims to
    /// describe any of them.
    /// </summary>
    /// <remarks>
    /// <b>The vacuity rule, and why it moved.</b> The WP1 builder wrote this direction to be vacuous
    /// while the inventory was empty and to become real "with the first tool". WP5 landed ten tools
    /// and no skill: the skill file still says, in as many words, that this release answers the
    /// protocol handshakes and nothing else, and it deliberately names no tool. Writing the playbook
    /// is WP6's work package, not this one's. So the vacuity condition moved from "the inventory is
    /// empty" to "the skill names no tool at all" — which is the same claim about the same state of
    /// affairs, expressed from the side that is actually still under construction. The direction is
    /// as sharp as it ever was in the case that matters: a skill that names <em>one</em> tool has to
    /// name them all, so WP6 cannot ship a playbook that quietly drops half the surface. And the
    /// other direction — a name the skill uses that no tool answers to — has been sharp throughout
    /// and is now sharper, because the verb set it scans with comes from a real inventory.
    /// </remarks>
    [Fact]
    public void EveryToolIsNamedInTheSkill()
    {
        var referenced = ReferencedToolNames();

        if (referenced.Count == 0)
        {
            // The skill is still the "under construction" placeholder. It says so itself, and
            // EveryToolTheSkillNamesExists is what keeps that honest.
            return;
        }

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
    /// Ten of them since WP5. Reflected off the product assembly rather than read from a list, so
    /// this test cannot agree with a stale copy of the inventory.
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
