using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

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

    [Fact]
    public void OpeningRecordsTheDocumentAsTheClientSentIt()
    {
        var mirror = new DocumentMirror(NullLogger.Instance);
        var document = mirror.Open(DidOpen(Uri, "csharp", 1, "class C { }"));

        Assert.NotNull(document);
        Assert.Equal(Uri, document.Uri);
        Assert.Equal("csharp", document.LanguageId);
        Assert.Equal(1, document.Version);
        Assert.Equal("class C { }", document.Text);
        Assert.Equal(1, mirror.Count);
    }

    /// <summary>
    /// Full synchronisation (D13): the change carries the whole document, so the mirror replaces
    /// rather than patches. That is what makes a replay correct after any number of edits.
    /// </summary>
    [Fact]
    public void AFullTextChangeReplacesTheDocumentAndAdvancesItsVersion()
    {
        var mirror = new DocumentMirror(NullLogger.Instance);
        mirror.Open(DidOpen(Uri, "csharp", 1, "class C { }"));

        var changed = mirror.Change(DidChange(Uri, 4, "class C { int X; }"));

        Assert.NotNull(changed);
        Assert.Equal(4, changed.Version);
        Assert.Equal("class C { int X; }", changed.Text);
        Assert.Equal("csharp", changed.LanguageId);
        Assert.Equal(1, mirror.Count);
    }

    [Fact]
    public void ClosingForgetsTheDocument()
    {
        var mirror = new DocumentMirror(NullLogger.Instance);
        mirror.Open(DidOpen(Uri, "csharp", 1, "class C { }"));

        Assert.Equal(Uri, mirror.Close(DidClose(Uri)));
        Assert.Equal(0, mirror.Count);
        Assert.Empty(mirror.BuildReplay());
    }

    /// <summary>
    /// The recovery primitive, asserted on its shape rather than on its effect: one
    /// <c>textDocument/didOpen</c> per document, carrying the latest text and the latest version.
    /// Three edits before a restart replay as one notification, not four.
    /// </summary>
    [Fact]
    public void TheReplayIsOneDidOpenPerDocumentAtItsLatestText()
    {
        var mirror = new DocumentMirror(NullLogger.Instance);

        mirror.Open(DidOpen(Uri, "csharp", 1, "one"));
        mirror.Change(DidChange(Uri, 2, "two"));
        mirror.Change(DidChange(Uri, 3, "three"));
        mirror.Open(DidOpen("file:///w/Other.cs", "csharp", 1, "other"));

        var replay = mirror.BuildReplay();

        Assert.Equal(2, replay.Count);

        using var first = JsonDocument.Parse(replay[0]);
        var root = first.RootElement;

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal("textDocument/didOpen", root.GetProperty("method").GetString());
        Assert.False(root.TryGetProperty("id", out _));

        var item = root.GetProperty("params").GetProperty("textDocument");

        Assert.Equal(Uri, item.GetProperty("uri").GetString());
        Assert.Equal("csharp", item.GetProperty("languageId").GetString());
        Assert.Equal(3, item.GetProperty("version").GetInt32());
        Assert.Equal("three", item.GetProperty("text").GetString());
    }

    /// <summary>
    /// A change for a document that was never opened still lands, because the notification's own
    /// arrival is the fact — losing it would leave the mirror silently behind the client.
    /// </summary>
    [Fact]
    public void AChangeForAnUnknownDocumentIsStillRecorded()
    {
        var mirror = new DocumentMirror(NullLogger.Instance);
        var changed = mirror.Change(DidChange(Uri, 7, "text"));

        Assert.NotNull(changed);
        Assert.Equal("csharp", changed.LanguageId);
        Assert.Equal(1, mirror.Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"jsonrpc":"2.0","method":"textDocument/didOpen"}""")]
    [InlineData("""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{}}""")]
    public void AnUnreadableNotificationChangesNothing(string message)
    {
        var mirror = new DocumentMirror(NullLogger.Instance);

        Assert.Null(mirror.Open(Encoding.UTF8.GetBytes(message)));
        Assert.Equal(0, mirror.Count);
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
