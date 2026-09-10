using System.Text.Json;

using ClaudeRoslynLsp.Configuration;

using Xunit;

namespace ClaudeRoslynLsp.Tests;

/// <summary>
/// The two Claude Code plugin manifests that make this repository installable with
/// <c>/plugin marketplace add lahma/claude-roslyn-lsp</c>: <c>.claude-plugin/marketplace.json</c>
/// (the catalog) and <c>.claude-plugin/plugin.json</c> (the plugin itself, whose source is the
/// repository root).
/// </summary>
/// <remarks>
/// <para>
/// One install delivers three things — the skill, an LSP server and an MCP server — described by
/// paths and version strings that nothing else in the build reads back. A skill path that stops
/// resolving ships a plugin with no skill; a <c>dnx</c> pin left behind at release time ships this
/// version's skill driving last version's binary; a <c>.cs</c> mapping that goes missing ships an LSP
/// server that is never launched, with no error anywhere. None of that fails elsewhere, which is what
/// this file is for.
/// </para>
/// <para>
/// The two server entries are the part worth being strict about. They are the <em>same binary</em>
/// with a different final argument, so their environment blocks have to be the same block: a
/// configuration option that reaches the MCP half and not the LSP half produces an adapter whose two
/// faces disagree about which solution they are looking at, and the symptom is answers that are
/// merely wrong.
/// </para>
/// <para>
/// Read with <see cref="JsonDocument"/> rather than deserialized, because the test project runs with
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c> (D6) and these files have no business in a
/// <c>JsonSerializerContext</c>.
/// </para>
/// </remarks>
public class PluginManifestTests
{
    /// <summary>The file that identifies the repository root when walking up from the test assembly.</summary>
    private const string RootMarker = "claude-roslyn-lsp.slnx";

    private const string MarketplacePath = ".claude-plugin/marketplace.json";
    private const string PluginPath = ".claude-plugin/plugin.json";
    private const string ServerManifestPath = ".mcp/server.json";
    private const string ChangelogPath = "CHANGELOG.md";

    /// <summary>The plugin's name, the marketplace's name, the binary's name and the package id.</summary>
    /// <remarks>
    /// All four are the same string here, unlike in sonarqube-mcp where a reserved nuget.org prefix
    /// forced the package id apart from everything else. That they agree is worth asserting anyway —
    /// it is a property of this repository, not a coincidence to be discovered later.
    /// </remarks>
    private const string PluginName = "claude-roslyn-lsp";

    /// <summary>
    /// The one environment variable the manifest is allowed to set under its own name, and the value
    /// it must be set to.
    /// </summary>
    /// <remarks>
    /// It is exempt because it is not a user option at all: Claude Code substitutes
    /// <c>${CLAUDE_PLUGIN_DATA}</c> with a real directory it owns, so there is no "user left it
    /// blank" case to guard against and no plain variable underneath for it to shadow.
    /// </remarks>
    private const string PluginDataVariable = "CLAUDE_ROSLYN_LSP_HOME";

    /// <inheritdoc cref="PluginDataVariable"/>
    private const string PluginDataPlaceholder = "${CLAUDE_PLUGIN_DATA}";

    /// <summary>
    /// The plugin's source is the repository root, which is what lets the manifest point at the one
    /// canonical <c>SKILL.md</c> under <c>.claude/skills/</c> instead of a second copy. A plugin
    /// cannot reference files outside its own root, so any other source would force a duplicate.
    /// </summary>
    [Fact]
    public void TheMarketplaceListsThisRepositoryAsItsOnePlugin()
    {
        var root = FindRepositoryRoot();

        using var marketplace = Read(root, MarketplacePath);
        using var plugin = Read(root, PluginPath);

        Assert.Equal(PluginName, marketplace.RootElement.GetProperty("name").GetString());
        Assert.Equal(PluginName, plugin.RootElement.GetProperty("name").GetString());

        Assert.False(
            string.IsNullOrWhiteSpace(marketplace.RootElement.GetProperty("owner").GetProperty("name").GetString()),
            "A marketplace needs an owner name; users see it before they trust the source.");

        var entry = Assert.Single(marketplace.RootElement.GetProperty("plugins").EnumerateArray().ToList());

        Assert.Equal(PluginName, entry.GetProperty("name").GetString());
        Assert.Equal("./", entry.GetProperty("source").GetString());
    }

    /// <summary>
    /// <c>CHANGELOG.md</c> is the version authority the whole build reads from, and an explicit
    /// plugin <c>version</c> is what Claude Code keys updates off: leave it behind and users are told
    /// they are already up to date.
    /// </summary>
    [Fact]
    public void ThePluginVersionIsTheVersionAuthority()
    {
        var root = FindRepositoryRoot();

        using var plugin = Read(root, PluginPath);

        Assert.Equal(ReadChangelogVersion(root), plugin.RootElement.GetProperty("version").GetString());
    }

    /// <summary>
    /// Both servers are the same pinned package, launched with the verb that selects which protocol
    /// it speaks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pin is explicit, never floating: Claude Code keys plugin updates off the plugin
    /// <c>version</c>, so a floating <c>dnx claude-roslyn-lsp</c> would change what the plugin runs
    /// without the plugin version moving — invisible to <c>/plugin update</c>, and able to pair this
    /// release's skill with a binary that no longer matches it.
    /// </para>
    /// <para>
    /// The package id is read out of <c>.mcp/server.json</c> rather than written here, which closes
    /// the chain: <see cref="McpServerManifestTests"/> pins that identifier to the csproj's
    /// <c>&lt;PackageId&gt;</c>, so a rename has exactly one place to be made.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("lspServers", "lsp")]
    [InlineData("mcpServers", "mcp")]
    public void EachServerRunsThePinnedPackageWithItsOwnVerb(string section, string verb)
    {
        var root = FindRepositoryRoot();
        var version = ReadChangelogVersion(root);

        using var plugin = Read(root, PluginPath);
        using var server = Read(root, ServerManifestPath);

        var packageId = NuGetPackage(server).GetProperty("identifier").GetString();
        var entry = Server(plugin, section);

        var command = entry.GetProperty("command").GetString()!;

        // A command with a space in it is a command Claude Code will look for as one file name, and
        // the failure is a server that never starts with nothing in the log to say why.
        Assert.DoesNotContain(" ", command, StringComparison.Ordinal);

        Assert.Equal(
            new[] { $"{packageId}@{version}", "--yes", verb },
            entry.GetProperty("args").EnumerateArray().Select(argument => argument.GetString()).ToArray());
    }

    /// <summary>
    /// The two servers are one binary, so they get one environment. Compared as an ordered sequence
    /// of pairs, which is as close to "the same block" as a parsed document can express.
    /// </summary>
    [Fact]
    public void BothServersAreGivenTheIdenticalEnvironmentBlock()
    {
        var root = FindRepositoryRoot();

        using var plugin = Read(root, PluginPath);

        var lsp = EnvironmentPairs(Server(plugin, "lspServers"));
        var mcp = EnvironmentPairs(Server(plugin, "mcpServers"));

        Assert.Equal(lsp, mcp);
        Assert.NotEmpty(lsp);
    }

    /// <summary>
    /// Every configured value is written to a <c>CLAUDE_PLUGIN_OPTION_</c> name, and the single
    /// exception is the plugin's own data directory.
    /// </summary>
    /// <remarks>
    /// Mapping an option straight onto its plain variable name sets that variable to the empty string
    /// whenever the user leaves the option blank, which shadows whatever their environment already
    /// said. The adapter reads the prefixed name first and treats blank as absent, so the two halves
    /// only work together — see <see cref="ClaudeRoslynLspOptions.PluginOptionPrefix"/>.
    /// </remarks>
    [Theory]
    [InlineData("lspServers")]
    [InlineData("mcpServers")]
    public void EveryVariableIsWrittenUnderThePluginOptionPrefixExceptTheDataDirectory(string section)
    {
        var root = FindRepositoryRoot();

        using var plugin = Read(root, PluginPath);

        var environment = EnvironmentPairs(Server(plugin, section));
        var exceptions = new List<string>();

        foreach (var (name, value) in environment)
        {
            if (name.StartsWith(ClaudeRoslynLspOptions.PluginOptionPrefix, StringComparison.Ordinal))
            {
                // The prefix is only half of it: the suffix has to be a variable the adapter
                // actually reads. A misspelling here fails nowhere — the launcher writes the value,
                // the reader looks for a different name, and the user's answer to the prompt is
                // discarded in silence.
                var suffix = name[ClaudeRoslynLspOptions.PluginOptionPrefix.Length..];

                Assert.True(
                    EnvironmentSurface.Accepted.Contains(suffix),
                    $"{section} maps an option onto '{name}', but nothing in ClaudeRoslynLspOptions reads "
                    + $"'{suffix}'. The prompt would be answered and thrown away.");

                continue;
            }

            exceptions.Add(name);

            Assert.True(
                name == PluginDataVariable,
                $"{section} sets '{name}' directly. An unset option substitutes as the empty string, which "
                + $"would shadow a {name} the user already has in their environment. Map it to "
                + $"{ClaudeRoslynLspOptions.PluginOptionPrefix}{name} instead.");

            Assert.Equal(PluginDataPlaceholder, value);
        }

        Assert.Equal([PluginDataVariable], exceptions);
    }

    /// <summary>
    /// Every prompt the plugin declares reaches the server, and nothing reaches it that was never
    /// prompted for.
    /// </summary>
    /// <remarks>
    /// A mistyped <c>${user_config.…}</c> placeholder does not fail anywhere: Claude Code passes the
    /// placeholder text through as the value, so the server receives the literal string
    /// <c>"${user_config.solutoin}"</c> and treats it as a path. An option nothing references is the
    /// mirror image — a prompt whose answer is discarded.
    /// </remarks>
    [Fact]
    public void EveryUserConfigOptionIsReferencedByExactlyOneEnvironmentEntry()
    {
        var root = FindRepositoryRoot();

        using var plugin = Read(root, PluginPath);

        var options = plugin.RootElement.GetProperty("userConfig")
            .EnumerateObject()
            .Select(option => option.Name)
            .ToList();

        Assert.NotEmpty(options);

        // The two blocks are identical (asserted separately), so one of them describes both.
        var referenced = new List<string>();

        foreach (var (name, value) in EnvironmentPairs(Server(plugin, "lspServers")))
        {
            if (name == PluginDataVariable)
            {
                continue;
            }

            Assert.True(
                value.StartsWith("${user_config.", StringComparison.Ordinal) && value.EndsWith('}'),
                $"{name} is passed as '{value}', which is a literal, not a configured value.");

            referenced.Add(value["${user_config.".Length..^1]);
        }

        Assert.Equal(
            options.OrderBy(name => name, StringComparer.Ordinal),
            referenced.OrderBy(name => name, StringComparer.Ordinal));

        Assert.Equal(referenced.Count, referenced.Distinct(StringComparer.Ordinal).Count());

        foreach (var option in options)
        {
            var declared = plugin.RootElement.GetProperty("userConfig").GetProperty(option);

            Assert.Equal("string", declared.GetProperty("type").GetString());

            // Every option is optional, and an omitted default is not the same as "": Claude Code
            // substitutes the empty string either way, and stating it keeps the prompt honest.
            Assert.Equal(string.Empty, declared.GetProperty("default").GetString());

            Assert.False(
                string.IsNullOrWhiteSpace(declared.GetProperty("description").GetString()),
                $"userConfig option '{option}' has no description; that is what the user reads next to the prompt.");
        }
    }

    /// <summary>
    /// The file-extension mapping is what actually launches the LSP server. Without it the entry is
    /// configuration nothing ever triggers, and the failure is silent — no error, just an agent that
    /// keeps using grep on C#.
    /// </summary>
    [Fact]
    public void TheLspServerIsMappedToCSharpFiles()
    {
        var root = FindRepositoryRoot();

        using var plugin = Read(root, PluginPath);

        var mapping = Server(plugin, "lspServers").GetProperty("extensionToLanguage");

        Assert.Equal("csharp", mapping.GetProperty(".cs").GetString());
    }

    /// <summary>
    /// The launch envelope: how long the client waits for a server that is loading a solution, and
    /// how many times it restarts one that crashed.
    /// </summary>
    /// <remarks>
    /// These are not defaults worth re-deriving later. <c>startupTimeout</c> unset means "wait
    /// forever", which turns a broken install into a session that hangs; 120 s is the same budget the
    /// adapter's own readiness gate uses, so the client and the server give up at the same moment.
    /// <c>maxRestarts</c> is a budget the adapter is designed to protect by absorbing Roslyn crashes
    /// itself rather than spending.
    /// </remarks>
    [Fact]
    public void TheLspServerDeclaresItsLaunchEnvelope()
    {
        var root = FindRepositoryRoot();

        using var plugin = Read(root, PluginPath);

        var entry = Server(plugin, "lspServers");

        Assert.Equal(120_000, entry.GetProperty("startupTimeout").GetInt32());
        Assert.Equal(10_000, entry.GetProperty("shutdownTimeout").GetInt32());
        Assert.True(entry.GetProperty("restartOnCrash").GetBoolean());
        Assert.Equal(3, entry.GetProperty("maxRestarts").GetInt32());
        Assert.True(entry.GetProperty("diagnostics").GetBoolean());
    }

    /// <summary>
    /// The manifest names the skill directory explicitly, because the plugin's source is the
    /// repository root and the default <c>skills/</c> scan would otherwise find nothing.
    /// </summary>
    [Fact]
    public void EveryDeclaredSkillPathResolvesToASkill()
    {
        var root = FindRepositoryRoot();

        using var plugin = Read(root, PluginPath);

        var paths = plugin.RootElement.GetProperty("skills")
            .EnumerateArray()
            .Select(path => path.GetString()!)
            .ToList();

        Assert.NotEmpty(paths);

        foreach (var path in paths)
        {
            Assert.StartsWith("./", path, StringComparison.Ordinal);

            var skill = Path.Combine(root, path[2..].Replace('/', Path.DirectorySeparatorChar), "SKILL.md");

            Assert.True(
                File.Exists(skill),
                $"{PluginPath} declares the skill path '{path}', which has no SKILL.md. The plugin would "
                + "install with no skill at all.");
        }
    }

    /// <summary>The one server entry in a section, which is <c>roslyn</c> in both.</summary>
    private static JsonElement Server(JsonDocument plugin, string section)
    {
        var servers = plugin.RootElement.GetProperty(section).EnumerateObject().ToList();

        var entry = Assert.Single(servers);
        Assert.Equal("roslyn", entry.Name);

        return entry.Value;
    }

    /// <summary>One server's environment block, in declaration order.</summary>
    private static List<(string Name, string Value)> EnvironmentPairs(JsonElement server) =>
        server.GetProperty("env")
            .EnumerateObject()
            .Select(entry => (entry.Name, entry.Value.GetString()!))
            .ToList();

    private static JsonDocument Read(string root, string relativePath)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(root, relativePath)));

    /// <summary>
    /// The one <c>packages</c> entry of <c>.mcp/server.json</c> that describes the NuGet channel —
    /// the id <c>dnx</c> is given comes from there.
    /// </summary>
    private static JsonElement NuGetPackage(JsonDocument server)
        => server.RootElement.GetProperty("packages")
            .EnumerateArray()
            .Single(package => package.GetProperty("registryType").GetString() == "nuget");

    /// <summary>The version authority: <c>CHANGELOG.md</c>'s first line is a <c># version</c> header.</summary>
    private static string ReadChangelogVersion(string root)
    {
        var first = File.ReadLines(Path.Combine(root, ChangelogPath)).First().Trim();

        Assert.StartsWith("# ", first, StringComparison.Ordinal);
        return first[2..].Trim();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RootMarker)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not find {RootMarker} above {AppContext.BaseDirectory}.");
    }
}
