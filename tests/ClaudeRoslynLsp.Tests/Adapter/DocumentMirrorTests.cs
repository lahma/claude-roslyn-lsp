using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// The mirror, and the replay that is the whole reason it exists.
/// </summary>
/// <remarks>
/// A relaunched Roslyn knows only what is on disk. If the client has unsaved edits — which, with an
/// agent driving, it usually does — every answer from that Roslyn is computed against text nobody is
/// looking at, and the positions in it are wrong by however many lines the edits added. The replay is
/// one <c>didOpen</c> per open document at its latest text, and it has to be exactly that shape or a
/// server that has never heard of the document will reject it.
/// </remarks>
public class DocumentMirrorTests
{
    private const string Uri = "file:///w/Fixture/Fixture.Core/Calculator.cs";

    [Test]
    public void OpeningRecordsTheDocumentAsTheClientSentIt()
    {
        var mirror = new DocumentMirror(NullLogger.Instance);
        var document = mirror.Open(DidOpen(Uri, "csharp", 1, "class C { }"));

        Assert.That(document, Is.Not.Null);
        Assert.That(document!.Uri, Is.EqualTo(Uri));
        Assert.That(document.LanguageId, Is.EqualTo("csharp"));
        Assert.That(document.Version, Is.EqualTo(1));
        Assert.That(document.Text, Is.EqualTo("class C { }"));
        Assert.That(mirror.Count, Is.EqualTo(1));
    }

    /// <summary>
    /// Full synchronisation (D13): the change carries the whole document, so the mirror replaces
    /// rather than patches. That is what makes a replay correct after any number of edits.
    /// </summary>
    [Test]
    public void AFullTextChangeReplacesTheDocumentAndAdvancesItsVersion()
    {
        var mirror = new DocumentMirror(NullLogger.Instance);
        mirror.Open(DidOpen(Uri, "csharp", 1, "class C { }"));

        var changed = mirror.Change(DidChange(Uri, 4, "class C { int X; }"));

        Assert.That(changed, Is.Not.Null);
        Assert.That(changed!.Version, Is.EqualTo(4));
        Assert.That(changed.Text, Is.EqualTo("class C { int X; }"));
        Assert.That(changed.LanguageId, Is.EqualTo("csharp"));
        Assert.That(mirror.Count, Is.EqualTo(1));
    }

    [Test]
    public void ClosingForgetsTheDocument()
    {
        var mirror = new DocumentMirror(NullLogger.Instance);
        mirror.Open(DidOpen(Uri, "csharp", 1, "class C { }"));

        Assert.That(mirror.Close(DidClose(Uri)), Is.EqualTo(Uri));
        Assert.That(mirror.Count, Is.EqualTo(0));
        Assert.That(mirror.BuildReplay(), Is.Empty);
    }

    /// <summary>
    /// The recovery primitive, asserted on its shape rather than on its effect: one
    /// <c>textDocument/didOpen</c> per document, carrying the latest text and the latest version.
    /// Three edits before a restart replay as one notification, not four.
    /// </summary>
    [Test]
    public void TheReplayIsOneDidOpenPerDocumentAtItsLatestText()
    {
        var mirror = new DocumentMirror(NullLogger.Instance);

        mirror.Open(DidOpen(Uri, "csharp", 1, "one"));
        mirror.Change(DidChange(Uri, 2, "two"));
        mirror.Change(DidChange(Uri, 3, "three"));
        mirror.Open(DidOpen("file:///w/Other.cs", "csharp", 1, "other"));

        var replay = mirror.BuildReplay();

        Assert.That(replay.Count, Is.EqualTo(2));

        using var first = JsonDocument.Parse(replay[0]);
        var root = first.RootElement;

        Assert.That(root.GetProperty("jsonrpc").GetString(), Is.EqualTo("2.0"));
        Assert.That(root.GetProperty("method").GetString(), Is.EqualTo("textDocument/didOpen"));
        Assert.That(root.TryGetProperty("id", out _), Is.False);

        var item = root.GetProperty("params").GetProperty("textDocument");

        Assert.That(item.GetProperty("uri").GetString(), Is.EqualTo(Uri));
        Assert.That(item.GetProperty("languageId").GetString(), Is.EqualTo("csharp"));
        Assert.That(item.GetProperty("version").GetInt32(), Is.EqualTo(3));
        Assert.That(item.GetProperty("text").GetString(), Is.EqualTo("three"));
    }

    /// <summary>
    /// A change for a document that was never opened still lands, because the notification's own
    /// arrival is the fact — losing it would leave the mirror silently behind the client.
    /// </summary>
    [Test]
    public void AChangeForAnUnknownDocumentIsStillRecorded()
    {
        var mirror = new DocumentMirror(NullLogger.Instance);
        var changed = mirror.Change(DidChange(Uri, 7, "text"));

        Assert.That(changed, Is.Not.Null);
        Assert.That(changed!.LanguageId, Is.EqualTo("csharp"));
        Assert.That(mirror.Count, Is.EqualTo(1));
    }

    [TestCase("not json")]
    [TestCase("""{"jsonrpc":"2.0","method":"textDocument/didOpen"}""")]
    [TestCase("""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{}}""")]
    public void AnUnreadableNotificationChangesNothing(string message)
    {
        var mirror = new DocumentMirror(NullLogger.Instance);

        Assert.That(mirror.Open(Encoding.UTF8.GetBytes(message)), Is.Null);
        Assert.That(mirror.Count, Is.EqualTo(0));
    }

    // ------------------------------------------------------------------ the byte order mark (C67, D111)

    /// <summary>
    /// Issue #3: Claude Code sends a UTF-8 file's mark as the document's first character. Roslyn would
    /// take it for text, so the notification is rebuilt without it, and nothing else about it changes.
    /// </summary>
    [Test]
    public void ADidOpenLosesItsByteOrderMarkAndKeepsEverythingElse()
    {
        var rebuilt = DocumentMirror.WithoutByteOrderMark(
            DidOpen(Uri, "csharp", 3, "\uFEFFusing System;\r\n"), "textDocument/didOpen");

        var item = new DocumentMirror(NullLogger.Instance).Open(rebuilt);

        Assert.That(item, Is.Not.Null);
        Assert.That(item!.Text, Is.EqualTo("using System;\r\n"));
        Assert.That(item.Uri, Is.EqualTo(Uri));
        Assert.That(item.LanguageId, Is.EqualTo("csharp"));
        Assert.That(item.Version, Is.EqualTo(3));
    }

    [Test]
    public void AFullTextDidChangeLosesItsByteOrderMarkAndKeepsItsVersion()
    {
        var rebuilt = DocumentMirror.WithoutByteOrderMark(DidChange(Uri, 9, "\uFEFFclass C { }"), "textDocument/didChange");

        using var document = JsonDocument.Parse(rebuilt);
        var parameters = document.RootElement.GetProperty("params");

        Assert.That(document.RootElement.GetProperty("method").GetString(), Is.EqualTo("textDocument/didChange"));
        Assert.That(parameters.GetProperty("textDocument").GetProperty("uri").GetString(), Is.EqualTo(Uri));
        Assert.That(parameters.GetProperty("textDocument").GetProperty("version").GetInt32(), Is.EqualTo(9));
        Assert.That(parameters.GetProperty("contentChanges")[0].GetProperty("text").GetString(), Is.EqualTo("class C { }"));
        Assert.That(parameters.GetProperty("contentChanges")[0].TryGetProperty("range", out _), Is.False);
    }

    /// <summary>D47: only a notification that carried a mark is reserialised; the rest keep the client's bytes.</summary>
    [Test]
    public void ANotificationWithoutAMarkKeepsTheClientsBytes()
    {
        var open = DidOpen(Uri, "csharp", 1, "using System;");
        var change = DidChange(Uri, 2, "using System;");
        var close = DidClose(Uri);

        Assert.That(DocumentMirror.WithoutByteOrderMark(open, "textDocument/didOpen"), Is.SameAs(open));
        Assert.That(DocumentMirror.WithoutByteOrderMark(change, "textDocument/didChange"), Is.SameAs(change));
        Assert.That(DocumentMirror.WithoutByteOrderMark(close, "textDocument/didClose"), Is.SameAs(close));
    }

    /// <summary>One mark is encoding; a second one after it is a character the file really contains.</summary>
    [Test]
    public void OnlyOneMarkIsRemoved()
    {
        var rebuilt = DocumentMirror.WithoutByteOrderMark(
            DidOpen(Uri, "csharp", 1, "\uFEFF\uFEFFx"), "textDocument/didOpen");

        Assert.That(new DocumentMirror(NullLogger.Instance).Open(rebuilt)!.Text, Is.EqualTo("\uFEFFx"));
    }

    /// <summary>A ranged change is out of contract (D13), and its offsets are the client's to answer for.</summary>
    [Test]
    public void ARangedDidChangeIsLeftAlone()
    {
        var ranged = Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"file:///w/a.cs","version":2},"contentChanges":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":0}},"text":"\uFEFFx"}]}}""");

        Assert.That(DocumentMirror.WithoutByteOrderMark(ranged, "textDocument/didChange"), Is.SameAs(ranged));
    }

    private static byte[] DidOpen(string uri, string languageId, int version, string text) =>
        Encoding.UTF8.GetBytes(Fill(
            """
            {"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"#URI#","languageId":"#LANG#","version":#VERSION#,"text":#TEXT#}}}
            """,
            ("#URI#", uri),
            ("#LANG#", languageId),
            ("#VERSION#", Text(version)),
            ("#TEXT#", JsonSerializer.Serialize(text, TestJson.String))));

    private static byte[] DidChange(string uri, int version, string text) =>
        Encoding.UTF8.GetBytes(Fill(
            """
            {"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"#URI#","version":#VERSION#},"contentChanges":[{"text":#TEXT#}]}}
            """,
            ("#URI#", uri),
            ("#VERSION#", Text(version)),
            ("#TEXT#", JsonSerializer.Serialize(text, TestJson.String))));

    private static byte[] DidClose(string uri) =>
        Encoding.UTF8.GetBytes(Fill(
            """
            {"jsonrpc":"2.0","method":"textDocument/didClose","params":{"textDocument":{"uri":"#URI#"}}}
            """,
            ("#URI#", uri)));

    private static string Text(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Substitutes into a plain raw-string JSON template.
    /// </summary>
    /// <remarks>
    /// An interpolated raw string cannot carry these payloads legibly: an LSP message ends in a run
    /// of closing braces long enough that the number of <c>$</c> characters needed to escape it stops
    /// being readable, and one brace out of step is a compile error somewhere else entirely. A
    /// placeholder the JSON itself can never contain keeps the literal looking like the message it is.
    /// </remarks>
    /// <param name="template">The JSON, with <c>#NAME#</c> placeholders.</param>
    /// <param name="values">What to put in them.</param>
    private static string Fill(string template, params (string Token, string Value)[] values)
    {
        foreach (var (token, value) in values)
        {
            template = template.Replace(token, value, StringComparison.Ordinal);
        }

        return template;
    }
}
