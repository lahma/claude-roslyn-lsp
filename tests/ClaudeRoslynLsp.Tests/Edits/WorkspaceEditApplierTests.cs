using System.Text;

using ClaudeRoslynLsp.Edits;
using ClaudeRoslynLsp.Mcp.Engine;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Edits;

/// <summary>
/// The one component in this product that changes a source file (D21, D61).
/// </summary>
public class WorkspaceEditApplierTests
{
    [Test]
    public void EditsAreAppliedInReverseOrderSoEarlierOffsetsStayValid()
    {
        using var workspace = TempWorkspace.Create("applier-order");
        var file = workspace.File_("a.cs", "int Compute() => Compute2();\n");

        var (applier, _) = Applier(workspace);

        // Two edits in ascending order, exactly as Roslyn sends them. Applying them front to back
        // would shift the second one by the first one's length delta.
        var plan = applier.Plan(Edit(file, [
            Replace(0, 4, 0, 11, "Calculate"),
            Replace(0, 17, 0, 25, "Calculate2"),
        ]));

        WorkspaceEditApplier.Apply(plan);

        Assert.That(File.ReadAllText(file), Is.EqualTo("int Calculate() => Calculate2();\n"));
        Assert.That(plan.TotalEditCount, Is.EqualTo(2));
    }

    /// <summary>
    /// Overlaps are refused rather than resolved. LSP forbids them, so one means the answer is wrong
    /// already, and "merged" would be a guess written to somebody's source file.
    /// </summary>
    [Test]
    public void OverlappingEditsAreRefused()
    {
        using var workspace = TempWorkspace.Create("applier-overlap");
        var file = workspace.File_("a.cs", "abcdefghij\n");

        var (applier, _) = Applier(workspace);

        var exception = Assert.Throws<WorkspaceEditRefusedException>(() => applier.Plan(Edit(file, [
            Replace(0, 0, 0, 5, "X"),
            Replace(0, 3, 0, 8, "Y"),
        ])));

        Assert.That(exception!.Message, Contains.Substring("overlapping").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// A column is a count of UTF-16 code units, which is what makes a surrogate pair two columns on
    /// both sides of the wire (D14 pins the encoding for exactly this reason).
    /// </summary>
    [Test]
    public void ColumnsCountUtf16CodeUnitsSoAstralCharactersOccupyTwo()
    {
        using var workspace = TempWorkspace.Create("applier-surrogate");

        // The rocket is one rune and two UTF-16 code units, so "name" starts at character 8 and not
        // at character 7 — a counted-by-runes implementation would replace " nam" instead.
        var file = workspace.File_("a.cs", "// \U0001F680 x name;\n");

        Assert.That("// \U0001F680 x ".Length, Is.EqualTo(8));

        var (applier, _) = Applier(workspace);

        WorkspaceEditApplier.Apply(applier.Plan(Edit(file, [Replace(0, 8, 0, 12, "renamed")])));

        Assert.That(File.ReadAllText(file), Is.EqualTo("// \U0001F680 x renamed;\n"));
    }

    /// <summary>C22 and the BOM rule together: the file keeps its mark and its endings.</summary>
    [Test]
    public void TheFilesMarkAndLineEndingSurviveAnEditWhoseTextMixesBoth()
    {
        using var workspace = TempWorkspace.Create("applier-bom");
        var file = workspace.Path_("a.cs");

        File.WriteAllBytes(
            file,
            [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("one\r\ntwo\r\nthree\r\n")]);

        var (applier, _) = Applier(workspace);

        WorkspaceEditApplier.Apply(applier.Plan(Edit(file, [Replace(1, 0, 1, 3, "TWO\nEXTRA")])));

        var bytes = File.ReadAllBytes(file);

        Assert.That(bytes[..3], Is.EqualTo([0xEF, 0xBB, 0xBF]));
        Assert.That(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), Is.EqualTo("one\r\nTWO\r\nEXTRA\r\nthree\r\n"));
    }

    [Test]
    public void AUtf16FileStaysUtf16()
    {
        using var workspace = TempWorkspace.Create("applier-utf16");
        var file = workspace.Path_("a.cs");

        File.WriteAllBytes(file, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("abc\n")).ToArray());

        var (applier, _) = Applier(workspace);

        WorkspaceEditApplier.Apply(applier.Plan(Edit(file, [Replace(0, 0, 0, 3, "xyz")])));

        var bytes = File.ReadAllBytes(file);

        Assert.That(bytes[..2], Is.EqualTo(Encoding.Unicode.GetPreamble()));
        Assert.That(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Is.EqualTo("xyz\n"));
    }

    /// <summary>
    /// C21: <c>Move type to X.cs</c> creates the file and then edits into it, in that order, in one
    /// array. Both halves have to work, and the order has to be honoured.
    /// </summary>
    [Test]
    public void ACreateFollowedByEditsIntoTheNewFileWorks()
    {
        using var workspace = TempWorkspace.Create("applier-create");
        var target = workspace.Path_("Core", "Circle.cs");
        var (applier, _) = Applier(workspace);

        var plan = applier.Plan(new WorkspaceEdit
        {
            DocumentChanges =
            [
                new DocumentChange { Kind = "create", Uri = Uri(target) },
                new DocumentChange
                {
                    TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = Uri(target) },
                    Edits = [new LspTextEdit { Range = ZeroRange, NewText = "class Circle;\n" }],
                },
            ],
        });

        WorkspaceEditApplier.Apply(plan);

        Assert.That(File.ReadAllText(target), Is.EqualTo("class Circle;\n"));
        Assert.That(plan.ChangedFiles, Has.Exactly(1).Items);
        Assert.That(plan.ChangedFiles.Single().Created, Is.True);
    }

    [Test]
    public void ARenameMovesTheFileAndLaterEditsLandOnTheNewName()
    {
        using var workspace = TempWorkspace.Create("applier-rename");
        var source = workspace.File_("Old.cs", "class Old;\n");
        var target = workspace.Path_("New.cs");
        var (applier, guard) = Applier(workspace);

        var plan = applier.Plan(new WorkspaceEdit
        {
            DocumentChanges =
            [
                new DocumentChange { Kind = "rename", OldUri = Uri(source), NewUri = Uri(target) },
                new DocumentChange
                {
                    TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = Uri(target) },
                    Edits = [new LspTextEdit { Range = Range(0, 6, 0, 9), NewText = "New" }],
                },
            ],
        });

        WorkspaceEditApplier.Apply(plan);

        Assert.That(File.Exists(source), Is.False);
        Assert.That(File.ReadAllText(target), Is.EqualTo("class New;\n"));

        var moved = plan.ChangedFiles.Single(file => file.MovedToRelativePath is not null);
        Assert.That(moved.MovedToRelativePath, Is.EqualTo("New.cs"));
        Assert.That(guard.ToRelative(moved.Path), Is.EqualTo("Old.cs"));
    }

    [Test]
    public void ADeleteRemovesTheFileAndIsReported()
    {
        using var workspace = TempWorkspace.Create("applier-delete");
        var file = workspace.File_("Gone.cs", "class Gone;\n");
        var (applier, _) = Applier(workspace);

        var plan = applier.Plan(new WorkspaceEdit
        {
            DocumentChanges = [new DocumentChange { Kind = "delete", Uri = Uri(file) }],
        });

        WorkspaceEditApplier.Apply(plan);

        Assert.That(File.Exists(file), Is.False);
        Assert.That(plan.ChangedFiles, Has.Exactly(1).Items);
        Assert.That(plan.ChangedFiles.Single().Deleted, Is.True);
    }

    /// <summary>
    /// The guard, from the applier's side. Roslyn hands out URIs outside the workspace as a matter of
    /// course — navigation into a BCL symbol lands in the system temp directory (C25).
    /// </summary>
    [Test]
    public void AUriOutsideTheWorkspaceIsRefused()
    {
        using var workspace = TempWorkspace.Create("applier-escape");
        var (applier, _) = Applier(workspace);

        var outside = Path.Combine(Path.GetTempPath(), "MetadataAsSource", "Console.cs");

        var exception = Assert.Throws<WorkspaceEditRefusedException>(() => applier.Plan(new WorkspaceEdit
        {
            DocumentChanges =
            [
                new DocumentChange
                {
                    TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = Uri(outside) },
                    Edits = [new LspTextEdit { Range = ZeroRange, NewText = "x" }],
                },
            ],
        }));

        Assert.That(exception!.Message, Contains.Substring("outside the workspace root").Using(StringComparison.Ordinal));
    }

    /// <summary>A traversal that resolves back inside the root is fine; one that escapes is not.</summary>
    [Test]
    public void ATraversalSegmentIsJudgedByWhereItResolvesTo()
    {
        using var workspace = TempWorkspace.Create("applier-traversal");
        workspace.File_("Core/a.cs", "x\n");
        var (_, guard) = Applier(workspace);

        var inside = new Uri(Path.Combine(workspace.Root, "Core", "..", "Core", "a.cs")).AbsoluteUri;
        Assert.That(guard.TryResolve(inside, out _, out _), Is.True);

        var outside = new Uri(Path.Combine(workspace.Root, "..", "elsewhere.cs")).AbsoluteUri;
        Assert.That(guard.TryResolve(outside, out _, out var reason), Is.False);
        Assert.That(reason, Contains.Substring("outside the workspace root").Using(StringComparison.Ordinal));
    }

    [Test]
    public void ANonFileSchemeIsRefused()
    {
        using var workspace = TempWorkspace.Create("applier-scheme");
        var (_, guard) = Applier(workspace);

        Assert.That(guard.TryResolve("https://example.com/a.cs", out _, out var reason), Is.False);
        Assert.That(reason, Contains.Substring("file:").Using(StringComparison.Ordinal));
    }

    /// <summary>Planning reads and computes; it must not write. That is what makes preview honest.</summary>
    [Test]
    public void PlanningWritesNothing()
    {
        using var workspace = TempWorkspace.Create("applier-plan");
        var file = workspace.File_("a.cs", "abc\n");
        var (applier, _) = Applier(workspace);

        var plan = applier.Plan(Edit(file, [Replace(0, 0, 0, 3, "xyz")]));

        Assert.That(File.ReadAllText(file), Is.EqualTo("abc\n"));
        Assert.That(plan.ChangedFiles, Has.Exactly(1).Items);
        Assert.That(plan.ChangedFiles.Single().NewText, Is.EqualTo("xyz\n"));
    }

    [Test]
    public void AnEditWithNoDocumentChangesPlansToNothing()
    {
        using var workspace = TempWorkspace.Create("applier-empty");
        var (applier, _) = Applier(workspace);

        Assert.That(applier.Plan(new WorkspaceEdit()).IsEmpty, Is.True);
        Assert.That(applier.Plan(null).IsEmpty, Is.True);
    }

    private static (WorkspaceEditApplier Applier, WorkspacePathGuard Guard) Applier(TempWorkspace workspace)
    {
        var guard = new WorkspacePathGuard(workspace.Root);
        return (new WorkspaceEditApplier(guard), guard);
    }

    private static string Uri(string path) => WorkspacePathGuard.ToUri(path);

    private static LspRange ZeroRange => Range(0, 0, 0, 0);

    private static LspRange Range(int line, int character, int endLine, int endCharacter) =>
        new()
        {
            Start = new LspPosition { Line = line, Character = character },
            End = new LspPosition { Line = endLine, Character = endCharacter },
        };

    private static LspTextEdit Replace(int line, int character, int endLine, int endCharacter, string newText) =>
        new() { Range = Range(line, character, endLine, endCharacter), NewText = newText };

    private static WorkspaceEdit Edit(string path, LspTextEdit[] edits) => new()
    {
        DocumentChanges =
        [
            new DocumentChange
            {
                TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = Uri(path) },
                Edits = edits,
            },
        ],
    };
}
