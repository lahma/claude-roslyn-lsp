using ClaudeRoslynLsp.Roslyn;

using NUnit.Framework;

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

    [Test]
    public void EveryLineOfListRuntimesBecomesAnEntry()
    {
        var runtimes = DotnetHostLocator.ParseRuntimes(ListRuntimes);

        Assert.That(runtimes.Count, Is.EqualTo(5));
        Assert.That(runtimes[0].Name, Is.EqualTo("Microsoft.AspNetCore.App"));
        Assert.That(runtimes[0].Version, Is.EqualTo("9.0.14"));
        Assert.That(runtimes[0].Location, Is.EqualTo(@"C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App"));
    }

    /// <summary>
    /// The bracketed path is the reason this is parsed positionally: it contains spaces, and on a
    /// developer machine it contains brackets too.
    /// </summary>
    [Test]
    public void APathWithSpacesAndBracketsSurvivesParsing()
    {
        var runtimes = DotnetHostLocator.ParseRuntimes(
            @"Microsoft.NETCore.App 10.0.1 [/home/me/my [odd] dir/dotnet/shared/Microsoft.NETCore.App]");

        Assert.That(runtimes, Has.Exactly(1).Items);
        Assert.That(runtimes[0].Location, Is.EqualTo("/home/me/my [odd] dir/dotnet/shared/Microsoft.NETCore.App"));
    }

    [TestCase("")]
    [TestCase(null)]
    [TestCase("not a runtime line")]
    [TestCase("Microsoft.NETCore.App 10.0.1 no brackets here")]
    public void OutputThatIsNotARuntimeListYieldsNothing(string? output)
    {
        Assert.That(DotnetHostLocator.ParseRuntimes(output), Is.Empty);
    }

    [Test]
    public void OnlyANetTenSharedFrameworkCounts()
    {
        var runtimes = DotnetHostLocator.ParseRuntimes(ListRuntimes);
        var selected = DotnetHostLocator.SelectNet10(runtimes);

        Assert.That(selected, Is.Not.Null);
        Assert.That(selected!.Name, Is.EqualTo("Microsoft.NETCore.App"));
        Assert.That(selected.Version, Is.EqualTo("10.0.1"));
    }

    /// <summary>
    /// <c>--list-runtimes</c> sorts as text, which puts 10.0.10 before 10.0.9.
    /// </summary>
    [Test]
    public void TheNewestNetTenWinsByVersionNotByListOrder()
    {
        var runtimes = DotnetHostLocator.ParseRuntimes(
            """
            Microsoft.NETCore.App 10.0.10 [/x]
            Microsoft.NETCore.App 10.0.9 [/x]
            """);

        Assert.That(DotnetHostLocator.SelectNet10(runtimes)!.Version, Is.EqualTo("10.0.10"));
    }

    [Test]
    public void AStableBuildBeatsAPrereleaseOfTheSameVersion()
    {
        var runtimes = DotnetHostLocator.ParseRuntimes(
            """
            Microsoft.NETCore.App 10.0.0-preview.7 [/x]
            Microsoft.NETCore.App 10.0.0 [/x]
            """);

        Assert.That(DotnetHostLocator.SelectNet10(runtimes)!.Version, Is.EqualTo("10.0.0"));
    }

    [Test]
    public void AHostWithNoNetTenIsNotUsableAndSaysWhy()
    {
        var runtimes = DotnetHostLocator.ParseRuntimes("Microsoft.NETCore.App 9.0.14 [/x]");

        Assert.That(DotnetHostLocator.SelectNet10(runtimes), Is.Null);
    }

    [Test]
    public void DotnetRootIsSearchedFirst()
    {
        var probe = Probe(
            variables: new() { ["DOTNET_ROOT"] = "/opt/dotnet", ["PATH"] = "/usr/bin" },
            existing: [Host("/opt/dotnet"), Host("/usr/bin")],
            listRuntimes: ListRuntimes);

        var result = DotnetHostLocator.Locate(probe);

        Assert.That(result.Path, Is.EqualTo(Host("/opt/dotnet")));
        Assert.That(result.Source, Is.EqualTo("DOTNET_ROOT"));
        Assert.That(result.DotnetRoot, Is.EqualTo(Path.GetDirectoryName(Host("/opt/dotnet"))));
        Assert.That(result.IsUsable, Is.True);
        Assert.That(result.Failure, Is.Null);
    }

    [Test]
    public void PathIsSearchedWhenDotnetRootIsUnset()
    {
        var probe = Probe(
            variables: new() { ["PATH"] = "/nowhere" + Path.PathSeparator + "/usr/bin" },
            existing: [Host("/usr/bin")],
            listRuntimes: ListRuntimes);

        var result = DotnetHostLocator.Locate(probe);

        Assert.That(result.Path, Is.EqualTo(Host("/usr/bin")));
        Assert.That(result.Source, Is.EqualTo("PATH"));
    }

    [Test]
    public void TheDefaultInstallLocationsAreSearchedLast()
    {
        var probe = Probe(
            variables: new() { ["HOME"] = "/home/me" },
            existing: [Host("/usr/share/dotnet")],
            listRuntimes: ListRuntimes);

        var result = DotnetHostLocator.Locate(probe);

        Assert.That(result.Path, Is.EqualTo(Host("/usr/share/dotnet")));
        Assert.That(result.Source, Is.EqualTo("default install location"));
    }

    /// <summary>
    /// A stale <c>DOTNET_ROOT</c> pointing at an uninstalled SDK must not end the search: that is the
    /// most common shape of "the runtime is missing" that is not actually a missing runtime.
    /// </summary>
    [Test]
    public void AStaleDotnetRootFallsThroughToTheRestOfTheChain()
    {
        var probe = Probe(
            variables: new() { ["DOTNET_ROOT"] = "/gone", ["PATH"] = "/usr/bin" },
            existing: [Host("/usr/bin")],
            listRuntimes: ListRuntimes);

        var result = DotnetHostLocator.Locate(probe);

        Assert.That(result.Path, Is.EqualTo(Host("/usr/bin")));
        Assert.That(
            result.Chain,
            Has.Some.Matches<string>(step => step!.Contains(Host("/gone") + " (not found)", StringComparison.Ordinal)));
    }

    [Test]
    public void NoHostAtAllIsAFailureThatNamesTheFix()
    {
        var result = DotnetHostLocator.Locate(Probe(new() { ["PATH"] = "/usr/bin" }, [], ListRuntimes));

        Assert.That(result.Path, Is.Null);
        Assert.That(result.IsUsable, Is.False);
        Assert.That(result.Failure, Contains.Substring("Install .NET 10").Using(StringComparison.Ordinal));
        Assert.That(result.Chain, Is.Not.Empty);
    }

    [Test]
    public void AHostWithoutNetTenIsFoundButRefused()
    {
        var result = DotnetHostLocator.Locate(Probe(
            new() { ["PATH"] = "/usr/bin" },
            [Host("/usr/bin")],
            "Microsoft.NETCore.App 9.0.14 [/x]"));

        Assert.That(result.Path, Is.EqualTo(Host("/usr/bin")));
        Assert.That(result.IsUsable, Is.False);
        Assert.That(result.Failure, Contains.Substring("no Microsoft.NETCore.App 10.x runtime").Using(StringComparison.Ordinal));
    }

    [Test]
    public void AHostThatCannotBeRunIsSkipped()
    {
        var probe = new DotnetProbe(
            name => name == "PATH" ? "/broken" + Path.PathSeparator + "/usr/bin" : null,
            path => path == Host("/broken") || path == Host("/usr/bin"),
            path => path == Host("/broken") ? null : ListRuntimes,
            IsWindows: false);

        var result = DotnetHostLocator.Locate(probe);

        Assert.That(result.Path, Is.EqualTo(Host("/usr/bin")));
    }

    /// <summary>The whole point of the search: the host it finds must be one Roslyn can run on.</summary>
    [Test]
    public void TheRealHostIsResolvableOnThisMachine()
    {
        var result = DotnetHostLocator.Resolve();

        Assert.That(result.IsUsable, Is.True, result.Failure ?? "no failure was reported either");
        Assert.That(DotnetHostLocator.Resolve(), Is.SameAs(result));
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
