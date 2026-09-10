using System.ComponentModel;
using System.Globalization;

using ClaudeRoslynLsp.Mcp.Models;

using ModelContextProtocol.Server;

namespace ClaudeRoslynLsp.Mcp.Tools;

/// <summary>
/// The three read-only tools that answer questions about symbols: where one is, what a type
/// contains, and who uses it.
/// </summary>
/// <remarks>
/// <para>
/// These are the tools that exist to replace a grep. Claude Code's built-in <c>LSP</c> tool already
/// does hover, definition and implementation, but every one of its operations wants a file, a line
/// and a character — which a model does not have until it has searched for one, and searching for
/// one is exactly the text-tool habit this product is trying to break. So the pairing to teach is
/// <c>resolveSymbol</c> first, then the <c>LSP</c> tool from the position it reports.
/// </para>
/// <para>
/// <c>findReferences</c> is here rather than left to the <c>LSP</c> tool for two reasons the protocol
/// makes unavoidable: Roslyn duplicates results once per target framework of a multi-targeted project
/// (C24), and a bare list of positions is unreadable without the source line beside it.
/// </para>
/// </remarks>
[McpServerToolType]
internal sealed class SymbolTools
{
    /// <summary>The default and maximum number of matches <c>resolveSymbol</c> reports.</summary>
    private const int DefaultSymbolResults = 20;

    /// <summary>The default page size for <c>findReferences</c>.</summary>
    private const int DefaultReferenceResults = 100;

    private SymbolTools()
    {
    }

    /// <summary>Finds where a symbol is declared.</summary>
    /// <param name="context">The injected tool context.</param>
    /// <param name="symbol">The symbol to find.</param>
    /// <param name="kind">An optional kind filter.</param>
    /// <param name="maxResults">How many matches to report.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    [McpServerTool(
        Name = "resolveSymbol",
        Title = "Resolve symbol",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Finds where a C# symbol is declared, semantically, and reports 1-based line and column positions you can "
        + "pass straight to the LSP tool's hover, definition, implementation and callHierarchy operations. Use this "
        + "instead of grepping for a declaration: it understands partial classes, overloads, generics and multi-target "
        + "projects, and it never matches a comment or a string. The symbol may be a simple name (Calculator), a "
        + "partially qualified name (IScheduler.Start — matched as a dot-segment suffix), or a position you already "
        + "have (src/Core/Calculator.cs:7:15). Ambiguous names return every candidate rather than guessing.")]
    public static async Task<SymbolResolveResult> ResolveSymbolAsync(
        RoslynToolContext context,
        [Description("The symbol: a simple or partially qualified name (IScheduler.Start), or a position as path:line:col with 1-based numbers.")]
        string symbol,
        [Description("Only return symbols of this kind: class, interface, struct, enum, method, property, field, event, constructor, namespace.")]
        string? kind = null,
        [Description("How many matches to report. Default 20.")]
        int? maxResults = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await ToolErrors.ExecuteAsync("resolveSymbol", async () =>
        {
            var address = SymbolAddress.Parse(symbol);
            var state = await context.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

            if (!state.IsReady)
            {
                return NotReadyResults.Symbols(state, address.Text);
            }

            var found = await ToolLookup.FindAsync(context, address, kind, cancellationToken).ConfigureAwait(false);
            var cap = Math.Clamp(maxResults ?? DefaultSymbolResults, 1, 500);

            var matches = new List<SymbolMatch>(Math.Min(found.Count, cap));

            foreach (var candidate in found.Take(cap))
            {
                var signature = candidate.Match.Signature
                    ?? await context.Engine
                        .HoverAsync(candidate.Uri, candidate.Position, cancellationToken)
                        .ConfigureAwait(false);

                matches.Add(candidate.Match with { Signature = signature });
            }

            return new SymbolResolveResult(
                ToolStatus.Ok,
                address.Text,
                found.Count,
                matches,
                found.Count > matches.Count,
                found.Count == 0
                    ? "Nothing matched. Try the simple name on its own, check the spelling, or call getWorkspaceStatus "
                        + "— a project that failed to load contributes no symbols."
                    : null);
        }).ConfigureAwait(false);
    }

    /// <summary>Lists a type's members.</summary>
    /// <param name="context">The injected tool context.</param>
    /// <param name="type">The type to list.</param>
    /// <param name="maxResults">How many members to report.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    [McpServerTool(
        Name = "getTypeMembers",
        Title = "Get type members",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Lists the members a C# type declares — methods, properties, fields, events, nested types — with their "
        + "signatures and 1-based positions, without reading the file. Use it to learn a type's shape before calling "
        + "it, instead of reading the whole source file into context. Only the members declared in the file the type "
        + "is resolved to are listed: a partial class declared in two files reports the half you addressed, so pass a "
        + "path:line:col to pick which one.")]
    public static async Task<TypeMembersResult> GetTypeMembersAsync(
        RoslynToolContext context,
        [Description("The type: a simple or partially qualified name (Quartz.IScheduler), or a position as path:line:col with 1-based numbers.")]
        string type,
        [Description("How many members to report. Default 200.")]
        int? maxResults = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await ToolErrors.ExecuteAsync("getTypeMembers", async () =>
        {
            var address = SymbolAddress.Parse(type);
            var state = await context.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

            if (!state.IsReady)
            {
                return NotReadyResults.TypeMembers(state);
            }

            var resolved = await ToolLookup
                .FindOneAsync(context, "getTypeMembers", address, kind: null, cancellationToken)
                .ConfigureAwait(false);

            var symbols = await context.Engine
                .DocumentSymbolAsync(resolved.Uri, cancellationToken)
                .ConfigureAwait(false);

            var node = ToolLookup.InnermostAt(symbols, resolved.Position)
                ?? throw ToolErrors.NotFound(
                    "getTypeMembers",
                    $"type declaration at {resolved.Match.Path}:{resolved.Match.Line}:{resolved.Match.Column}");

            var children = node.Children ?? [];
            var cap = Math.Clamp(maxResults ?? 200, 1, 1000);

            var members = children
                .Take(cap)
                .Select(child => new TypeMember(
                    child.Name,
                    ToolLookup.KindName(child.Kind),
                    child.Detail,
                    child.SelectionRange.Start.OneBasedLine,
                    child.SelectionRange.Start.OneBasedColumn))
                .ToArray();

            return new TypeMembersResult(
                ToolStatus.Ok,
                resolved.Match,
                members,
                children.Length,
                children.Length > members.Length);
        }).ConfigureAwait(false);
    }

    /// <summary>Finds every use of a symbol.</summary>
    /// <param name="context">The injected tool context.</param>
    /// <param name="symbol">The symbol to search for.</param>
    /// <param name="includeDeclaration">Whether the declaration counts as a reference.</param>
    /// <param name="maxResults">The page size.</param>
    /// <param name="offset">Where the page starts.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    [McpServerTool(
        Name = "findReferences",
        Title = "Find references",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Finds every semantic reference to a C# symbol across the whole solution, with the source line beside each "
        + "one and a per-file summary. Use it instead of grepping for a name: it crosses projects, follows overrides "
        + "and interface implementations, ignores identically named symbols in other types, and never matches a "
        + "comment or a string literal. Results are de-duplicated across the target frameworks of a multi-targeted "
        + "project, and paged — read byFile first to decide whether you need every page.")]
    public static async Task<ReferencesResult> FindReferencesAsync(
        RoslynToolContext context,
        [Description("The symbol: a simple or partially qualified name (IScheduler.Start), or a position as path:line:col with 1-based numbers.")]
        string symbol,
        [Description("Include the declaration itself in the results. Default true.")]
        bool includeDeclaration = true,
        [Description("How many references to report in this page. Default 100.")]
        int? maxResults = null,
        [Description("How many references to skip, for paging. Default 0.")]
        int? offset = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await ToolErrors.ExecuteAsync("findReferences", async () =>
        {
            var address = SymbolAddress.Parse(symbol);
            var state = await context.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

            if (!state.IsReady)
            {
                return NotReadyResults.References(state, address.Text);
            }

            var resolved = await ToolLookup
                .FindOneAsync(context, "findReferences", address, kind: null, cancellationToken)
                .ConfigureAwait(false);

            var locations = await context.Engine
                .ReferencesAsync(resolved.Uri, resolved.Position, includeDeclaration, cancellationToken)
                .ConfigureAwait(false);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var all = new List<ReferenceMatch>(locations.Count);
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var location in locations)
            {
                if (!context.Guard.TryResolve(location.Uri, out var full, out _))
                {
                    continue;
                }

                var relative = context.Guard.ToRelative(full);

                // C24 again: the same reference arrives once per target framework.
                var key = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{relative}:{location.Range.Start.Line}:{location.Range.Start.Character}");

                if (!seen.Add(key))
                {
                    continue;
                }

                paths[relative] = full;

                all.Add(new ReferenceMatch(
                    relative,
                    location.Range.Start.OneBasedLine,
                    location.Range.Start.OneBasedColumn,
                    location.Range.End.OneBasedLine,
                    location.Range.End.OneBasedColumn));
            }

            all.Sort(static (left, right) =>
            {
                var byPath = string.CompareOrdinal(left.Path, right.Path);

                if (byPath != 0)
                {
                    return byPath;
                }

                var byLine = left.Line.CompareTo(right.Line);
                return byLine != 0 ? byLine : left.Column.CompareTo(right.Column);
            });

            var byFile = all
                .GroupBy(reference => reference.Path, StringComparer.Ordinal)
                .Select(group => new ReferenceFileSummary(group.Key, group.Count()))
                .OrderByDescending(summary => summary.Count)
                .ThenBy(summary => summary.Path, StringComparer.Ordinal)
                .ToArray();

            var skip = Math.Max(0, offset ?? 0);
            var take = Math.Clamp(maxResults ?? DefaultReferenceResults, 1, 1000);

            var page = all
                .Skip(skip)
                .Take(take)
                .Select(reference => reference with
                {
                    LineText = paths.TryGetValue(reference.Path, out var full)
                        ? ToolLookup.ReadLineText(full, reference.Line)
                        : null,
                })
                .ToArray();

            return new ReferencesResult(
                ToolStatus.Ok,
                address.Text,
                all.Count,
                skip,
                skip + page.Length < all.Count,
                byFile,
                page,
                all.Count == 0
                    ? "No references. For a public API that may be correct; for anything else, check that the symbol "
                        + "resolved to what you meant with resolveSymbol."
                    : null);
        }).ConfigureAwait(false);
    }
}
