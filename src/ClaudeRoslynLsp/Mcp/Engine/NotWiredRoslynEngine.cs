using System.Text.Json;

using ModelContextProtocol;

namespace ClaudeRoslynLsp.Mcp.Engine;

/// <summary>
/// The engine a build with no backend wired in runs with: every member fails with one sentence that
/// says so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than a null (D69).</b> The MCP handshake and <c>tools/list</c> must keep
/// working with no Roslyn in the picture at all — that is the leg <c>SmokeTest</c> drives against a
/// published Native AOT binary on every release RID, and it is also what a client sees during the
/// seconds before anything has been launched. Registering nothing would make the container throw at
/// the first tool call with the SDK's generic "An error occurred", which tells a model nothing;
/// registering this tells it exactly what is wrong in words it can relay to a human.
/// </para>
/// <para>
/// <see cref="EnsureReadyAsync"/> is the one member that does not throw. It answers
/// <see cref="WorkspaceLoadStatus.Failed"/>, which is a state every tool already handles, so the
/// nine gated tools return a status object rather than an error — and <c>getWorkspaceStatus</c>, the
/// tool whose entire job is to say what state the workspace is in, answers correctly instead of
/// failing.
/// </para>
/// </remarks>
internal sealed class NotWiredRoslynEngine : IRoslynEngine
{
    /// <summary>
    /// What every member says. One sentence, naming the verb that does work today and the command
    /// that diagnoses the backend, because a model reading this has to decide what to do next.
    /// </summary>
    internal const string Message =
        "The Roslyn backend is not wired into this build of the MCP server, so no C# question can be answered. "
        + "Run `claude-roslyn-lsp doctor` to check the Roslyn installation, and use the LSP server "
        + "(`claude-roslyn-lsp lsp`) for navigation in the meantime.";

    /// <inheritdoc />
    public Task<WorkspaceState> EnsureReadyAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult(new WorkspaceState(WorkspaceLoadStatus.Failed, Message: Message));

    /// <inheritdoc />
    public Task<IReadOnlyList<SymbolInformation>> WorkspaceSymbolAsync(string query, CancellationToken cancellationToken) =>
        throw NotWired();

    /// <inheritdoc />
    public Task<IReadOnlyList<LspLocation>> ReferencesAsync(
        string uri,
        LspPosition position,
        bool includeDeclaration,
        CancellationToken cancellationToken) => throw NotWired();

    /// <inheritdoc />
    public Task<LspRange?> PrepareRenameAsync(string uri, LspPosition position, CancellationToken cancellationToken) =>
        throw NotWired();

    /// <inheritdoc />
    public Task<WorkspaceEdit?> RenameAsync(
        string uri,
        LspPosition position,
        string newName,
        CancellationToken cancellationToken) => throw NotWired();

    /// <inheritdoc />
    public Task<IReadOnlyList<RawCodeAction>> CodeActionsAsync(
        string uri,
        LspRange range,
        IReadOnlyList<RawDiagnostic>? diagnostics,
        CancellationToken cancellationToken) => throw NotWired();

    /// <inheritdoc />
    public Task<RawCodeAction> ResolveCodeActionAsync(RawCodeAction action, CancellationToken cancellationToken) =>
        throw NotWired();

    /// <inheritdoc />
    public Task<WorkspaceEdit?> ResolveFixAllAsync(
        string title,
        JsonElement data,
        FixAllScope scope,
        CancellationToken cancellationToken) => throw NotWired();

    /// <inheritdoc />
    public Task<IReadOnlyList<LspTextEdit>> FormattingAsync(
        string uri,
        LspFormattingOptions options,
        CancellationToken cancellationToken) => throw NotWired();

    /// <inheritdoc />
    public Task<DocumentDiagnosticReport> DocumentDiagnosticAsync(string uri, CancellationToken cancellationToken) =>
        throw NotWired();

    /// <inheritdoc />
    public Task<WorkspaceDiagnosticReport> WorkspaceDiagnosticAsync(
        IReadOnlyList<PreviousResultId> previousResultIds,
        CancellationToken cancellationToken) => throw NotWired();

    /// <inheritdoc />
    public Task SetCompilerDiagnosticsScopeAsync(CompilerDiagnosticsScope scope, CancellationToken cancellationToken) =>
        throw NotWired();

    /// <inheritdoc />
    public Task SetOrganizeImportsOnFormatAsync(bool enabled, CancellationToken cancellationToken) =>
        throw NotWired();

    /// <inheritdoc />
    public Task OpenDocumentAsync(string uri, string text, CancellationToken cancellationToken) => throw NotWired();

    /// <inheritdoc />
    public Task CloseDocumentAsync(string uri, CancellationToken cancellationToken) => throw NotWired();

    /// <inheritdoc />
    public bool IsDocumentOpen(string uri) => false;

    /// <inheritdoc />
    public Task<IReadOnlyList<DocumentSymbolNode>> DocumentSymbolAsync(string uri, CancellationToken cancellationToken) =>
        throw NotWired();

    /// <inheritdoc />
    public Task NotifyFilesChangedAsync(IReadOnlyList<FileChange> changes, CancellationToken cancellationToken) =>
        throw NotWired();

    /// <inheritdoc />
    public Task<string?> HoverAsync(string uri, LspPosition position, CancellationToken cancellationToken) =>
        throw NotWired();

    private static McpException NotWired() => new(Message);
}
