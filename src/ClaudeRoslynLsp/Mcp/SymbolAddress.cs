using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using ClaudeRoslynLsp.Mcp.Engine;

namespace ClaudeRoslynLsp.Mcp;

/// <summary>
/// How every tool addresses a symbol: by name, or by <c>path:line:col</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why both forms (D63).</b> The name form is the one that fixes the behaviour this project
/// exists to fix — a model that can write <c>resolveSymbol("Quartz.IScheduler.Start")</c> never has
/// to grep for a line number first, and a name survives an edit that moves the declaration down
/// three lines. The position form is what makes the tools compose with Claude Code's own <c>LSP</c>
/// tool, which reports positions and takes positions: an answer from one is an argument to the
/// other, and requiring a round trip through a name would be a translation step the model has to
/// perform and can get wrong.
/// </para>
/// <para>
/// <b>Positions are 1-based here and 0-based on the wire.</b> Every line and column a model reads or
/// writes is 1-based, because that is what every editor, compiler error and stack trace in the C#
/// world uses, and a model that has just read a build error should be able to paste its numbers in.
/// LSP counts from zero. This type is one of the two places the conversion happens; the other is the
/// result mapping.
/// </para>
/// <para>
/// <b>Parsing.</b> The position form is recognised by taking the last two colon-separated segments
/// and requiring both to be positive integers, which is what keeps <c>C:\src\Foo.cs:12:5</c> working
/// — a Windows drive letter is a colon too, and splitting from the left would make the drive the
/// path and the rest nonsense.
/// </para>
/// </remarks>
internal sealed record SymbolAddress
{
    private SymbolAddress(string text, string? path, int line, int column, string? name)
    {
        Text = text;
        Path = path;
        Line = line;
        Column = column;
        Name = name;
    }

    /// <summary>The argument exactly as the caller wrote it, for error messages.</summary>
    internal string Text { get; }

    /// <summary>The path, when this is a position address.</summary>
    internal string? Path { get; }

    /// <summary>The 1-based line, when this is a position address.</summary>
    internal int Line { get; }

    /// <summary>The 1-based column, when this is a position address.</summary>
    internal int Column { get; }

    /// <summary>The name, when this is a name address.</summary>
    internal string? Name { get; }

    /// <summary>Whether this addresses a position rather than a name.</summary>
    [MemberNotNullWhen(true, nameof(Path))]
    [MemberNotNullWhen(false, nameof(Name))]
    internal bool IsPosition => Path is not null;

    /// <summary>
    /// The last dot-separated segment of a name — the part <c>workspace/symbol</c> can actually
    /// search for, since it matches simple names rather than fully qualified ones.
    /// </summary>
    internal string SimpleName
    {
        get
        {
            if (Name is null)
            {
                return string.Empty;
            }

            var withoutArity = Name;
            var backtick = withoutArity.IndexOf('`', StringComparison.Ordinal);

            if (backtick > 0)
            {
                withoutArity = withoutArity[..backtick];
            }

            var lastDot = withoutArity.LastIndexOf('.');
            return lastDot >= 0 ? withoutArity[(lastDot + 1)..] : withoutArity;
        }
    }

    /// <summary>The zero-based LSP position this address names.</summary>
    internal LspPosition ToPosition() => LspPosition.FromOneBased(Line, Column);

    /// <summary>Parses an address, or explains why it will not.</summary>
    /// <param name="value">The <c>symbol</c> argument.</param>
    /// <param name="address">The parsed address.</param>
    /// <param name="reason">Why it was refused, when it was.</param>
    internal static bool TryParse(
        string? value,
        [NotNullWhen(true)] out SymbolAddress? address,
        [NotNullWhen(false)] out string? reason)
    {
        address = null;
        reason = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            reason = "no symbol was given";
            return false;
        }

        var text = value.Trim();
        var lastColon = text.LastIndexOf(':');

        if (lastColon > 0)
        {
            var secondColon = text.LastIndexOf(':', lastColon - 1);

            if (secondColon > 0
                && int.TryParse(text[(secondColon + 1)..lastColon], NumberStyles.None, CultureInfo.InvariantCulture, out var line)
                && int.TryParse(text[(lastColon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var column))
            {
                var path = text[..secondColon];

                if (path.Length == 0)
                {
                    reason = $"'{text}' looks like path:line:col but has no path";
                    return false;
                }

                if (line < 1 || column < 1)
                {
                    reason = $"'{text}' has a line or column below 1; both are 1-based";
                    return false;
                }

                address = new SymbolAddress(text, path, line, column, name: null);
                return true;
            }
        }

        address = new SymbolAddress(text, path: null, line: 0, column: 0, name: text);
        return true;
    }

    /// <summary>Parses an address, throwing the parser's own reason when it will not.</summary>
    /// <param name="value">The <c>symbol</c> argument.</param>
    internal static SymbolAddress Parse(string? value) =>
        TryParse(value, out var address, out var reason)
            ? address
            : throw new ArgumentException(reason, nameof(value));

    /// <summary>
    /// Whether a fully qualified candidate name satisfies this address.
    /// </summary>
    /// <remarks>
    /// A simple name matches any candidate whose own simple name is equal; a dotted name has to match
    /// as a whole dot-segment suffix, so <c>IScheduler.Start</c> matches
    /// <c>Quartz.IScheduler.Start</c> but <c>Scheduler.Start</c> does not. Suffix matching on segment
    /// boundaries is what lets a caller write as much of a name as it takes to be unambiguous and no
    /// more.
    /// </remarks>
    /// <param name="candidate">A fully qualified name.</param>
    internal bool MatchesName(string? candidate)
    {
        if (Name is null || string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        if (string.Equals(candidate, Name, StringComparison.Ordinal))
        {
            return true;
        }

        return candidate.EndsWith("." + Name, StringComparison.Ordinal);
    }
}
