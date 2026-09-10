using ClaudeRoslynLsp.Mcp.Engine;

namespace ClaudeRoslynLsp.Edits;

/// <summary>
/// Turns LSP positions into offsets in a decoded string.
/// </summary>
/// <remarks>
/// <para>
/// A position is a line number plus a count of <b>UTF-16 code units</b> within that line, because
/// the adapter pins <c>general.positionEncodings: ["utf-16"]</c> (D14). A .NET <see cref="string"/>
/// <em>is</em> a sequence of UTF-16 code units, so the character number is a plain index into the
/// line and an astral character — an emoji, a rare CJK ideograph — occupies two of them on both
/// sides of the wire. That agreement is the whole reason to pin the encoding: under <c>utf-8</c> or
/// <c>utf-32</c> every column would need converting, and the conversion is exactly the kind of thing
/// that is right for ASCII and silently wrong for everything else.
/// </para>
/// <para>
/// Line starts are found by scanning for <c>\r\n</c>, a lone <c>\n</c> and a lone <c>\r</c>, all
/// three of which LSP counts as line terminators, so a file with mixed endings is indexed the way
/// Roslyn indexed it.
/// </para>
/// </remarks>
internal sealed class TextOffsets
{
    private readonly string _text;
    private readonly int[] _lineStarts;

    /// <summary>Indexes one document's text.</summary>
    /// <param name="text">The decoded text.</param>
    internal TextOffsets(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        _text = text;

        var starts = new List<int>(Math.Max(16, text.Length / 40)) { 0 };

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (character == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                starts.Add(index + 1);
            }
            else if (character == '\n')
            {
                starts.Add(index + 1);
            }
        }

        _lineStarts = [.. starts];
    }

    /// <summary>How many lines the text has.</summary>
    internal int LineCount => _lineStarts.Length;

    /// <summary>
    /// The offset a position addresses, clamped into the text.
    /// </summary>
    /// <remarks>
    /// Clamping rather than throwing: LSP explicitly allows a character number past the end of a
    /// line, and a position past the end of the document is what an edit that appends to a file
    /// looks like.
    /// </remarks>
    /// <param name="position">The zero-based position.</param>
    internal int OffsetOf(LspPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        return OffsetOf(position.Line, position.Character);
    }

    /// <summary>The offset a zero-based line and character address, clamped into the text.</summary>
    /// <param name="line">Zero-based line.</param>
    /// <param name="character">Zero-based UTF-16 code unit offset within it.</param>
    internal int OffsetOf(int line, int character)
    {
        if (line < 0)
        {
            return 0;
        }

        if (line >= _lineStarts.Length)
        {
            return _text.Length;
        }

        var start = _lineStarts[line];
        var end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : _text.Length;

        if (character <= 0)
        {
            return start;
        }

        return Math.Min(start + character, end);
    }

    /// <summary>The text of one zero-based line, without its terminator.</summary>
    /// <param name="line">Zero-based line number.</param>
    internal string LineText(int line)
    {
        if (line < 0 || line >= _lineStarts.Length)
        {
            return string.Empty;
        }

        var start = _lineStarts[line];
        var end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : _text.Length;

        while (end > start && (_text[end - 1] == '\n' || _text[end - 1] == '\r'))
        {
            end--;
        }

        return _text[start..end];
    }
}
