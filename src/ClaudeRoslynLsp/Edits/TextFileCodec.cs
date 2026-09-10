using System.Text;

namespace ClaudeRoslynLsp.Edits;

/// <summary>How a file on disk is encoded and how it ends its lines.</summary>
/// <param name="Encoding">The byte encoding.</param>
/// <param name="HasByteOrderMark">Whether the file starts with a byte order mark.</param>
/// <param name="NewLine">The dominant line terminator, <c>\r\n</c> or <c>\n</c>.</param>
internal readonly record struct TextFileFormat(TextFileEncoding Encoding, bool HasByteOrderMark, string NewLine)
{
    /// <summary>
    /// What a file this server creates from nothing is written as: UTF-8 without a mark, which is
    /// what the .NET SDK's own templates emit and what every C# toolchain reads.
    /// </summary>
    internal static TextFileFormat CreatedFileDefault { get; } = new(TextFileEncoding.Utf8, false, "\n");
}

/// <summary>The byte encodings this server preserves.</summary>
internal enum TextFileEncoding
{
    /// <summary>UTF-8, with or without a mark.</summary>
    Utf8,

    /// <summary>UTF-16 little-endian.</summary>
    Utf16LittleEndian,

    /// <summary>UTF-16 big-endian.</summary>
    Utf16BigEndian,
}

/// <summary>
/// Reads a source file into a string and writes it back the way it was.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a component and not two calls to <see cref="File"/> (D61).</b> An LSP edit is a
/// range in a UTF-16 string; a file is bytes with a mark and a line-ending convention. A round trip
/// through <c>File.ReadAllText</c>/<c>File.WriteAllText</c> silently drops a UTF-8 byte order mark
/// and re-encodes a UTF-16 file as UTF-8, and the diff that produces touches every line of a file
/// the refactoring changed one word in. Worse, it is invisible: the code still compiles, and the
/// damage shows up in somebody's review as "why is this whole file modified".
/// </para>
/// <para>
/// Line endings get the same treatment for the same reason, plus one of Roslyn's own: a single
/// <c>newText</c> can mix <c>\r\n</c> and <c>\n</c> (C22), so normalising the <em>file</em> is not
/// enough — the incoming text has to be normalised to the file's convention before it is spliced in.
/// </para>
/// <para>
/// A file with no byte order mark is read as UTF-8. UTF-16 without a mark is not sniffed: C# sources
/// in that shape are vanishingly rare, and a heuristic that guessed wrong would corrupt a file
/// rather than refuse it.
/// </para>
/// </remarks>
internal static class TextFileCodec
{
    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LePreamble = [0xFF, 0xFE];
    private static readonly byte[] Utf16BePreamble = [0xFE, 0xFF];

    /// <summary>Reads a file and reports both its text and how to write it back.</summary>
    /// <param name="path">The file.</param>
    internal static (string Text, TextFileFormat Format) Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Decode(File.ReadAllBytes(path));
    }

    /// <summary>Decodes file bytes and reports how to encode them again.</summary>
    /// <param name="bytes">The file's contents.</param>
    internal static (string Text, TextFileFormat Format) Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var (encoding, hasMark, preambleLength) = DetectEncoding(bytes);

        var text = EncodingFor(encoding, byteOrderMark: false)
            .GetString(bytes, preambleLength, bytes.Length - preambleLength);

        return (text, new TextFileFormat(encoding, hasMark, DetectNewLine(text)));
    }

    /// <summary>Encodes text the way <paramref name="format"/> says the file was written.</summary>
    /// <param name="text">The text.</param>
    /// <param name="format">The file's format.</param>
    internal static byte[] Encode(string text, TextFileFormat format)
    {
        ArgumentNullException.ThrowIfNull(text);

        var encoding = EncodingFor(format.Encoding, format.HasByteOrderMark);
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(text);

        if (preamble.Length == 0)
        {
            return body;
        }

        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    /// <summary>
    /// Rewrites every line terminator in <paramref name="text"/> as <paramref name="newLine"/>.
    /// </summary>
    /// <remarks>
    /// Handles <c>\r\n</c>, a lone <c>\n</c> and a lone <c>\r</c>, in one pass, because C22 says a
    /// single replacement string really does arrive with two of them in it.
    /// </remarks>
    /// <param name="text">The text to normalise.</param>
    /// <param name="newLine">The terminator to use.</param>
    internal static string NormalizeNewLines(string text, string newLine)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(newLine);

        if (text.IndexOfAny(['\r', '\n']) < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            switch (character)
            {
                case '\r':
                    builder.Append(newLine);

                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index++;
                    }

                    break;

                case '\n':
                    builder.Append(newLine);
                    break;

                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The dominant line terminator of a piece of text: <c>\r\n</c> when at least as many lines end
    /// that way as end with a bare <c>\n</c>, otherwise <c>\n</c>.
    /// </summary>
    /// <remarks>
    /// "Dominant" rather than "the first one" because a file with mixed endings — which happens
    /// whenever somebody has edited it on two platforms — should keep the convention most of it uses,
    /// not the one that happens to be at the top. A file with no line ending at all reports
    /// <c>\n</c>: it has no convention to preserve, and LF is what this repository writes.
    /// </remarks>
    /// <param name="text">The text.</param>
    internal static string DetectNewLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var crlf = 0;
        var lf = 0;

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n')
            {
                continue;
            }

            if (index > 0 && text[index - 1] == '\r')
            {
                crlf++;
            }
            else
            {
                lf++;
            }
        }

        if (crlf == 0 && lf == 0)
        {
            return "\n";
        }

        return crlf >= lf ? "\r\n" : "\n";
    }

    private static (TextFileEncoding Encoding, bool HasMark, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (StartsWith(bytes, Utf8Preamble))
        {
            return (TextFileEncoding.Utf8, true, Utf8Preamble.Length);
        }

        if (StartsWith(bytes, Utf16LePreamble))
        {
            return (TextFileEncoding.Utf16LittleEndian, true, Utf16LePreamble.Length);
        }

        if (StartsWith(bytes, Utf16BePreamble))
        {
            return (TextFileEncoding.Utf16BigEndian, true, Utf16BePreamble.Length);
        }

        return (TextFileEncoding.Utf8, false, 0);
    }

    private static bool StartsWith(byte[] bytes, byte[] preamble)
    {
        if (bytes.Length < preamble.Length)
        {
            return false;
        }

        for (var index = 0; index < preamble.Length; index++)
        {
            if (bytes[index] != preamble[index])
            {
                return false;
            }
        }

        return true;
    }

    private static Encoding EncodingFor(TextFileEncoding encoding, bool byteOrderMark) => encoding switch
    {
        TextFileEncoding.Utf16LittleEndian => new UnicodeEncoding(bigEndian: false, byteOrderMark),
        TextFileEncoding.Utf16BigEndian => new UnicodeEncoding(bigEndian: true, byteOrderMark),

        // throwOnInvalidBytes is deliberately false: a source file with a broken byte sequence in a
        // comment is still a file this server has to be able to edit without destroying, and the
        // replacement character round-trips.
        _ => new UTF8Encoding(byteOrderMark),
    };
}
