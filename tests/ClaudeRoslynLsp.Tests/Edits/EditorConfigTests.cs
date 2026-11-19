using ClaudeRoslynLsp.Edits;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Edits;

/// <summary>
/// D113: the <c>.editorconfig</c> chain <c>formatCode</c> reads <c>end_of_line</c> and
/// <c>insert_final_newline</c> from, resolved the way the EditorConfig specification says.
/// </summary>
public class EditorConfigTests
{
    [TestCase("*", "a.cs", true)]
    [TestCase("*.cs", "src/deep/a.cs", true)]
    [TestCase("*.cs", "a.csx", false)]
    [TestCase("*.{cs,vb}", "src/a.vb", true)]
    [TestCase("*.{cs,vb}", "src/a.fs", false)]
    [TestCase("src/*.cs", "src/a.cs", true)]
    [TestCase("src/*.cs", "src/deep/a.cs", false)]
    [TestCase("src/**/*.cs", "src/a.cs", true)]
    [TestCase("src/**/*.cs", "src/deep/er/a.cs", true)]
    [TestCase("/src/*.cs", "src/a.cs", true)]
    [TestCase("src/**.cs", "src/deep/a.cs", true)]
    [TestCase("**/obj/**", "a/obj/b/c.cs", true)]
    [TestCase("a?.cs", "ab.cs", true)]
    [TestCase("a?.cs", "a/.cs", false)]
    [TestCase("[ab].cs", "b.cs", true)]
    [TestCase("[!ab].cs", "b.cs", false)]
    [TestCase("[!ab].cs", "c.cs", true)]
    [TestCase("file{1..3}.cs", "file2.cs", true)]
    [TestCase("file{1..3}.cs", "file4.cs", false)]
    [TestCase("{a}.cs", "{a}.cs", true)]
    [TestCase("a\\*.cs", "a*.cs", true)]
    [TestCase("a\\*.cs", "ab.cs", false)]
    [TestCase("*.CS", "a.cs", false)]
    [TestCase("my (2)+dir/*.cs", "my (2)+dir/a.cs", true)]
    public void ASectionGlobMatchesTheWayTheSpecificationSays(string glob, string relativePath, bool expected) =>
        Assert.That(new EditorConfig.Section(glob).Matches(relativePath), Is.EqualTo(expected), glob);

    [Test]
    public void AnUnbalancedOrOversizedGlobMatchesNothing()
    {
        Assert.That(new EditorConfig.Section("file{1..5000}.cs").Matches("file3.cs"), Is.False);
        Assert.That(new EditorConfig.Section("{a,b").Matches("a"), Is.False);
    }

    [Test]
    public void TheNearestFileWinsAndARootFileStopsTheSearch()
    {
        using var workspace = TempWorkspace.Create("editorconfig-chain");

        // Above the root: never read.
        workspace.File_(".editorconfig", "[*]\nend_of_line = cr\ninsert_final_newline = true\n");
        workspace.File_("repo/.editorconfig", "root = true\n\n[*]\nend_of_line = lf\ninsert_final_newline = true\n");
        workspace.File_("repo/src/.editorconfig", "[*.cs]\nend_of_line = crlf\n");
        var file = workspace.File_("repo/src/Lib/a.cs", string.Empty);

        var layout = EditorConfig.LayoutFor(file);

        Assert.That(layout.EndOfLine, Is.EqualTo("\r\n"));
        Assert.That(layout.InsertFinalNewline, Is.True);
    }

    [Test]
    public void ALaterSectionOverridesAnEarlierOneAndUnsetRemovesAProperty()
    {
        using var workspace = TempWorkspace.Create("editorconfig-order");

        workspace.File_(".editorconfig", """
            root = true
            # a comment
            ; another one
            [*]
            END_OF_LINE = LF
            insert_final_newline = true

            [*.cs]
            end_of_line = crlf
            insert_final_newline = unset
            """);
        var file = workspace.File_("a.cs", string.Empty);

        var layout = EditorConfig.LayoutFor(file);

        Assert.That(layout.EndOfLine, Is.EqualTo("\r\n"));
        Assert.That(layout.InsertFinalNewline, Is.Null);
    }

    [Test]
    public void NoEditorConfigMeansNoLayout()
    {
        using var workspace = TempWorkspace.Create("editorconfig-none");
        workspace.File_(".editorconfig", "root = true\n[*.md]\nend_of_line = crlf\n");
        var file = workspace.File_("a.cs", string.Empty);

        Assert.That(EditorConfig.LayoutFor(file).IsSet, Is.False);
    }

    [TestCase("a\nb\n", "\r\n", false, "a\r\nb")]
    [TestCase("a\r\nb", "\n", true, "a\nb\n")]
    [TestCase("a\nb\n\n", null, false, "a\nb")]
    [TestCase("a\nb", null, true, "a\nb\n")]
    [TestCase("a\nb\n", null, true, "a\nb\n")]
    [TestCase("", null, true, "")]
    public void TheLayoutRewritesLineEndingsAndTheFinalNewline(string text, string? endOfLine, bool finalNewline, string expected) =>
        Assert.That(new TextLayout(endOfLine, finalNewline).Apply(text, "\n"), Is.EqualTo(expected));
}
