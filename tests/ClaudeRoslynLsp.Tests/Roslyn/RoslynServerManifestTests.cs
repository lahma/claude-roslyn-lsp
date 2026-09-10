using ClaudeRoslynLsp.Roslyn;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Roslyn;

/// <summary>
/// Rule-enforcing tests for the pin. Do not delete one of these to make a change pass (AGENTS.md,
/// hard rule 7): what they hold in place is that a release cannot ship claiming to verify a download
/// it has no hash for, and cannot ship a version its own changelog does not mention.
/// </summary>
/// <remarks>
/// This is the plan's <c>RoslynReleaseTests</c> under the name the code actually uses. The hash table
/// itself is generated (<c>dotnet fallout UpdateRoslynPin</c>), so what is asserted here is its
/// <em>shape</em> and its agreement with the rest of the repository — the numbers are the target's
/// job, and a test that restated them would only be a second place to type them wrong.
/// </remarks>
public class RoslynServerManifestTests
{
    /// <summary>Base64 of 64 bytes is 88 characters ending in two pad characters.</summary>
    private const int Sha512Base64Length = 88;

    [Fact]
    public void EveryPublishedRuntimeIdentifierHasAHash()
    {
        Assert.Equal(
            RuntimeIdentifier.All.Order(StringComparer.Ordinal),
            RoslynServerManifest.Sha512ByRuntimeIdentifier.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryHashIsABase64Sha512()
    {
        foreach (var (rid, hash) in RoslynServerManifest.Sha512ByRuntimeIdentifier)
        {
            Assert.True(
                hash.Length == Sha512Base64Length,
                $"{rid}: expected {Sha512Base64Length} base64 characters, got {hash.Length} ('{hash}').");

            Assert.EndsWith("==", hash, StringComparison.Ordinal);
            Assert.True(Convert.TryFromBase64String(hash, new byte[64], out var written), $"{rid}: not base64.");
            Assert.Equal(64, written);
        }
    }

    [Fact]
    public void TheHashesAreAllDifferent()
    {
        // Eight identical hashes would mean the generator wrote one payload's number eight times,
        // which is exactly the mistake a table of long base64 strings hides best.
        var hashes = RoslynServerManifest.Sha512ByRuntimeIdentifier.Values.ToList();

        Assert.Equal(hashes.Count, hashes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ThePinnedVersionIsANuGetPrereleaseVersion()
    {
        var dash = RoslynServerManifest.Version.IndexOf('-', StringComparison.Ordinal);

        Assert.True(dash > 0, $"'{RoslynServerManifest.Version}' has no prerelease label; the server is prerelease-only.");
        Assert.True(Version.TryParse(RoslynServerManifest.Version[..dash], out var numeric));
        Assert.True(numeric!.Major > 0);
        Assert.NotEmpty(RoslynServerManifest.Version[(dash + 1)..]);
    }

    /// <summary>
    /// The pinned version is user-visible — it is what a user actually runs — so a bump to it is a
    /// release note even when nothing in this repository moves.
    /// </summary>
    [Fact]
    public void TheChangelogNamesThePinnedVersion()
    {
        var changelog = File.ReadAllText(RepositoryLayout.Path_("CHANGELOG.md"));

        Assert.Contains(RoslynServerManifest.Version, changelog, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratedBlockIsMarkedAsGenerated()
    {
        var manifest = File.ReadAllText(
            RepositoryLayout.Path_("src", "ClaudeRoslynLsp", "Roslyn", "RoslynServerManifest.cs"));

        Assert.Contains("// <generated pin>", manifest, StringComparison.Ordinal);
        Assert.Contains("// </generated pin>", manifest, StringComparison.Ordinal);
        Assert.Contains("UpdateRoslynPin", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePayloadUrlIsTheFlatContainerAddressInLowerCase()
    {
        var url = RoslynServerManifest.PackageUrl("win-x64", "5.12.0-PREVIEW.1");

        Assert.Equal(
            "https://api.nuget.org/v3-flatcontainer/roslyn-language-server.win-x64/5.12.0-preview.1/" +
            "roslyn-language-server.win-x64.5.12.0-preview.1.nupkg",
            url.AbsoluteUri);
    }

    [Fact]
    public void OnlyThePinnedVersionHasAKnownHash()
    {
        Assert.NotNull(RoslynServerManifest.Sha512For("win-x64", RoslynServerManifest.Version));
        Assert.Null(RoslynServerManifest.Sha512For("win-x64", "5.5.0-2.26103.6"));
        Assert.Null(RoslynServerManifest.Sha512For("win-x86", RoslynServerManifest.Version));
    }

    /// <summary>
    /// D26: a flag the pinned build has is not a flag an older build has, and Roslyn exits on an
    /// unknown option rather than ignoring it.
    /// </summary>
    [Fact]
    public void FeatureFlagsAreThePinsOnlyForThePin()
    {
        var pinned = RoslynServerManifest.FeaturesFor(RoslynServerManifest.Version);

        Assert.True(pinned.SupportsStdio);
        Assert.True(pinned.SupportsClientProcessId);
        Assert.True(pinned.SupportsDaemon);

        var older = RoslynServerManifest.FeaturesFor("5.5.0-2.26103.6");

        Assert.True(older.SupportsStdio);
        Assert.False(older.SupportsClientProcessId);
        Assert.False(older.SupportsDaemon);
    }

    [Fact]
    public void ThePayloadPrefixIsTheServerDirectoryInsideTheNupkg()
    {
        Assert.Equal("tools/net10.0/linux-musl-arm64/", RoslynServerManifest.PayloadPrefix("linux-musl-arm64"));
        Assert.Equal("roslyn-language-server.osx-arm64", RoslynServerManifest.PackageId("osx-arm64"));
    }
}
