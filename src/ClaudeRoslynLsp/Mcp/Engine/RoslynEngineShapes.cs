using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeRoslynLsp.Mcp.Engine;

/// <summary>A zero-based LSP position. The tool layer converts to and from 1-based (D63).</summary>
internal sealed record LspPosition
{
    /// <summary>Zero-based line.</summary>
    [JsonPropertyName("line")]
    public int Line { get; init; }

    /// <summary>Zero-based UTF-16 code unit offset within the line (D14 pins `utf-16`).</summary>
    [JsonPropertyName("character")]
    public int Character { get; init; }

    /// <summary>Builds a position from 1-based line and column numbers.</summary>
    internal static LspPosition FromOneBased(int line, int column) =>
        new() { Line = line - 1, Character = column - 1 };

    /// <summary>The 1-based line number this position sits on.</summary>
    [JsonIgnore]
    internal int OneBasedLine => Line + 1;

    /// <summary>The 1-based column number this position sits on.</summary>
    [JsonIgnore]
    internal int OneBasedColumn => Character + 1;
}

/// <summary>A zero-based LSP range.</summary>
internal sealed record LspRange
{
    /// <summary>The inclusive start.</summary>
    [JsonPropertyName("start")]
    public LspPosition Start { get; init; } = new();

    /// <summary>The exclusive end.</summary>
    [JsonPropertyName("end")]
    public LspPosition End { get; init; } = new();

    /// <summary>An empty range at one position.</summary>
    internal static LspRange At(LspPosition position) => new() { Start = position, End = position };

    /// <summary>A range from 1-based coordinates.</summary>
    internal static LspRange FromOneBased(int line, int column, int endLine, int endColumn) =>
        new()
        {
            Start = LspPosition.FromOneBased(line, column),
            End = LspPosition.FromOneBased(endLine, endColumn),
        };
}

/// <summary>An LSP location: a document and a range inside it.</summary>
internal sealed record LspLocation
{
    /// <summary>The document's <c>file:</c> URI.</summary>
    [JsonPropertyName("uri")]
    public string Uri { get; init; } = string.Empty;

    /// <summary>The range inside it.</summary>
    [JsonPropertyName("range")]
    public LspRange Range { get; init; } = new();
}

/// <summary>One replacement inside a document.</summary>
internal sealed record LspTextEdit
{
    /// <summary>The range being replaced.</summary>
    [JsonPropertyName("range")]
    public LspRange Range { get; init; } = new();

    /// <summary>The replacement text. May itself mix <c>\r\n</c> and <c>\n</c> (C22).</summary>
    [JsonPropertyName("newText")]
    public string NewText { get; init; } = string.Empty;
}

/// <summary>The document a <c>TextDocumentEdit</c> addresses. Roslyn always sends a null version (C21).</summary>
internal sealed record OptionalVersionedTextDocumentIdentifier
{
    /// <summary>The document's <c>file:</c> URI.</summary>
    [JsonPropertyName("uri")]
    public string Uri { get; init; } = string.Empty;

    /// <summary>Always <see langword="null"/> from this Roslyn build (C21); modelled because LSP allows a number.</summary>
    [JsonPropertyName("version")]
    public int? Version { get; init; }
}

/// <summary>
/// One entry of a <see cref="WorkspaceEdit"/>'s <c>documentChanges</c> array, flattened.
/// </summary>
/// <remarks>
/// LSP models this position as a union of four shapes — <c>TextDocumentEdit</c>, and the
/// <c>create</c>/<c>rename</c>/<c>delete</c> resource operations — discriminated by the presence of
/// a <c>kind</c> member. A polymorphic converter would need a type discriminator the specification
/// does not provide in the form <c>System.Text.Json</c>'s source generator wants, so the union is
/// flattened into one record whose members are all optional and <c>Kind</c> is read as the
/// discriminator. That is a modelling choice, not a protocol one: every member below is spelled
/// exactly as the specification spells it.
/// </remarks>
internal sealed record DocumentChange
{
    /// <summary><c>create</c>, <c>rename</c> or <c>delete</c>; absent for a <c>TextDocumentEdit</c>.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>The target of a <c>create</c> or <c>delete</c>.</summary>
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    /// <summary>The source of a <c>rename</c>.</summary>
    [JsonPropertyName("oldUri")]
    public string? OldUri { get; init; }

    /// <summary>The destination of a <c>rename</c>.</summary>
    [JsonPropertyName("newUri")]
    public string? NewUri { get; init; }

    /// <summary>The resource operation's options.</summary>
    [JsonPropertyName("options")]
    public ResourceOperationOptions? Options { get; init; }

    /// <summary>The document a <c>TextDocumentEdit</c> addresses.</summary>
    [JsonPropertyName("textDocument")]
    public OptionalVersionedTextDocumentIdentifier? TextDocument { get; init; }

    /// <summary>The edits a <c>TextDocumentEdit</c> carries.</summary>
    [JsonPropertyName("edits")]
    public LspTextEdit[]? Edits { get; init; }
}

/// <summary>The options a create/rename/delete resource operation may carry.</summary>
internal sealed record ResourceOperationOptions
{
    /// <summary>Overwrite an existing destination.</summary>
    [JsonPropertyName("overwrite")]
    public bool? Overwrite { get; init; }

    /// <summary>Do nothing if the destination already exists.</summary>
    [JsonPropertyName("ignoreIfExists")]
    public bool? IgnoreIfExists { get; init; }

    /// <summary>Delete a directory and everything under it.</summary>
    [JsonPropertyName("recursive")]
    public bool? Recursive { get; init; }

    /// <summary>Do nothing if the target does not exist.</summary>
    [JsonPropertyName("ignoreIfNotExists")]
    public bool? IgnoreIfNotExists { get; init; }
}

/// <summary>
/// A resolved workspace edit. Roslyn always answers with <c>documentChanges</c> (C21), so the
/// <c>changes</c> map LSP also allows is deliberately absent — modelling it would invite an applier
/// branch nothing ever reaches.
/// </summary>
internal sealed record WorkspaceEdit
{
    /// <summary>The ordered changes. Create-before-edit ordering is load-bearing (C21).</summary>
    [JsonPropertyName("documentChanges")]
    public DocumentChange[]? DocumentChanges { get; init; }
}

/// <summary>One diagnostic as Roslyn reports it.</summary>
internal sealed record RawDiagnostic
{
    /// <summary>Where it sits.</summary>
    [JsonPropertyName("range")]
    public LspRange Range { get; init; } = new();

    /// <summary>1 = Error, 2 = Warning, 3 = Information, 4 = Hint.</summary>
    [JsonPropertyName("severity")]
    public int? Severity { get; init; }

    /// <summary>
    /// The diagnostic id. LSP allows <c>integer | string</c>, so it is kept as a
    /// <see cref="JsonElement"/> and read through <see cref="CodeText"/>.
    /// </summary>
    [JsonPropertyName("code")]
    public JsonElement? Code { get; init; }

    /// <summary>The documentation link, when the analyzer supplies one.</summary>
    [JsonPropertyName("codeDescription")]
    public CodeDescription? CodeDescription { get; init; }

    /// <summary>The human-readable message.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// LSP tags (1 = Unnecessary, 2 = Deprecated) mixed with VS-private values
    /// 2147483640-2147483645 that must never be forwarded (C16).
    /// </summary>
    [JsonPropertyName("tags")]
    public int[]? Tags { get; init; }

    /// <summary>The reporting source, when Roslyn names one.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>The diagnostic id as text, whichever JSON type it arrived as.</summary>
    [JsonIgnore]
    internal string CodeText => Code switch
    {
        { ValueKind: JsonValueKind.String } code => code.GetString() ?? string.Empty,
        { ValueKind: JsonValueKind.Number } code => code.GetRawText(),
        _ => string.Empty,
    };
}

/// <summary>The documentation link on a diagnostic.</summary>
internal sealed record CodeDescription
{
    /// <summary>The URL.</summary>
    [JsonPropertyName("href")]
    public string? Href { get; init; }
}

/// <summary>A <c>textDocument/diagnostic</c> report.</summary>
internal sealed record DocumentDiagnosticReport
{
    /// <summary><c>full</c> or <c>unchanged</c> (C12).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "full";

    /// <summary>The token to pass back as a <c>previousResultId</c>; absent when a source has nothing to say (C10).</summary>
    [JsonPropertyName("resultId")]
    public string? ResultId { get; init; }

    /// <summary>The diagnostics.</summary>
    [JsonPropertyName("items")]
    public RawDiagnostic[] Items { get; init; } = [];
}

/// <summary>One document's entry in a <c>workspace/diagnostic</c> report.</summary>
internal sealed record WorkspaceDocumentDiagnosticReport
{
    /// <summary>The document's <c>file:</c> URI. A multi-targeted project appears once per TFM (C15).</summary>
    [JsonPropertyName("uri")]
    public string Uri { get; init; } = string.Empty;

    /// <summary><c>full</c> or <c>unchanged</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "full";

    /// <summary>The token to pass back as a <c>previousResultId</c>.</summary>
    [JsonPropertyName("resultId")]
    public string? ResultId { get; init; }

    /// <summary>The diagnostics.</summary>
    [JsonPropertyName("items")]
    public RawDiagnostic[] Items { get; init; } = [];
}

/// <summary>A <c>workspace/diagnostic</c> report.</summary>
internal sealed record WorkspaceDiagnosticReport
{
    /// <summary>One entry per document Roslyn looked at.</summary>
    [JsonPropertyName("items")]
    public WorkspaceDocumentDiagnosticReport[] Items { get; init; } = [];
}

/// <summary>A <c>previousResultId</c> pair for an incremental workspace pull (C12).</summary>
/// <param name="Uri">The document.</param>
/// <param name="Value">The <c>resultId</c> the last full report carried.</param>
internal readonly record struct PreviousResultId(string Uri, string Value);

/// <summary>One code action, with its <c>data</c> kept verbatim (C18).</summary>
internal sealed record RawCodeAction
{
    /// <summary>The action's title, which is also how a fix-all entry is addressed (C20).</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary><c>quickfix</c>, <c>refactor</c>, <c>refactor.extract</c>, ....</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>The diagnostics this action addresses. Frequently empty even for a quick fix (S4).</summary>
    [JsonPropertyName("diagnostics")]
    public RawDiagnostic[]? Diagnostics { get; init; }

    /// <summary>The edit, present only after a resolve.</summary>
    [JsonPropertyName("edit")]
    public WorkspaceEdit? Edit { get; init; }

    /// <summary>
    /// The client-side marker command (<c>roslyn.client.fixAllCodeAction</c>,
    /// <c>roslyn.client.nestedCodeAction</c>). <c>executeCommandProvider.commands</c> is empty, so
    /// these are never executed — they are read (C19).
    /// </summary>
    [JsonPropertyName("command")]
    public CodeActionCommand? Command { get; init; }

    /// <summary>
    /// <c>CodeActionResolveData</c>, kept as a <see cref="JsonElement"/> and round-tripped verbatim:
    /// its members are PascalCase, its contents are Roslyn's business, and re-serialising it would
    /// put this repository in the business of modelling them (C18).
    /// </summary>
    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }
}

/// <summary>A code action's client-side marker command.</summary>
internal sealed record CodeActionCommand
{
    /// <summary>The command's own title, normally equal to the action's.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>The command identifier.</summary>
    [JsonPropertyName("command")]
    public string? Command { get; init; }

    /// <summary>The arguments; argument zero is another <c>CodeActionResolveData</c> (C18).</summary>
    [JsonPropertyName("arguments")]
    public JsonElement[]? Arguments { get; init; }
}

/// <summary>A <c>workspace/symbol</c> hit.</summary>
internal sealed record SymbolInformation
{
    /// <summary>The symbol's simple name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>The LSP <c>SymbolKind</c> number.</summary>
    [JsonPropertyName("kind")]
    public int Kind { get; init; }

    /// <summary>Roslyn's container text, e.g. <c>project Fixture.Core (net10.0, netstandard2.0)</c>.</summary>
    [JsonPropertyName("containerName")]
    public string? ContainerName { get; init; }

    /// <summary>Where the symbol is declared.</summary>
    [JsonPropertyName("location")]
    public LspLocation Location { get; init; } = new();
}

/// <summary>A hierarchical <c>textDocument/documentSymbol</c> node.</summary>
internal sealed record DocumentSymbolNode
{
    /// <summary>The symbol's name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Roslyn's signature-ish detail text.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    /// <summary>The LSP <c>SymbolKind</c> number.</summary>
    [JsonPropertyName("kind")]
    public int Kind { get; init; }

    /// <summary>The whole declaration.</summary>
    [JsonPropertyName("range")]
    public LspRange Range { get; init; } = new();

    /// <summary>The identifier alone.</summary>
    [JsonPropertyName("selectionRange")]
    public LspRange SelectionRange { get; init; } = new();

    /// <summary>Nested symbols.</summary>
    [JsonPropertyName("children")]
    public DocumentSymbolNode[]? Children { get; init; }
}

/// <summary>The formatting options a <c>textDocument/formatting</c> request carries.</summary>
/// <remarks>
/// <b>Use <see cref="Default"/>, never <c>new LspFormattingOptions()</c>.</b> A positional record
/// struct's parameter defaults belong to the primary constructor, and the parameterless one a struct
/// always has zeroes every field instead — so <c>new LspFormattingOptions()</c> sends
/// <c>tabSize: 0</c>, which Roslyn's formatter answers with an assertion failure rather than an
/// error (C59). A live run found that; nothing in the type system does.
/// </remarks>
/// <param name="TabSize">Indent width.</param>
/// <param name="InsertSpaces">Indent with spaces rather than tabs.</param>
internal readonly record struct LspFormattingOptions(int TabSize = 4, bool InsertSpaces = true)
{
    /// <summary>Four spaces, which is what <c>.editorconfig</c> overrides where it has an opinion.</summary>
    internal static LspFormattingOptions Default { get; } = new(TabSize: 4, InsertSpaces: true);
}

/// <summary>What happened to a file, for <c>workspace/didChangeWatchedFiles</c>.</summary>
internal enum FileChangeType
{
    /// <summary>The file appeared.</summary>
    Created = 1,

    /// <summary>The file's contents changed.</summary>
    Changed = 2,

    /// <summary>The file was removed.</summary>
    Deleted = 3,
}

/// <summary>One watched-file change.</summary>
/// <param name="Uri">The file's <c>file:</c> URI.</param>
/// <param name="Type">What happened to it.</param>
internal readonly record struct FileChange(string Uri, FileChangeType Type);

/// <summary>
/// Which documents Roslyn computes diagnostics for.
/// </summary>
/// <remarks>
/// The value goes into <c>csharp|background_analysis.dotnet_compiler_diagnostics_scope</c> (and its
/// analyzer twin). <c>fullSolution</c> is the only setting under which <c>workspace/diagnostic</c>
/// reports closed files at all (C14), and it is also the expensive one — which is why the adapter's
/// standing answer is <c>openFiles</c> (D48) and the MCP half raises it deliberately and per call.
/// </remarks>
internal enum CompilerDiagnosticsScope
{
    /// <summary>Only documents the client has opened. The adapter's default (D48).</summary>
    OpenFiles,

    /// <summary>Every document in the solution. What a solution-wide pull needs (C14).</summary>
    FullSolution,
}

/// <summary>How far a fix-all reaches. The spelling is case-sensitive on the wire (C20).</summary>
internal enum FixAllScope
{
    /// <summary>The file the action was requested in.</summary>
    Document,

    /// <summary>Every file of the owning project.</summary>
    Project,

    /// <summary>Every file of the solution.</summary>
    Solution,
}

/// <summary>Whether the workspace can answer questions yet.</summary>
internal enum WorkspaceLoadStatus
{
    /// <summary>Roslyn is starting or the solution is still loading (C31).</summary>
    Loading,

    /// <summary><c>workspace/projectInitializationComplete</c> has arrived.</summary>
    Ready,

    /// <summary>The backend could not be started or the solution could not be opened.</summary>
    Failed,
}

/// <summary>One project of the loaded workspace.</summary>
/// <param name="Name">The project's name.</param>
/// <param name="Path">Its absolute file path.</param>
/// <param name="TargetFrameworks">Its target frameworks, which is why a document can appear twice in a pull (C15).</param>
internal sealed record WorkspaceProject(string Name, string Path, IReadOnlyList<string> TargetFrameworks);

/// <summary>
/// What <c>getWorkspaceStatus</c> reports and every other tool gates on.
/// </summary>
/// <param name="Status">Loading, ready or failed.</param>
/// <param name="SolutionPath">The solution or project file that was opened, when there is one.</param>
/// <param name="ProjectsLoaded">How many projects have finished loading.</param>
/// <param name="ProjectsTotal">How many there are in total, when that is known.</param>
/// <param name="Projects">The projects, with their target frameworks.</param>
/// <param name="LoadErrors">What went wrong while loading, verbatim.</param>
/// <param name="RoslynVersion">The pinned Roslyn version this session is running.</param>
/// <param name="ProcessId">The process that actually holds the workspace (C45), not the one that was launched.</param>
/// <param name="WorkingSetBytes">That process's working set, because a loaded solution is a quarter of a gigabyte (C38).</param>
/// <param name="Message">Why the workspace is not ready, when it is not.</param>
/// <param name="Engine">
/// How this process got its Roslyn: <c>owned</c> for one it launched itself, <c>attached</c> for one
/// another <c>claude-roslyn-lsp</c> process is hosting (D23). Reporting the distinction is what makes
/// "there is one Roslyn, not two" observable in an answer rather than only in a log.
/// </param>
/// <param name="HostProcessId">
/// The <c>claude-roslyn-lsp</c> process hosting the shared Roslyn, when this one is attached to it.
/// Null for an owned engine, where the answer would be this process.
/// </param>
internal sealed record WorkspaceState(
    WorkspaceLoadStatus Status,
    string? SolutionPath = null,
    int ProjectsLoaded = 0,
    int ProjectsTotal = 0,
    IReadOnlyList<WorkspaceProject>? Projects = null,
    IReadOnlyList<string>? LoadErrors = null,
    string? RoslynVersion = null,
    int? ProcessId = null,
    long? WorkingSetBytes = null,
    string? Message = null,
    string? Engine = null,
    int? HostProcessId = null)
{
    /// <summary>Whether the workspace will answer a question truthfully rather than emptily (C27).</summary>
    internal bool IsReady => Status == WorkspaceLoadStatus.Ready;
}
