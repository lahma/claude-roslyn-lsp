using System.Text.Json;
using System.Xml.Linq;

using Xunit;

namespace ClaudeRoslynLsp.Tests;

/// <summary>
/// <c>.mcp/server.json</c> is the MCP server manifest packed into the NuGet package at
/// <c>/.mcp/server.json</c>, where nuget.org reads it to render the package's MCP tab and to generate
/// client configuration.
/// </summary>
/// <remarks>
/// <para>
/// It restates three things the build already knows — the version, the package id and the argument
/// that selects MCP mode — and nothing in the build reads it back, so a released package could
/// advertise last release's version indefinitely without anything failing. That is what this test is
/// for.
/// </para>
/// <para>
/// The <c>packageArguments</c> entry is the one that is new here. This binary is two servers, and the
/// verb is what picks which: a manifest with no argument would tell every client to run the tool with
/// none, which exits 2 with a usage message that a client renders as "the server died immediately".
/// </para>
/// <para>
/// The manifest is read with <see cref="JsonDocument"/> rather than deserialized into a record,
/// because the test project runs with <c>JsonSerializerIsReflectionEnabledByDefault=false</c> (D6)
/// and this file has no business in a <c>JsonSerializerContext</c>.
/// </para>
/// </remarks>
public class McpServerManifestTests
{
    /// <summary>The file that identifies the repository root when walking up from the test assembly.</summary>
    private const string RootMarker = "claude-roslyn-lsp.slnx";

    private const string ManifestPath = ".mcp/server.json";
    private const string ChangelogPath = "CHANGELOG.md";
    private const string ServerProjectPath = "src/ClaudeRoslynLsp/ClaudeRoslynLsp.csproj";

    [Fact]
    public void ManifestVersionMatchesTheChangelog()
    {
        var root = FindRepositoryRoot();
        var changelogVersion = ReadChangelogVersion(root);

        using var manifest = ReadManifest(root);
        var package = SingleNuGetPackage(manifest);

        Assert.Equal(changelogVersion, manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal(changelogVersion, package.GetProperty("version").GetString());
    }

    [Fact]
    public void ManifestIdentifierMatchesThePackagedId()
    {
        var root = FindRepositoryRoot();

        var packageId = XDocument
            .Load(Path.Combine(root, ServerProjectPath))
            .Descendants("PackageId")
            .Select(x => x.Value)
            .SingleOrDefault();

        Assert.False(string.IsNullOrWhiteSpace(packageId), $"{ServerProjectPath} declares no <PackageId>.");

        using var manifest = ReadManifest(root);
        Assert.Equal(packageId, SingleNuGetPackage(manifest).GetProperty("identifier").GetString());
    }

    /// <summary>
    /// nuget.org only looks at the first <c>packages</c> entry whose <c>registryType</c> is
    /// <c>nuget</c>, and the server speaks stdio only — a second entry or a different transport would
    /// silently change what clients are told to run.
    /// </summary>
    [Fact]
    public void ManifestDeclaresExactlyOneStdioNuGetPackage()
    {
        var root = FindRepositoryRoot();
        using var manifest = ReadManifest(root);

        var package = SingleNuGetPackage(manifest);

        Assert.Equal("stdio", package.GetProperty("transport").GetProperty("type").GetString());
        Assert.Equal("io.github.lahma/claude-roslyn-lsp", manifest.RootElement.GetProperty("name").GetString());
    }

    /// <summary>
    /// The manifest has to name the <c>mcp</c> verb, because the binary has no default one.
    /// </summary>
    /// <remarks>
    /// A generated client configuration that omits it produces a process that exits 2 before the
    /// handshake — which a client reports as a server that failed to start, with the actual usage
    /// message on a stderr stream most of them do not show.
    /// </remarks>
    [Fact]
    public void ManifestPassesTheMcpVerbAsAPositionalArgument()
    {
        var root = FindRepositoryRoot();
        using var manifest = ReadManifest(root);

        var argument = Assert.Single(
            SingleNuGetPackage(manifest).GetProperty("packageArguments").EnumerateArray().ToList());

        Assert.Equal("positional", argument.GetProperty("type").GetString());
        Assert.Equal("mcp", argument.GetProperty("value").GetString());
    }

    /// <summary>
    /// The manifest is the configuration contract a client reads before it ever runs the server. None
    /// of these variables is a credential — this adapter has nothing to authenticate against — so
    /// nothing may be marked secret, and nothing may be marked required either: the whole point of
    /// the discovery chain is that the server starts usefully with an empty environment.
    /// </summary>
    [Fact]
    public void ManifestDeclaresOptionalNonSecretEnvironmentVariables()
    {
        var root = FindRepositoryRoot();
        using var manifest = ReadManifest(root);

        var variables = SingleNuGetPackage(manifest)
            .GetProperty("environmentVariables")
            .EnumerateArray()
            .ToList();

        Assert.NotEmpty(variables);

        foreach (var variable in variables)
        {
            var name = variable.GetProperty("name").GetString();

            Assert.StartsWith("CLAUDE_ROSLYN_LSP_", name, StringComparison.Ordinal);

            Assert.False(
                string.IsNullOrWhiteSpace(variable.GetProperty("description").GetString()),
                $"{name} has no description; the description is what a client shows next to the prompt.");

            Assert.False(
                variable.TryGetProperty("isSecret", out var secret) && secret.GetBoolean(),
                $"{name} is marked secret, but this server has no credentials — a variable that claims to be "
                + "one gets prompted for as though it were.");

            Assert.False(
                variable.TryGetProperty("isRequired", out var required) && required.GetBoolean(),
                $"{name} is marked required, but the server must start and be useful with an empty "
                + "environment; every one of these has a discovered or pinned default.");
        }
    }

    /// <summary>
    /// The manifest lists every variable the adapter reads, under its canonical spelling, and
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the configuration contract a client reads <em>before</em> it runs the server, and for
    /// several clients it is what generates the environment block a user then edits. A knob missing
    /// from it is a knob nobody discovers; a knob in it that nothing reads is a knob a user sets and
    /// then cannot work out why it did nothing. Both are silent, and neither fails anywhere else in
    /// the build.
    /// </para>
    /// <para>
    /// The expected set comes from <see cref="EnvironmentSurface"/>, which recovers it from
    /// <c>FromEnvironment</c>'s own source, so this test cannot agree with a stale list — adding a
    /// variable to the options record fails here until the manifest describes it. The two accepted
    /// aliases are deliberately <em>not</em> listed: they are read so that following the other half
    /// of the design document is not a silent no-op, not advertised as a second way to spell a
    /// setting.
    /// </para>
    /// </remarks>
    [Fact]
    public void ManifestDescribesEveryVariableTheAdapterReads()
    {
        var root = FindRepositoryRoot();
        using var manifest = ReadManifest(root);

        var declared = SingleNuGetPackage(manifest)
            .GetProperty("environmentVariables")
            .EnumerateArray()
            .Select(variable => variable.GetProperty("name").GetString()!)
            .ToList();

        Assert.Equal(declared.Count, declared.Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(
            EnvironmentSurface.Canonical.OrderBy(name => name, StringComparer.Ordinal),
            declared.OrderBy(name => name, StringComparer.Ordinal));
    }

    private static JsonElement SingleNuGetPackage(JsonDocument manifest)
    {
        var packages = manifest.RootElement.GetProperty("packages")
            .EnumerateArray()
            .Where(x => x.GetProperty("registryType").GetString() == "nuget")
            .ToList();

        Assert.Single(packages);
        return packages[0];
    }

    private static JsonDocument ReadManifest(string root)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ManifestPath)));

    /// <summary>
    /// The version authority: CHANGELOG.md's first line is a <c># version</c> header, which is what
    /// the Fallout build parses in <c>OnBuildInitialized</c>.
    /// </summary>
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
