using System.Text.Json;

using ClaudeRoslynLsp.Adapter;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// The bridge that keeps Roslyn's view of the disk in step with the disk.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of test here, deliberately. The decisions — what is excluded, what needs the C33
/// project nudge, which project file is the nearest one — are pure and are tested as such against a
/// real temporary tree, because path handling is exactly what a fake filesystem gets subtly wrong.
/// The end-to-end case runs a genuine <see cref="FileSystemWatcher"/> on the system clock, because
/// what it proves is that the events arrive at all.
/// </para>
/// <para>
/// The end-to-end case is also the only one that can prove the rule that matters: a new
/// <c>.cs</c> file has to produce a <c>Changed</c> for its <c>.csproj</c>, or Roslyn does nothing
/// with it (C33).
/// </para>
/// </remarks>
public class FileWatchBridgeTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Build output and machine state are never reported.</summary>
    [Theory]
    [InlineData(@"C:\w\src\A.cs", false)]
    [InlineData(@"C:\w\src\bin\Debug\A.dll", true)]
    [InlineData(@"C:\w\src\obj\Debug\A.g.cs", true)]
    [InlineData(@"C:\w\.git\HEAD", true)]
    [InlineData(@"C:\w\node_modules\x\index.js", true)]
    [InlineData(@"C:\w\artifacts\publish\A.exe", true)]
    [InlineData(@"C:\w\TestResults\log.trx", true)]
    [InlineData("/w/src/.vs/state.json", true)]
    public void BuildOutputAndMachineStateAreExcluded(string path, bool excluded) =>
        Assert.Equal(excluded, FileWatchBridge.IsExcluded(path));

    /// <summary>
    /// The one exception: <c>project.assets.json</c> lives under <c>obj</c> and <em>is</em> the
    /// restore result, so Roslyn reloads a project's references when it changes.
    /// </summary>
    [Fact]
    public void TheRestoreResultSurvivesTheObjExclusion()
    {
        Assert.False(FileWatchBridge.IsExcluded(@"C:\w\src\Hello.Core\obj\project.assets.json"));
        Assert.True(FileWatchBridge.IsExcluded(@"C:\w\src\Hello.Core\obj\project.nuget.cache"));
    }

    /// <summary>
    /// C33: only an appearance or a disappearance needs the synthetic project event. A plain edit of
    /// an existing file is something Roslyn already handles.
    /// </summary>
    [Theory]
    [InlineData("A.cs", FileWatchBridge.Created, true)]
    [InlineData("A.cs", FileWatchBridge.Deleted, true)]
    [InlineData("A.cs", FileWatchBridge.Changed, false)]
    [InlineData("A.txt", FileWatchBridge.Created, false)]
    [InlineData("A.csproj", FileWatchBridge.Created, false)]
    public void OnlyAnAppearingOrVanishingSourceFileNeedsTheProjectNudge(
        string name,
        int changeType,
        bool expected) =>
        Assert.Equal(expected, FileWatchBridge.NeedsProjectNudge(@"C:\w\src\" + name, changeType));

    /// <summary>The nudge names the nearest project above the file, not the first one anywhere.</summary>
    [Fact]
    public void TheNearestProjectFileIsTheOneWalkingUpwards()
    {
        using var workspace = TempWorkspace.Create("watch-nearest");

        workspace.File_("Outer.csproj", "<Project />");
        workspace.File_("src/Hello.Core/Hello.Core.csproj", "<Project />");
        var deep = workspace.File_("src/Hello.Core/nested/deeper/New.cs", "class N { }");

        var found = FileWatchBridge.NearestProjectFile(deep, workspace.Root);

        Assert.NotNull(found);
        Assert.Equal("Hello.Core.csproj", Path.GetFileName(found));
    }

    /// <summary>The walk stops at the workspace root rather than climbing out of it.</summary>
    [Fact]
    public void TheWalkNeverClimbsPastTheWorkspaceRoot()
    {
        using var workspace = TempWorkspace.Create("watch-root");

        var loose = workspace.File_("src/Loose.cs", "class L { }");

        Assert.Null(FileWatchBridge.NearestProjectFile(loose, workspace.Root));
    }

    /// <summary>With no workspace root there is nothing to walk and nothing to watch.</summary>
    [Fact]
    public void WithNoWorkspaceRootThereIsNothingToNudge() =>
        Assert.Null(FileWatchBridge.NearestProjectFile(@"C:\w\src\A.cs", root: null));

    /// <summary>
    /// The lost-event recovery reports every build file in the workspace as changed, which is the
    /// only thing that makes Roslyn re-evaluate what it missed.
    /// </summary>
    [Fact]
    public void AResynchronisationReportsEveryBuildFileUnderTheRoot()
    {
        using var workspace = TempWorkspace.Create("watch-resync");

        workspace.File_("Directory.Build.props", "<Project />");
        workspace.File_("src/Hello.Core/Hello.Core.csproj", "<Project />");
        workspace.File_("src/Hello.Core/obj/project.assets.json", "{}");
        workspace.File_("src/Hello.Core/obj/Hello.Core.csproj.nuget.g.props", "<Project />");
        workspace.File_("src/Hello.Core/Calculator.cs", "class C { }");
        workspace.File_("src/Hello.App/Hello.App.csproj", "<Project />");

        var channel = new StubAdapterChannel { WorkspaceRoot = workspace.Root };
        var time = new Testing.TestTimeProvider();
        var projectFilesChanged = 0;

        using var bridge = new FileWatchBridge(
            workspace.Root, new DocumentMirror(NullLogger.Instance), channel, time, NullLogger.Instance);

        bridge.ProjectFilesChanged += () => projectFilesChanged++;
        bridge.Resynchronise();
        time.Advance(FileWatchBridge.BatchWindow);

        var changes = Changes(Assert.Single(channel.ToServer));
        var names = changes.Select(x => Path.GetFileName(new Uri(x.Uri).LocalPath)).Order(StringComparer.Ordinal);

        Assert.Equal(
            ["Directory.Build.props", "Hello.App.csproj", "Hello.Core.csproj", "project.assets.json"],
            names);

        // Everything is Changed: C33's mechanism is a project re-evaluation, not a file event.
        Assert.All(changes, x => Assert.Equal(FileWatchBridge.Changed, x.Type));

        // The generated props under obj/ is build output and stays out, even though it matches.
        Assert.DoesNotContain(changes, x => x.Uri.Contains("nuget.g.props", StringComparison.Ordinal));

        Assert.Equal(1, projectFilesChanged);
    }

    /// <summary>
    /// The end-to-end rule: a <c>.cs</c> file that appears is reported <em>and</em> its project is
    /// reported as changed, in one batch (C33).
    /// </summary>
    [Fact]
    public async Task ANewSourceFileAlsoReportsItsProjectAsChanged()
    {
        using var workspace = TempWorkspace.Create("watch-c33");

        var project = workspace.File_("Hello.Core/Hello.Core.csproj", "<Project />");
        workspace.File_("Hello.Core/Existing.cs", "class E { }");

        var channel = new StubAdapterChannel { WorkspaceRoot = workspace.Root };

        // The system clock here, not a hand-driven one: what this case proves is that a real
        // FileSystemWatcher delivers, and a fake clock would only prove the batching again.
        using var bridge = new FileWatchBridge(
            workspace.Root,
            new DocumentMirror(NullLogger.Instance),
            channel,
            TimeProvider.System,
            NullLogger.Instance);

        bridge.Schedule([new CollapsedWatcher(
            new Uri(Path.Combine(workspace.Root, "Hello.Core") + Path.DirectorySeparatorChar).AbsoluteUri,
            ["**/*{.cs,.razor}", "Hello.Core.csproj"])]);

        await WaitAsync(() => bridge.WatcherCount > 0, Cancellation);

        File.WriteAllText(Path.Combine(workspace.Root, "Hello.Core", "Triangle.cs"), "class Triangle { }");

        await WaitAsync(() => channel.ToServer.Count > 0, Cancellation);
        await Task.Delay(300, Cancellation);

        var changes = channel.ToServer.SelectMany(Changes).ToArray();

        Assert.Contains(
            changes,
            x => x.Uri.EndsWith("Triangle.cs", StringComparison.Ordinal) && x.Type == FileWatchBridge.Created);

        Assert.Contains(
            changes,
            x => x.Uri.EndsWith("Hello.Core.csproj", StringComparison.Ordinal)
                 && x.Type == FileWatchBridge.Changed);

        // FileSystemWatcher reports a new file as Created and then Changed as the bytes land, so the
        // project nudge is asserted by name rather than by being the only Changed entry.
        var nudges = changes.Where(x => x.Type == FileWatchBridge.Changed && x.Uri.EndsWith(".csproj", StringComparison.Ordinal));

        Assert.Equal(new Uri(project).AbsoluteUri, Assert.Single(nudges).Uri);
    }

    /// <summary>
    /// A file the client has open is not reported: Roslyn already has the live buffer, and telling
    /// it to re-read the saved copy would discard the unsaved edits.
    /// </summary>
    [Fact]
    public async Task AnOpenDocumentIsNotReportedAsAWatchedFileChange()
    {
        using var workspace = TempWorkspace.Create("watch-open");

        workspace.File_("Hello.Core/Hello.Core.csproj", "<Project />");
        var path = Path.Combine(workspace.Root, "Hello.Core", "Open.cs");

        var mirror = new DocumentMirror(NullLogger.Instance);
        mirror.Open(System.Text.Encoding.UTF8.GetBytes($$$"""
            {"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":
             {"uri":"{{{new Uri(path).AbsoluteUri}}}","languageId":"csharp","version":1,"text":"class O { }"}
            }}
            """));

        var channel = new StubAdapterChannel { WorkspaceRoot = workspace.Root };

        using var bridge = new FileWatchBridge(
            workspace.Root, mirror, channel, TimeProvider.System, NullLogger.Instance);

        bridge.Schedule([new CollapsedWatcher(
            new Uri(Path.Combine(workspace.Root, "Hello.Core") + Path.DirectorySeparatorChar).AbsoluteUri,
            ["**/*.cs"])]);

        await WaitAsync(() => bridge.WatcherCount > 0, Cancellation);

        File.WriteAllText(path, "class O { int x; }");
        await Task.Delay(700, Cancellation);

        Assert.DoesNotContain(
            channel.ToServer.SelectMany(Changes),
            x => x.Uri.EndsWith("Open.cs", StringComparison.Ordinal));
    }

    /// <summary>
    /// C32: 113 of Roslyn's watchers are rooted in the NuGet package cache. Standing up filesystem
    /// watchers there would be absurd, so a base outside the workspace root is dropped.
    /// </summary>
    [Fact]
    public async Task WatchersRootedOutsideTheWorkspaceAreDropped()
    {
        using var workspace = TempWorkspace.Create("watch-outside");
        using var elsewhere = TempWorkspace.Create("watch-cache");

        workspace.File_("Hello.Core/Hello.Core.csproj", "<Project />");
        elsewhere.File_("system.text.json/9.0.0/lib/net10.0/System.Text.Json.dll", "not really");

        var channel = new StubAdapterChannel { WorkspaceRoot = workspace.Root };

        using var bridge = new FileWatchBridge(
            workspace.Root,
            new DocumentMirror(NullLogger.Instance),
            channel,
            TimeProvider.System,
            NullLogger.Instance);

        bridge.Schedule([
            new CollapsedWatcher(
                new Uri(Path.Combine(workspace.Root, "Hello.Core") + Path.DirectorySeparatorChar).AbsoluteUri,
                ["**/*.cs"]),
            new CollapsedWatcher(
                new Uri(Path.Combine(elsewhere.Root, "system.text.json", "9.0.0") + Path.DirectorySeparatorChar)
                    .AbsoluteUri,
                ["**/*.dll"]),
        ]);

        await WaitAsync(() => bridge.WatcherCount > 0, Cancellation);

        Assert.Equal(1, bridge.WatcherCount);
    }

    /// <summary>Without a workspace root nothing can be watched, and nothing throws.</summary>
    [Fact]
    public void WithNoWorkspaceRootNothingIsWatched()
    {
        var channel = new StubAdapterChannel();
        var time = new Testing.TestTimeProvider();

        using var bridge = new FileWatchBridge(
            null, new DocumentMirror(NullLogger.Instance), channel, time, NullLogger.Instance);

        bridge.Schedule([new CollapsedWatcher("file:///nowhere/", ["**/*.cs"])]);
        time.Advance(FileWatchBridge.RebuildDelay);

        Assert.Equal(0, bridge.WatcherCount);
        Assert.Empty(channel.ToServer);
    }

    /// <summary>Reads a <c>workspace/didChangeWatchedFiles</c> back into something assertable.</summary>
    private static (string Uri, int Type)[] Changes(JsonElement notification)
    {
        Assert.Equal("workspace/didChangeWatchedFiles", notification.GetProperty("method").GetString());

        return notification.GetProperty("params").GetProperty("changes").EnumerateArray()
            .Select(x => (x.GetProperty("uri").GetString()!, x.GetProperty("type").GetInt32()))
            .ToArray();
    }

    /// <summary>Polls a condition on a short budget, for the cases driven by a real watcher.</summary>
    private static async Task WaitAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        Assert.Fail("The filesystem watcher did not deliver within 15 s.");
    }
}
