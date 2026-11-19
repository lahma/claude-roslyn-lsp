using ClaudeRoslynLsp.Roslyn;

using NUnit.Framework;

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

    [Test]
    public void EveryPublishedRuntimeIdentifierHasAHash()
    {
        Assert.That(
            RoslynServerManifest.Sha512ByRuntimeIdentifier.Keys.Order(StringComparer.Ordinal),
            Is.EqualTo(RuntimeIdentifier.All.Order(StringComparer.Ordinal)));
    }

    [Test]
    public void EveryHashIsABase64Sha512()
    {
        foreach (var (rid, hash) in RoslynServerManifest.Sha512ByRuntimeIdentifier)
        {
            Assert.That(
                hash.Length == Sha512Base64Length, Is.True,
                $"{rid}: expected {Sha512Base64Length} base64 characters, got {hash.Length} ('{hash}').");

            Assert.That(hash, Does.EndWith("==").Using(StringComparison.Ordinal));
            Assert.That(Convert.TryFromBase64String(hash, new byte[64], out var written), Is.True, $"{rid}: not base64.");
            Assert.That(written, Is.EqualTo(64));
        }
    }

    [Test]
    public void TheHashesAreAllDifferent()
    {
        // Eight identical hashes would mean the generator wrote one payload's number eight times,
        // which is exactly the mistake a table of long base64 strings hides best.
        var hashes = RoslynServerManifest.Sha512ByRuntimeIdentifier.Values.ToList();

        Assert.That(hashes.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(hashes.Count));
    }

    [Test]
    public void ThePinnedVersionIsANuGetPrereleaseVersion()
    {
        var dash = RoslynServerManifest.Version.IndexOf('-', StringComparison.Ordinal);

        Assert.That(dash > 0, Is.True, $"'{RoslynServerManifest.Version}' has no prerelease label; the server is prerelease-only.");
        Assert.That(Version.TryParse(RoslynServerManifest.Version[..dash], out var numeric), Is.True);
        Assert.That(numeric!.Major > 0, Is.True);
        Assert.That(RoslynServerManifest.Version[(dash + 1)..], Is.Not.Empty);
    }

    /// <summary>
    /// The pinned version is user-visible — it is what a user actually runs — so a bump to it is a
    /// release note even when nothing in this repository moves.
    /// </summary>
    [Test]
    public void TheChangelogNamesThePinnedVersion()
    {
        var changelog = File.ReadAllText(RepositoryLayout.Path_("CHANGELOG.md"));

        Assert.That(changelog, Contains.Substring(RoslynServerManifest.Version).Using(StringComparison.Ordinal));
    }

    [Test]
    public void TheGeneratedBlockIsMarkedAsGenerated()
    {
        var manifest = File.ReadAllText(
            RepositoryLayout.Path_("src", "ClaudeRoslynLsp", "Roslyn", "RoslynServerManifest.cs"));

        Assert.That(manifest, Contains.Substring("// <generated pin>").Using(StringComparison.Ordinal));
        Assert.That(manifest, Contains.Substring("// </generated pin>").Using(StringComparison.Ordinal));
        Assert.That(manifest, Contains.Substring("UpdateRoslynPin").Using(StringComparison.Ordinal));
    }

    [Test]
    public void ThePayloadUrlIsTheFlatContainerAddressInLowerCase()
    {
        var url = RoslynServerManifest.PackageUrl("win-x64", "5.12.0-PREVIEW.1");

        Assert.That(
            url.AbsoluteUri,
            Is.EqualTo("https://api.nuget.org/v3-flatcontainer/roslyn-language-server.win-x64/5.12.0-preview.1/" +
            "roslyn-language-server.win-x64.5.12.0-preview.1.nupkg"));
    }

    [Test]
    public void OnlyThePinnedVersionHasAKnownHash()
    {
        Assert.That(RoslynServerManifest.Sha512For("win-x64", RoslynServerManifest.Version), Is.Not.Null);
        Assert.That(RoslynServerManifest.Sha512For("win-x64", "5.5.0-2.26103.6"), Is.Null);
        Assert.That(RoslynServerManifest.Sha512For("win-x86", RoslynServerManifest.Version), Is.Null);
    }

    /// <summary>
    /// D26: a flag the pinned build has is not a flag an older build has, and Roslyn exits on an
    /// unknown option rather than ignoring it.
    /// </summary>
    [Test]
    public void FeatureFlagsAreThePinsOnlyForThePin()
    {
        var pinned = RoslynServerManifest.FeaturesFor(RoslynServerManifest.Version);

        Assert.That(pinned.SupportsStdio, Is.True);
        Assert.That(pinned.SupportsClientProcessId, Is.True);
        Assert.That(pinned.SupportsDaemon, Is.True);

        var older = RoslynServerManifest.FeaturesFor("5.5.0-2.26103.6");

        Assert.That(older.SupportsStdio, Is.True);
        Assert.That(older.SupportsClientProcessId, Is.False);
        Assert.That(older.SupportsDaemon, Is.False);
    }

    [Test]
    public void ThePayloadPrefixIsTheServerDirectoryInsideTheNupkg()
    {
        Assert.That(RoslynServerManifest.PayloadPrefix("linux-musl-arm64"), Is.EqualTo("tools/net10.0/linux-musl-arm64/"));
        Assert.That(RoslynServerManifest.PackageId("osx-arm64"), Is.EqualTo("roslyn-language-server.osx-arm64"));
    }
}
