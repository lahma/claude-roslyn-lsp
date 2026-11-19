using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Mcp.Tools;
using ClaudeRoslynLsp.Tests.Tools;

using ModelContextProtocol;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Mcp;

/// <summary>
/// The project list read off the solution file (D78), which <c>getWorkspaceStatus</c> reports and
/// every <c>project</c> argument is matched against.
/// </summary>
public class WorkspaceProjectsTests
{
    private static readonly string[] SlnProjectNames = ["Lib.Tests", "my-lib"];
    private static readonly string[] SlnxProjectNames = ["Lib"];
    private static readonly string[] TwoFrameworks = ["net10.0", "net8.0"];
    private static readonly string[] OneFramework = ["net10.0"];

    /// <summary>
    /// Issue #4: a <c>.sln</c> project line is <c>Project("{type}") = "Name", "Path", "{guid}"</c>, and
    /// reading the fourth quoted run took the project guid for the path, so every <c>.sln</c> listed
    /// no project and <c>formatCode project:</c> matched nothing.
    /// </summary>
    [Test]
    public void ASlnListsItsProjectsAndNotItsSolutionFolders()
    {
        using var workspace = TempWorkspace.Create("sln-projects");

        workspace.File_("sub/src/my-lib/my-lib.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        workspace.File_("sub/tests/Lib.Tests/Lib.Tests.csproj", "<Project><PropertyGroup><TargetFrameworks>net10.0;net8.0</TargetFrameworks></PropertyGroup></Project>");
        var solution = workspace.File_("sub/Repro.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "src", "src", "{827E0CD3-B72D-47B6-A68D-7590B98EB39B}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "my-lib", "src\my-lib\my-lib.csproj", "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib.Tests", "tests\Lib.Tests\Lib.Tests.csproj", "{1E0B1F2D-3C4B-4A5D-8E6F-7A8B9C0D1E2F}"
            EndProject
            Global
            EndGlobal
            """);

        var projects = WorkspaceProjects.Read(solution, []);

        Assert.That(projects.Select(project => project.Name), Is.EqualTo(SlnProjectNames));
        Assert.That(projects[0].TargetFrameworks, Is.EqualTo(TwoFrameworks));
        Assert.That(projects[1].Path, Is.EqualTo(workspace.Path_("sub", "src", "my-lib", "my-lib.csproj")));
        Assert.That(projects[1].TargetFrameworks, Is.EqualTo(OneFramework));
    }

    [Test]
    public void ASlnxListsItsProjects()
    {
        using var workspace = TempWorkspace.Create("slnx-projects");

        workspace.File_("src/Lib/Lib.csproj", "<Project />");
        var solution = workspace.File_("Repro.slnx", """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/Lib/Lib.csproj" />
              </Folder>
            </Solution>
            """);

        var projects = WorkspaceProjects.Read(solution, []);

        Assert.That(projects.Select(project => project.Name), Is.EqualTo(SlnxProjectNames));
        Assert.That(projects[0].TargetFrameworks, Is.Empty);
    }

    [TestCase("my-lib")]
    [TestCase("MY-LIB")]
    [TestCase("src/my-lib/my-lib.csproj")]
    [TestCase("src\\my-lib\\my-lib.csproj")]
    public void AProjectIsFoundByNameOrByTheEndOfItsPath(string argument)
    {
        using var workspace = TempWorkspace.Create("find-project");
        var (context, _) = ToolTestHost.CreateContext(workspace);
        var path = workspace.File_("src/my-lib/my-lib.csproj", "<Project />");

        var state = new WorkspaceState(
            WorkspaceLoadStatus.Ready,
            Projects: [new WorkspaceProject("my-lib", path, [])]);

        Assert.That(ToolLookup.FindProject(context, state, "formatCode", argument).Path, Is.EqualTo(path));
    }

    /// <summary>
    /// Issue #4's second complaint: no match used to read "matched 0 candidates … and will not guess
    /// between them", as if there had been several.
    /// </summary>
    [Test]
    public void AProjectThatMatchesNothingIsNotFoundAndTheRealOnesAreNamed()
    {
        using var workspace = TempWorkspace.Create("find-project-missing");
        var (context, _) = ToolTestHost.CreateContext(workspace);

        var state = new WorkspaceState(
            WorkspaceLoadStatus.Ready,
            Projects:
            [
                new WorkspaceProject("Core", workspace.Path_("Core", "Core.csproj"), []),
                new WorkspaceProject("App", workspace.Path_("App", "App.csproj"), []),
            ]);

        var failure = Assert.Throws<McpException>(() => ToolLookup.FindProject(context, state, "formatCode", "Lib"));

        Assert.That(failure!.Message, Does.StartWith("formatCode found no project 'Lib' among the workspace's 2 (Core, App)")
            .Using(StringComparison.Ordinal));
        Assert.That(failure.Message, !Contains.Substring("will not guess").Using(StringComparison.Ordinal));
    }

    [Test]
    public void AWorkspaceThatListsNoProjectsSaysSo()
    {
        using var workspace = TempWorkspace.Create("find-project-none");
        var (context, _) = ToolTestHost.CreateContext(workspace);

        var failure = Assert.Throws<McpException>(() =>
            ToolLookup.FindProject(context, new WorkspaceState(WorkspaceLoadStatus.Ready), "getDiagnostics", "Lib"));

        Assert.That(failure!.Message, Contains.Substring("the workspace lists no projects").Using(StringComparison.Ordinal));
    }
}
