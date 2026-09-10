using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Mcp.Models;

using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ClaudeRoslynLsp.Mcp.Tools;

/// <summary>
/// Listing Roslyn's quick fixes and refactorings, applying one, and applying one everywhere.
/// </summary>
/// <remarks>
/// This is where the product earns its keep on a large change. <c>fixDiagnostics</c> in particular
/// exists because the alternative — a model editing the same warning out of forty files by hand — is
/// forty chances to get one of them wrong, and Roslyn will do all forty in one request with the
/// compiler's own understanding of what the code means.
/// </remarks>
[McpServerToolType]
internal sealed class CodeActionTools
{
    private CodeActionTools()
    {
    }

    /// <summary>Lists the code actions Roslyn offers at a position.</summary>
    /// <param name="context">The injected tool context.</param>
    /// <param name="path">The file.</param>
    /// <param name="line">The 1-based line the selection starts on.</param>
    /// <param name="col">The 1-based column it starts at.</param>
    /// <param name="endLine">The 1-based line the selection ends on.</param>
    /// <param name="endCol">The 1-based column it ends at.</param>
    /// <param name="kind">An optional kind filter.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    [McpServerTool(
        Name = "getCodeActions",
        Title = "Get code actions",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Lists the quick fixes and refactorings Roslyn offers at a position — the same list an IDE's lightbulb shows: "
        + "remove unnecessary usings, make a member static, use an expression body, extract an interface, move a type "
        + "to its own file, and every fix for the diagnostics reported there. Each entry has a short id to pass to "
        + "applyCodeAction, and fixAllScopes tells you whether the same fix can be applied across the document, the "
        + "project or the solution in one call. Positions are 1-based; omit endLine and endCol for a caret position "
        + "rather than a selection.")]
    public static async Task<CodeActionsResult> GetCodeActionsAsync(
        RoslynToolContext context,
        [Description("The file, workspace-relative with forward slashes.")]
        string path,
        [Description("The 1-based line the selection starts on.")]
        int line,
        [Description("The 1-based column the selection starts at.")]
        int col,
        [Description("The 1-based line the selection ends on. Defaults to line.")]
        int? endLine = null,
        [Description("The 1-based column the selection ends at. Defaults to col.")]
        int? endCol = null,
        [Description("Only return actions of this kind: quickfix, refactor, or a prefix like refactor.extract.")]
        string? kind = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await ToolErrors.ExecuteAsync("getCodeActions", async () =>
        {
            var state = await context.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

            if (!state.IsReady)
            {
                return NotReadyResults.CodeActions(state, path);
            }

            var full = context.Guard.FromModelPath(path);

            var catalogue = await ListAsync(
                context,
                "getCodeActions",
                full,
                Range(line, col, endLine, endCol),
                cancellationToken).ConfigureAwait(false);

            var filtered = kind is null
                ? catalogue
                : [.. catalogue.Where(action =>
                    action.Kind is not null && action.Kind.StartsWith(kind, StringComparison.OrdinalIgnoreCase))];

            return new CodeActionsResult(
                ToolStatus.Ok,
                context.Guard.ToRelative(full),
                line,
                col,
                [.. filtered.Select(ToEntry)],
                filtered.Count == 0
                    ? "Roslyn offers nothing here. Point at the identifier itself rather than at whitespace, or call "
                        + "getDiagnostics on the file to see what there is to fix."
                    : null);
        }).ConfigureAwait(false);
    }

    /// <summary>Applies one code action, optionally everywhere it applies.</summary>
    /// <param name="context">The injected tool context.</param>
    /// <param name="path">The file.</param>
    /// <param name="line">The 1-based line.</param>
    /// <param name="col">The 1-based column.</param>
    /// <param name="id">The action id from <c>getCodeActions</c>.</param>
    /// <param name="title">The action title, as an alternative to the id.</param>
    /// <param name="endLine">The 1-based line the selection ends on.</param>
    /// <param name="endCol">The 1-based column it ends at.</param>
    /// <param name="fixAllScope">How far to apply it.</param>
    /// <param name="preview">Whether to write anything.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    [McpServerTool(
        Name = "applyCodeAction",
        Title = "Apply code action",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Applies one of the code actions getCodeActions listed, writing the result to disk. Identify the action by "
        + "the id getCodeActions reported, or by its exact title. With fixAllScope set to document, project or "
        + "solution the same fix is applied everywhere it applies in that scope, in one semantic operation — which is "
        + "what makes it safe where forty hand edits are not. Some actions create, rename or delete files (Move type "
        + "to X.cs does all three), so the result lists every file it touched. Pass preview: true first to see the "
        + "diff without writing anything; the apply that follows reuses exactly the edit you previewed.")]
    public static async Task<EditResult> ApplyCodeActionAsync(
        RoslynToolContext context,
        [Description("The file, workspace-relative with forward slashes.")]
        string path,
        [Description("The 1-based line the selection starts on — the same position you passed to getCodeActions.")]
        int line,
        [Description("The 1-based column the selection starts at.")]
        int col,
        [Description("The action's id, as getCodeActions reported it. Preferred over title.")]
        string? id = null,
        [Description("The action's exact title, e.g. \"Remove unnecessary usings\". Used when id is not given, or when the list has moved on.")]
        string? title = null,
        [Description("The 1-based line the selection ends on. Defaults to line.")]
        int? endLine = null,
        [Description("The 1-based column the selection ends at. Defaults to col.")]
        int? endCol = null,
        [Description("Apply the fix everywhere in this scope: document, project or solution. Only for actions whose fixAllScopes list it.")]
        string? fixAllScope = null,
        [Description("Report the diff without writing anything. Default false.")]
        bool preview = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await ToolErrors.ExecuteAsync("applyCodeAction", async () =>
        {
            var state = await context.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

            if (!state.IsReady)
            {
                return NotReadyResults.Edit(state);
            }

            var full = context.Guard.FromModelPath(path);

            var key = Edits.EditCache.KeyFor(
                "applyCodeAction",
                context.Guard.ToRelative(full),
                Text(line),
                Text(col),
                Text(endLine),
                Text(endCol),
                id,
                title,
                fixAllScope);

            if (!preview && context.Cache.TryTake(key) is { } cached)
            {
                return await context
                    .ApplyEditAsync("applyCodeAction", key, cached, preview: false, cancellationToken)
                    .ConfigureAwait(false);
            }

            var range = Range(line, col, endLine, endCol);

            // Re-listed at apply time rather than remembered from the getCodeActions call: the file
            // may have changed since, and an action resolved against a stale list would edit the
            // wrong range with total confidence.
            var catalogue = await ListAsync(context, "applyCodeAction", full, range, cancellationToken)
                .ConfigureAwait(false);

            if (!CodeActionCatalog.TryFind(catalogue, id, title, out var action))
            {
                throw NoSuchAction(catalogue, id, title, context.Guard.ToRelative(full), line, col);
            }

            var edit = await ResolveAsync(context, action, fixAllScope, cancellationToken).ConfigureAwait(false);

            return await context.ApplyEditAsync("applyCodeAction", key, edit, preview, cancellationToken)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Applies the fix for one diagnostic id everywhere in a scope.</summary>
    /// <param name="context">The injected tool context.</param>
    /// <param name="diagnosticId">The diagnostic to fix.</param>
    /// <param name="scope">file, project or solution.</param>
    /// <param name="path">The file, for file scope or to infer the project.</param>
    /// <param name="project">The project, for project scope.</param>
    /// <param name="preview">Whether to write anything.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    [McpServerTool(
        Name = "fixDiagnostics",
        Title = "Fix diagnostics",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Fixes every occurrence of one diagnostic id — IDE0005, CA1822, CS0168 — across a file, a project or the "
        + "whole solution, using Roslyn's own fix-all. This is the tool for cleaning up warnings at scale: it finds a "
        + "site of the diagnostic, asks Roslyn for the fix, and applies it everywhere in the scope in one semantic "
        + "operation, instead of you editing each file. Not every diagnostic has a fix-all (some have no automatic "
        + "fix at all) — the error says so and names what was offered. Pass preview: true first to see the diff.")]
    public static async Task<EditResult> FixDiagnosticsAsync(
        RoslynToolContext context,
        [Description("The diagnostic id to fix, e.g. IDE0005 or CA1822.")]
        string diagnosticId,
        [Description("How far to fix: file (default, needs path), project (needs project, or path to infer it), or solution.")]
        string? scope = null,
        [Description("The file, workspace-relative with forward slashes. Required for scope: file.")]
        string? path = null,
        [Description("The project, by name or by workspace-relative .csproj path. Required for scope: project unless path is given.")]
        string? project = null,
        [Description("Report the diff without writing anything. Default false.")]
        bool preview = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await ToolErrors.ExecuteAsync("fixDiagnostics", async () =>
        {
            var state = await context.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

            if (!state.IsReady)
            {
                return NotReadyResults.Edit(state);
            }

            var id = (diagnosticId ?? string.Empty).Trim();

            if (id.Length == 0)
            {
                throw new ArgumentException("a diagnostic id is required", nameof(diagnosticId));
            }

            var requested = DiagnosticQuery.ParseScope(scope);

            var key = Edits.EditCache.KeyFor(
                "fixDiagnostics",
                id,
                requested.ToString(),
                path,
                project);

            if (!preview && context.Cache.TryTake(key) is { } cached)
            {
                return await context
                    .ApplyEditAsync("fixDiagnostics", key, cached, preview: false, cancellationToken)
                    .ConfigureAwait(false);
            }

            var site = await FindSiteAsync(context, id, requested, path, project, cancellationToken)
                .ConfigureAwait(false);

            var range = LspRange.FromOneBased(site.Line, site.Column, site.EndLine, site.EndColumn);
            var full = context.Guard.FromModelPath(site.Path);

            var catalogue = await ListAsync(context, "fixDiagnostics", full, range, cancellationToken)
                .ConfigureAwait(false);

            var action = CodeActionCatalog.Flatten(catalogue)
                    .FirstOrDefault(candidate =>
                        candidate.FixAllScopes.Count > 0
                        && candidate.DiagnosticIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                ?? CodeActionCatalog.Flatten(catalogue)
                    .FirstOrDefault(candidate => candidate.FixAllScopes.Count > 0)
                ?? throw new McpException(
                    $"fixDiagnostics found {id} at {site.Path}:{site.Line}:{site.Column}, but Roslyn offers no fix-all "
                    + $"for it there. What it does offer: "
                    + string.Join(", ", catalogue.Select(candidate => "\"" + candidate.Title + "\""))
                    + ". Apply one of those with applyCodeAction, or fix it by hand.");

            var wanted = requested switch
            {
                DiagnosticScope.File => FixAllScope.Document,
                DiagnosticScope.Project => FixAllScope.Project,
                _ => FixAllScope.Solution,
            };

            if (!action.FixAllScopes.Contains(wanted))
            {
                throw new McpException(
                    $"fixDiagnostics cannot apply \"{action.Title}\" at {wanted} scope; Roslyn offers it for "
                    + string.Join(", ", action.FixAllScopes)
                    + ". Ask for one of those scopes instead.");
            }

            var edit = await context.Engine
                .ResolveFixAllAsync(action.Title, DataOrThrow(action), wanted, cancellationToken)
                .ConfigureAwait(false);

            return await context.ApplyEditAsync("fixDiagnostics", key, edit, preview, cancellationToken)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists and catalogues the code actions at one range, with the document open for the call.
    /// </summary>
    /// <param name="context">The tool context.</param>
    /// <param name="tool">The calling tool's MCP name.</param>
    /// <param name="fullPath">The absolute file path.</param>
    /// <param name="range">The zero-based range.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    private static async Task<IReadOnlyList<CatalogedCodeAction>> ListAsync(
        RoslynToolContext context,
        string tool,
        string fullPath,
        LspRange range,
        CancellationToken cancellationToken)
    {
        await using var session = await DocumentSession
            .OpenAsync(context, tool, fullPath, cancellationToken)
            .ConfigureAwait(false);

        // The context diagnostics are deliberately not supplied. Roslyn computes them itself for the
        // range, and a caller that had to pull diagnostics first just to ask for a fix would be
        // paying for the same analysis twice (S4 lists fixes with an empty context).
        var actions = await context.Engine
            .CodeActionsAsync(session.Uri, range, diagnostics: null, cancellationToken)
            .ConfigureAwait(false);

        return CodeActionCatalog.Build(actions);
    }

    private static async Task<WorkspaceEdit?> ResolveAsync(
        RoslynToolContext context,
        CatalogedCodeAction action,
        string? fixAllScope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fixAllScope))
        {
            var resolved = await context.Engine
                .ResolveCodeActionAsync(action.Action, cancellationToken)
                .ConfigureAwait(false);

            if (resolved.Edit is null && action.Nested.Count > 0)
            {
                throw new McpException(
                    $"\"{action.Title}\" is a group rather than a fix; it resolves to no edit. Its choices are "
                    + string.Join(", ", action.Nested.Select(child => $"\"{child.Title}\" (id {child.Id})"))
                    + ". Apply one of those.");
            }

            return resolved.Edit;
        }

        if (!Enum.TryParse<FixAllScope>(fixAllScope.Trim(), ignoreCase: true, out var scope))
        {
            throw new ArgumentException(
                $"fixAllScope must be document, project or solution, not '{fixAllScope}'",
                nameof(fixAllScope));
        }

        if (action.FixAllScopes.Count > 0 && !action.FixAllScopes.Contains(scope))
        {
            throw new McpException(
                $"\"{action.Title}\" cannot be applied at {scope} scope; Roslyn offers it for "
                + string.Join(", ", action.FixAllScopes)
                + ". Ask for one of those, or drop fixAllScope to apply it at this position only.");
        }

        // C20: the scope is mandatory and case-sensitive, and the plain action's own data is accepted
        // — which is why the catalogue folds the separate "Fix All: ..." entry away.
        return await context.Engine
            .ResolveFixAllAsync(action.Title, DataOrThrow(action), scope, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<DiagnosticEntry> FindSiteAsync(
        RoslynToolContext context,
        string id,
        DiagnosticScope scope,
        string? path,
        string? project,
        CancellationToken cancellationToken)
    {
        // The site exists only to obtain the action; how far the fix reaches travels separately, in
        // the scope handed to codeAction/resolveFixAll. So it is looked for in the cheapest place
        // that can answer: a file the caller named is a *document* pull, which always sees analyzer
        // diagnostics, where a workspace pull sees them only once the analyzer scope has been raised
        // and the full-solution analyzer pass has finished — which on a busy machine it may not have
        // (C14, C57). Falling back to the asked-for scope keeps the no-path case working.
        var narrowed = path is { Length: > 0 } ? DiagnosticScope.File : scope;

        if (await LookForAsync(context, id, narrowed, path, project, cancellationToken).ConfigureAwait(false)
            is { } site)
        {
            return site;
        }

        if (narrowed != scope
            && await LookForAsync(context, id, scope, path, project, cancellationToken).ConfigureAwait(false)
                is { } wider)
        {
            return wider;
        }

        throw ToolErrors.NotFound(
            "fixDiagnostics",
            $"occurrence of {id} in the {scope.ToString().ToLowerInvariant()} scope");
    }

    /// <summary>One diagnostic of an id in a scope, or <see langword="null"/> when there is none.</summary>
    private static async Task<DiagnosticEntry?> LookForAsync(
        RoslynToolContext context,
        string id,
        DiagnosticScope scope,
        string? path,
        string? project,
        CancellationToken cancellationToken)
    {
        var diagnostics = await DiagnosticTools.GetDiagnosticsAsync(
            context,
            scope.ToString().ToLowerInvariant(),
            path,
            project,
            minSeverity: "hint",
            includeAnalyzers: true,
            ids: [id],
            maxResults: 1,
            cancellationToken).ConfigureAwait(false);

        return diagnostics.Diagnostics is { Count: > 0 } found ? found[0] : null;
    }

    private static JsonElement DataOrThrow(CatalogedCodeAction action) =>
        action.Data
        ?? throw new McpException(
            $"\"{action.Title}\" carries no resolve data, so Roslyn cannot be asked to apply it. "
            + "Call getCodeActions again — the list is regenerated per request and this one is stale.");

    private static McpException NoSuchAction(
        IReadOnlyList<CatalogedCodeAction> catalogue,
        string? id,
        string? title,
        string relativePath,
        int line,
        int col)
    {
        var asked = id is { Length: > 0 } ? $"id '{id}'" : $"title '{title}'";

        if (catalogue.Count == 0)
        {
            return new McpException(
                $"applyCodeAction found no code actions at all at {relativePath}:{line}:{col}, so nothing matched "
                + $"{asked}. Point at the identifier itself, or call getDiagnostics on the file first.");
        }

        var options = CodeActionCatalog.Flatten(catalogue)
            .Select(action => $"  - {action.Id}  \"{action.Title}\""
                + (action.FixAllScopes.Count > 0
                    ? "  (fixAllScopes: " + string.Join(", ", action.FixAllScopes) + ")"
                    : string.Empty));

        return new McpException(
            $"applyCodeAction found nothing matching {asked} at {relativePath}:{line}:{col}. "
            + "The actions offered there right now are:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, options));
    }

    private static CodeActionEntry ToEntry(CatalogedCodeAction action) =>
        new(
            action.Id,
            action.Title,
            action.Kind,
            action.DiagnosticIds.Count > 0 ? action.DiagnosticIds : null,
            action.FixAllScopes.Count > 0
                ? [.. action.FixAllScopes.Select(scope => scope.ToString().ToLowerInvariant())]
                : null,
            action.Nested.Count > 0 ? [.. action.Nested.Select(ToEntry)] : null);

    private static LspRange Range(int line, int col, int? endLine, int? endCol) =>
        LspRange.FromOneBased(line, col, endLine ?? line, endCol ?? col);

    private static string? Text(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
