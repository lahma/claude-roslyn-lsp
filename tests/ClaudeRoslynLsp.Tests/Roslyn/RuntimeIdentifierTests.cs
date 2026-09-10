using System.Runtime.InteropServices;

using ClaudeRoslynLsp.Roslyn;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Roslyn;

/// <summary>
/// Covers the host-to-RID mapping. The interesting cases are all on platforms the test is not
/// running on, which is why <see cref="RuntimeIdentifier.Compose"/> takes the platform rather than
/// reading it.
/// </summary>
public class RuntimeIdentifierTests
{
    /// <summary>Every platform Microsoft publishes the server for, and the RID it must produce.</summary>
    public static TheoryData<bool, bool, bool, Architecture, string> Platforms => new()
    {
        { true, false, false, Architecture.X64, "win-x64" },
        { true, false, false, Architecture.Arm64, "win-arm64" },
        { false, false, false, Architecture.X64, "linux-x64" },
        { false, false, false, Architecture.Arm64, "linux-arm64" },
        { false, false, true, Architecture.X64, "linux-musl-x64" },
        { false, false, true, Architecture.Arm64, "linux-musl-arm64" },
        { false, true, false, Architecture.X64, "osx-x64" },
        { false, true, false, Architecture.Arm64, "osx-arm64" },
    };

    [Theory]
    [MemberData(nameof(Platforms))]
    public void EveryPublishedPlatformMapsToItsRuntimeIdentifier(
        bool isWindows,
        bool isMacOs,
        bool isMusl,
        Architecture architecture,
        string expected)
    {
        var rid = RuntimeIdentifier.Compose(isWindows, isMacOs, isMusl, architecture);

        Assert.Equal(expected, rid);
        Assert.True(RuntimeIdentifier.IsSupported(rid));
    }

    [Fact]
    public void TheListHoldsTheEightPublishedRuntimeIdentifiers()
    {
        Assert.Equal(8, RuntimeIdentifier.All.Count);
        Assert.Equal(RuntimeIdentifier.All.Count, RuntimeIdentifier.All.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("linux-musl-arm64", RuntimeIdentifier.All);
    }

    /// <summary>
    /// A host Microsoft does not publish for must be named, not corrected. Downloading the 64-bit
    /// payload for a 32-bit host produces a child that dies with a message about an image format.
    /// </summary>
    [Theory]
    [InlineData(true, false, false, Architecture.X86, "win-x86")]
    [InlineData(false, false, false, Architecture.Arm, "linux-arm")]
    public void AnUnpublishedPlatformProducesAnUnsupportedRuntimeIdentifier(
        bool isWindows,
        bool isMacOs,
        bool isMusl,
        Architecture architecture,
        string expected)
    {
        var rid = RuntimeIdentifier.Compose(isWindows, isMacOs, isMusl, architecture);

        Assert.Equal(expected, rid);
        Assert.False(RuntimeIdentifier.IsSupported(rid));
    }

    [Fact]
    public void MuslIsDetectedFromTheRuntimeIdentifierTheRuntimeReports()
    {
        Assert.True(RuntimeIdentifier.DetectMusl("linux-musl-x64", static () => []));
        Assert.True(RuntimeIdentifier.DetectMusl("LINUX-MUSL-ARM64", static () => []));
    }

    /// <summary>
    /// The loader probe is the half that matters: a portable build on Alpine reports the RID it was
    /// <em>built</em> for, which is plain <c>linux-x64</c>.
    /// </summary>
    [Fact]
    public void MuslIsDetectedFromTheDynamicLoaderWhenTheRuntimeIdentifierDoesNotSaySo()
    {
        Assert.True(RuntimeIdentifier.DetectMusl("linux-x64", static () => ["/lib/ld-musl-x86_64.so.1"]));
        Assert.False(RuntimeIdentifier.DetectMusl("linux-x64", static () => []));
    }

    [Fact]
    public void MuslDetectionToleratesAnUnknownRuntimeIdentifier()
    {
        Assert.False(RuntimeIdentifier.DetectMusl(null, static () => []));
    }

    /// <summary>
    /// The host's own RID has to be one this test run could actually acquire a server for, or every
    /// other test in this file is describing a machine nobody has.
    /// </summary>
    [Fact]
    public void ThisHostResolvesToAPublishedRuntimeIdentifier()
    {
        Assert.True(
            RuntimeIdentifier.IsSupported(RuntimeIdentifier.Current),
            $"This host resolved to '{RuntimeIdentifier.Current}', which is not one of " +
            $"{string.Join(", ", RuntimeIdentifier.All)}.");
    }
}
