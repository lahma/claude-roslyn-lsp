using System.Globalization;

using ClaudeRoslynLsp.Edits;
using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Mcp.Models;

namespace ClaudeRoslynLsp.Mcp.Tools;

/// <summary>
/// A symbol that has been located: everything a tool needs to ask Roslyn the next question, plus the
/// model-facing description of it.
/// </summary>
/// <param name="Uri">The document's <c>file:</c> URI.</param>
/// <param name="Position">The zero-based position to send.</param>
/// <param name="Match">The 1-based, workspace-relative description.</param>
internal sealed record ResolvedSymbol(string Uri, LspPosition Position, SymbolMatch Match);

/// <summary>
/// Turning a <c>symbol</c> argument into a place in a file, and the small translations every tool
/// shares.
/// </summary>
/// <remarks>
/// The name path goes through <c>workspace/symbol</c>, which matches <em>simple</em> names and
/// answers with a container string rather than a namespace — <c>in Circle (project Fixture.Core
/// (net10.0, netstandard2.0))</c> for a member, <c>project Fixture.Core (...)</c> for a type (S5). So
/// a dotted argument is matched by querying the last segment and requiring the container text to
/// name the segment before it. That is a heuristic and is documented as one; it is also the only
/// thing this protocol makes possible, and it is what makes <c>IScheduler.Start</c> mean something
/// different from <c>Trigger.Start</c>.
/// </remarks>
internal static class ToolLookup
{
    /// <summary>The container prefix Roslyn uses for a member's declaring type (S5).</summary>
    private const string MemberContainerPrefix = "in ";

    /// <summary>
    /// Finds every symbol an address could mean, most useful first.
    /// </summary>
    /// <param name="context">The tool context.</param>
    /// <param name="address">The parsed <c>symbol</c> argument.</param>
    /// <param name="kind">An optional kind filter, e.g. <c>class</c> or <c>method</c>.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    internal static async Task<IReadOnlyList<ResolvedSymbol>> FindAsync(
        RoslynToolContext context,
        SymbolAddress address,
        string? kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(address);

        return address.IsPosition
            ? [await AtPositionAsync(context, address, cancellationToken).ConfigureAwait(false)]
            : await ByNameAsync(context, address, kind, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the one symbol an address means, or fails with a message that lists what it matched.
    /// </summary>
    /// <param name="context">The tool context.</param>
    /// <param name="tool">The calling tool's MCP name, for the error text.</param>
    /// <param name="address">The parsed <c>symbol</c> argument.</param>
    /// <param name="kind">An optional kind filter.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    internal static async Task<ResolvedSymbol> FindOneAsync(
        RoslynToolContext context,
        string tool,
        SymbolAddress address,
        string? kind,
        CancellationToken cancellationToken)
    {
        var matches = await FindAsync(context, address, kind, cancellationToken).ConfigureAwait(false);

        return matches.Count switch
        {
            0 => throw ToolErrors.NotFound(tool, $"symbol named '{address.Text}'"),
            1 => matches[0],
            _ => throw ToolErrors.Ambiguous(
                tool,
                $"'{address.Text}'",
                matches.Select(match => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{match.Match.FullName} ({match.Match.Kind}) at {match.Match.Path}:{match.Match.Line}:{match.Match.Column}"))),
        };
    }

    private static async Task<ResolvedSymbol> AtPositionAsync(
        RoslynToolContext context,
        SymbolAddress address,
        CancellationToken cancellationToken)
    {
        var path = context.Guard.FromModelPath(address.Path);

        if (!File.Exists(path))
        {
            throw ToolErrors.NotFound("resolveSymbol", $"file at '{address.Path}'");
        }

        var uri = WorkspacePathGuard.ToUri(path);
        var position = address.ToPosition();
        var relative = context.Guard.ToRelative(path);

        var symbols = await context.Engine.DocumentSymbolAsync(uri, cancellationToken).ConfigureAwait(false);
        var node = InnermostAt(symbols, position);

        if (node is not null)
        {
            return new ResolvedSymbol(
                uri,
                node.SelectionRange.Start,
                new SymbolMatch(
                    node.Name,
                    node.Name,
                    KindName(node.Kind),
                    relative,
                    node.SelectionRange.Start.OneBasedLine,
                    node.SelectionRange.Start.OneBasedColumn,
                    node.SelectionRange.End.OneBasedLine,
                    node.SelectionRange.End.OneBasedColumn,
                    Signature: node.Detail));
        }

        // A local variable, a parameter or a lambda has no documentSymbol node, and a position
        // address must still work for it — that is most of what a caller coming from the LSP tool is
        // pointing at. The name is read off the line, which is textual and therefore always available.
        var name = IdentifierAt(path, address.Line, address.Column);

        return new ResolvedSymbol(
            uri,
            position,
            new SymbolMatch(
                name,
                name,
                "unknown",
                relative,
                address.Line,
                address.Column,
                address.Line,
                address.Column + Math.Max(name.Length, 1)));
    }

    private static async Task<IReadOnlyList<ResolvedSymbol>> ByNameAsync(
        RoslynToolContext context,
        SymbolAddress address,
        string? kind,
        CancellationToken cancellationToken)
    {
        var simple = address.SimpleName;
        var symbols = await context.Engine.WorkspaceSymbolAsync(simple, cancellationToken).ConfigureAwait(false);

        var parent = ParentSegment(address.Name!);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<ResolvedSymbol>();

        foreach (var symbol in symbols)
        {
            // workspace/symbol is a fuzzy search: asking for Circle also offers CircleBuffer.
            if (!string.Equals(symbol.Name, simple, StringComparison.Ordinal))
            {
                continue;
            }

            if (parent is not null && !ContainerNames(symbol.ContainerName, parent))
            {
                continue;
            }

            if (kind is not null && !string.Equals(KindName(symbol.Kind), kind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (context.RelativeOrNull(symbol.Location.Uri) is not { } relative)
            {
                // Outside the workspace: a decompiled metadata source (C25), which nothing here can
                // navigate to usefully and nothing here may write to.
                continue;
            }

            // C24: a multi-targeted project answers once per TFM for some requests. De-duplicate by
            // where the symbol actually is, which is the only thing that differs between them.
            var key = string.Create(
                CultureInfo.InvariantCulture,
                $"{relative}:{symbol.Location.Range.Start.Line}:{symbol.Location.Range.Start.Character}");

            if (!seen.Add(key))
            {
                continue;
            }

            matches.Add(new ResolvedSymbol(
                symbol.Location.Uri,
                symbol.Location.Range.Start,
                new SymbolMatch(
                    symbol.Name,
                    FullNameOf(symbol),
                    KindName(symbol.Kind),
                    relative,
                    symbol.Location.Range.Start.OneBasedLine,
                    symbol.Location.Range.Start.OneBasedColumn,
                    symbol.Location.Range.End.OneBasedLine,
                    symbol.Location.Range.End.OneBasedColumn,
                    symbol.ContainerName)));
        }

        return matches;
    }

    /// <summary>
    /// The innermost document symbol whose whole range contains a position, or <see langword="null"/>
    /// when none does.
    /// </summary>
    /// <param name="symbols">The document's symbol tree.</param>
    /// <param name="position">The zero-based position.</param>
    internal static DocumentSymbolNode? InnermostAt(IReadOnlyList<DocumentSymbolNode> symbols, LspPosition position)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(position);

        DocumentSymbolNode? best = null;

        foreach (var symbol in symbols)
        {
            if (!Contains(symbol.Range, position))
            {
                continue;
            }

            best = symbol;

            if (symbol.Children is { Length: > 0 } children && InnermostAt(children, position) is { } deeper)
            {
                best = deeper;
            }

            break;
        }

        return best;
    }

    /// <summary>Whether a range contains a position, with the end treated as inclusive.</summary>
    /// <param name="range">The range.</param>
    /// <param name="position">The position.</param>
    internal static bool Contains(LspRange range, LspPosition position)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(position);

        var afterStart = position.Line > range.Start.Line
            || (position.Line == range.Start.Line && position.Character >= range.Start.Character);

        var beforeEnd = position.Line < range.End.Line
            || (position.Line == range.End.Line && position.Character <= range.End.Character);

        return afterStart && beforeEnd;
    }

    /// <summary>
    /// The LSP <c>SymbolKind</c> number as the word a model reads and can filter on.
    /// </summary>
    /// <param name="kind">The wire number.</param>
    internal static string KindName(int kind) => kind switch
    {
        1 => "file",
        2 => "module",
        3 => "namespace",
        4 => "package",
        5 => "class",
        6 => "method",
        7 => "property",
        8 => "field",
        9 => "constructor",
        10 => "enum",
        11 => "interface",
        12 => "function",
        13 => "variable",
        14 => "constant",
        15 => "string",
        16 => "number",
        17 => "boolean",
        18 => "array",
        19 => "object",
        20 => "key",
        21 => "null",
        22 => "enumMember",
        23 => "struct",
        24 => "event",
        25 => "operator",
        26 => "typeParameter",
        _ => "unknown",
    };

    /// <summary>The LSP severity number as the word a model reads.</summary>
    /// <param name="severity">The wire number, 1-4.</param>
    internal static string SeverityName(int? severity) => severity switch
    {
        1 => "error",
        2 => "warning",
        3 => "information",
        4 => "hint",
        _ => "information",
    };

    /// <summary>The severity floor a <c>minSeverity</c> argument names, as an LSP number.</summary>
    /// <param name="minSeverity">error, warning, information or hint.</param>
    internal static int SeverityFloor(string? minSeverity) => minSeverity?.Trim().ToLowerInvariant() switch
    {
        "error" => 1,
        "warning" or null or "" => 2,
        "information" or "info" => 3,
        "hint" => 4,
        _ => throw new ArgumentException(
            $"minSeverity must be error, warning, information or hint, not '{minSeverity}'",
            nameof(minSeverity)),
    };

    /// <summary>
    /// The LSP tags on a diagnostic, with Roslyn's VS-private ones removed.
    /// </summary>
    /// <remarks>
    /// C16: the <c>tags</c> array mixes the specification's <c>Unnecessary</c> (1) and
    /// <c>Deprecated</c> (2) with values in the 2147483640-2147483645 range that mean something only
    /// inside Visual Studio. Forwarding those would put numbers with no meaning into a model's
    /// context.
    /// </remarks>
    /// <param name="tags">The wire tags.</param>
    internal static IReadOnlyList<string>? TagNames(int[]? tags)
    {
        if (tags is not { Length: > 0 })
        {
            return null;
        }

        var names = new List<string>(2);

        foreach (var tag in tags)
        {
            switch (tag)
            {
                case 1:
                    names.Add("unnecessary");
                    break;

                case 2:
                    names.Add("deprecated");
                    break;

                default:
                    break;
            }
        }

        return names.Count > 0 ? names : null;
    }

    /// <summary>
    /// One line of a file, trimmed, so a reference list is readable without opening anything.
    /// </summary>
    /// <param name="path">The absolute path.</param>
    /// <param name="oneBasedLine">The 1-based line number.</param>
    internal static string? ReadLineText(string path, int oneBasedLine)
    {
        try
        {
            var (text, _) = TextFileCodec.Read(path);
            var line = new TextOffsets(text).LineText(oneBasedLine - 1).Trim();
            return line.Length == 0 ? null : line;
        }
        catch (IOException)
        {
            // The line text is a convenience; a file that cannot be read right now must not turn a
            // successful search into a failure.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ParentSegment(string name)
    {
        var lastDot = name.LastIndexOf('.');

        if (lastDot <= 0)
        {
            return null;
        }

        var head = name[..lastDot];
        var previousDot = head.LastIndexOf('.');
        return previousDot >= 0 ? head[(previousDot + 1)..] : head;
    }

    private static bool ContainerNames(string? container, string parent) =>
        container is not null && container.Contains(parent, StringComparison.Ordinal);

    private static string FullNameOf(SymbolInformation symbol)
    {
        if (symbol.ContainerName is not { Length: > 0 } container)
        {
            return symbol.Name;
        }

        if (!container.StartsWith(MemberContainerPrefix, StringComparison.Ordinal))
        {
            return symbol.Name;
        }

        var declaring = container[MemberContainerPrefix.Length..];
        var space = declaring.IndexOf(' ', StringComparison.Ordinal);

        if (space > 0)
        {
            declaring = declaring[..space];
        }

        return declaring.Length > 0 ? declaring + "." + symbol.Name : symbol.Name;
    }

    private static string IdentifierAt(string path, int oneBasedLine, int oneBasedColumn)
    {
        var line = ReadLineTextRaw(path, oneBasedLine);

        if (line is null)
        {
            return string.Empty;
        }

        var index = Math.Clamp(oneBasedColumn - 1, 0, Math.Max(0, line.Length - 1));

        if (line.Length == 0 || !IsIdentifier(line[index]))
        {
            return string.Empty;
        }

        var start = index;

        while (start > 0 && IsIdentifier(line[start - 1]))
        {
            start--;
        }

        var end = index;

        while (end + 1 < line.Length && IsIdentifier(line[end + 1]))
        {
            end++;
        }

        return line[start..(end + 1)];
    }

    private static string? ReadLineTextRaw(string path, int oneBasedLine)
    {
        try
        {
            var (text, _) = TextFileCodec.Read(path);
            return new TextOffsets(text).LineText(oneBasedLine - 1);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsIdentifier(char character) => char.IsLetterOrDigit(character) || character == '_';
}
