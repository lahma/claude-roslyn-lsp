using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeRoslynLsp.Mcp.Engine;

/// <summary>
/// The <c>params</c> shapes the MCP half sends Roslyn, and the two answer shapes it has to read
/// that are not already in <c>RoslynEngineShapes</c>.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>RoslynEngineShapes</c> because that file is the vocabulary of Roslyn's
/// <em>answers</em> — the shapes the tool layer reasons about and the fake engine in the tests
/// produces — and these are the outbound halves nothing above the engine ever sees. Keeping them
/// apart is what stops a tool test from being able to construct a request.
/// </para>
/// <para>
/// Every member carries an explicit <c>[JsonPropertyName]</c>, for the same reason the rest of this
/// namespace does (D7): these are the LSP specification's names, not this repository's, and a naming
/// policy that guessed them right today could guess wrong after a pin bump.
/// </para>
/// </remarks>
internal sealed record TextDocumentRef
{
    /// <summary>The document's <c>file:</c> URI.</summary>
    [JsonPropertyName("uri")]
    public string Uri { get; init; } = string.Empty;
}

/// <summary><c>workspace/symbol</c>.</summary>
internal sealed record WorkspaceSymbolRequest
{
    /// <summary>The name to search for. Roslyn searches simple names, not qualified ones (D63).</summary>
    [JsonPropertyName("query")]
    public string Query { get; init; } = string.Empty;
}

/// <summary>A document plus a position: hover, definition, prepareRename and friends.</summary>
internal sealed record TextDocumentPositionRequest
{
    /// <summary>The document.</summary>
    [JsonPropertyName("textDocument")]
    public TextDocumentRef TextDocument { get; init; } = new();

    /// <summary>Where in it, zero-based.</summary>
    [JsonPropertyName("position")]
    public LspPosition Position { get; init; } = new();
}

/// <summary>Whether the declaration itself counts as a reference.</summary>
internal sealed record ReferenceContextShape
{
    /// <summary>Include the declaration.</summary>
    [JsonPropertyName("includeDeclaration")]
    public bool IncludeDeclaration { get; init; }
}

/// <summary><c>textDocument/references</c>.</summary>
internal sealed record ReferencesRequest
{
    /// <summary>The document.</summary>
    [JsonPropertyName("textDocument")]
    public TextDocumentRef TextDocument { get; init; } = new();

    /// <summary>Where in it.</summary>
    [JsonPropertyName("position")]
    public LspPosition Position { get; init; } = new();

    /// <summary>The context. Roslyn requires it; an omitted one is a request it refuses.</summary>
    [JsonPropertyName("context")]
    public ReferenceContextShape Context { get; init; } = new();
}

/// <summary><c>textDocument/rename</c>.</summary>
internal sealed record RenameRequest
{
    /// <summary>The document.</summary>
    [JsonPropertyName("textDocument")]
    public TextDocumentRef TextDocument { get; init; } = new();

    /// <summary>Where in it.</summary>
    [JsonPropertyName("position")]
    public LspPosition Position { get; init; } = new();

    /// <summary>The new name.</summary>
    [JsonPropertyName("newName")]
    public string NewName { get; init; } = string.Empty;
}

/// <summary>The diagnostics a code-action request is made in the context of.</summary>
internal sealed record CodeActionContextShape
{
    /// <summary>The diagnostics at the position, when the caller has them.</summary>
    [JsonPropertyName("diagnostics")]
    public RawDiagnostic[] Diagnostics { get; init; } = [];
}

/// <summary><c>textDocument/codeAction</c>.</summary>
internal sealed record CodeActionRequest
{
    /// <summary>The document.</summary>
    [JsonPropertyName("textDocument")]
    public TextDocumentRef TextDocument { get; init; } = new();

    /// <summary>The selection the actions are offered for.</summary>
    [JsonPropertyName("range")]
    public LspRange Range { get; init; } = new();

    /// <summary>The context. Mandatory in LSP even when it carries nothing.</summary>
    [JsonPropertyName("context")]
    public CodeActionContextShape Context { get; init; } = new();
}

/// <summary>
/// <c>codeAction/resolveFixAll</c>, Roslyn's own method (C20).
/// </summary>
/// <remarks>
/// <c>scope</c> is mandatory and case-sensitive: omitting it fails with an
/// <c>InvalidCastException</c> inside Roslyn and a lowercase spelling with "Sequence contains no
/// elements" (C20, S4b). It is therefore rendered from the enum rather than from caller text.
/// </remarks>
internal sealed record ResolveFixAllRequest
{
    /// <summary>The action's title, exactly as the list reported it.</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>The plain action's own <c>data</c>, round-tripped verbatim.</summary>
    [JsonPropertyName("data")]
    public JsonElement Data { get; init; }

    /// <summary><c>Document</c>, <c>Project</c> or <c>Solution</c>.</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; init; } = string.Empty;
}

/// <summary>Indent width and tabs-versus-spaces.</summary>
internal sealed record FormattingOptionsShape
{
    /// <summary>Indent width.</summary>
    [JsonPropertyName("tabSize")]
    public int TabSize { get; init; } = 4;

    /// <summary>Indent with spaces rather than tabs.</summary>
    [JsonPropertyName("insertSpaces")]
    public bool InsertSpaces { get; init; } = true;
}

/// <summary><c>textDocument/formatting</c>.</summary>
internal sealed record FormattingRequest
{
    /// <summary>The document.</summary>
    [JsonPropertyName("textDocument")]
    public TextDocumentRef TextDocument { get; init; } = new();

    /// <summary>The options. <c>.editorconfig</c> still wins where it has an opinion.</summary>
    [JsonPropertyName("options")]
    public FormattingOptionsShape Options { get; init; } = new();
}

/// <summary>
/// <c>textDocument/diagnostic</c>, deliberately without an <c>identifier</c> (C9).
/// </summary>
/// <remarks>
/// One pull with no identifier returns the union of every source, which is one round trip instead of
/// ten for the same set. There is no <c>previousResultId</c> either: an MCP call is a question asked
/// once, and an <c>unchanged</c> answer would be a report with no items in it.
/// </remarks>
internal sealed record DocumentDiagnosticRequest
{
    /// <summary>The document.</summary>
    [JsonPropertyName("textDocument")]
    public TextDocumentRef TextDocument { get; init; } = new();
}

/// <summary>One <c>previousResultId</c> entry of a workspace pull (C12).</summary>
internal sealed record PreviousResultIdShape
{
    /// <summary>The document.</summary>
    [JsonPropertyName("uri")]
    public string Uri { get; init; } = string.Empty;

    /// <summary>The <c>resultId</c> the last full report for it carried.</summary>
    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;
}

/// <summary><c>workspace/diagnostic</c>.</summary>
internal sealed record WorkspaceDiagnosticRequest
{
    /// <summary>What the caller already has, so an unchanged document answers cheaply.</summary>
    [JsonPropertyName("previousResultIds")]
    public PreviousResultIdShape[] PreviousResultIds { get; init; } = [];
}

/// <summary><c>textDocument/didClose</c>.</summary>
internal sealed record DidCloseRequest
{
    /// <summary>The document.</summary>
    [JsonPropertyName("textDocument")]
    public TextDocumentRef TextDocument { get; init; } = new();
}

/// <summary>One entry of a <c>workspace/didChangeWatchedFiles</c> notification.</summary>
internal sealed record FileEventShape
{
    /// <summary>The file's <c>file:</c> URI.</summary>
    [JsonPropertyName("uri")]
    public string Uri { get; init; } = string.Empty;

    /// <summary>1 created, 2 changed, 3 deleted.</summary>
    [JsonPropertyName("type")]
    public int Type { get; init; }
}

/// <summary><c>workspace/didChangeWatchedFiles</c>.</summary>
internal sealed record DidChangeWatchedFilesRequest
{
    /// <summary>What changed.</summary>
    [JsonPropertyName("changes")]
    public FileEventShape[] Changes { get; init; } = [];
}

/// <summary>The markdown block a <c>textDocument/hover</c> answers with.</summary>
internal sealed record HoverContents
{
    /// <summary>Always <c>markdown</c> here, because D14 asks for it.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>The markdown, which opens with a <c>```csharp</c> fence carrying the signature (S5).</summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

/// <summary>A <c>textDocument/hover</c> answer.</summary>
internal sealed record HoverResult
{
    /// <summary>The contents.</summary>
    [JsonPropertyName("contents")]
    public HoverContents? Contents { get; init; }

    /// <summary>The range the hover describes.</summary>
    [JsonPropertyName("range")]
    public LspRange? Range { get; init; }
}

/// <summary>
/// A <c>textDocument/prepareRename</c> answer in its second permitted shape.
/// </summary>
/// <remarks>
/// This Roslyn build answers with a bare <c>Range</c> (C23), which deserialises straight into
/// <see cref="LspRange"/>. LSP 3.17 also allows <c>{ range, placeholder }</c>, and a build that
/// switched to it would otherwise produce a range of zeros — a rename that silently refuses every
/// symbol. Reading both costs one record.
/// </remarks>
internal sealed record PrepareRenameResult
{
    /// <summary>The range Roslyn is willing to rename.</summary>
    [JsonPropertyName("range")]
    public LspRange? Range { get; init; }

    /// <summary>The text the range currently holds.</summary>
    [JsonPropertyName("placeholder")]
    public string? Placeholder { get; init; }
}
