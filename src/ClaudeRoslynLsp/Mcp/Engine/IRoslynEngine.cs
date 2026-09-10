using System.Text.Json;

namespace ClaudeRoslynLsp.Mcp.Engine;

/// <summary>
/// Everything the MCP tool layer needs from a running Roslyn, expressed at the LSP level.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an interface at all (D60).</b> The tool layer is where almost all the product's judgement
/// lives — symbol addressing, code-action ids, diagnostic filtering, edit application — and none of
/// that needs a 250 MB child process to be exercised. Behind this seam the tools run against a
/// scripted fake in milliseconds; in front of it, WP5b implements exactly these members on the real
/// launcher and nothing about the tools changes. The seam is drawn at the LSP level rather than at a
/// domain level on purpose: a "find the references of a symbol" method would put the interesting
/// decisions on the wrong side of it, where no test can reach them.
/// </para>
/// <para>
/// <b>Positions are LSP's.</b> Everything here is zero-based and counted in UTF-16 code units,
/// because that is what crosses the wire (D14 pins <c>positionEncodings: ["utf-16"]</c>). The 1-based
/// line and column numbers a model reads exist only in the tool layer (D63).
/// </para>
/// <para>
/// <b>Failure is an exception.</b> An implementation throws when Roslyn answers with an error or
/// cannot be reached; <c>ToolErrors</c> turns that into an <see cref="ModelContextProtocol.McpException"/>
/// with text a model can act on. The one non-exceptional failure is "not ready", which
/// <see cref="EnsureReadyAsync"/> reports in its result rather than by throwing, because a loading
/// workspace is a normal state of a session that has just started (C31).
/// </para>
/// </remarks>
internal interface IRoslynEngine
{
    /// <summary>
    /// Waits, up to <paramref name="timeout"/>, for the workspace to finish loading, and reports
    /// where it got to.
    /// </summary>
    /// <remarks>
    /// Every tool calls this first. Roslyn answers requests issued before
    /// <c>workspace/projectInitializationComplete</c> with an <em>empty successful result</em> rather
    /// than an error (C27), so a tool that skipped the gate would confidently report "no references"
    /// for the first several seconds of every session. The timeout is
    /// <c>CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS</c>, and reaching it returns a
    /// <see cref="WorkspaceLoadStatus.Loading"/> state rather than throwing — a tool then answers
    /// with a status object, which is a better thing for a model to receive than a call that hangs
    /// for two minutes.
    /// </remarks>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    Task<WorkspaceState> EnsureReadyAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary><c>workspace/symbol</c>: the name-addressed entry point every tool resolves through.</summary>
    /// <param name="query">The symbol name to search for.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    Task<IReadOnlyList<SymbolInformation>> WorkspaceSymbolAsync(string query, CancellationToken cancellationToken);

    /// <summary><c>textDocument/references</c>.</summary>
    /// <param name="uri">The document the symbol is addressed in.</param>
    /// <param name="position">Where in it.</param>
    /// <param name="includeDeclaration">Whether the declaration itself is a result.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    Task<IReadOnlyList<LspLocation>> ReferencesAsync(
        string uri,
        LspPosition position,
        bool includeDeclaration,
        CancellationToken cancellationToken);

    /// <summary>
    /// <c>textDocument/prepareRename</c>: the range Roslyn is willing to rename, or
    /// <see langword="null"/> when the position is not a renamable symbol (C23).
    /// </summary>
    /// <param name="uri">The document.</param>
    /// <param name="position">Where in it.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    Task<LspRange?> PrepareRenameAsync(string uri, LspPosition position, CancellationToken cancellationToken);

    /// <summary><c>textDocument/rename</c>. Always <c>documentChanges</c>, one entry per file (C23).</summary>
    /// <param name="uri">The document.</param>
    /// <param name="position">Where in it.</param>
    /// <param name="newName">The new name.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    Task<WorkspaceEdit?> RenameAsync(
        string uri,
        LspPosition position,
        string newName,
        CancellationToken cancellationToken);

    /// <summary>
    /// <c>textDocument/codeAction</c>: the unresolved list, with each action's <c>data</c> kept
    /// verbatim (C18).
    /// </summary>
    /// <param name="uri">The document.</param>
    /// <param name="range">The selection the actions are offered for.</param>
    /// <param name="diagnostics">The context diagnostics, when the caller has them; Roslyn offers fixes without them too.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    Task<IReadOnlyList<RawCodeAction>> CodeActionsAsync(
        string uri,
        LspRange range,
        IReadOnlyList<RawDiagnostic>? diagnostics,
        CancellationToken cancellationToken);

    /// <summary>
    /// <c>codeAction/resolve</c>: the same action with its <c>edit</c> filled in. A <c>Fix All:</c>
    /// entry resolves to no edit at all (C20) — that is what
    /// <see cref="ResolveFixAllAsync"/> is for.
    /// </summary>
    /// <param name="action">The action as it came off the list.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    Task<RawCodeAction> ResolveCodeActionAsync(RawCodeAction action, CancellationToken cancellationToken);

    /// <summary>
    /// <c>codeAction/resolveFixAll</c>, Roslyn's custom method (C39).
    /// </summary>
    /// <remarks>
    /// <paramref name="scope"/> is mandatory and case-sensitive: omitting it fails with an
    /// <c>InvalidCastException</c> and a lowercase spelling with "Sequence contains no elements"
    /// (C20, S4b). The plain action's <c>data</c> is accepted, so a caller does not have to have
    /// found the <c>Fix All:</c> entry to use this.
    /// </remarks>
    /// <param name="title">The action title, as the list reported it.</param>
    /// <param name="data">The action's <c>data</c>, round-tripped verbatim.</param>
    /// <param name="scope">How far the fix reaches.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    Task<WorkspaceEdit?> ResolveFixAllAsync(
        string title,
        JsonElement data,
        FixAllScope scope,
        CancellationToken cancellationToken);

    /// <summary><c>textDocument/formatting</c>.</summary>
    /// <param name="uri">The document.</param>
    /// <param name="options">Indent width and tabs-versus-spaces.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    Task<IReadOnlyList<LspTextEdit>> FormattingAsync(
        string uri,
        LspFormattingOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// <c>textDocument/diagnostic</c> with <b>no</b> <c>identifier</c>, which is the union of every
    /// source (C9) and the only shape worth asking for on demand.
    /// </summary>
    /// <remarks>
    /// A file that is not open always answers with zero items regardless of scope (C13), so the tool
    /// layer opens it first — see <see cref="OpenDocumentAsync"/>.
    /// </remarks>
    /// <param name="uri">The document.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    Task<DocumentDiagnosticReport> DocumentDiagnosticAsync(string uri, CancellationToken cancellationToken);

    /// <summary>
    /// <c>workspace/diagnostic</c>: the only way to reach files nobody opened (C13).
    /// </summary>
    /// <remarks>
    /// It reports closed files only under a <c>fullSolution</c> compiler scope (C14), so a caller
    /// raises the scope with <see cref="SetCompilerDiagnosticsScopeAsync"/> first. It also skips open
    /// documents, includes <c>obj/**/*.cs</c> and <c>.csproj</c> entries, and lists a multi-targeted
    /// project once per TFM (C15) — all of which the tool layer filters.
    /// </remarks>
    /// <param name="previousResultIds">Per-document result ids from a previous pull, for an incremental answer (C12).</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    Task<WorkspaceDiagnosticReport> WorkspaceDiagnosticAsync(
        IReadOnlyList<PreviousResultId> previousResultIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Changes the compiler and analyzer diagnostic scopes Roslyn runs with.
    /// </summary>
    /// <remarks>
    /// Implemented as a <c>workspace/didChangeConfiguration</c> notification followed by answering
    /// the <c>workspace/configuration</c> pull Roslyn makes in response — Roslyn never reads a
    /// setting the client pushes, it only ever asks (C46, D48). Raising the scope to
    /// <see cref="CompilerDiagnosticsScope.FullSolution"/> is what makes a solution-wide pull report
    /// anything at all (C14); it is expensive, which is why it is a call rather than a default.
    /// </remarks>
    /// <param name="scope">The scope to run with.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    Task SetCompilerDiagnosticsScopeAsync(CompilerDiagnosticsScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Sets <c>csharp|formatting.dotnet_organize_imports_on_format</c>, which is what makes
    /// <c>textDocument/formatting</c> sort and prune using directives.
    /// </summary>
    /// <param name="enabled">Whether formatting also organises imports.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    Task SetOrganizeImportsOnFormatAsync(bool enabled, CancellationToken cancellationToken);

    /// <summary>
    /// <c>textDocument/didOpen</c> with the text as it is on disk.
    /// </summary>
    /// <remarks>
    /// C13: a file that is not open answers every diagnostic pull with zero items, whatever the
    /// scope. So a file-scoped pull has to open the document first — with the bytes from disk, never
    /// with a guess, because Roslyn then answers about the text it was given.
    /// </remarks>
    /// <param name="uri">The document.</param>
    /// <param name="text">Its text, exactly as it is on disk.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    Task OpenDocumentAsync(string uri, string text, CancellationToken cancellationToken);

    /// <summary><c>textDocument/didClose</c>.</summary>
    /// <param name="uri">The document.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    Task CloseDocumentAsync(string uri, CancellationToken cancellationToken);

    /// <summary>
    /// Whether a document is already open — which decides whether a file-scoped pull has to open and
    /// close it, and whether closing it afterwards would take a document away from the LSP client
    /// that opened it.
    /// </summary>
    /// <param name="uri">The document.</param>
    bool IsDocumentOpen(string uri);

    /// <summary>
    /// <c>textDocument/documentSymbol</c>, hierarchical: how <c>getTypeMembers</c> lists a type's
    /// members without reading the file, and how a <c>path:line:col</c> address is turned back into
    /// a name.
    /// </summary>
    /// <param name="uri">The document.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    Task<IReadOnlyList<DocumentSymbolNode>> DocumentSymbolAsync(string uri, CancellationToken cancellationToken);

    /// <summary>
    /// <c>workspace/didChangeWatchedFiles</c>: what the MCP half owes Roslyn after writing files.
    /// </summary>
    /// <remarks>
    /// C33 is the rule that makes this non-obvious: a <c>Created</c> event for a new <c>.cs</c> file
    /// does nothing at all, and only a <c>Changed</c> event for the owning <c>.csproj</c> makes the
    /// new file join its project. An implementation therefore sends the synthetic project change
    /// alongside a creation or a deletion; the tool layer just reports what it wrote.
    /// </remarks>
    /// <param name="changes">What changed on disk.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    Task NotifyFilesChangedAsync(IReadOnlyList<FileChange> changes, CancellationToken cancellationToken);

    /// <summary>
    /// <c>textDocument/hover</c>, reduced to the signature line — the <c>```csharp</c> fence Roslyn
    /// opens its markdown with (S5) — so <c>resolveSymbol</c> can report what a match actually is.
    /// </summary>
    /// <param name="uri">The document.</param>
    /// <param name="position">Where in it.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    Task<string?> HoverAsync(string uri, LspPosition position, CancellationToken cancellationToken);
}
