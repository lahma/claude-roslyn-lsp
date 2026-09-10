using ClaudeRoslynLsp.Mcp.Engine;

namespace ClaudeRoslynLsp.Mcp.Models;

/// <summary>What <c>getWorkspaceStatus</c> reports.</summary>
/// <param name="Status">ok, loading or failed.</param>
/// <param name="Solution">The solution or project file that was opened, workspace-relative.</param>
/// <param name="ProjectsLoaded">How many projects have finished loading.</param>
/// <param name="ProjectsTotal">How many there are.</param>
/// <param name="Projects">The projects, with their target frameworks.</param>
/// <param name="LoadErrors">What went wrong while loading.</param>
/// <param name="RoslynVersion">The Roslyn build this session is running.</param>
/// <param name="ProcessId">The process that holds the workspace (C45).</param>
/// <param name="MemoryMegabytes">That process's working set.</param>
/// <param name="Note">What to do next, when the answer is not a real one.</param>
/// <param name="Engine">
/// How this server got its Roslyn: <c>owned</c> when it launched one of its own. The plan reserves
/// <c>attached</c> for the shared engine (D23), and until that lands the value is worth reporting
/// precisely because it is always <c>owned</c> — a repository running both servers has two Roslyn
/// processes and this is where that becomes visible rather than merely documented.
/// </param>
internal sealed record WorkspaceStatusResult(
    string Status,
    string? Solution = null,
    int ProjectsLoaded = 0,
    int ProjectsTotal = 0,
    IReadOnlyList<ProjectSummary>? Projects = null,
    IReadOnlyList<string>? LoadErrors = null,
    string? RoslynVersion = null,
    int? ProcessId = null,
    int? MemoryMegabytes = null,
    string? Note = null,
    string? Engine = null);

/// <summary>One project of the loaded workspace.</summary>
/// <param name="Name">The project's name.</param>
/// <param name="Path">Its workspace-relative path.</param>
/// <param name="TargetFrameworks">Its target frameworks.</param>
internal sealed record ProjectSummary(string Name, string Path, IReadOnlyList<string> TargetFrameworks);

/// <summary>One symbol, at a position a caller can hand to the <c>LSP</c> tool.</summary>
/// <param name="Name">The symbol's simple name.</param>
/// <param name="FullName">Its name with the container Roslyn reported, when there is one.</param>
/// <param name="Kind">Its kind: class, method, property, ....</param>
/// <param name="Path">The workspace-relative file it is declared in.</param>
/// <param name="Line">The 1-based line of the declaration.</param>
/// <param name="Column">The 1-based column of the declaration.</param>
/// <param name="EndLine">The 1-based line the declaration ends on.</param>
/// <param name="EndColumn">The 1-based column the declaration ends at.</param>
/// <param name="Container">Roslyn's container text, which names the project and its target frameworks.</param>
/// <param name="Signature">The signature line from hover, when one was asked for.</param>
internal sealed record SymbolMatch(
    string Name,
    string FullName,
    string Kind,
    string Path,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string? Container = null,
    string? Signature = null);

/// <summary>What <c>resolveSymbol</c> reports.</summary>
/// <param name="Status">ok, loading or failed.</param>
/// <param name="Query">The symbol argument as it was given.</param>
/// <param name="TotalCount">How many matches there were before the cap.</param>
/// <param name="Matches">The matches.</param>
/// <param name="HasMore">Whether the cap cut the list.</param>
/// <param name="Note">What to do next.</param>
internal sealed record SymbolResolveResult(
    string Status,
    string Query,
    int TotalCount = 0,
    IReadOnlyList<SymbolMatch>? Matches = null,
    bool HasMore = false,
    string? Note = null);

/// <summary>One member of a type.</summary>
/// <param name="Name">The member's name.</param>
/// <param name="Kind">Its kind.</param>
/// <param name="Detail">Roslyn's signature-ish detail text.</param>
/// <param name="Line">The 1-based line it is declared on.</param>
/// <param name="Column">The 1-based column.</param>
internal sealed record TypeMember(string Name, string Kind, string? Detail, int Line, int Column);

/// <summary>What <c>getTypeMembers</c> reports.</summary>
/// <param name="Status">ok, loading or failed.</param>
/// <param name="Type">The type that was resolved.</param>
/// <param name="Members">Its members, in declaration order.</param>
/// <param name="TotalCount">How many members there were before the cap.</param>
/// <param name="HasMore">Whether the cap cut the list.</param>
/// <param name="Note">What to do next.</param>
internal sealed record TypeMembersResult(
    string Status,
    SymbolMatch? Type = null,
    IReadOnlyList<TypeMember>? Members = null,
    int TotalCount = 0,
    bool HasMore = false,
    string? Note = null);

/// <summary>One reference to a symbol.</summary>
/// <param name="Path">The workspace-relative file.</param>
/// <param name="Line">The 1-based line.</param>
/// <param name="Column">The 1-based column.</param>
/// <param name="EndLine">The 1-based line the reference ends on.</param>
/// <param name="EndColumn">The 1-based column it ends at.</param>
/// <param name="LineText">The source line, read from disk and trimmed, so a caller does not have to open the file.</param>
internal sealed record ReferenceMatch(
    string Path,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string? LineText = null);

/// <summary>How many references one file holds.</summary>
/// <param name="Path">The workspace-relative file.</param>
/// <param name="Count">How many references are in it.</param>
internal sealed record ReferenceFileSummary(string Path, int Count);

/// <summary>What <c>findReferences</c> reports.</summary>
/// <param name="Status">ok, loading or failed.</param>
/// <param name="Symbol">The symbol argument as it was given.</param>
/// <param name="TotalCount">How many references there are in total.</param>
/// <param name="Offset">Where this page starts.</param>
/// <param name="HasMore">Whether there is another page.</param>
/// <param name="ByFile">One row per file, over the whole result rather than this page.</param>
/// <param name="References">This page of references.</param>
/// <param name="Note">What to do next.</param>
internal sealed record ReferencesResult(
    string Status,
    string Symbol,
    int TotalCount = 0,
    int Offset = 0,
    bool HasMore = false,
    IReadOnlyList<ReferenceFileSummary>? ByFile = null,
    IReadOnlyList<ReferenceMatch>? References = null,
    string? Note = null);

/// <summary>One diagnostic.</summary>
/// <param name="Id">The diagnostic id: CS0029, IDE0005, CA1822.</param>
/// <param name="Severity">error, warning, information or hint.</param>
/// <param name="Message">Roslyn's message.</param>
/// <param name="Path">The workspace-relative file.</param>
/// <param name="Line">The 1-based line.</param>
/// <param name="Column">The 1-based column.</param>
/// <param name="EndLine">The 1-based line it ends on.</param>
/// <param name="EndColumn">The 1-based column it ends at.</param>
/// <param name="Tags">LSP tags only: <c>unnecessary</c>, <c>deprecated</c> (C16).</param>
/// <param name="HelpUri">The rule's documentation, when the analyzer supplies one.</param>
internal sealed record DiagnosticEntry(
    string Id,
    string Severity,
    string Message,
    string Path,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    IReadOnlyList<string>? Tags = null,
    string? HelpUri = null);

/// <summary>How many diagnostics of each severity the whole result held.</summary>
/// <param name="Error">Errors.</param>
/// <param name="Warning">Warnings.</param>
/// <param name="Information">Informational diagnostics.</param>
/// <param name="Hint">Hints.</param>
internal sealed record DiagnosticCounts(int Error, int Warning, int Information, int Hint);

/// <summary>What <c>getDiagnostics</c> reports.</summary>
/// <param name="Status">ok, loading or failed.</param>
/// <param name="Scope">file, project or solution.</param>
/// <param name="TotalCount">How many diagnostics matched before the cap.</param>
/// <param name="Counts">Severity counts over everything that matched.</param>
/// <param name="Diagnostics">This page, sorted by file then position.</param>
/// <param name="HasMore">Whether the cap cut the list.</param>
/// <param name="Note">The design-time caveat, and what to do next.</param>
internal sealed record DiagnosticsResult(
    string Status,
    string Scope,
    int TotalCount = 0,
    DiagnosticCounts? Counts = null,
    IReadOnlyList<DiagnosticEntry>? Diagnostics = null,
    bool HasMore = false,
    string? Note = null);

/// <summary>One offered code action.</summary>
/// <param name="Id">The short stable id to pass to <c>applyCodeAction</c>.</param>
/// <param name="Title">Roslyn's title, which <c>applyCodeAction</c> also accepts.</param>
/// <param name="Kind">quickfix, refactor, refactor.extract, ....</param>
/// <param name="DiagnosticIds">The diagnostics this action fixes, when Roslyn attached any.</param>
/// <param name="FixAllScopes">The <c>fixAllScope</c> values this action accepts, when it accepts any.</param>
/// <param name="Nested">Sub-actions, each addressable by its own id.</param>
internal sealed record CodeActionEntry(
    string Id,
    string Title,
    string? Kind = null,
    IReadOnlyList<string>? DiagnosticIds = null,
    IReadOnlyList<string>? FixAllScopes = null,
    IReadOnlyList<CodeActionEntry>? Nested = null);

/// <summary>What <c>getCodeActions</c> reports.</summary>
/// <param name="Status">ok, loading or failed.</param>
/// <param name="Path">The workspace-relative file the actions are for.</param>
/// <param name="Line">The 1-based line the selection starts on.</param>
/// <param name="Column">The 1-based column it starts at.</param>
/// <param name="Actions">The offered actions.</param>
/// <param name="Note">What to do next.</param>
internal sealed record CodeActionsResult(
    string Status,
    string? Path = null,
    int Line = 0,
    int Column = 0,
    IReadOnlyList<CodeActionEntry>? Actions = null,
    string? Note = null);

/// <summary>One file a workspace edit touched.</summary>
/// <param name="Path">The workspace-relative path.</param>
/// <param name="Edits">How many text edits landed in it.</param>
/// <param name="Created">The file did not exist before.</param>
/// <param name="Deleted">The file is gone.</param>
/// <param name="RenamedTo">Where the file moved to.</param>
internal sealed record EditedFile(
    string Path,
    int Edits,
    bool? Created = null,
    bool? Deleted = null,
    string? RenamedTo = null);

/// <summary>
/// What every mutating tool reports: what it did, to which files, and what the caller now has to do
/// about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The note is the load-bearing member (D62).</b> Claude Code's own <c>Edit</c> tool refuses to
/// edit a file that changed since its last <c>Read</c>, so a model that renamed a symbol and then
/// tries to edit one of the affected files gets a refusal it did not expect and cannot diagnose from
/// the message. Saying so in the result — in the same words every time, so it is recognisable — turns
/// that from a mystery into an instruction.
/// </para>
/// </remarks>
/// <param name="Status">ok, loading or failed.</param>
/// <param name="Applied">Whether anything was written. False for a preview.</param>
/// <param name="FilesChanged">How many files the edit touches.</param>
/// <param name="Edits">How many text edits it carries in total.</param>
/// <param name="Files">One row per file.</param>
/// <param name="Diff">A unified diff, budgeted.</param>
/// <param name="Note">What the caller has to do next.</param>
internal sealed record EditResult(
    string Status,
    bool Applied,
    int FilesChanged = 0,
    int Edits = 0,
    IReadOnlyList<EditedFile>? Files = null,
    string? Diff = null,
    string? Note = null)
{
    /// <summary>
    /// The note on every applied edit, word for word. Fixed text because a model learns to recognise
    /// it, and because a test can assert it.
    /// </summary>
    internal const string AppliedNote = "Files were changed on disk; re-read a file before editing it.";

    /// <summary>The note on a preview: nothing happened, and here is how to make it happen.</summary>
    internal const string PreviewNote =
        "Nothing was written. Call the same tool again with preview: false to apply exactly these edits.";

    /// <summary>The note when the operation resolved to no edits at all.</summary>
    internal const string NothingToDoNote = "There was nothing to change; no file was touched.";
}

/// <summary>Builds the "the workspace is not ready" form of every result.</summary>
/// <remarks>
/// One place, so the nine gated tools cannot drift into nine slightly different ways of saying the
/// same thing — and so the shape a caller has to handle is identical whichever tool it called.
/// </remarks>
internal static class NotReadyResults
{
    /// <summary><c>getWorkspaceStatus</c>'s own not-ready form, which still carries everything known.</summary>
    /// <param name="state">The workspace state.</param>
    /// <param name="solution">The solution path, workspace-relative.</param>
    /// <param name="projects">The projects, when any are known.</param>
    internal static WorkspaceStatusResult Workspace(
        WorkspaceState state,
        string? solution,
        IReadOnlyList<ProjectSummary>? projects) =>
        new(
            ToolStatus.For(state),
            solution,
            state.ProjectsLoaded,
            state.ProjectsTotal,
            projects,
            state.LoadErrors,
            state.RoslynVersion,
            state.ProcessId,
            state.WorkingSetBytes is { } bytes ? (int) (bytes / 1024 / 1024) : null,
            ToolStatus.NoteFor(state),
            state.Engine);

    /// <inheritdoc cref="Workspace" />
    /// <param name="state">The workspace state.</param>
    /// <param name="query">The symbol argument.</param>
    internal static SymbolResolveResult Symbols(WorkspaceState state, string query) =>
        new(ToolStatus.For(state), query, Note: ToolStatus.NoteFor(state));

    /// <inheritdoc cref="Workspace" />
    /// <param name="state">The workspace state.</param>
    internal static TypeMembersResult TypeMembers(WorkspaceState state) =>
        new(ToolStatus.For(state), Note: ToolStatus.NoteFor(state));

    /// <inheritdoc cref="Workspace" />
    /// <param name="state">The workspace state.</param>
    /// <param name="symbol">The symbol argument.</param>
    internal static ReferencesResult References(WorkspaceState state, string symbol) =>
        new(ToolStatus.For(state), symbol, Note: ToolStatus.NoteFor(state));

    /// <inheritdoc cref="Workspace" />
    /// <param name="state">The workspace state.</param>
    /// <param name="scope">The requested scope.</param>
    internal static DiagnosticsResult Diagnostics(WorkspaceState state, string scope) =>
        new(ToolStatus.For(state), scope, Note: ToolStatus.NoteFor(state));

    /// <inheritdoc cref="Workspace" />
    /// <param name="state">The workspace state.</param>
    /// <param name="path">The file the actions were asked for.</param>
    internal static CodeActionsResult CodeActions(WorkspaceState state, string? path) =>
        new(ToolStatus.For(state), path, Note: ToolStatus.NoteFor(state));

    /// <inheritdoc cref="Workspace" />
    /// <param name="state">The workspace state.</param>
    internal static EditResult Edit(WorkspaceState state) =>
        new(ToolStatus.For(state), Applied: false, Note: ToolStatus.NoteFor(state));
}
