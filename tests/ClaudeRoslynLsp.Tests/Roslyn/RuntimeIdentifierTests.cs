using System.Runtime.InteropServices;

using ClaudeRoslynLsp.Roslyn;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Roslyn;

/// <summary>
/// Covers the host-to-RID mapping. The interesting cases are all on platforms the test is not
/// running on, which is why <see cref="RuntimeIdentifier.Compose"/> takes the platform rather than
/// reading it.
/// </summary>
public class RuntimeIdentifierTests
{
    /// <summary>Every platform Microsoft publishes the server for, and the RID it must produce.</summary>
    public static IEnumerable<object[]> Platforms =>
    [
        [true, false, false, Architecture.X64, "win-x64"],
        [true, false, false, Architecture.Arm64, "win-arm64"],
        [false, false, false, Architecture.X64, "linux-x64"],
        [false, false, false, Architecture.Arm64, "linux-arm64"],
        [false, false, true, Architecture.X64, "linux-musl-x64"],
        [false, false, true, Architecture.Arm64, "linux-musl-arm64"],
        [false, true, false, Architecture.X64, "osx-x64"],
        [false, true, false, Architecture.Arm64, "osx-arm64"],
    ];

    [TestCaseSource(nameof(Platforms))]
    public void EveryPublishedPlatformMapsToItsRuntimeIdentifier(
        bool isWindows,
        bool isMacOs,
        bool isMusl,
        Architecture architecture,
        string expected)
    {
        var rid = RuntimeIdentifier.Compose(isWindows, isMacOs, isMusl, architecture);

        Assert.That(rid, Is.EqualTo(expected));
        Assert.That(RuntimeIdentifier.IsSupported(rid), Is.True);
    }

    [Test]
    public void TheListHoldsTheEightPublishedRuntimeIdentifiers()
    {
        Assert.That(RuntimeIdentifier.All.Count, Is.EqualTo(8));
        Assert.That(RuntimeIdentifier.All.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(RuntimeIdentifier.All.Count));
        Assert.That(RuntimeIdentifier.All, Does.Contain("linux-musl-arm64"));
    }

    /// <summary>
    /// A host Microsoft does not publish for must be named, not corrected. Downloading the 64-bit
    /// payload for a 32-bit host produces a child that dies with a message about an image format.
    /// </summary>
    [TestCase(true, false, false, Architecture.X86, "win-x86")]
    [TestCase(false, false, false, Architecture.Arm, "linux-arm")]
    public void AnUnpublishedPlatformProducesAnUnsupportedRuntimeIdentifier(
        bool isWindows,
        bool isMacOs,
        bool isMusl,
        Architecture architecture,
        string expected)
    {
        var rid = RuntimeIdentifier.Compose(isWindows, isMacOs, isMusl, architecture);

        Assert.That(rid, Is.EqualTo(expected));
        Assert.That(RuntimeIdentifier.IsSupported(rid), Is.False);
    }

    [Test]
    public void MuslIsDetectedFromTheRuntimeIdentifierTheRuntimeReports()
    {
        Assert.That(RuntimeIdentifier.DetectMusl("linux-musl-x64", static () => []), Is.True);
        Assert.That(RuntimeIdentifier.DetectMusl("LINUX-MUSL-ARM64", static () => []), Is.True);
    }

    /// <summary>
    /// The loader probe is the half that matters: a portable build on Alpine reports the RID it was
    /// <em>built</em> for, which is plain <c>linux-x64</c>.
    /// </summary>
    [Test]
    public void MuslIsDetectedFromTheDynamicLoaderWhenTheRuntimeIdentifierDoesNotSaySo()
    {
        Assert.That(RuntimeIdentifier.DetectMusl("linux-x64", static () => ["/lib/ld-musl-x86_64.so.1"]), Is.True);
        Assert.That(RuntimeIdentifier.DetectMusl("linux-x64", static () => []), Is.False);
    }

    [Test]
    public void MuslDetectionToleratesAnUnknownRuntimeIdentifier()
    {
        Assert.That(RuntimeIdentifier.DetectMusl(null, static () => []), Is.False);
    }

    /// <summary>
    /// The host's own RID has to be one this test run could actually acquire a server for, or every
    /// other test in this file is describing a machine nobody has.
    /// </summary>
    [Test]
    public void ThisHostResolvesToAPublishedRuntimeIdentifier()
    {
        Assert.That(
            RuntimeIdentifier.IsSupported(RuntimeIdentifier.Current), Is.True,
            $"This host resolved to '{RuntimeIdentifier.Current}', which is not one of " +
            $"{string.Join(", ", RuntimeIdentifier.All)}.");
    }
}
