using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeRoslynLsp.Protocol;

/// <summary>The JSON-RPC constants the LSP base protocol fixes, in one place.</summary>
/// <remarks>
/// The error <em>codes</em> live in <see cref="JsonRpcErrors"/> alongside the builders that use
/// them; what stays here is the protocol version and the one shared JSON null.
/// </remarks>
internal static class JsonRpc
{
    /// <summary>The only protocol version JSON-RPC 2.0 messages carry.</summary>
    internal const string Version = "2.0";

    /// <summary>
    /// A JSON <c>null</c>, for the places the protocol requires a member to be present and empty.
    /// </summary>
    /// <remarks>
    /// Cloned out of its document deliberately — a <see cref="JsonElement"/> that still points into a
    /// live <see cref="JsonDocument"/> would be invalid the moment that document was disposed, and
    /// this one outlives the process.
    /// </remarks>
    internal static JsonElement Null { get; } = JsonDocument.Parse("null").RootElement.Clone();
}

// ---------------------------------------------------------------------------------------------
// What the adapter answers Claude with
// ---------------------------------------------------------------------------------------------

/// <summary>The answer to <c>initialize</c>.</summary>
internal sealed record InitializeResponse
{
    /// <summary>The JSON-RPC version.</summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpcVersion { get; init; } = JsonRpc.Version;

    /// <summary>The id of the request being answered, echoed exactly.</summary>
    [JsonPropertyName("id")]
    public required JsonElement Id { get; init; }

    /// <summary>The capabilities and identity this server advertises.</summary>
    [JsonPropertyName("result")]
    public required InitializeResult Result { get; init; }
}

/// <summary>What the client learns about this server from the handshake.</summary>
internal sealed record InitializeResult
{
    /// <summary>The capabilities the server advertises.</summary>
    [JsonPropertyName("capabilities")]
    public required ServerCapabilities Capabilities { get; init; }

    /// <summary>The server's name and version.</summary>
    [JsonPropertyName("serverInfo")]
    public required ServerInfo ServerInfo { get; init; }
}

/// <summary>
/// The capability document the adapter advertises to its client — authored here, never derived from
/// what Roslyn happens to answer with (D45).
/// </summary>
/// <remarks>
/// <para>
/// Roslyn advertises <c>semanticTokensProvider</c>, <c>codeLensProvider</c>,
/// <c>inlayHintProvider</c> and a <c>_vs_onAutoInsertProvider</c> statically, whether or not the
/// client asked for any of them (C26). Forwarding its document would therefore promise Claude Code
/// four features the adapter has no intention of bridging, and every promise here is a request the
/// client is entitled to send and to wait for an answer to. So the list below is exactly what the
/// mediation actually carries, and nothing that merely exists upstream.
/// </para>
/// <para>
/// Answered <b>immediately</b>, before the backend has been launched, let alone loaded a solution.
/// Claude Code holds <c>initialize</c> open forever if it is not answered, and the whole point of
/// the readiness gate is that the handshake and the workspace load are separate clocks.
/// </para>
/// </remarks>
internal sealed record ServerCapabilities
{
    /// <summary>How the client should send document contents.</summary>
    [JsonPropertyName("textDocumentSync")]
    public required TextDocumentSyncOptions TextDocumentSync { get; init; }

    /// <summary>Hover, forwarded to Roslyn as markdown.</summary>
    [JsonPropertyName("hoverProvider")]
    public bool HoverProvider { get; init; } = true;

    /// <summary>Go to definition.</summary>
    [JsonPropertyName("definitionProvider")]
    public bool DefinitionProvider { get; init; } = true;

    /// <summary>Go to the type of the expression under the cursor.</summary>
    [JsonPropertyName("typeDefinitionProvider")]
    public bool TypeDefinitionProvider { get; init; } = true;

    /// <summary>Go to implementations of an interface or virtual member.</summary>
    [JsonPropertyName("implementationProvider")]
    public bool ImplementationProvider { get; init; } = true;

    /// <summary>Find references.</summary>
    [JsonPropertyName("referencesProvider")]
    public bool ReferencesProvider { get; init; } = true;

    /// <summary>The symbol outline of one document.</summary>
    [JsonPropertyName("documentSymbolProvider")]
    public bool DocumentSymbolProvider { get; init; } = true;

    /// <summary>Solution-wide symbol search.</summary>
    [JsonPropertyName("workspaceSymbolProvider")]
    public bool WorkspaceSymbolProvider { get; init; } = true;

    /// <summary>Incoming and outgoing calls. Roslyn implements this; nothing is synthesised.</summary>
    [JsonPropertyName("callHierarchyProvider")]
    public bool CallHierarchyProvider { get; init; } = true;

    /// <summary>Semantic rename, with the prepare step.</summary>
    [JsonPropertyName("renameProvider")]
    public RenameOptions RenameProvider { get; init; } = new();

    /// <summary>Quick fixes and refactorings, resolved lazily.</summary>
    [JsonPropertyName("codeActionProvider")]
    public CodeActionOptions CodeActionProvider { get; init; } = new();

    /// <summary>Whole-document formatting.</summary>
    [JsonPropertyName("documentFormattingProvider")]
    public bool DocumentFormattingProvider { get; init; } = true;

    /// <summary>Range formatting.</summary>
    [JsonPropertyName("documentRangeFormattingProvider")]
    public bool DocumentRangeFormattingProvider { get; init; } = true;

    /// <summary>Signature help.</summary>
    [JsonPropertyName("signatureHelpProvider")]
    public SignatureHelpOptions SignatureHelpProvider { get; init; } = new();

    /// <summary>Completion, resolved lazily.</summary>
    [JsonPropertyName("completionProvider")]
    public CompletionOptions CompletionProvider { get; init; } = new();
}

/// <summary>Document synchronisation options.</summary>
internal sealed record TextDocumentSyncOptions
{
    /// <summary>Whether the client should send <c>didOpen</c> and <c>didClose</c>.</summary>
    [JsonPropertyName("openClose")]
    public required bool OpenClose { get; init; }

    /// <summary>
    /// The <c>TextDocumentSyncKind</c>: 0 none, 1 full, 2 incremental.
    /// </summary>
    /// <remarks>
    /// Full (1), not incremental (2) — D13. Roslyn accepts full-text <c>didChange</c> even though it
    /// advertises incremental (C17), and a mirror the adapter can replay verbatim after a Roslyn
    /// crash is worth far more than the bytes incremental sync would save: a document rebuilt from a
    /// range-edit history that was interrupted halfway is wrong in a way that shows up as wrong
    /// answers rather than as an error.
    /// </remarks>
    [JsonPropertyName("change")]
    public required int Change { get; init; }

    /// <summary>Whether and how the client should send <c>didSave</c>.</summary>
    [JsonPropertyName("save")]
    public required SaveOptions Save { get; init; }
}

/// <summary>Save-notification options.</summary>
internal sealed record SaveOptions
{
    /// <summary>
    /// Whether <c>didSave</c> carries the document text. False: the adapter's mirror already holds
    /// it, so including it would double the bytes on every save for no new information.
    /// </summary>
    [JsonPropertyName("includeText")]
    public required bool IncludeText { get; init; }
}

/// <summary>Rename options.</summary>
internal sealed record RenameOptions
{
    /// <summary>
    /// Whether <c>textDocument/prepareRename</c> is available. True — Roslyn implements it and
    /// returns a bare range (C23), which is what stops a client renaming something unrenameable.
    /// </summary>
    [JsonPropertyName("prepareProvider")]
    public bool PrepareProvider { get; init; } = true;
}

/// <summary>Code-action options.</summary>
internal sealed record CodeActionOptions
{
    /// <summary>
    /// The kinds offered. Exactly what Roslyn advertises, because the adapter forwards the list it
    /// gets and promising a kind Roslyn never produces would be an empty menu entry.
    /// </summary>
    [JsonPropertyName("codeActionKinds")]
    public IReadOnlyList<string> CodeActionKinds { get; init; } = ["quickfix", "refactor"];

    /// <summary>
    /// Whether edits are computed on resolve. True: Roslyn's own actions carry an opaque
    /// <c>data</c> payload (C18) and are only resolved on demand, so computing every edit up front
    /// would make every code-action request pay for the whole menu.
    /// </summary>
    [JsonPropertyName("resolveProvider")]
    public bool ResolveProvider { get; init; } = true;
}

/// <summary>Signature-help options.</summary>
internal sealed record SignatureHelpOptions
{
    /// <summary>The characters that open a signature. Roslyn's own set.</summary>
    [JsonPropertyName("triggerCharacters")]
    public IReadOnlyList<string> TriggerCharacters { get; init; } = ["(", ",", "[", "<", "{"];

    /// <summary>The characters that re-trigger it.</summary>
    [JsonPropertyName("retriggerCharacters")]
    public IReadOnlyList<string> RetriggerCharacters { get; init; } = [")", "]", ">", "}"];
}

/// <summary>Completion options.</summary>
internal sealed record CompletionOptions
{
    /// <summary>The characters that open a completion list. Roslyn's own set.</summary>
    [JsonPropertyName("triggerCharacters")]
    public IReadOnlyList<string> TriggerCharacters { get; init; } = [".", " ", ":", "(", "[", "<", "#", "\"", "\\", "{", "~", ">"];

    /// <summary>Whether items are resolved lazily. Roslyn resolves documentation on demand.</summary>
    [JsonPropertyName("resolveProvider")]
    public bool ResolveProvider { get; init; } = true;
}

/// <summary>The server's identity, as reported in the handshake.</summary>
internal sealed record ServerInfo
{
    /// <summary>The server name. Asserted by <c>SmokeTest</c>'s LSP leg on every release RID.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>The server version, which the build stamps from <c>CHANGELOG.md</c>.</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

// ---------------------------------------------------------------------------------------------
// What the client sends the adapter
// ---------------------------------------------------------------------------------------------

/// <summary>
/// The parts of the client's <c>initialize</c> the adapter has to understand.
/// </summary>
/// <remarks>
/// <c>capabilities</c> and <c>initializationOptions</c> stay <see cref="JsonElement"/>s. The adapter
/// asks two questions of the first (does this client apply workspace edits, and does it want
/// diagnostics pushed) and one of the second (did it carry <c>settings.roslyn</c>), and modelling
/// the rest would be tracking somebody else's specification for the sake of members nothing reads.
/// </remarks>
internal sealed record ClientInitializeParams
{
    /// <summary>The client's process id, or null.</summary>
    [JsonPropertyName("processId")]
    public int? ProcessId { get; init; }

    /// <summary>The workspace root as a URI. Claude Code sends this.</summary>
    [JsonPropertyName("rootUri")]
    public string? RootUri { get; init; }

    /// <summary>The workspace root as a path, deprecated but still sent by some clients.</summary>
    [JsonPropertyName("rootPath")]
    public string? RootPath { get; init; }

    /// <summary>The multi-root folder list, when the client supports one.</summary>
    [JsonPropertyName("workspaceFolders")]
    public IReadOnlyList<WorkspaceFolder>? WorkspaceFolders { get; init; }

    /// <summary>The client's capability document, kept raw.</summary>
    [JsonPropertyName("capabilities")]
    public JsonElement Capabilities { get; init; }

    /// <summary>Client-supplied options, kept raw. Where a <c>.lsp.json</c> settings block arrives.</summary>
    [JsonPropertyName("initializationOptions")]
    public JsonElement InitializationOptions { get; init; }
}

/// <summary>One entry of a client's workspace-folder list.</summary>
internal sealed record WorkspaceFolder
{
    /// <summary>The folder's URI.</summary>
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    /// <summary>A display name for the folder.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

/// <summary>The envelope of a notification whose parameters are one raw object.</summary>
internal sealed record RawParamsNotification
{
    /// <summary>The method name.</summary>
    [JsonPropertyName("method")]
    public string? Method { get; init; }

    /// <summary>The parameters, unparsed.</summary>
    [JsonPropertyName("params")]
    public JsonElement Params { get; init; }
}

/// <summary>The envelope of a request whose parameters are one raw object.</summary>
internal sealed record RawParamsRequest
{
    /// <summary>The request id.</summary>
    [JsonPropertyName("id")]
    public JsonElement Id { get; init; }

    /// <summary>The method name.</summary>
    [JsonPropertyName("method")]
    public string? Method { get; init; }

    /// <summary>The parameters, unparsed.</summary>
    [JsonPropertyName("params")]
    public JsonElement Params { get; init; }
}

/// <summary>A message whose <c>params</c> is a <c>textDocument/didOpen</c> payload.</summary>
internal sealed record DidOpenNotification
{
    /// <summary>The JSON-RPC version.</summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpcVersion { get; init; } = JsonRpc.Version;

    /// <summary>Always <c>textDocument/didOpen</c>.</summary>
    [JsonPropertyName("method")]
    public string Method { get; init; } = "textDocument/didOpen";

    /// <summary>The document being opened.</summary>
    [JsonPropertyName("params")]
    public required DidOpenParams Params { get; init; }
}

/// <summary>The parameters of <c>textDocument/didOpen</c>.</summary>
internal sealed record DidOpenParams
{
    /// <summary>The document, with its full text.</summary>
    [JsonPropertyName("textDocument")]
    public required TextDocumentItem TextDocument { get; init; }
}

/// <summary>A document as it arrives on <c>didOpen</c>, or as the mirror replays it.</summary>
internal sealed record TextDocumentItem
{
    /// <summary>The document URI.</summary>
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    /// <summary>The language id — <c>csharp</c> for the files this adapter is configured for.</summary>
    [JsonPropertyName("languageId")]
    public required string LanguageId { get; init; }

    /// <summary>The document version this text belongs to.</summary>
    [JsonPropertyName("version")]
    public required int Version { get; init; }

    /// <summary>The whole document.</summary>
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

/// <summary>The parameters of <c>textDocument/didChange</c>.</summary>
internal sealed record DidChangeParams
{
    /// <summary>Which document, and at which version.</summary>
    [JsonPropertyName("textDocument")]
    public VersionedTextDocumentIdentifier? TextDocument { get; init; }

    /// <summary>
    /// The changes. Under full synchronisation (D13) the last entry without a <c>range</c> is the
    /// whole document.
    /// </summary>
    [JsonPropertyName("contentChanges")]
    public IReadOnlyList<TextDocumentContentChange>? ContentChanges { get; init; }
}

/// <summary>A document identifier carrying its version.</summary>
internal sealed record VersionedTextDocumentIdentifier
{
    /// <summary>The document URI.</summary>
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    /// <summary>The version this notification advances the document to.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; }
}

/// <summary>One content change.</summary>
internal sealed record TextDocumentContentChange
{
    /// <summary>
    /// The range replaced, or absent for a full-document change. Present only if a client ignored
    /// the <c>change: 1</c> this server advertises.
    /// </summary>
    [JsonPropertyName("range")]
    public JsonElement Range { get; init; }

    /// <summary>The replacement text.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

/// <summary>The parameters of <c>didClose</c> and <c>didSave</c>.</summary>
internal sealed record TextDocumentParams
{
    /// <summary>Which document.</summary>
    [JsonPropertyName("textDocument")]
    public TextDocumentIdentifier? TextDocument { get; init; }
}

/// <summary>A bare document identifier.</summary>
internal sealed record TextDocumentIdentifier
{
    /// <summary>The document URI.</summary>
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

// ---------------------------------------------------------------------------------------------
// What Roslyn sends the adapter
// ---------------------------------------------------------------------------------------------

/// <summary>The parameters of <c>client/registerCapability</c>.</summary>
internal sealed record RegistrationParams
{
    /// <summary>The registrations being added.</summary>
    [JsonPropertyName("registrations")]
    public IReadOnlyList<Registration>? Registrations { get; init; }
}

/// <summary>One dynamic registration.</summary>
internal sealed record Registration
{
    /// <summary>The registration's id, used to unregister it again.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>The method being registered for.</summary>
    [JsonPropertyName("method")]
    public string? Method { get; init; }

    /// <summary>
    /// The method-specific options, kept raw. Diagnostic registrations carry an
    /// <c>identifier</c> (C11) and watched-file registrations carry a <c>watchers</c> array (C32);
    /// modelling the union of every method's options would be modelling the whole protocol.
    /// </summary>
    [JsonPropertyName("registerOptions")]
    public JsonElement RegisterOptions { get; init; }
}

/// <summary>
/// The parameters of <c>client/unregisterCapability</c>.
/// </summary>
/// <remarks>
/// The member is spelled <c>unregisterations</c>, not <c>unregistrations</c> — a typo in the LSP
/// specification itself that every implementation now has to reproduce, Roslyn included (C32).
/// </remarks>
internal sealed record UnregistrationParams
{
    /// <summary>The registrations being removed.</summary>
    [JsonPropertyName("unregisterations")]
    public IReadOnlyList<Unregistration>? Unregisterations { get; init; }
}

/// <summary>One registration being withdrawn.</summary>
internal sealed record Unregistration
{
    /// <summary>The id given when it was registered.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>The method it was registered for.</summary>
    [JsonPropertyName("method")]
    public string? Method { get; init; }
}

/// <summary>The parameters of <c>workspace/configuration</c>.</summary>
internal sealed record ConfigurationParams
{
    /// <summary>The sections being asked about, in the order the answer array must follow.</summary>
    [JsonPropertyName("items")]
    public IReadOnlyList<ConfigurationItem>? Items { get; init; }
}

/// <summary>One requested configuration section.</summary>
internal sealed record ConfigurationItem
{
    /// <summary>
    /// The section name — <c>csharp|background_analysis.dotnet_compiler_diagnostics_scope</c> and
    /// the like. The pipe is what makes Claude Code's own dotted settings lookup unable to answer
    /// these, which is why the adapter answers them itself.
    /// </summary>
    [JsonPropertyName("section")]
    public string? Section { get; init; }

    /// <summary>The scope the section is asked for, when the server scopes it.</summary>
    [JsonPropertyName("scopeUri")]
    public string? ScopeUri { get; init; }
}

/// <summary>The parameters of <c>window/workDoneProgress/create</c>.</summary>
internal sealed record WorkDoneProgressCreateParams
{
    /// <summary>The token the matching <c>$/progress</c> stream will carry. A bare GUID (C31).</summary>
    [JsonPropertyName("token")]
    public JsonElement Token { get; init; }
}

/// <summary>The parameters of a <c>$/progress</c> notification.</summary>
internal sealed record ProgressParams
{
    /// <summary>The token this report belongs to.</summary>
    [JsonPropertyName("token")]
    public JsonElement Token { get; init; }

    /// <summary>The report itself.</summary>
    [JsonPropertyName("value")]
    public ProgressValue? Value { get; init; }
}

/// <summary>One work-done progress report.</summary>
internal sealed record ProgressValue
{
    /// <summary><c>begin</c>, <c>report</c> or <c>end</c>.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>The operation title, on <c>begin</c>.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>A human-readable status line.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>Completion, 0-100.</summary>
    [JsonPropertyName("percentage")]
    public int? Percentage { get; init; }
}

/// <summary>The parameters of <c>window/logMessage</c> and <c>window/showMessage</c>.</summary>
internal sealed record LogMessageParams
{
    /// <summary>The severity: 1 error, 2 warning, 3 info, 4 log, 5 debug.</summary>
    [JsonPropertyName("type")]
    public int Type { get; init; }

    /// <summary>The text.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>A complete <c>window/logMessage</c> notification.</summary>
internal sealed record LogMessageNotification
{
    /// <summary>The JSON-RPC version.</summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpcVersion { get; init; } = JsonRpc.Version;

    /// <summary>Always <c>window/logMessage</c>.</summary>
    [JsonPropertyName("method")]
    public string Method { get; init; } = "window/logMessage";

    /// <summary>The severity and text.</summary>
    [JsonPropertyName("params")]
    public required LogMessageParams Params { get; init; }
}

/// <summary>The answer to <c>workspace/applyEdit</c> when the adapter has to refuse it itself.</summary>
internal sealed record ApplyWorkspaceEditResult
{
    /// <summary>Whether the edit was applied.</summary>
    [JsonPropertyName("applied")]
    public required bool Applied { get; init; }

    /// <summary>Why not, when it was not.</summary>
    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; init; }
}

// ---------------------------------------------------------------------------------------------
// What the adapter sends Roslyn
// ---------------------------------------------------------------------------------------------

/// <summary>The parameters of Roslyn's custom <c>solution/open</c> (C39).</summary>
internal sealed record SolutionOpenParams
{
    /// <summary>The <c>.sln</c> or <c>.slnx</c> to load, as a file URI.</summary>
    [JsonPropertyName("solution")]
    public required string Solution { get; init; }
}

/// <summary>The complete <c>solution/open</c> notification.</summary>
internal sealed record SolutionOpenNotification
{
    /// <summary>The JSON-RPC version.</summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpcVersion { get; init; } = JsonRpc.Version;

    /// <summary>Always <c>solution/open</c>.</summary>
    [JsonPropertyName("method")]
    public string Method { get; init; } = "solution/open";

    /// <summary>Which solution.</summary>
    [JsonPropertyName("params")]
    public required SolutionOpenParams Params { get; init; }
}

/// <summary>The parameters of Roslyn's custom <c>project/open</c> (C39).</summary>
internal sealed record ProjectOpenParams
{
    /// <summary>The project files to load, as file URIs.</summary>
    [JsonPropertyName("projects")]
    public required IReadOnlyList<string> Projects { get; init; }
}

/// <summary>
/// The <c>initialize</c> the adapter sends Roslyn — authored, not derived from the client's (D14).
/// </summary>
/// <remarks>
/// <para>
/// Claude Code's own capability document would be the wrong thing to forward in both directions. It
/// declares things this adapter has to do itself (it refuses <c>client/registerCapability</c> with
/// <c>-32601</c>, and answers <c>workspace/configuration</c> only when a <c>settings</c> block
/// happens to be in the plugin config), and omits things the adapter needs Roslyn to do (dynamic
/// diagnostics, watched files, progress). Authoring it means the mediation's contract with Roslyn is
/// stated in one file rather than being whatever the client of the day happened to ask for.
/// </para>
/// <para>
/// What is deliberately absent matters as much as what is present: no semantic tokens, no inlay
/// hints, no code lens, so Roslyn never registers or refreshes them (C26). <c>linkSupport</c> is
/// false everywhere, so navigation comes back as plain <c>Location</c>s that pass straight through
/// to a client that does not understand <c>LocationLink</c>.
/// </para>
/// </remarks>
internal sealed record RoslynInitializeParams
{
    /// <summary>
    /// This adapter's process id, so Roslyn exits when the adapter does rather than being orphaned.
    /// </summary>
    [JsonPropertyName("processId")]
    public required int ProcessId { get; init; }

    /// <summary>The adapter's identity, which shows up in Roslyn's own logs.</summary>
    [JsonPropertyName("clientInfo")]
    public required ServerInfo ClientInfo { get; init; }

    /// <summary>The workspace root, taken from the client's.</summary>
    [JsonPropertyName("rootUri")]
    public string? RootUri { get; init; }

    /// <summary>The workspace folders, taken from the client's.</summary>
    [JsonPropertyName("workspaceFolders")]
    public IReadOnlyList<WorkspaceFolder>? WorkspaceFolders { get; init; }

    /// <summary>The authored capability document.</summary>
    [JsonPropertyName("capabilities")]
    public RoslynClientCapabilities Capabilities { get; init; } = new();
}

/// <summary>The capability document the adapter declares to Roslyn.</summary>
internal sealed record RoslynClientCapabilities
{
    /// <summary>Workspace-level capabilities.</summary>
    [JsonPropertyName("workspace")]
    public RoslynWorkspaceCapabilities Workspace { get; init; } = new();

    /// <summary>Document-level capabilities.</summary>
    [JsonPropertyName("textDocument")]
    public RoslynTextDocumentCapabilities TextDocument { get; init; } = new();

    /// <summary>Window-level capabilities.</summary>
    [JsonPropertyName("window")]
    public RoslynWindowCapabilities Window { get; init; } = new();

    /// <summary>General capabilities.</summary>
    [JsonPropertyName("general")]
    public RoslynGeneralCapabilities General { get; init; } = new();
}

/// <summary>Workspace capabilities the adapter declares.</summary>
internal sealed record RoslynWorkspaceCapabilities
{
    /// <summary>
    /// The adapter answers <c>workspace/configuration</c> itself. Without this Roslyn silently keeps
    /// its own defaults, which means full-solution background analysis on a machine that is also
    /// running an agent.
    /// </summary>
    [JsonPropertyName("configuration")]
    public bool Configuration { get; init; } = true;

    /// <summary>The adapter answers <c>workspace/workspaceFolders</c> with the client's root.</summary>
    [JsonPropertyName("workspaceFolders")]
    public bool WorkspaceFolders { get; init; } = true;

    /// <summary>Dynamic registration for configuration changes.</summary>
    [JsonPropertyName("didChangeConfiguration")]
    public DynamicRegistration DidChangeConfiguration { get; init; } = new();

    /// <summary>
    /// Dynamic registration for watched files, with relative patterns.
    /// </summary>
    /// <remarks>
    /// Without this capability Roslyn registers no watchers at all and has no in-process fallback
    /// (C34) — a file created by Bash or by git would then never join its project, and every later
    /// answer would be silently stale.
    /// </remarks>
    [JsonPropertyName("didChangeWatchedFiles")]
    public WatchedFilesCapability DidChangeWatchedFiles { get; init; } = new();

    /// <summary>Workspace symbol search.</summary>
    [JsonPropertyName("symbol")]
    public SymbolCapability Symbol { get; init; } = new();

    /// <summary>The adapter accepts <c>workspace/diagnostic/refresh</c>.</summary>
    [JsonPropertyName("diagnostics")]
    public RefreshCapability Diagnostics { get; init; } = new();

    /// <summary>What shape of workspace edit the adapter can carry back to a client.</summary>
    [JsonPropertyName("workspaceEdit")]
    public WorkspaceEditCapability WorkspaceEdit { get; init; } = new();
}

/// <summary>A capability whose only property is dynamic registration.</summary>
internal sealed record DynamicRegistration
{
    /// <summary>Whether the server may register for this dynamically.</summary>
    [JsonPropertyName("dynamicRegistration")]
    public bool Dynamic { get; init; } = true;
}

/// <summary>The watched-files capability.</summary>
internal sealed record WatchedFilesCapability
{
    /// <summary>Whether watchers may be registered dynamically. They are, ~140 of them (C32).</summary>
    [JsonPropertyName("dynamicRegistration")]
    public bool DynamicRegistration { get; init; } = true;

    /// <summary>Whether a watcher may be expressed as a base URI plus a pattern.</summary>
    [JsonPropertyName("relativePatternSupport")]
    public bool RelativePatternSupport { get; init; } = true;
}

/// <summary>The workspace-symbol capability.</summary>
internal sealed record SymbolCapability
{
    /// <summary>Static registration only.</summary>
    [JsonPropertyName("dynamicRegistration")]
    public bool DynamicRegistration { get; init; }

    /// <summary>Which symbol kinds the adapter passes through: all of the 3.17 set.</summary>
    [JsonPropertyName("symbolKind")]
    public SymbolKindCapability SymbolKind { get; init; } = new();
}

/// <summary>The set of symbol kinds understood.</summary>
internal sealed record SymbolKindCapability
{
    /// <summary>Kinds 1-26, the whole LSP 3.17 enumeration.</summary>
    [JsonPropertyName("valueSet")]
    public IReadOnlyList<int> ValueSet { get; init; } =
        [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26];
}

/// <summary>A capability whose only property is refresh support.</summary>
internal sealed record RefreshCapability
{
    /// <summary>Whether the server may ask for a refresh.</summary>
    [JsonPropertyName("refreshSupport")]
    public bool RefreshSupport { get; init; } = true;
}

/// <summary>The workspace-edit capability.</summary>
internal sealed record WorkspaceEditCapability
{
    /// <summary>
    /// Edits arrive as <c>documentChanges</c>. Roslyn always uses them anyway (C21), and they are
    /// the only form that carries a document version.
    /// </summary>
    [JsonPropertyName("documentChanges")]
    public bool DocumentChanges { get; init; } = true;

    /// <summary>
    /// File create, rename and delete. Roslyn only emits a <c>Move type to X.cs</c> refactoring's
    /// create operation when this is declared (C21); without it the same action resolves to an
    /// empty edit that looks like a bug.
    /// </summary>
    [JsonPropertyName("resourceOperations")]
    public IReadOnlyList<string> ResourceOperations { get; init; } = ["create", "rename", "delete"];
}

/// <summary>Document capabilities the adapter declares.</summary>
internal sealed record RoslynTextDocumentCapabilities
{
    /// <summary>Document synchronisation. <c>didSave</c> without text — the mirror already has it.</summary>
    [JsonPropertyName("synchronization")]
    public SynchronizationCapability Synchronization { get; init; } = new();

    /// <summary>Hover, in markdown with a plaintext fallback.</summary>
    [JsonPropertyName("hover")]
    public HoverCapability Hover { get; init; } = new();

    /// <summary>Go to definition, without link support.</summary>
    [JsonPropertyName("definition")]
    public LinkCapability Definition { get; init; } = new();

    /// <summary>Go to type definition, without link support.</summary>
    [JsonPropertyName("typeDefinition")]
    public LinkCapability TypeDefinition { get; init; } = new();

    /// <summary>Go to implementation, without link support.</summary>
    [JsonPropertyName("implementation")]
    public LinkCapability Implementation { get; init; } = new();

    /// <summary>Find references.</summary>
    [JsonPropertyName("references")]
    public DynamicOffCapability References { get; init; } = new();

    /// <summary>Document symbols, hierarchical.</summary>
    [JsonPropertyName("documentSymbol")]
    public DocumentSymbolCapability DocumentSymbol { get; init; } = new();

    /// <summary>Call hierarchy.</summary>
    [JsonPropertyName("callHierarchy")]
    public DynamicOffCapability CallHierarchy { get; init; } = new();

    /// <summary>Rename, with the prepare step.</summary>
    [JsonPropertyName("rename")]
    public RenameCapability Rename { get; init; } = new();

    /// <summary>Code actions, with opaque data and lazy edit resolution.</summary>
    [JsonPropertyName("codeAction")]
    public CodeActionCapability CodeAction { get; init; } = new();

    /// <summary>Whole-document formatting.</summary>
    [JsonPropertyName("formatting")]
    public DynamicOffCapability Formatting { get; init; } = new();

    /// <summary>Range formatting.</summary>
    [JsonPropertyName("rangeFormatting")]
    public DynamicOffCapability RangeFormatting { get; init; } = new();

    /// <summary>Signature help.</summary>
    /// <remarks>
    /// Declared because the adapter advertises <c>signatureHelpProvider</c> to its own client. A
    /// provider promised in one direction and never declared in the other is the exact asymmetry
    /// that produces empty answers with no error anywhere.
    /// </remarks>
    [JsonPropertyName("signatureHelp")]
    public DynamicOffCapability SignatureHelp { get; init; } = new();

    /// <summary>Completion, for the same reason as signature help.</summary>
    [JsonPropertyName("completion")]
    public CompletionCapability Completion { get; init; } = new();

    /// <summary>
    /// Pull diagnostics, registered dynamically per source.
    /// </summary>
    /// <remarks>
    /// This is what makes Roslyn send the ten <c>textDocument/diagnostic</c> registrations the
    /// bridge keys on (C11). Without it there is no way to ask a single source for its diagnostics,
    /// and an unqualified pull returns the union of everything (C9).
    /// </remarks>
    [JsonPropertyName("diagnostic")]
    public DiagnosticCapability Diagnostic { get; init; } = new();
}

/// <summary>Document synchronisation capabilities.</summary>
internal sealed record SynchronizationCapability
{
    /// <summary>Static registration only.</summary>
    [JsonPropertyName("dynamicRegistration")]
    public bool DynamicRegistration { get; init; }

    /// <summary>The adapter does not use will-save.</summary>
    [JsonPropertyName("willSave")]
    public bool WillSave { get; init; }

    /// <summary>Nor the blocking form of it.</summary>
    [JsonPropertyName("willSaveWaitUntil")]
    public bool WillSaveWaitUntil { get; init; }

    /// <summary>Saves are forwarded, because a save is when a diagnostics pull is worth paying for.</summary>
    [JsonPropertyName("didSave")]
    public bool DidSave { get; init; } = true;
}

/// <summary>The hover capability.</summary>
internal sealed record HoverCapability
{
    /// <summary>Static registration only.</summary>
    [JsonPropertyName("dynamicRegistration")]
    public bool DynamicRegistration { get; init; }

    /// <summary>Markdown first, plaintext as the fallback — the order is the preference.</summary>
    [JsonPropertyName("contentFormat")]
    public IReadOnlyList<string> ContentFormat { get; init; } = ["markdown", "plaintext"];
}

/// <summary>A navigation capability that declines <c>LocationLink</c>.</summary>
internal sealed record LinkCapability
{
    /// <summary>Static registration only.</summary>
    [JsonPropertyName("dynamicRegistration")]
    public bool DynamicRegistration { get; init; }

    /// <summary>
    /// False on purpose. With link support Roslyn answers with <c>LocationLink</c>, which a client
    /// that only understands <c>Location</c> renders as nothing at all — and the adapter forwards
    /// navigation results verbatim.
    /// </summary>
    [JsonPropertyName("linkSupport")]
    public bool LinkSupport { get; init; }
}

/// <summary>A capability whose only property is that dynamic registration is off.</summary>
internal sealed record DynamicOffCapability
{
    /// <summary>Static registration only.</summary>
    [JsonPropertyName("dynamicRegistration")]
    public bool DynamicRegistration { get; init; }
}

/// <summary>The document-symbol capability.</summary>
internal sealed record DocumentSymbolCapability
{
    /// <summary>Static registration only.</summary>
    [JsonPropertyName("dynamicRegistration")]
    public bool DynamicRegistration { get; init; }

    /// <summary>Nested symbols rather than a flat list.</summary>
    [JsonPropertyName("hierarchicalDocumentSymbolSupport")]
    public bool HierarchicalDocumentSymbolSupport { get; init; } = true;

    /// <summary>Which symbol kinds are understood.</summary>
    [JsonPropertyName("symbolKind")]
    public SymbolKindCapability SymbolKind { get; init; } = new();
}

/// <summary>The rename capability.</summary>
internal sealed record RenameCapability
{
    /// <summary>Static registration only.</summary>
    [JsonPropertyName("dynamicRegistration")]
    public bool DynamicRegistration { get; init; }

    /// <summary>Whether <c>prepareRename</c> may be sent first.</summary>
    [JsonPropertyName("prepareSupport")]
    public bool PrepareSupport { get; init; } = true;
}

/// <summary>The code-action capability.</summary>
internal sealed record CodeActionCapability
{
    /// <summary>Static registration only.</summary>
    [JsonPropertyName("dynamicRegistration")]
    public bool DynamicRegistration { get; init; }

    /// <summary>
    /// Whether an action may carry an opaque <c>data</c> payload. Roslyn's do — a
    /// <c>CodeActionResolveData</c> with PascalCase members (C18) — and it must round-trip verbatim.
    /// </summary>
    [JsonPropertyName("dataSupport")]
    public bool DataSupport { get; init; } = true;

    /// <summary>Which members are filled in only on resolve.</summary>
    [JsonPropertyName("resolveSupport")]
    public ResolveSupportCapability ResolveSupport { get; init; } = new();

    /// <summary>The kinds understood.</summary>
    [JsonPropertyName("codeActionLiteralSupport")]
    public CodeActionLiteralSupport CodeActionLiteralSupport { get; init; } = new();
}

/// <summary>Which properties are resolved lazily.</summary>
internal sealed record ResolveSupportCapability
{
    /// <summary>Just the edit: the adapter does not execute commands.</summary>
    [JsonPropertyName("properties")]
    public IReadOnlyList<string> Properties { get; init; } = ["edit"];
}

/// <summary>Code-action literal support.</summary>
internal sealed record CodeActionLiteralSupport
{
    /// <summary>The kinds the adapter passes through.</summary>
    [JsonPropertyName("codeActionKind")]
    public CodeActionKindCapability CodeActionKind { get; init; } = new();
}

/// <summary>The set of code-action kinds understood.</summary>
internal sealed record CodeActionKindCapability
{
    /// <summary>The two kinds Roslyn advertises (C19), plus the empty catch-all the spec defines.</summary>
    [JsonPropertyName("valueSet")]
    public IReadOnlyList<string> ValueSet { get; init; } = ["", "quickfix", "refactor"];
}

/// <summary>The completion capability.</summary>
internal sealed record CompletionCapability
{
    /// <summary>Static registration only.</summary>
    [JsonPropertyName("dynamicRegistration")]
    public bool DynamicRegistration { get; init; }

    /// <summary>What an individual item may carry.</summary>
    [JsonPropertyName("completionItem")]
    public CompletionItemCapability CompletionItem { get; init; } = new();
}

/// <summary>What a completion item may carry.</summary>
internal sealed record CompletionItemCapability
{
    /// <summary>Snippets are passed through unchanged.</summary>
    [JsonPropertyName("snippetSupport")]
    public bool SnippetSupport { get; init; } = true;

    /// <summary>Which members arrive only on resolve.</summary>
    [JsonPropertyName("resolveSupport")]
    public CompletionResolveSupport ResolveSupport { get; init; } = new();
}

/// <summary>Which completion members are resolved lazily.</summary>
internal sealed record CompletionResolveSupport
{
    /// <summary>The three Roslyn defers.</summary>
    [JsonPropertyName("properties")]
    public IReadOnlyList<string> Properties { get; init; } = ["documentation", "detail", "additionalTextEdits"];
}

/// <summary>The pull-diagnostics capability.</summary>
internal sealed record DiagnosticCapability
{
    /// <summary>Dynamic, so each Roslyn source registers itself and can be pulled individually.</summary>
    [JsonPropertyName("dynamicRegistration")]
    public bool DynamicRegistration { get; init; } = true;

    /// <summary>Whether a report may include diagnostics for other documents.</summary>
    [JsonPropertyName("relatedDocumentSupport")]
    public bool RelatedDocumentSupport { get; init; } = true;
}

/// <summary>Window capabilities the adapter declares.</summary>
internal sealed record RoslynWindowCapabilities
{
    /// <summary>
    /// The adapter answers <c>window/workDoneProgress/create</c>. It has to: Claude Code refuses it
    /// with <c>-32601</c>, and Roslyn's solution-load progress stream is the only place the load's
    /// own account of itself appears (C31).
    /// </summary>
    [JsonPropertyName("workDoneProgress")]
    public bool WorkDoneProgress { get; init; } = true;
}

/// <summary>General capabilities the adapter declares.</summary>
internal sealed record RoslynGeneralCapabilities
{
    /// <summary>
    /// UTF-16 only. It is what Claude Code uses and what LSP defaults to, and a mismatch would move
    /// every position in a file containing one non-BMP character.
    /// </summary>
    [JsonPropertyName("positionEncodings")]
    public IReadOnlyList<string> PositionEncodings { get; init; } = ["utf-16"];
}

/// <summary>The complete <c>project/open</c> notification.</summary>
internal sealed record ProjectOpenNotification
{
    /// <summary>The JSON-RPC version.</summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpcVersion { get; init; } = JsonRpc.Version;

    /// <summary>Always <c>project/open</c>.</summary>
    [JsonPropertyName("method")]
    public string Method { get; init; } = "project/open";

    /// <summary>Which projects.</summary>
    [JsonPropertyName("params")]
    public required ProjectOpenParams Params { get; init; }
}
