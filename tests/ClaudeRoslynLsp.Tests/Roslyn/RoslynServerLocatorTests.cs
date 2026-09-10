using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Roslyn;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Roslyn;

/// <summary>
/// Covers the acquisition chain of D32: override, cache, tool store at the pinned version only, and
/// the two ways the last step can refuse.
/// </summary>
/// <remarks>
/// No test here downloads anything: the locator is constructed without a downloader factory, which is
/// also how <c>doctor</c> constructs it, so "would have downloaded" is a state the chain reports
/// rather than an action it takes.
/// </remarks>
public class RoslynServerLocatorTests
{
    /// <summary>The file whose presence means "a Roslyn server lives here".</summary>
    private const string Assembly = RoslynServerManifest.ServerAssemblyName;

    [Fact]
    public async Task AnOverrideDirectoryWinsOutright()
    {
        using var temp = TempWorkspace.Create("locator-override-dir");
        var server = temp.Directory_("elsewhere");
        File.WriteAllText(Path.Combine(server, Assembly), "server");

        var result = await Resolve(temp, new ClaudeRoslynLspOptions
        {
            Home = temp.Path_("home"),
            RoslynPath = server,
            RoslynPathVariable = "CLAUDE_ROSLYN_LSP_ROSLYN_PATH",
        });

        Assert.Equal(RoslynSourceKind.Override, result.Kind);
        Assert.Equal(Path.Combine(server, Assembly), result.LaunchTarget);
        Assert.Equal(RoslynLaunchKind.Managed, result.LaunchKind);
        Assert.False(result.Verified);
        Assert.Contains(result.Chain, step => step.Outcome == RoslynResolutionStepOutcome.Used);
    }

    [Fact]
    public async Task AnOverrideMayNameTheAssemblyItself()
    {
        using var temp = TempWorkspace.Create("locator-override-dll");
        var assembly = temp.File_(Path.Combine("elsewhere", Assembly), "server");

        var result = await Resolve(temp, new ClaudeRoslynLspOptions { Home = temp.Path_("home"), RoslynPath = assembly });

        Assert.Equal(RoslynSourceKind.Override, result.Kind);
        Assert.Equal(assembly, result.LaunchTarget);
        Assert.Equal(RoslynLaunchKind.Managed, result.LaunchKind);
    }

    /// <summary>
    /// The smoke test's shape: point the variable at a native executable and let
    /// <c>CLAUDE_ROSLYN_LSP_ROSLYN_ARGS</c> add the verb.
    /// </summary>
    [Fact]
    public async Task AnOverrideMayNameANativeExecutable()
    {
        using var temp = TempWorkspace.Create("locator-override-exe");
        var executable = temp.File_(Path.Combine("fake", "claude-roslyn-lsp.exe"), "binary");

        var result = await Resolve(temp, new ClaudeRoslynLspOptions { Home = temp.Path_("home"), RoslynPath = executable });

        Assert.Equal(RoslynLaunchKind.Native, result.LaunchKind);
        Assert.Equal(executable, result.LaunchTarget);
    }

    /// <summary>
    /// D34. Falling back to a download would be indistinguishable from the variable being ignored,
    /// which is the configuration bug with no symptom.
    /// </summary>
    [Fact]
    public async Task AnOverrideThatDoesNotResolveFailsInsteadOfFallingThrough()
    {
        using var temp = TempWorkspace.Create("locator-override-missing");

        var result = await Resolve(temp, new ClaudeRoslynLspOptions
        {
            Home = temp.Path_("home"),
            RoslynPath = temp.Path_("nothing-here"),
            RoslynPathVariable = "CLAUDE_ROSLYN_LSP_SERVER_PATH",
        });

        Assert.False(result.IsResolved);
        Assert.Contains("CLAUDE_ROSLYN_LSP_SERVER_PATH", result.Failure, StringComparison.Ordinal);
        Assert.Contains("deliberately not ignored", result.Failure, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Chain, step => step.Source == "cache");
    }

    [Fact]
    public async Task AWarmCacheWinsOverEverythingAfterIt()
    {
        using var temp = TempWorkspace.Create("locator-cache");
        var options = new ClaudeRoslynLspOptions { Home = temp.Path_("home") };
        var paths = AdapterPaths.Resolve(options, static _ => null);
        var hash = RoslynServerManifest.Sha512ByRuntimeIdentifier[RuntimeIdentifier.Current];

        var directory = paths.ServerDirectory(RoslynServerManifest.Version, RuntimeIdentifier.Current);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, Assembly), "server");
        File.WriteAllText(Path.Combine(directory, ".complete"), hash);

        var result = await Resolve(temp, options);

        Assert.Equal(RoslynSourceKind.Cache, result.Kind);
        Assert.Equal(directory, result.Directory);
        Assert.True(result.Verified);
        Assert.True(result.Features.SupportsClientProcessId);
    }

    [Fact]
    public async Task ACacheEntryWhoseMarkerDisagreesWithThePinIsRejected()
    {
        using var temp = TempWorkspace.Create("locator-cache-stale");
        var options = new ClaudeRoslynLspOptions { Home = temp.Path_("home") };
        var paths = AdapterPaths.Resolve(options, static _ => null);

        var directory = paths.ServerDirectory(RoslynServerManifest.Version, RuntimeIdentifier.Current);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, Assembly), "server");
        File.WriteAllText(Path.Combine(directory, ".complete"), "a-hash-from-an-older-pin");

        var result = await Resolve(temp, options);

        Assert.NotEqual(RoslynSourceKind.Cache, result.Kind);
        Assert.Contains(
            result.Chain,
            step => step.Source == "cache" && step.Outcome == RoslynResolutionStepOutcome.Rejected);
    }

    [Fact]
    public async Task AGlobalToolStoreAtThePinnedVersionIsUsedButNotCalledVerified()
    {
        using var temp = TempWorkspace.Create("locator-toolstore");
        var store = temp.Directory_("store");

        var payload = RoslynServerLocator.ToolStorePayloadDirectory(
            store, RoslynServerManifest.Version, RuntimeIdentifier.Current);

        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, Assembly), "server");

        var result = await Resolve(temp, new ClaudeRoslynLspOptions { Home = temp.Path_("home") }, store);

        Assert.Equal(RoslynSourceKind.ToolStore, result.Kind);
        Assert.Equal(payload, result.Directory);

        // C4: the .nupkg.sha512 beside a tool-store payload is not the hash of that payload, so there
        // is nothing here that could be verified even in principle.
        Assert.False(result.Verified);
    }

    /// <summary>D33: present, not used, and said so — the most useful line doctor can print.</summary>
    [Fact]
    public async Task AGlobalToolStoreAtAnotherVersionIsReportedAndNotUsed()
    {
        using var temp = TempWorkspace.Create("locator-toolstore-wrong");
        var store = temp.Directory_("store");

        var payload = RoslynServerLocator.ToolStorePayloadDirectory(store, "5.5.0-2.26103.6", RuntimeIdentifier.Current);
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, Assembly), "server");

        var result = await Resolve(temp, new ClaudeRoslynLspOptions { Home = temp.Path_("home") }, store);

        Assert.NotEqual(RoslynSourceKind.ToolStore, result.Kind);

        var step = Assert.Single(result.Chain, x => x.Source == "global tool store");
        Assert.Equal(RoslynResolutionStepOutcome.Rejected, step.Outcome);
        Assert.Contains("5.5.0-2.26103.6", step.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OfflineTurnsAMissingServerIntoAnExplanationRatherThanADownload()
    {
        using var temp = TempWorkspace.Create("locator-offline");

        var result = await Resolve(temp, new ClaudeRoslynLspOptions { Home = temp.Path_("home"), Offline = true });

        Assert.False(result.IsResolved);
        Assert.Contains("CLAUDE_ROSLYN_LSP_OFFLINE", result.Failure, StringComparison.Ordinal);
        Assert.Contains("install", result.Failure, StringComparison.Ordinal);

        var step = Assert.Single(result.Chain, x => x.Source == "nuget.org");
        Assert.Equal(RoslynResolutionStepOutcome.Skipped, step.Outcome);
    }

    /// <summary>
    /// D43: <c>doctor</c> resolves with downloads disallowed, and the chain says what it would have
    /// fetched rather than fetching it.
    /// </summary>
    [Fact]
    public async Task WithDownloadsDisallowedTheChainSaysWhatItWouldHaveFetched()
    {
        using var temp = TempWorkspace.Create("locator-would-download");

        var result = await Resolve(temp, new ClaudeRoslynLspOptions { Home = temp.Path_("home") });

        Assert.False(result.IsResolved);

        var step = Assert.Single(result.Chain, x => x.Source == "nuget.org");
        Assert.Contains("would download", step.Detail, StringComparison.Ordinal);
        Assert.Contains("api.nuget.org", step.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnpublishedRuntimeIdentifierIsRefusedBeforeAnyNetworkCall()
    {
        using var temp = TempWorkspace.Create("locator-bad-rid");
        var options = new ClaudeRoslynLspOptions { Home = temp.Path_("home") };

        var locator = new RoslynServerLocator(
            options,
            AdapterPaths.Resolve(options, static _ => null),
            NullLogger.Instance,
            runtimeIdentifier: "win-x86",
            toolStoreRoot: temp.Path_("no-store"));

        var result = await locator.ResolveAsync(allowDownload: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsResolved);
        Assert.Contains("does not publish roslyn-language-server for 'win-x86'", result.Failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// A version override has no hash in the pin (D25), so the result must not claim verification and
    /// must not claim the pin's feature flags either (D26).
    /// </summary>
    [Fact]
    public async Task AVersionOverrideMovesTheCacheDirectoryAndDropsTheHash()
    {
        using var temp = TempWorkspace.Create("locator-version-override");
        var options = new ClaudeRoslynLspOptions { Home = temp.Path_("home"), RoslynVersion = "5.5.0-2.26103.6" };
        var paths = AdapterPaths.Resolve(options, static _ => null);

        var directory = paths.ServerDirectory("5.5.0-2.26103.6", RuntimeIdentifier.Current);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, Assembly), "server");
        File.WriteAllText(Path.Combine(directory, ".complete"), "whatever");

        var result = await Resolve(temp, options);

        Assert.Equal(RoslynSourceKind.Cache, result.Kind);
        Assert.Equal("5.5.0-2.26103.6", result.Version);
        Assert.False(result.Verified);
        Assert.False(result.Features.SupportsClientProcessId);
    }

    private static Task<RoslynResolution> Resolve(
        TempWorkspace temp,
        ClaudeRoslynLspOptions options,
        string? toolStoreRoot = null)
    {
        var locator = new RoslynServerLocator(
            options,
            AdapterPaths.Resolve(options, static _ => null),
            NullLogger.Instance,
            toolStoreRoot: toolStoreRoot ?? temp.Path_("no-store"));

        return locator.ResolveAsync(allowDownload: false, cancellationToken: TestContext.Current.CancellationToken);
    }
}
