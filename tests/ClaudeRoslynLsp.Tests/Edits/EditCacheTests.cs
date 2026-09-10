using ClaudeRoslynLsp.Edits;
using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Tests.Testing;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Edits;

/// <summary>
/// The preview-to-apply cache (D62): it may only answer while the files it read are unchanged.
/// </summary>
public class EditCacheTests
{
    /// <summary>
    /// <c>preview</c> is deliberately not part of the key, because the preview call and the apply
    /// call differ in exactly that argument — if it were, they would never meet.
    /// </summary>
    [Fact]
    public void TheKeyIsTheToolAndItsAnswerChangingArguments()
    {
        Assert.Equal(
            EditCache.KeyFor("renameSymbol", "IScheduler.Start", "Begin"),
            EditCache.KeyFor("renameSymbol", "IScheduler.Start", "Begin"));

        Assert.NotEqual(
            EditCache.KeyFor("renameSymbol", "IScheduler.Start", "Begin"),
            EditCache.KeyFor("renameSymbol", "IScheduler.Start", "Start2"));

        Assert.NotEqual(
            EditCache.KeyFor("renameSymbol", "A", "B"),
            EditCache.KeyFor("formatCode", "A", "B"));
    }

    [Fact]
    public void AStoredEditIsReturnedWhileNothingHasChanged()
    {
        using var workspace = TempWorkspace.Create("cache-hit");
        var (cache, plan, edit) = Store(workspace, "rename");

        Assert.Same(edit, cache.TryTake("rename"));
        Assert.Same(edit, cache.TryTake("rename"));
        Assert.Single(plan.Files);
    }

    /// <summary>
    /// The rule that turns the dangerous case into the slow case: between a preview and its apply the
    /// model may have written to the file, and re-using an edit computed against the old text would
    /// apply something nobody approved.
    /// </summary>
    [Fact]
    public void AChangedFileInvalidatesTheEntry()
    {
        using var workspace = TempWorkspace.Create("cache-changed");
        var (cache, plan, _) = Store(workspace, "rename");

        var path = plan.Files[0].Path;
        File.WriteAllText(path, "class Changed;\nand longer\n");

        Assert.Null(cache.TryTake("rename"));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void ADeletedFileInvalidatesTheEntry()
    {
        using var workspace = TempWorkspace.Create("cache-deleted");
        var (cache, plan, _) = Store(workspace, "rename");

        File.Delete(plan.Files[0].Path);

        Assert.Null(cache.TryTake("rename"));
    }

    [Fact]
    public void AnUnknownKeyIsAMiss()
    {
        using var workspace = TempWorkspace.Create("cache-miss");
        var (cache, _, _) = Store(workspace, "rename");

        Assert.Null(cache.TryTake("formatCode nothing"));
    }

    /// <summary>
    /// A preview nobody followed up on must not decide what a call ten minutes later writes.
    /// </summary>
    [Fact]
    public void AnEntryExpires()
    {
        using var workspace = TempWorkspace.Create("cache-expiry");
        var time = new TestTimeProvider();
        var (cache, _, edit) = Store(workspace, "rename", time);

        time.Advance(EditCache.Lifetime - TimeSpan.FromSeconds(1));
        Assert.Same(edit, cache.TryTake("rename"));

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(cache.TryTake("rename"));
    }

    /// <summary>An applied edit has changed every file it read, so nothing it remembers is usable.</summary>
    [Fact]
    public void ClearForgetsEverything()
    {
        using var workspace = TempWorkspace.Create("cache-clear");
        var (cache, _, _) = Store(workspace, "rename");

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Null(cache.TryTake("rename"));
    }

    private static (EditCache Cache, WorkspaceEditPlan Plan, WorkspaceEdit Edit) Store(
        TempWorkspace workspace,
        string key,
        TimeProvider? timeProvider = null)
    {
        var path = workspace.File_("a.cs", "class A;\n");
        var guard = new WorkspacePathGuard(workspace.Root);
        var applier = new WorkspaceEditApplier(guard);

        var edit = new WorkspaceEdit
        {
            DocumentChanges =
            [
                new DocumentChange
                {
                    TextDocument = new OptionalVersionedTextDocumentIdentifier
                    {
                        Uri = WorkspacePathGuard.ToUri(path),
                    },
                    Edits =
                    [
                        new LspTextEdit
                        {
                            Range = new LspRange
                            {
                                Start = new LspPosition { Line = 0, Character = 6 },
                                End = new LspPosition { Line = 0, Character = 7 },
                            },
                            NewText = "B",
                        },
                    ],
                },
            ],
        };

        var plan = applier.Plan(edit);
        var cache = new EditCache(timeProvider);
        cache.Store(key, edit, plan);

        return (cache, plan, edit);
    }
}
