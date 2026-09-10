using ClaudeRoslynLsp.Edits;
using ClaudeRoslynLsp.Mcp.Engine;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Edits;

/// <summary>
/// The budgeted diff (D62). What is cut has to be named, or a caller cannot tell a small change from
/// a truncated description of a large one.
/// </summary>
public class UnifiedDiffTests
{
    [Fact]
    public void AOneLineChangeRendersAsOneHunkWithContext()
    {
        using var workspace = TempWorkspace.Create("diff-one");
        var plan = Plan(workspace, "one\ntwo\nthree\nfour\nfive\n", 2, "TWO");

        var diff = UnifiedDiff.Render(plan);

        Assert.Contains("--- a/a.cs", diff, StringComparison.Ordinal);
        Assert.Contains("+++ b/a.cs", diff, StringComparison.Ordinal);
        Assert.Contains("-two", diff, StringComparison.Ordinal);
        Assert.Contains("+TWO", diff, StringComparison.Ordinal);
        Assert.Contains(" one", diff, StringComparison.Ordinal);
        Assert.Contains("@@ ", diff, StringComparison.Ordinal);
    }

    /// <summary>
    /// Unchanged lines far from the change must not be printed, or a one-word rename in a large file
    /// costs the whole file.
    /// </summary>
    [Fact]
    public void UnchangedLinesOutsideTheContextWindowAreNotPrinted()
    {
        using var workspace = TempWorkspace.Create("diff-context");

        var original = string.Join('\n', Enumerable.Range(1, 200).Select(index => "line" + index)) + "\n";
        var plan = Plan(workspace, original, 100, "CHANGED");

        var diff = UnifiedDiff.Render(plan);

        Assert.Contains("+CHANGED", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("line1\n", diff, StringComparison.Ordinal);
        Assert.True(diff.Split('\n').Length < 15, diff);
    }

    /// <summary>The budget is enforced and the shortfall is named, not swallowed.</summary>
    [Fact]
    public void ThePerFileBudgetTruncatesAndSaysHowManyHunksAreLeft()
    {
        using var workspace = TempWorkspace.Create("diff-budget");

        var original = string.Join('\n', Enumerable.Range(1, 400).Select(index => "line" + index)) + "\n";
        var lines = original.Split('\n').ToArray();

        // Twenty separate one-line changes, far enough apart to be twenty hunks.
        var edits = new List<LspTextEdit>();

        for (var index = 0; index < 20; index++)
        {
            var line = 10 + (index * 15);

            edits.Add(new LspTextEdit
            {
                Range = new LspRange
                {
                    Start = new LspPosition { Line = line, Character = 0 },
                    End = new LspPosition { Line = line, Character = lines[line].Length },
                },
                NewText = "CHANGED" + index,
            });
        }

        var plan = Plan(workspace, original, edits);

        var diff = UnifiedDiff.Render(plan, maxDiffLines: 30);

        Assert.Contains("more hunks", diff, StringComparison.Ordinal);
        Assert.True(diff.Split('\n').Length <= 34, diff);
    }

    /// <summary>A created file diffs against nothing, which is what <c>/dev/null</c> means.</summary>
    [Fact]
    public void ACreatedFileDiffsAgainstDevNull()
    {
        using var workspace = TempWorkspace.Create("diff-create");
        var guard = new WorkspacePathGuard(workspace.Root);
        var applier = new WorkspaceEditApplier(guard);
        var target = workspace.Path_("New.cs");

        var plan = applier.Plan(new WorkspaceEdit
        {
            DocumentChanges =
            [
                new DocumentChange { Kind = "create", Uri = WorkspacePathGuard.ToUri(target) },
                new DocumentChange
                {
                    TextDocument = new OptionalVersionedTextDocumentIdentifier
                    {
                        Uri = WorkspacePathGuard.ToUri(target),
                    },
                    Edits =
                    [
                        new LspTextEdit
                        {
                            Range = new LspRange(),
                            NewText = "class New;\n",
                        },
                    ],
                },
            ],
        });

        var diff = UnifiedDiff.Render(plan);

        Assert.Contains("--- /dev/null", diff, StringComparison.Ordinal);
        Assert.Contains("+++ b/New.cs", diff, StringComparison.Ordinal);
        Assert.Contains("+class New;", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyPlanRendersNothing() =>
        Assert.Empty(UnifiedDiff.Render(new WorkspaceEditPlan([])));

    private static WorkspaceEditPlan Plan(TempWorkspace workspace, string original, int oneBasedLine, string newText)
    {
        var lines = original.Split('\n');

        return Plan(workspace, original, [
            new LspTextEdit
            {
                Range = new LspRange
                {
                    Start = new LspPosition { Line = oneBasedLine - 1, Character = 0 },
                    End = new LspPosition { Line = oneBasedLine - 1, Character = lines[oneBasedLine - 1].Length },
                },
                NewText = newText,
            },
        ]);
    }

    private static WorkspaceEditPlan Plan(TempWorkspace workspace, string original, IReadOnlyList<LspTextEdit> edits)
    {
        var path = workspace.File_("a.cs", original);
        var applier = new WorkspaceEditApplier(new WorkspacePathGuard(workspace.Root));

        return applier.Plan(new WorkspaceEdit
        {
            DocumentChanges =
            [
                new DocumentChange
                {
                    TextDocument = new OptionalVersionedTextDocumentIdentifier
                    {
                        Uri = WorkspacePathGuard.ToUri(path),
                    },
                    Edits = [.. edits],
                },
            ],
        });
    }
}
