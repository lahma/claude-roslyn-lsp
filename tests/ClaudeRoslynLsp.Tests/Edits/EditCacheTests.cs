using ClaudeRoslynLsp.Edits;
using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Tests.Testing;

using NUnit.Framework;

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
    [Test]
    public void TheKeyIsTheToolAndItsAnswerChangingArguments()
    {
        Assert.That(
            EditCache.KeyFor("renameSymbol", "IScheduler.Start", "Begin"),
            Is.EqualTo(EditCache.KeyFor("renameSymbol", "IScheduler.Start", "Begin")));

        Assert.That(
            EditCache.KeyFor("renameSymbol", "IScheduler.Start", "Start2"),
            Is.Not.EqualTo(EditCache.KeyFor("renameSymbol", "IScheduler.Start", "Begin")));

        Assert.That(
            EditCache.KeyFor("formatCode", "A", "B"),
            Is.Not.EqualTo(EditCache.KeyFor("renameSymbol", "A", "B")));
    }

    [Test]
    public void AStoredEditIsReturnedWhileNothingHasChanged()
    {
        using var workspace = TempWorkspace.Create("cache-hit");
        var (cache, plan, edit) = Store(workspace, "rename");

        Assert.That(cache.TryTake("rename"), Is.SameAs(edit));
        Assert.That(cache.TryTake("rename"), Is.SameAs(edit));
        Assert.That(plan.Files, Has.Exactly(1).Items);
    }

    /// <summary>
    /// The rule that turns the dangerous case into the slow case: between a preview and its apply the
    /// model may have written to the file, and re-using an edit computed against the old text would
    /// apply something nobody approved.
    /// </summary>
    [Test]
    public void AChangedFileInvalidatesTheEntry()
    {
        using var workspace = TempWorkspace.Create("cache-changed");
        var (cache, plan, _) = Store(workspace, "rename");

        var path = plan.Files[0].Path;
        File.WriteAllText(path, "class Changed;\nand longer\n");

        Assert.That(cache.TryTake("rename"), Is.Null);
        Assert.That(cache.Count, Is.EqualTo(0));
    }

    [Test]
    public void ADeletedFileInvalidatesTheEntry()
    {
        using var workspace = TempWorkspace.Create("cache-deleted");
        var (cache, plan, _) = Store(workspace, "rename");

        File.Delete(plan.Files[0].Path);

        Assert.That(cache.TryTake("rename"), Is.Null);
    }

    [Test]
    public void AnUnknownKeyIsAMiss()
    {
        using var workspace = TempWorkspace.Create("cache-miss");
        var (cache, _, _) = Store(workspace, "rename");

        Assert.That(cache.TryTake("formatCode nothing"), Is.Null);
    }

    /// <summary>
    /// A preview nobody followed up on must not decide what a call ten minutes later writes.
    /// </summary>
    [Test]
    public void AnEntryExpires()
    {
        using var workspace = TempWorkspace.Create("cache-expiry");
        var time = new TestTimeProvider();
        var (cache, _, edit) = Store(workspace, "rename", time);

        time.Advance(EditCache.Lifetime - TimeSpan.FromSeconds(1));
        Assert.That(cache.TryTake("rename"), Is.SameAs(edit));

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.That(cache.TryTake("rename"), Is.Null);
    }

    /// <summary>An applied edit has changed every file it read, so nothing it remembers is usable.</summary>
    [Test]
    public void ClearForgetsEverything()
    {
        using var workspace = TempWorkspace.Create("cache-clear");
        var (cache, _, _) = Store(workspace, "rename");

        cache.Clear();

        Assert.That(cache.Count, Is.EqualTo(0));
        Assert.That(cache.TryTake("rename"), Is.Null);
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
