using System.Text.Json;

using ClaudeRoslynLsp.Mcp.Engine;

namespace ClaudeRoslynLsp.Tests.Mcp;

/// <summary>
/// A scriptable <see cref="IRoslynEngine"/>: canned answers in, a record of what was asked out.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole point of the engine seam (D60). Every judgement the tool layer makes — symbol
/// addressing, de-duplication across target frameworks, code-action ids, diagnostic filtering, the
/// preview-to-apply cache, the edit applier — is exercised here in milliseconds against payloads
/// shaped exactly like the ones the WP0 spikes captured off the real server, instead of against a
/// quarter-gigabyte child process that takes nine seconds to load a solution.
/// </para>
/// <para>
/// It records rather than asserts: <see cref="Calls"/>, <see cref="NotifiedChanges"/>,
/// <see cref="CompilerScope"/> and <see cref="OrganizeImportsOnFormat"/> are what let a test say
/// "and it told Roslyn about the files it wrote", which is the failure that would otherwise be
/// invisible until an answer went stale.
/// </para>
/// </remarks>
internal sealed class FakeRoslynEngine : IRoslynEngine
{
    /// <summary>What <see cref="EnsureReadyAsync"/> answers.</summary>
    internal WorkspaceState State { get; set; } = new(
        WorkspaceLoadStatus.Ready,
        ProjectsLoaded: 2,
        ProjectsTotal: 2,
        RoslynVersion: "5.12.0-1.26426.8",
        ProcessId: 4242,
        WorkingSetBytes: 253L * 1024 * 1024);

    /// <summary>What <c>workspace/symbol</c> answers, for every query.</summary>
    internal List<SymbolInformation> Symbols { get; } = [];

    /// <summary>What <c>textDocument/references</c> answers.</summary>
    internal List<LspLocation> References { get; } = [];

    /// <summary>What <c>textDocument/documentSymbol</c> answers, keyed by URI.</summary>
    internal Dictionary<string, List<DocumentSymbolNode>> DocumentSymbols { get; } = new(StringComparer.Ordinal);

    /// <summary>What <c>textDocument/codeAction</c> answers.</summary>
    internal List<RawCodeAction> CodeActions { get; } = [];

    /// <summary>What <c>codeAction/resolve</c> answers, keyed by action title.</summary>
    internal Dictionary<string, WorkspaceEdit> ResolvedEdits { get; } = new(StringComparer.Ordinal);

    /// <summary>What <c>codeAction/resolveFixAll</c> answers.</summary>
    internal WorkspaceEdit? FixAllEdit { get; set; }

    /// <summary>What <c>textDocument/prepareRename</c> answers; <see langword="null"/> means "not renamable".</summary>
    internal LspRange? PrepareRename { get; set; } = new();

    /// <summary>What <c>textDocument/rename</c> answers.</summary>
    internal WorkspaceEdit? RenameEdit { get; set; }

    /// <summary>What <c>textDocument/formatting</c> answers, keyed by URI.</summary>
    internal Dictionary<string, List<LspTextEdit>> FormattingEdits { get; } = new(StringComparer.Ordinal);

    /// <summary>What <c>textDocument/diagnostic</c> answers, keyed by URI.</summary>
    internal Dictionary<string, DocumentDiagnosticReport> DocumentDiagnostics { get; } = new(StringComparer.Ordinal);

    /// <summary>What <c>workspace/diagnostic</c> answers.</summary>
    internal WorkspaceDiagnosticReport WorkspaceDiagnostics { get; set; } = new();

    /// <summary>What <c>textDocument/hover</c> answers.</summary>
    internal string? Hover { get; set; }

    /// <summary>Every method name that was called, in order.</summary>
    internal List<string> Calls { get; } = [];

    /// <summary>The documents currently open, as <c>didOpen</c> and <c>didClose</c> left them.</summary>
    internal HashSet<string> OpenDocuments { get; } = new(StringComparer.Ordinal);

    /// <summary>Everything <c>workspace/didChangeWatchedFiles</c> was told about.</summary>
    internal List<FileChange> NotifiedChanges { get; } = [];

    /// <summary>The last compiler diagnostics scope that was set, if any.</summary>
    internal CompilerDiagnosticsScope? CompilerScope { get; private set; }

    /// <summary>The last organize-imports-on-format setting, if any.</summary>
    internal bool? OrganizeImportsOnFormat { get; private set; }

    /// <summary>The scope the last <c>resolveFixAll</c> asked for.</summary>
    internal FixAllScope? RequestedFixAllScope { get; private set; }

    /// <summary>The title the last <c>resolveFixAll</c> asked for.</summary>
    internal string? RequestedFixAllTitle { get; private set; }

    /// <summary>The new name the last <c>rename</c> asked for.</summary>
    internal string? RequestedNewName { get; private set; }

    /// <summary>How many times the workspace pull ran, so a test can prove a cache hit skipped it.</summary>
    internal int WorkspaceDiagnosticCalls { get; private set; }

    /// <inheritdoc />
    public Task<WorkspaceState> EnsureReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(EnsureReadyAsync));
        return Task.FromResult(State);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SymbolInformation>> WorkspaceSymbolAsync(string query, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(WorkspaceSymbolAsync) + ":" + query);
        return Task.FromResult<IReadOnlyList<SymbolInformation>>(Symbols);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LspLocation>> ReferencesAsync(
        string uri,
        LspPosition position,
        bool includeDeclaration,
        CancellationToken cancellationToken)
    {
        Calls.Add(nameof(ReferencesAsync));
        return Task.FromResult<IReadOnlyList<LspLocation>>(References);
    }

    /// <inheritdoc />
    public Task<LspRange?> PrepareRenameAsync(string uri, LspPosition position, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(PrepareRenameAsync));
        return Task.FromResult(PrepareRename);
    }

    /// <inheritdoc />
    public Task<WorkspaceEdit?> RenameAsync(
        string uri,
        LspPosition position,
        string newName,
        CancellationToken cancellationToken)
    {
        Calls.Add(nameof(RenameAsync));
        RequestedNewName = newName;
        return Task.FromResult(RenameEdit);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RawCodeAction>> CodeActionsAsync(
        string uri,
        LspRange range,
        IReadOnlyList<RawDiagnostic>? diagnostics,
        CancellationToken cancellationToken)
    {
        Calls.Add(nameof(CodeActionsAsync));
        return Task.FromResult<IReadOnlyList<RawCodeAction>>(CodeActions);
    }

    /// <inheritdoc />
    public Task<RawCodeAction> ResolveCodeActionAsync(RawCodeAction action, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(ResolveCodeActionAsync) + ":" + action.Title);

        return Task.FromResult(ResolvedEdits.TryGetValue(action.Title, out var edit)
            ? action with { Edit = edit }
            : action);
    }

    /// <inheritdoc />
    public Task<WorkspaceEdit?> ResolveFixAllAsync(
        string title,
        JsonElement data,
        FixAllScope scope,
        CancellationToken cancellationToken)
    {
        Calls.Add(nameof(ResolveFixAllAsync) + ":" + title + ":" + scope);
        RequestedFixAllTitle = title;
        RequestedFixAllScope = scope;
        return Task.FromResult(FixAllEdit);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LspTextEdit>> FormattingAsync(
        string uri,
        LspFormattingOptions options,
        CancellationToken cancellationToken)
    {
        Calls.Add(nameof(FormattingAsync));

        return Task.FromResult<IReadOnlyList<LspTextEdit>>(
            FormattingEdits.TryGetValue(uri, out var edits) ? edits : []);
    }

    /// <inheritdoc />
    public Task<DocumentDiagnosticReport> DocumentDiagnosticAsync(string uri, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(DocumentDiagnosticAsync));

        return Task.FromResult(DocumentDiagnostics.TryGetValue(uri, out var report) ? report : new DocumentDiagnosticReport());
    }

    /// <inheritdoc />
    public Task<WorkspaceDiagnosticReport> WorkspaceDiagnosticAsync(
        IReadOnlyList<PreviousResultId> previousResultIds,
        CancellationToken cancellationToken)
    {
        Calls.Add(nameof(WorkspaceDiagnosticAsync));
        WorkspaceDiagnosticCalls++;
        return Task.FromResult(WorkspaceDiagnostics);
    }

    /// <inheritdoc />
    public Task SetCompilerDiagnosticsScopeAsync(CompilerDiagnosticsScope scope, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(SetCompilerDiagnosticsScopeAsync) + ":" + scope);
        CompilerScope = scope;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetOrganizeImportsOnFormatAsync(bool enabled, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(SetOrganizeImportsOnFormatAsync) + ":" + enabled);
        OrganizeImportsOnFormat = enabled;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OpenDocumentAsync(string uri, string text, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(OpenDocumentAsync));
        OpenDocuments.Add(uri);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CloseDocumentAsync(string uri, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(CloseDocumentAsync));
        OpenDocuments.Remove(uri);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public bool IsDocumentOpen(string uri) => OpenDocuments.Contains(uri);

    /// <inheritdoc />
    public Task<IReadOnlyList<DocumentSymbolNode>> DocumentSymbolAsync(string uri, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(DocumentSymbolAsync));

        return Task.FromResult<IReadOnlyList<DocumentSymbolNode>>(
            DocumentSymbols.TryGetValue(uri, out var symbols) ? symbols : []);
    }

    /// <inheritdoc />
    public Task NotifyFilesChangedAsync(IReadOnlyList<FileChange> changes, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(NotifyFilesChangedAsync));
        NotifiedChanges.AddRange(changes);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> HoverAsync(string uri, LspPosition position, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(HoverAsync));
        return Task.FromResult(Hover);
    }
}
