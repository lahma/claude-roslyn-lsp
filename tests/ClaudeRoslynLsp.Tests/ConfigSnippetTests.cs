using System.Text.Json;
using System.Text.RegularExpressions;

using Xunit;

namespace ClaudeRoslynLsp.Tests;

/// <summary>
/// The client configuration packs under <c>docs/clients/</c>, plus this repository's own Copilot CLI
/// configuration at <c>.github/lsp.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// These files are not documentation about configuration; they <em>are</em> configuration, copied by
/// a reader into <c>~/.copilot/lsp-config.json</c> or <c>~/.codex/config.toml</c> verbatim. Nothing
/// in the build reads them, no client validates them until it fails to start, and every failure mode
/// they have is silent: a trailing comma that one parser tolerates and another rejects, a variable
/// name with a letter missing, a launch that forgot the verb and so exits 2 before the handshake, or
/// a <c>dnx</c> pin left behind at the release that renamed everything else.
/// </para>
/// <para>
/// <b>What is checked, and what deliberately is not.</b> Each file has to parse in its own format,
/// name only variables the adapter actually reads, and launch the binary with a verb. What no test
/// here can check is whether the surrounding key names are the ones the client expects — that is a
/// fact about somebody else's parser, it is recorded in <c>README.md</c> beside each snippet, and it
/// is verified by running the client. So this file is the floor, not the ceiling.
/// </para>
/// </remarks>
public class ConfigSnippetTests
{
    private const string ClientDirectory = "docs/clients";

    /// <summary>This repository's own Copilot CLI configuration — the project using itself.</summary>
    private const string RepositoryLspConfig = ".github/lsp.json";

    /// <summary>The Claude Code plugin manifest, which is the one place a <c>dnx</c> pin ships from.</summary>
    private const string PluginManifest = ".claude-plugin/plugin.json";

    private const string ChangelogPath = "CHANGELOG.md";

    /// <summary>The package id every <c>dnx</c> invocation has to name.</summary>
    private const string PackageId = "claude-roslyn-lsp";

    /// <summary>
    /// The two files whose format is JSON <em>with comments</em>, because their clients document it
    /// as such.
    /// </summary>
    /// <remarks>
    /// Every other <c>.json</c> file here is parsed with comments <b>disallowed</b>. A <c>//</c> line
    /// helpfully added to <c>.cursor/mcp.json</c> or to a Claude Code plugin manifest does not
    /// produce a warning; it produces a file the client cannot read, and in the plugin case a plugin
    /// that fails to load with nothing on screen to say so.
    /// </remarks>
    private static readonly HashSet<string> JsonWithComments = new(StringComparer.Ordinal)
    {
        "docs/clients/vscode-mcp.json",
        "docs/clients/zed-settings.json",
    };

    /// <summary>The verbs a configuration file may launch this binary with.</summary>
    /// <remarks>
    /// There is no default verb: the two servers speak different protocols on the same stdout, so a
    /// launch that omits one exits 2 with usage text on stderr — which every client reports as a
    /// server that died immediately.
    /// </remarks>
    private static readonly HashSet<string> Verbs = new(StringComparer.Ordinal) { "lsp", "mcp" };

    /// <summary>Any spelling of a variable belonging to this adapter, wherever it appears.</summary>
    private static readonly Regex VariableMention = new(
        "CLAUDE_(?:PLUGIN_OPTION_CLAUDE_)?ROSLYN_LSP_[A-Z0-9_]+",
        RegexOptions.Compiled);

    /// <summary>A TOML array assigned to a key: <c>args = ["lsp"]</c>.</summary>
    private static readonly Regex TomlArray = new(
        """^\s*[A-Za-z0-9_.\-]+\s*=\s*\[(?<items>[^\]]*)\]\s*$""",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>A Lua command table: <c>cmd = { 'exe', 'lsp' }</c>.</summary>
    private static readonly Regex LuaCommandTable = new(
        """cmd\s*=\s*\{(?<items>[^}]*)\}""",
        RegexOptions.Compiled);

    /// <summary>A quoted string in a TOML or Lua array.</summary>
    private static readonly Regex QuotedItem = new("""["']([^"']*)["']""", RegexOptions.Compiled);

    /// <summary>Every checked-in client configuration file, repository-relative.</summary>
    public static TheoryData<string> Snippets
    {
        get
        {
            var data = new TheoryData<string>();

            foreach (var path in SnippetPaths())
            {
                data.Add(path);
            }

            return data;
        }
    }

    /// <summary>
    /// The pack exists at all, and holds one file per client rather than a directory nobody filled
    /// in.
    /// </summary>
    /// <remarks>
    /// Every test below is a <see cref="TheoryAttribute"/> over the same enumeration, and an empty
    /// enumeration makes all of them pass. This is the assertion that stops that.
    /// </remarks>
    [Fact]
    public void TheClientConfigurationPackIsNotEmpty()
    {
        Assert.True(
            SnippetPaths().Count >= 10,
            $"Only {SnippetPaths().Count} client configuration files were found. README.md documents one per "
            + "client and every other test in this file is a theory over that list.");
    }

    /// <summary>Each file parses in the format its extension claims.</summary>
    [Theory]
    [MemberData(nameof(Snippets))]
    public void EverySnippetParses(string path)
    {
        var text = Read(path);

        switch (Path.GetExtension(path))
        {
            case ".json":
                ParseJson(path, text);
                break;

            case ".toml":
                AssertTomlLooksLikeToml(path, text);
                break;

            case ".lua":
                AssertLuaDelimitersBalance(path, text);
                break;

            default:
                Assert.Fail($"{path} has an extension no test here knows how to parse.");
                break;
        }
    }

    /// <summary>
    /// Every variable a snippet names is one the adapter reads.
    /// </summary>
    /// <remarks>
    /// The set comes from <c>ClaudeRoslynLspOptions.FromEnvironment</c>'s own source, so this cannot
    /// agree with a stale list. A misspelled variable is the worst kind of configuration bug: the
    /// server starts, the value is discarded, and the answers are confidently about something else.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Snippets))]
    public void EverySnippetNamesOnlyVariablesTheAdapterReads(string path)
    {
        foreach (Match mention in VariableMention.Matches(Read(path)))
        {
            var name = mention.Value;

            if (name.StartsWith("CLAUDE_PLUGIN_OPTION_", StringComparison.Ordinal))
            {
                name = name["CLAUDE_PLUGIN_OPTION_".Length..];
            }

            Assert.True(
                EnvironmentSurface.Accepted.Contains(name),
                $"{path} sets '{mention.Value}', which nothing in ClaudeRoslynLspOptions reads. Fix the "
                + "spelling, or read it — a documented variable that does nothing has no symptom to search "
                + "for.");
        }
    }

    /// <summary>
    /// Every launch names a verb, and it is the last argument.
    /// </summary>
    /// <remarks>
    /// Four spellings of the same idea across five clients: a <c>command</c> string beside an
    /// <c>args</c> array (Copilot CLI, Codex, Helix, Claude Code), one array holding both (OpenCode),
    /// a Lua <c>cmd</c> table (Neovim), and <c>path</c> beside <c>arguments</c> (Zed). All of them
    /// end up here.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Snippets))]
    public void EverySnippetLaunchesTheBinaryWithAVerb(string path)
    {
        var vectors = ArgumentVectors(path);

        Assert.True(vectors.Count > 0, $"{path} contains no launch arguments at all; it launches nothing.");

        foreach (var vector in vectors)
        {
            Assert.True(
                Verbs.Contains(vector[^1]),
                $"{path} launches with [{string.Join(", ", vector)}], whose last argument is not one of "
                + $"{string.Join("/", Verbs.Order(StringComparer.Ordinal))}. This binary has no default verb: "
                + "with none it exits 2 before the handshake, which a client reports as a server that failed "
                + "to start.");
        }
    }

    /// <summary>
    /// Wherever a configuration maps the <c>.cs</c> extension onto a language, the language is
    /// <c>csharp</c>.
    /// </summary>
    /// <remarks>
    /// Copilot CLI spells the map <c>fileExtensions</c> and Claude Code spells it
    /// <c>extensionToLanguage</c>; both are what actually causes the server to be launched for a
    /// file. A mapping that is missing or misspelled produces configuration nothing ever triggers,
    /// with no error anywhere — just an agent that carries on using grep on C#.
    /// </remarks>
    [Fact]
    public void EveryExtensionMappingSendsCSharpFilesToCsharp()
    {
        var mapped = 0;

        foreach (var path in SnippetPaths().Concat([PluginManifest]))
        {
            if (Path.GetExtension(path) != ".json")
            {
                continue;
            }

            using var document = ParseJson(path, Read(path));

            foreach (var map in Descendants(document.RootElement)
                .Where(element => element.ValueKind == JsonValueKind.Object)
                .SelectMany(element => element.EnumerateObject())
                .Where(property => property.Name is "fileExtensions" or "extensionToLanguage")
                .Select(property => property.Value))
            {
                Assert.Equal(JsonValueKind.Object, map.ValueKind);

                Assert.True(
                    map.TryGetProperty(".cs", out var language),
                    $"{path} declares an extension map with no '.cs' entry, which is the one extension this "
                    + "server exists for.");

                Assert.Equal("csharp", language.GetString());
                mapped++;
            }
        }

        Assert.True(mapped >= 2, $"Only {mapped} extension maps were found; Copilot CLI and Claude Code both "
            + "need one, so this scan has stopped finding them.");
    }

    /// <summary>
    /// Every <c>dnx</c> launch pins the package at the version <c>CHANGELOG.md</c> declares.
    /// </summary>
    /// <remarks>
    /// A floating <c>dnx claude-roslyn-lsp</c> would change what a configuration runs without the
    /// configuration changing, which is how this release's skill ends up driving next release's
    /// binary. A pin left behind at release time is the mirror image, and neither fails anywhere
    /// else: <c>dnx</c> happily resolves both.
    /// </remarks>
    [Fact]
    public void EveryDnxLaunchPinsTheChangelogVersion()
    {
        var expected = $"{PackageId}@{ChangelogVersion()}";
        var pinned = 0;

        foreach (var path in SnippetPaths().Concat([PluginManifest]))
        {
            foreach (var vector in ArgumentVectors(path))
            {
                var package = vector.FirstOrDefault(argument =>
                    argument.StartsWith(PackageId + "@", StringComparison.Ordinal));

                if (package is null)
                {
                    continue;
                }

                Assert.Equal(expected, package);

                Assert.Contains("--yes", vector, StringComparer.Ordinal);
                pinned++;
            }
        }

        Assert.True(
            pinned >= 2,
            $"Only {pinned} dnx launches were found. The plugin manifest and .github/lsp.json both run one, "
            + "so this scan has stopped finding them and the pin is no longer checked anywhere.");
    }

    // ------------------------------------------------------------------ helpers

    private static List<string> SnippetPaths()
    {
        var root = RepositoryLayout.Root;
        var clients = Path.Combine(root, ClientDirectory.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(Directory.Exists(clients), $"{ClientDirectory} does not exist.");

        var paths = Directory
            .EnumerateFiles(clients, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();

        paths.Add(RepositoryLspConfig);
        paths.Sort(StringComparer.Ordinal);

        return paths;
    }

    private static string Read(string path)
        => File.ReadAllText(RepositoryLayout.Path_(path.Split('/')));

    /// <summary>
    /// Parses one JSON file, allowing comments only where the client documents them and never
    /// allowing a trailing comma.
    /// </summary>
    private static JsonDocument ParseJson(string path, string text)
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonWithComments.Contains(path) ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        };

        try
        {
            return JsonDocument.Parse(text, options);
        }
        catch (JsonException error)
        {
            Assert.Fail($"{path} is not valid JSON for the client that reads it: {error.Message}");
            throw;
        }
    }

    /// <summary>
    /// Every argument vector a file launches something with, whatever shape the client spells it in.
    /// </summary>
    private static List<string[]> ArgumentVectors(string path)
    {
        var text = Read(path);
        var vectors = new List<string[]>();

        switch (Path.GetExtension(path))
        {
            case ".json":
                using (var document = ParseJson(path, text))
                {
                    foreach (var property in Descendants(document.RootElement)
                        .Where(element => element.ValueKind == JsonValueKind.Object)
                        .SelectMany(element => element.EnumerateObject()))
                    {
                        // "command" is an array in OpenCode and a string everywhere else; "args" and
                        // Zed's "arguments" are always arrays.
                        if (property.Name is not ("args" or "arguments" or "command")
                            || property.Value.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        var vector = property.Value.EnumerateArray().Select(item => item.GetString()!).ToArray();

                        if (vector.Length > 0)
                        {
                            vectors.Add(vector);
                        }
                    }
                }

                break;

            case ".toml":
                foreach (Match array in TomlArray.Matches(text))
                {
                    var vector = Items(array.Groups["items"].Value);

                    // Helix's `language-servers = [...]` is a list of server names, not arguments.
                    if (vector.Length > 0 && Verbs.Contains(vector[^1]))
                    {
                        vectors.Add(vector);
                    }
                }

                break;

            case ".lua":
                foreach (Match table in LuaCommandTable.Matches(text))
                {
                    var vector = Items(table.Groups["items"].Value);

                    if (vector.Length > 0)
                    {
                        vectors.Add(vector);
                    }
                }

                break;

            default:
                break;
        }

        return vectors;
    }

    private static string[] Items(string list)
        => QuotedItem.Matches(list).Select(item => item.Groups[1].Value).ToArray();

    /// <summary>Every element in a JSON document, the root included.</summary>
    private static IEnumerable<JsonElement> Descendants(JsonElement element)
    {
        yield return element;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var child in element.EnumerateObject().SelectMany(property => Descendants(property.Value)))
                {
                    yield return child;
                }

                break;

            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray().SelectMany(Descendants))
                {
                    yield return child;
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// TOML far enough to catch what a hand-edited file gets wrong: an unterminated string, a stray
    /// bracket, a line that is neither a table header nor an assignment.
    /// </summary>
    /// <remarks>
    /// Deliberately not a TOML parser. The package budget has no room for one in the test project
    /// either, and the failures worth catching in a six-line file are all structural.
    /// </remarks>
    private static void AssertTomlLooksLikeToml(string path, string text)
    {
        foreach (var (line, number) in text.Split('\n').Select((line, index) => (line.TrimEnd('\r'), index + 1)))
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith('['))
            {
                Assert.True(
                    trimmed.EndsWith(']') && Balanced(trimmed, '[', ']'),
                    $"{path}:{number} is an unbalanced table header: {trimmed}");

                continue;
            }

            Assert.True(
                trimmed.Contains('=', StringComparison.Ordinal),
                $"{path}:{number} is neither a table header nor an assignment: {trimmed}");

            Assert.True(
                trimmed.Count(character => character == '"') % 2 == 0,
                $"{path}:{number} has an unterminated string: {trimmed}");

            Assert.True(
                Balanced(trimmed, '[', ']') && Balanced(trimmed, '{', '}'),
                $"{path}:{number} has unbalanced brackets: {trimmed}");
        }
    }

    /// <summary>
    /// Lua far enough to catch the two mistakes an edit to a seven-line file makes: a delimiter left
    /// open, and a quote left open on a line.
    /// </summary>
    private static void AssertLuaDelimitersBalance(string path, string text)
    {
        var code = string.Join(
            '\n',
            text.Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        Assert.True(Balanced(code, '(', ')'), $"{path} has unbalanced parentheses.");
        Assert.True(Balanced(code, '{', '}'), $"{path} has unbalanced braces.");

        foreach (var (line, number) in code.Split('\n').Select((line, index) => (line, index + 1)))
        {
            Assert.True(
                line.Count(character => character == '\'') % 2 == 0,
                $"{path}:{number} has an unterminated string: {line.Trim()}");
        }
    }

    private static bool Balanced(string text, char open, char close)
        => text.Count(character => character == open) == text.Count(character => character == close);

    /// <summary>The version authority: <c>CHANGELOG.md</c>'s first line is a <c># version</c> header.</summary>
    private static string ChangelogVersion()
    {
        var first = File.ReadLines(RepositoryLayout.Path_(ChangelogPath)).First().Trim();

        Assert.StartsWith("# ", first, StringComparison.Ordinal);
        return first[2..].Trim();
    }
}
