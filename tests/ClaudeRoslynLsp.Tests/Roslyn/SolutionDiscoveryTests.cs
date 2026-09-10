using ClaudeRoslynLsp.Roslyn;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Roslyn;

/// <summary>
/// Covers D39-D42: explicit settings that fail loudly, the VS Code setting that is honoured, the
/// scoring, and the project fallback.
/// </summary>
public class SolutionDiscoveryTests
{
    /// <summary>Two projects, as a <c>.sln</c> would list them.</summary>
    private const string SolutionWithTwoProjects =
        """
        Microsoft Visual Studio Solution File, Format Version 12.00
        Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "A", "A\A.csproj", "{1}"
        EndProject
        Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "B", "B\B.csproj", "{2}"
        EndProject
        """;

    [Fact]
    public void OneSolutionInTheRootIsTheAnswer()
    {
        using var temp = TempWorkspace.Create("discovery-single");
        var solution = temp.File_("Only.sln", SolutionWithTwoProjects);

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.Equal(SolutionDiscoveryOutcome.Scored, result.Outcome);
        Assert.Equal(solution, result.SolutionPath);
        Assert.Single(result.Candidates);
        Assert.Equal(2, result.Candidates[0].ProjectCount);
    }

    /// <summary>Name matching the folder is worth more than everything else combined (D41).</summary>
    [Fact]
    public void TheSolutionNamedAfterTheRootFolderWins()
    {
        using var temp = TempWorkspace.Create("discovery-name");
        var rootName = Path.GetFileName(temp.Root);

        temp.File_("Zzz.sln", SolutionWithTwoProjects);
        var expected = temp.File_(rootName + ".sln", "Microsoft Visual Studio Solution File\n");

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.Equal(expected, result.SolutionPath);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains("name matches the folder", result.Candidates[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SlnxBeatsSlnAndProjectCountBreaksTheRest()
    {
        using var temp = TempWorkspace.Create("discovery-slnx");
        temp.File_("App.sln", SolutionWithTwoProjects);
        var modern = temp.File_("App.slnx", """<Solution><Project Path="A/A.csproj" /></Solution>""");

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.Equal(modern, result.SolutionPath);
        Assert.Equal(1, result.Candidates[0].ProjectCount);
        Assert.Contains("+10 .slnx", result.Candidates[0].Reason, StringComparison.Ordinal);
    }

    /// <summary>A solution at the root is the repository's; one three directories down is a sample.</summary>
    [Fact]
    public void AShallowerSolutionWinsATieWithADeeperOne()
    {
        using var temp = TempWorkspace.Create("discovery-depth");
        var root = temp.File_("zzz.sln", "");
        temp.File_(Path.Combine("samples", "aaa.sln"), "");

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.Equal(root, result.SolutionPath);
        Assert.Equal(0, result.Candidates[0].Depth);
    }

    [Fact]
    public void NestedSolutionsAreFoundToDepthThree()
    {
        using var temp = TempWorkspace.Create("discovery-nested");
        temp.File_(Path.Combine("a", "b", "c", "Deep.sln"), "");
        temp.File_(Path.Combine("a", "b", "c", "d", "TooDeep.sln"), "");

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.Single(result.Candidates);
        Assert.EndsWith("Deep.sln", result.SolutionPath, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildOutputDirectoriesAreNeverDescendedInto()
    {
        using var temp = TempWorkspace.Create("discovery-skip");

        foreach (var skipped in SolutionDiscovery.SkippedDirectories)
        {
            temp.File_(Path.Combine(skipped, "Ignored.sln"), SolutionWithTwoProjects);
        }

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.Empty(result.Candidates);
        Assert.Equal(SolutionDiscoveryOutcome.None, result.Outcome);
    }

    [Fact]
    public void WithNoSolutionEveryProjectIsOpened()
    {
        using var temp = TempWorkspace.Create("discovery-projects");
        temp.File_(Path.Combine("src", "App", "App.csproj"), "");
        temp.File_(Path.Combine("tests", "App.Tests", "App.Tests.csproj"), "");

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.Equal(SolutionDiscoveryOutcome.ProjectsOnly, result.Outcome);
        Assert.Equal(2, result.ProjectPaths.Count);

        // D42: test projects are candidates too. An agent asked to fix a failing test needs them.
        Assert.Contains(result.ProjectPaths, path => path.EndsWith("App.Tests.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyTreeIsMiscFilesMode()
    {
        using var temp = TempWorkspace.Create("discovery-empty");

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.Equal(SolutionDiscoveryOutcome.None, result.Outcome);
        Assert.False(result.HasWorkspace);
        Assert.Contains("misc-files mode", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitSettingWins()
    {
        using var temp = TempWorkspace.Create("discovery-explicit");
        temp.File_("Guessed.sln", SolutionWithTwoProjects);
        var wanted = temp.File_(Path.Combine("other", "Wanted.slnx"), "<Solution/>");

        var result = SolutionDiscovery.Discover(temp.Root, wanted);

        Assert.Equal(SolutionDiscoveryOutcome.Explicit, result.Outcome);
        Assert.Equal(wanted, result.SolutionPath);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void AnExplicitSettingMayBeRelativeToTheRoot()
    {
        using var temp = TempWorkspace.Create("discovery-relative");
        var wanted = temp.File_(Path.Combine("src", "Wanted.slnx"), "<Solution/>");

        var result = SolutionDiscovery.Discover(temp.Root, Path.Combine("src", "Wanted.slnx"));

        Assert.Equal(wanted, result.SolutionPath);
    }

    [Fact]
    public void AnExplicitProjectIsOpenedAsAProject()
    {
        using var temp = TempWorkspace.Create("discovery-explicit-project");
        var project = temp.File_(Path.Combine("src", "App", "App.csproj"), "");

        var result = SolutionDiscovery.Discover(temp.Root, project);

        Assert.Null(result.SolutionPath);
        Assert.Equal([project], result.ProjectPaths);
    }

    /// <summary>D39: never a silent fallback to a different solution.</summary>
    [Fact]
    public void AnExplicitSettingThatIsNotThereFailsInsteadOfGuessing()
    {
        using var temp = TempWorkspace.Create("discovery-explicit-missing");
        temp.File_("Guessed.sln", SolutionWithTwoProjects);

        var result = SolutionDiscovery.Discover(temp.Root, temp.Path_("Gone.sln"), "CLAUDE_ROSLYN_LSP_SOLUTION");

        Assert.Equal(SolutionDiscoveryOutcome.Failed, result.Outcome);
        Assert.Null(result.SolutionPath);
        Assert.Contains("CLAUDE_ROSLYN_LSP_SOLUTION", result.Failure, StringComparison.Ordinal);
        Assert.Contains("does not exist", result.Failure, StringComparison.Ordinal);
    }

    /// <summary>D40: the repository has already answered this question for its editor.</summary>
    [Fact]
    public void TheVsCodeDefaultSolutionSettingIsHonoured()
    {
        using var temp = TempWorkspace.Create("discovery-vscode");
        temp.File_("Guessed.sln", SolutionWithTwoProjects);
        var wanted = temp.File_(Path.Combine("src", "Chosen.sln"), "");

        temp.File_(
            Path.Combine(".vscode", "settings.json"),
            """{ "dotnet.defaultSolution": "src/Chosen.sln" }""");

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.Equal(SolutionDiscoveryOutcome.DefaultSolutionSetting, result.Outcome);
        Assert.Equal(wanted, result.SolutionPath);
    }

    [Fact]
    public void TheVsCodeSettingIsReadEvenWithCommentsAndTrailingCommas()
    {
        using var temp = TempWorkspace.Create("discovery-vscode-jsonc");
        temp.File_("Chosen.sln", "");

        temp.File_(
            Path.Combine(".vscode", "settings.json"),
            """
            {
              // written by the C# extension
              "dotnet.defaultSolution": "Chosen.sln",
            }
            """);

        Assert.Equal("Chosen.sln", SolutionDiscovery.ReadDefaultSolution(temp.Root));
    }

    [Fact]
    public void DisableMeansNoSolutionAtAll()
    {
        using var temp = TempWorkspace.Create("discovery-disable");
        temp.File_("Guessed.sln", SolutionWithTwoProjects);
        temp.File_(Path.Combine(".vscode", "settings.json"), """{ "dotnet.defaultSolution": "disable" }""");

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.Equal(SolutionDiscoveryOutcome.Disabled, result.Outcome);
        Assert.Null(result.SolutionPath);
        Assert.False(result.HasWorkspace);
    }

    [Fact]
    public void AnExplicitEnvironmentSettingOutranksTheVsCodeSetting()
    {
        using var temp = TempWorkspace.Create("discovery-precedence");
        var wanted = temp.File_("Wanted.sln", "");
        temp.File_("Other.sln", "");
        temp.File_(Path.Combine(".vscode", "settings.json"), """{ "dotnet.defaultSolution": "Other.sln" }""");

        var result = SolutionDiscovery.Discover(temp.Root, "Wanted.sln");

        Assert.Equal(SolutionDiscoveryOutcome.Explicit, result.Outcome);
        Assert.Equal(wanted, result.SolutionPath);
    }

    [Fact]
    public void ABrokenSettingsFileIsTreatedAsAbsentRatherThanAsAFailure()
    {
        using var temp = TempWorkspace.Create("discovery-vscode-broken");
        temp.File_("App.sln", "");
        temp.File_(Path.Combine(".vscode", "settings.json"), "{ this is not json");

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.Null(SolutionDiscovery.ReadDefaultSolution(temp.Root));
        Assert.Equal(SolutionDiscoveryOutcome.Scored, result.Outcome);
    }

    [Theory]
    [InlineData(".sln", 2)]
    [InlineData(".slnx", 3)]
    public void ProjectsAreCountedFromTheSolutionText(string extension, int expected)
    {
        using var temp = TempWorkspace.Create("discovery-count");

        var content = extension == ".sln"
            ? SolutionWithTwoProjects
            : """
              <Solution>
                <Project Path="A/A.csproj" />
                <Project Path="B/B.csproj" />
                <Project Path="C/C.csproj" />
              </Solution>
              """;

        var path = temp.File_("App" + extension, content);

        Assert.Equal(expected, SolutionDiscovery.CountProjects(path));
    }

    [Fact]
    public void ScoringIsTheSumOfTheThreeSignals()
    {
        var score = SolutionDiscovery.Score(
            Path.Combine("repo", "repo.slnx"), rootName: "repo", projectCount: 7, out var reason);

        Assert.Equal(117, score);
        Assert.Contains("+100", reason, StringComparison.Ordinal);
        Assert.Contains("+10 .slnx", reason, StringComparison.Ordinal);
        Assert.Contains("+7 project(s)", reason, StringComparison.Ordinal);
    }
}
