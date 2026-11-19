using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Roslyn;

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

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

    [Test]
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

        Assert.That(result.Kind, Is.EqualTo(RoslynSourceKind.Override));
        Assert.That(result.LaunchTarget, Is.EqualTo(Path.Combine(server, Assembly)));
        Assert.That(result.LaunchKind, Is.EqualTo(RoslynLaunchKind.Managed));
        Assert.That(result.Verified, Is.False);
        Assert.That(result.Chain, Has.Some.Matches<RoslynResolutionStep>(step => step!.Outcome == RoslynResolutionStepOutcome.Used));
    }

    [Test]
    public async Task AnOverrideMayNameTheAssemblyItself()
    {
        using var temp = TempWorkspace.Create("locator-override-dll");
        var assembly = temp.File_(Path.Combine("elsewhere", Assembly), "server");

        var result = await Resolve(temp, new ClaudeRoslynLspOptions { Home = temp.Path_("home"), RoslynPath = assembly });

        Assert.That(result.Kind, Is.EqualTo(RoslynSourceKind.Override));
        Assert.That(result.LaunchTarget, Is.EqualTo(assembly));
        Assert.That(result.LaunchKind, Is.EqualTo(RoslynLaunchKind.Managed));
    }

    /// <summary>
    /// The smoke test's shape: point the variable at a native executable and let
    /// <c>CLAUDE_ROSLYN_LSP_ROSLYN_ARGS</c> add the verb.
    /// </summary>
    [Test]
    public async Task AnOverrideMayNameANativeExecutable()
    {
        using var temp = TempWorkspace.Create("locator-override-exe");
        var executable = temp.File_(Path.Combine("fake", "claude-roslyn-lsp.exe"), "binary");

        var result = await Resolve(temp, new ClaudeRoslynLspOptions { Home = temp.Path_("home"), RoslynPath = executable });

        Assert.That(result.LaunchKind, Is.EqualTo(RoslynLaunchKind.Native));
        Assert.That(result.LaunchTarget, Is.EqualTo(executable));
    }

    /// <summary>
    /// D34. Falling back to a download would be indistinguishable from the variable being ignored,
    /// which is the configuration bug with no symptom.
    /// </summary>
    [Test]
    public async Task AnOverrideThatDoesNotResolveFailsInsteadOfFallingThrough()
    {
        using var temp = TempWorkspace.Create("locator-override-missing");

        var result = await Resolve(temp, new ClaudeRoslynLspOptions
        {
            Home = temp.Path_("home"),
            RoslynPath = temp.Path_("nothing-here"),
            RoslynPathVariable = "CLAUDE_ROSLYN_LSP_SERVER_PATH",
        });

        Assert.That(result.IsResolved, Is.False);
        Assert.That(result.Failure, Contains.Substring("CLAUDE_ROSLYN_LSP_SERVER_PATH").Using(StringComparison.Ordinal));
        Assert.That(result.Failure, Contains.Substring("deliberately not ignored").Using(StringComparison.Ordinal));
        Assert.That(result.Chain, Has.None.Matches<RoslynResolutionStep>(step => step!.Source == "cache"));
    }

    [Test]
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

        Assert.That(result.Kind, Is.EqualTo(RoslynSourceKind.Cache));
        Assert.That(result.Directory, Is.EqualTo(directory));
        Assert.That(result.Verified, Is.True);
        Assert.That(result.Features.SupportsClientProcessId, Is.True);
    }

    [Test]
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

        Assert.That(result.Kind, Is.Not.EqualTo(RoslynSourceKind.Cache));
        Assert.That(
            result.Chain,
            Has.Some.Matches<RoslynResolutionStep>(step => step!.Source == "cache" && step.Outcome == RoslynResolutionStepOutcome.Rejected));
    }

    [Test]
    public async Task AGlobalToolStoreAtThePinnedVersionIsUsedButNotCalledVerified()
    {
        using var temp = TempWorkspace.Create("locator-toolstore");
        var store = temp.Directory_("store");

        var payload = RoslynServerLocator.ToolStorePayloadDirectory(
            store, RoslynServerManifest.Version, RuntimeIdentifier.Current);

        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, Assembly), "server");

        var result = await Resolve(temp, new ClaudeRoslynLspOptions { Home = temp.Path_("home") }, store);

        Assert.That(result.Kind, Is.EqualTo(RoslynSourceKind.ToolStore));
        Assert.That(result.Directory, Is.EqualTo(payload));

        // C4: the .nupkg.sha512 beside a tool-store payload is not the hash of that payload, so there
        // is nothing here that could be verified even in principle.
        Assert.That(result.Verified, Is.False);
    }

    /// <summary>D33: present, not used, and said so — the most useful line doctor can print.</summary>
    [Test]
    public async Task AGlobalToolStoreAtAnotherVersionIsReportedAndNotUsed()
    {
        using var temp = TempWorkspace.Create("locator-toolstore-wrong");
        var store = temp.Directory_("store");

        var payload = RoslynServerLocator.ToolStorePayloadDirectory(store, "5.5.0-2.26103.6", RuntimeIdentifier.Current);
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, Assembly), "server");

        var result = await Resolve(temp, new ClaudeRoslynLspOptions { Home = temp.Path_("home") }, store);

        Assert.That(result.Kind, Is.Not.EqualTo(RoslynSourceKind.ToolStore));

        Assert.That(result.Chain, Has.Exactly(1).Matches<RoslynResolutionStep>(x => x!.Source == "global tool store"));
        var step = result.Chain.Single(x => x.Source == "global tool store");
        Assert.That(step.Outcome, Is.EqualTo(RoslynResolutionStepOutcome.Rejected));
        Assert.That(step.Detail, Contains.Substring("5.5.0-2.26103.6").Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task OfflineTurnsAMissingServerIntoAnExplanationRatherThanADownload()
    {
        using var temp = TempWorkspace.Create("locator-offline");

        var result = await Resolve(temp, new ClaudeRoslynLspOptions { Home = temp.Path_("home"), Offline = true });

        Assert.That(result.IsResolved, Is.False);
        Assert.That(result.Failure, Contains.Substring("CLAUDE_ROSLYN_LSP_OFFLINE").Using(StringComparison.Ordinal));
        Assert.That(result.Failure, Contains.Substring("install").Using(StringComparison.Ordinal));

        Assert.That(result.Chain, Has.Exactly(1).Matches<RoslynResolutionStep>(x => x!.Source == "nuget.org"));
        var step = result.Chain.Single(x => x.Source == "nuget.org");
        Assert.That(step.Outcome, Is.EqualTo(RoslynResolutionStepOutcome.Skipped));
    }

    /// <summary>
    /// D43: <c>doctor</c> resolves with downloads disallowed, and the chain says what it would have
    /// fetched rather than fetching it.
    /// </summary>
    [Test]
    public async Task WithDownloadsDisallowedTheChainSaysWhatItWouldHaveFetched()
    {
        using var temp = TempWorkspace.Create("locator-would-download");

        var result = await Resolve(temp, new ClaudeRoslynLspOptions { Home = temp.Path_("home") });

        Assert.That(result.IsResolved, Is.False);

        Assert.That(result.Chain, Has.Exactly(1).Matches<RoslynResolutionStep>(x => x!.Source == "nuget.org"));
        var step = result.Chain.Single(x => x.Source == "nuget.org");
        Assert.That(step.Detail, Contains.Substring("would download").Using(StringComparison.Ordinal));
        Assert.That(step.Detail, Contains.Substring("api.nuget.org").Using(StringComparison.Ordinal));
    }

    [Test]
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

        var result = await locator.ResolveAsync(allowDownload: true, cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsResolved, Is.False);
        Assert.That(
            result.Failure,
            Contains.Substring("does not publish roslyn-language-server for 'win-x86'").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// A version override has no hash in the pin (D25), so the result must not claim verification and
    /// must not claim the pin's feature flags either (D26).
    /// </summary>
    [Test]
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

        Assert.That(result.Kind, Is.EqualTo(RoslynSourceKind.Cache));
        Assert.That(result.Version, Is.EqualTo("5.5.0-2.26103.6"));
        Assert.That(result.Verified, Is.False);
        Assert.That(result.Features.SupportsClientProcessId, Is.False);
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

        return locator.ResolveAsync(allowDownload: false, cancellationToken: TestContext.CurrentContext.CancellationToken);
    }
}
