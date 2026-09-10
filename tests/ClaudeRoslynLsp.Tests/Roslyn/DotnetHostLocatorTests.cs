using ClaudeRoslynLsp.Roslyn;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Roslyn;

/// <summary>
/// Covers the <c>dotnet</c> search and the parsing of <c>--list-runtimes</c>. Everything goes through
/// <see cref="DotnetProbe"/>, so no test depends on what is installed on the machine running it —
/// which is the only way "there is no .NET 10 here" can be a test case at all.
/// </summary>
public class DotnetHostLocatorTests
{
    /// <summary>Real output, including the ASP.NET and desktop frameworks that must be ignored.</summary>
    private const string ListRuntimes =
        """
        Microsoft.AspNetCore.App 9.0.14 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]
        Microsoft.AspNetCore.App 10.0.1 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]
        Microsoft.NETCore.App 8.0.22 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
        Microsoft.NETCore.App 10.0.1 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
        Microsoft.WindowsDesktop.App 10.0.1 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
        """;

    [Fact]
    public void EveryLineOfListRuntimesBecomesAnEntry()
    {
        var runtimes = DotnetHostLocator.ParseRuntimes(ListRuntimes);

        Assert.Equal(5, runtimes.Count);
        Assert.Equal("Microsoft.AspNetCore.App", runtimes[0].Name);
        Assert.Equal("9.0.14", runtimes[0].Version);
        Assert.Equal(@"C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App", runtimes[0].Location);
    }

    /// <summary>
    /// The bracketed path is the reason this is parsed positionally: it contains spaces, and on a
    /// developer machine it contains brackets too.
    /// </summary>
    [Fact]
    public void APathWithSpacesAndBracketsSurvivesParsing()
    {
        var runtimes = DotnetHostLocator.ParseRuntimes(
            @"Microsoft.NETCore.App 10.0.1 [/home/me/my [odd] dir/dotnet/shared/Microsoft.NETCore.App]");

        Assert.Single(runtimes);
        Assert.Equal("/home/me/my [odd] dir/dotnet/shared/Microsoft.NETCore.App", runtimes[0].Location);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a runtime line")]
    [InlineData("Microsoft.NETCore.App 10.0.1 no brackets here")]
    public void OutputThatIsNotARuntimeListYieldsNothing(string? output)
    {
        Assert.Empty(DotnetHostLocator.ParseRuntimes(output));
    }

    [Fact]
    public void OnlyANetTenSharedFrameworkCounts()
    {
        var runtimes = DotnetHostLocator.ParseRuntimes(ListRuntimes);
        var selected = DotnetHostLocator.SelectNet10(runtimes);

        Assert.NotNull(selected);
        Assert.Equal("Microsoft.NETCore.App", selected!.Name);
        Assert.Equal("10.0.1", selected.Version);
    }

    /// <summary>
    /// <c>--list-runtimes</c> sorts as text, which puts 10.0.10 before 10.0.9.
    /// </summary>
    [Fact]
    public void TheNewestNetTenWinsByVersionNotByListOrder()
    {
        var runtimes = DotnetHostLocator.ParseRuntimes(
            """
            Microsoft.NETCore.App 10.0.10 [/x]
            Microsoft.NETCore.App 10.0.9 [/x]
            """);

        Assert.Equal("10.0.10", DotnetHostLocator.SelectNet10(runtimes)!.Version);
    }

    [Fact]
    public void AStableBuildBeatsAPrereleaseOfTheSameVersion()
    {
        var runtimes = DotnetHostLocator.ParseRuntimes(
            """
            Microsoft.NETCore.App 10.0.0-preview.7 [/x]
            Microsoft.NETCore.App 10.0.0 [/x]
            """);

        Assert.Equal("10.0.0", DotnetHostLocator.SelectNet10(runtimes)!.Version);
    }

    [Fact]
    public void AHostWithNoNetTenIsNotUsableAndSaysWhy()
    {
        var runtimes = DotnetHostLocator.ParseRuntimes("Microsoft.NETCore.App 9.0.14 [/x]");

        Assert.Null(DotnetHostLocator.SelectNet10(runtimes));
    }

    [Fact]
    public void DotnetRootIsSearchedFirst()
    {
        var probe = Probe(
            variables: new() { ["DOTNET_ROOT"] = "/opt/dotnet", ["PATH"] = "/usr/bin" },
            existing: [Host("/opt/dotnet"), Host("/usr/bin")],
            listRuntimes: ListRuntimes);

        var result = DotnetHostLocator.Locate(probe);

        Assert.Equal(Host("/opt/dotnet"), result.Path);
        Assert.Equal("DOTNET_ROOT", result.Source);
        Assert.Equal(Path.GetDirectoryName(Host("/opt/dotnet")), result.DotnetRoot);
        Assert.True(result.IsUsable);
        Assert.Null(result.Failure);
    }

    [Fact]
    public void PathIsSearchedWhenDotnetRootIsUnset()
    {
        var probe = Probe(
            variables: new() { ["PATH"] = "/nowhere" + Path.PathSeparator + "/usr/bin" },
            existing: [Host("/usr/bin")],
            listRuntimes: ListRuntimes);

        var result = DotnetHostLocator.Locate(probe);

        Assert.Equal(Host("/usr/bin"), result.Path);
        Assert.Equal("PATH", result.Source);
    }

    [Fact]
    public void TheDefaultInstallLocationsAreSearchedLast()
    {
        var probe = Probe(
            variables: new() { ["HOME"] = "/home/me" },
            existing: [Host("/usr/share/dotnet")],
            listRuntimes: ListRuntimes);

        var result = DotnetHostLocator.Locate(probe);

        Assert.Equal(Host("/usr/share/dotnet"), result.Path);
        Assert.Equal("default install location", result.Source);
    }

    /// <summary>
    /// A stale <c>DOTNET_ROOT</c> pointing at an uninstalled SDK must not end the search: that is the
    /// most common shape of "the runtime is missing" that is not actually a missing runtime.
    /// </summary>
    [Fact]
    public void AStaleDotnetRootFallsThroughToTheRestOfTheChain()
    {
        var probe = Probe(
            variables: new() { ["DOTNET_ROOT"] = "/gone", ["PATH"] = "/usr/bin" },
            existing: [Host("/usr/bin")],
            listRuntimes: ListRuntimes);

        var result = DotnetHostLocator.Locate(probe);

        Assert.Equal(Host("/usr/bin"), result.Path);
        Assert.Contains(
            result.Chain,
            step => step.Contains(Host("/gone") + " (not found)", StringComparison.Ordinal));
    }

    [Fact]
    public void NoHostAtAllIsAFailureThatNamesTheFix()
    {
        var result = DotnetHostLocator.Locate(Probe(new() { ["PATH"] = "/usr/bin" }, [], ListRuntimes));

        Assert.Null(result.Path);
        Assert.False(result.IsUsable);
        Assert.Contains("Install .NET 10", result.Failure, StringComparison.Ordinal);
        Assert.NotEmpty(result.Chain);
    }

    [Fact]
    public void AHostWithoutNetTenIsFoundButRefused()
    {
        var result = DotnetHostLocator.Locate(Probe(
            new() { ["PATH"] = "/usr/bin" },
            [Host("/usr/bin")],
            "Microsoft.NETCore.App 9.0.14 [/x]"));

        Assert.Equal(Host("/usr/bin"), result.Path);
        Assert.False(result.IsUsable);
        Assert.Contains("no Microsoft.NETCore.App 10.x runtime", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostThatCannotBeRunIsSkipped()
    {
        var probe = new DotnetProbe(
            name => name == "PATH" ? "/broken" + Path.PathSeparator + "/usr/bin" : null,
            path => path == Host("/broken") || path == Host("/usr/bin"),
            path => path == Host("/broken") ? null : ListRuntimes,
            IsWindows: false);

        var result = DotnetHostLocator.Locate(probe);

        Assert.Equal(Host("/usr/bin"), result.Path);
    }

    /// <summary>The whole point of the search: the host it finds must be one Roslyn can run on.</summary>
    [Fact]
    public void TheRealHostIsResolvableOnThisMachine()
    {
        var result = DotnetHostLocator.Resolve();

        Assert.True(result.IsUsable, result.Failure ?? "no failure was reported either");
        Assert.Same(result, DotnetHostLocator.Resolve());
    }

    /// <summary>
    /// The host path the locator will build for a directory, in this platform's spelling.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.Combine(string, string)"/> uses a backslash on Windows, so a test that wrote
    /// <c>"/usr/bin/dotnet"</c> as a literal would pass on Linux and fail here for a reason that has
    /// nothing to do with the code under test.
    /// </remarks>
    private static string Host(string directory) => Path.Combine(directory, "dotnet");

    private static DotnetProbe Probe(
        Dictionary<string, string> variables,
        string[] existing,
        string listRuntimes) =>
        new(
            name => variables.GetValueOrDefault(name),
            path => existing.Contains(path, StringComparer.Ordinal),
            _ => listRuntimes,
            IsWindows: false);
}
