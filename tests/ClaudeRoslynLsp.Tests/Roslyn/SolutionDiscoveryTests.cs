using ClaudeRoslynLsp.Roslyn;

using NUnit.Framework;

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

    [Test]
    public void OneSolutionInTheRootIsTheAnswer()
    {
        using var temp = TempWorkspace.Create("discovery-single");
        var solution = temp.File_("Only.sln", SolutionWithTwoProjects);

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.That(result.Outcome, Is.EqualTo(SolutionDiscoveryOutcome.Scored));
        Assert.That(result.SolutionPath, Is.EqualTo(solution));
        Assert.That(result.Candidates, Has.Exactly(1).Items);
        Assert.That(result.Candidates[0].ProjectCount, Is.EqualTo(2));
    }

    /// <summary>Name matching the folder is worth more than everything else combined (D41).</summary>
    [Test]
    public void TheSolutionNamedAfterTheRootFolderWins()
    {
        using var temp = TempWorkspace.Create("discovery-name");
        var rootName = Path.GetFileName(temp.Root);

        temp.File_("Zzz.sln", SolutionWithTwoProjects);
        var expected = temp.File_(rootName + ".sln", "Microsoft Visual Studio Solution File\n");

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.That(result.SolutionPath, Is.EqualTo(expected));
        Assert.That(result.Candidates.Count, Is.EqualTo(2));
        Assert.That(result.Candidates[0].Reason, Contains.Substring("name matches the folder").Using(StringComparison.Ordinal));
    }

    [Test]
    public void SlnxBeatsSlnAndProjectCountBreaksTheRest()
    {
        using var temp = TempWorkspace.Create("discovery-slnx");
        temp.File_("App.sln", SolutionWithTwoProjects);
        var modern = temp.File_("App.slnx", """<Solution><Project Path="A/A.csproj" /></Solution>""");

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.That(result.SolutionPath, Is.EqualTo(modern));
        Assert.That(result.Candidates[0].ProjectCount, Is.EqualTo(1));
        Assert.That(result.Candidates[0].Reason, Contains.Substring("+10 .slnx").Using(StringComparison.Ordinal));
    }

    /// <summary>A solution at the root is the repository's; one three directories down is a sample.</summary>
    [Test]
    public void AShallowerSolutionWinsATieWithADeeperOne()
    {
        using var temp = TempWorkspace.Create("discovery-depth");
        var root = temp.File_("zzz.sln", "");
        temp.File_(Path.Combine("samples", "aaa.sln"), "");

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.That(result.SolutionPath, Is.EqualTo(root));
        Assert.That(result.Candidates[0].Depth, Is.EqualTo(0));
    }

    [Test]
    public void NestedSolutionsAreFoundToDepthThree()
    {
        using var temp = TempWorkspace.Create("discovery-nested");
        temp.File_(Path.Combine("a", "b", "c", "Deep.sln"), "");
        temp.File_(Path.Combine("a", "b", "c", "d", "TooDeep.sln"), "");

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.That(result.Candidates, Has.Exactly(1).Items);
        Assert.That(result.SolutionPath, Does.EndWith("Deep.sln").Using(StringComparison.Ordinal));
    }

    [Test]
    public void BuildOutputDirectoriesAreNeverDescendedInto()
    {
        using var temp = TempWorkspace.Create("discovery-skip");

        foreach (var skipped in SolutionDiscovery.SkippedDirectories)
        {
            temp.File_(Path.Combine(skipped, "Ignored.sln"), SolutionWithTwoProjects);
        }

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.That(result.Candidates, Is.Empty);
        Assert.That(result.Outcome, Is.EqualTo(SolutionDiscoveryOutcome.None));
    }

    [Test]
    public void WithNoSolutionEveryProjectIsOpened()
    {
        using var temp = TempWorkspace.Create("discovery-projects");
        temp.File_(Path.Combine("src", "App", "App.csproj"), "");
        temp.File_(Path.Combine("tests", "App.Tests", "App.Tests.csproj"), "");

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.That(result.Outcome, Is.EqualTo(SolutionDiscoveryOutcome.ProjectsOnly));
        Assert.That(result.ProjectPaths.Count, Is.EqualTo(2));

        // D42: test projects are candidates too. An agent asked to fix a failing test needs them.
        Assert.That(result.ProjectPaths, Has.Some.Matches<string>(path => path!.EndsWith("App.Tests.csproj", StringComparison.Ordinal)));
    }

    [Test]
    public void AnEmptyTreeIsMiscFilesMode()
    {
        using var temp = TempWorkspace.Create("discovery-empty");

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.That(result.Outcome, Is.EqualTo(SolutionDiscoveryOutcome.None));
        Assert.That(result.HasWorkspace, Is.False);
        Assert.That(result.Explanation, Contains.Substring("misc-files mode").Using(StringComparison.Ordinal));
    }

    [Test]
    public void AnExplicitSettingWins()
    {
        using var temp = TempWorkspace.Create("discovery-explicit");
        temp.File_("Guessed.sln", SolutionWithTwoProjects);
        var wanted = temp.File_(Path.Combine("other", "Wanted.slnx"), "<Solution/>");

        var result = SolutionDiscovery.Discover(temp.Root, wanted);

        Assert.That(result.Outcome, Is.EqualTo(SolutionDiscoveryOutcome.Explicit));
        Assert.That(result.SolutionPath, Is.EqualTo(wanted));
        Assert.That(result.Candidates, Is.Empty);
    }

    [Test]
    public void AnExplicitSettingMayBeRelativeToTheRoot()
    {
        using var temp = TempWorkspace.Create("discovery-relative");
        var wanted = temp.File_(Path.Combine("src", "Wanted.slnx"), "<Solution/>");

        var result = SolutionDiscovery.Discover(temp.Root, Path.Combine("src", "Wanted.slnx"));

        Assert.That(result.SolutionPath, Is.EqualTo(wanted));
    }

    [Test]
    public void AnExplicitProjectIsOpenedAsAProject()
    {
        using var temp = TempWorkspace.Create("discovery-explicit-project");
        var project = temp.File_(Path.Combine("src", "App", "App.csproj"), "");

        var result = SolutionDiscovery.Discover(temp.Root, project);

        Assert.That(result.SolutionPath, Is.Null);
        Assert.That(result.ProjectPaths, Is.EqualTo([project]));
    }

    /// <summary>D39: never a silent fallback to a different solution.</summary>
    [Test]
    public void AnExplicitSettingThatIsNotThereFailsInsteadOfGuessing()
    {
        using var temp = TempWorkspace.Create("discovery-explicit-missing");
        temp.File_("Guessed.sln", SolutionWithTwoProjects);

        var result = SolutionDiscovery.Discover(temp.Root, temp.Path_("Gone.sln"), "CLAUDE_ROSLYN_LSP_SOLUTION");

        Assert.That(result.Outcome, Is.EqualTo(SolutionDiscoveryOutcome.Failed));
        Assert.That(result.SolutionPath, Is.Null);
        Assert.That(result.Failure, Contains.Substring("CLAUDE_ROSLYN_LSP_SOLUTION").Using(StringComparison.Ordinal));
        Assert.That(result.Failure, Contains.Substring("does not exist").Using(StringComparison.Ordinal));
    }

    /// <summary>D40: the repository has already answered this question for its editor.</summary>
    [Test]
    public void TheVsCodeDefaultSolutionSettingIsHonoured()
    {
        using var temp = TempWorkspace.Create("discovery-vscode");
        temp.File_("Guessed.sln", SolutionWithTwoProjects);
        var wanted = temp.File_(Path.Combine("src", "Chosen.sln"), "");

        temp.File_(
            Path.Combine(".vscode", "settings.json"),
            """{ "dotnet.defaultSolution": "src/Chosen.sln" }""");

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.That(result.Outcome, Is.EqualTo(SolutionDiscoveryOutcome.DefaultSolutionSetting));
        Assert.That(result.SolutionPath, Is.EqualTo(wanted));
    }

    [Test]
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

        Assert.That(SolutionDiscovery.ReadDefaultSolution(temp.Root), Is.EqualTo("Chosen.sln"));
    }

    [Test]
    public void DisableMeansNoSolutionAtAll()
    {
        using var temp = TempWorkspace.Create("discovery-disable");
        temp.File_("Guessed.sln", SolutionWithTwoProjects);
        temp.File_(Path.Combine(".vscode", "settings.json"), """{ "dotnet.defaultSolution": "disable" }""");

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.That(result.Outcome, Is.EqualTo(SolutionDiscoveryOutcome.Disabled));
        Assert.That(result.SolutionPath, Is.Null);
        Assert.That(result.HasWorkspace, Is.False);
    }

    [Test]
    public void AnExplicitEnvironmentSettingOutranksTheVsCodeSetting()
    {
        using var temp = TempWorkspace.Create("discovery-precedence");
        var wanted = temp.File_("Wanted.sln", "");
        temp.File_("Other.sln", "");
        temp.File_(Path.Combine(".vscode", "settings.json"), """{ "dotnet.defaultSolution": "Other.sln" }""");

        var result = SolutionDiscovery.Discover(temp.Root, "Wanted.sln");

        Assert.That(result.Outcome, Is.EqualTo(SolutionDiscoveryOutcome.Explicit));
        Assert.That(result.SolutionPath, Is.EqualTo(wanted));
    }

    [Test]
    public void ABrokenSettingsFileIsTreatedAsAbsentRatherThanAsAFailure()
    {
        using var temp = TempWorkspace.Create("discovery-vscode-broken");
        temp.File_("App.sln", "");
        temp.File_(Path.Combine(".vscode", "settings.json"), "{ this is not json");

        var result = SolutionDiscovery.Discover(temp.Root);

        Assert.That(SolutionDiscovery.ReadDefaultSolution(temp.Root), Is.Null);
        Assert.That(result.Outcome, Is.EqualTo(SolutionDiscoveryOutcome.Scored));
    }

    [TestCase(".sln", 2)]
    [TestCase(".slnx", 3)]
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

        Assert.That(SolutionDiscovery.CountProjects(path), Is.EqualTo(expected));
    }

    [Test]
    public void ScoringIsTheSumOfTheThreeSignals()
    {
        var score = SolutionDiscovery.Score(
            Path.Combine("repo", "repo.slnx"), rootName: "repo", projectCount: 7, out var reason);

        Assert.That(score, Is.EqualTo(117));
        Assert.That(reason, Contains.Substring("+100").Using(StringComparison.Ordinal));
        Assert.That(reason, Contains.Substring("+10 .slnx").Using(StringComparison.Ordinal));
        Assert.That(reason, Contains.Substring("+7 project(s)").Using(StringComparison.Ordinal));
    }
}
