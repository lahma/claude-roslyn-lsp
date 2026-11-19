using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeRoslynLsp.Edits;

/// <summary>
/// The two whole-file layout rules <c>formatCode</c> applies after Roslyn's own formatter.
/// </summary>
/// <param name="EndOfLine">The line terminator <c>end_of_line</c> asks for, or null when it says nothing.</param>
/// <param name="InsertFinalNewline">What <c>insert_final_newline</c> asks for, or null when it says nothing.</param>
internal readonly record struct TextLayout(string? EndOfLine, bool? InsertFinalNewline)
{
    /// <summary>Whether either rule applies at all.</summary>
    internal bool IsSet => EndOfLine is not null || InsertFinalNewline is not null;

    /// <summary>Rewrites a whole file's text to this layout.</summary>
    /// <param name="text">The text, as formatting left it.</param>
    /// <param name="fileNewLine">The file's own dominant terminator, used when <see cref="EndOfLine"/> is not set.</param>
    internal string Apply(string text, string fileNewLine)
    {
        ArgumentNullException.ThrowIfNull(text);

        var newLine = EndOfLine ?? fileNewLine;

        if (EndOfLine is not null)
        {
            text = TextFileCodec.NormalizeNewLines(text, newLine);
        }

        if (InsertFinalNewline is true && text.Length > 0 && !EndsWithNewLine(text))
        {
            text += newLine;
        }
        else if (InsertFinalNewline is false)
        {
            text = text.TrimEnd('\r', '\n');
        }

        return text;
    }

    private static bool EndsWithNewLine(string text) => text[^1] is '\n' or '\r';
}

/// <summary>
/// Reads the <c>.editorconfig</c> chain for one file, the way the EditorConfig specification
/// (editorconfig.org, "File Format" and "Glob Expressions") resolves it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists (D113, issue #4).</b> Roslyn's <c>textDocument/formatting</c> handles
/// indentation, spacing and wrapping, and never converts the line endings a file already has or adds
/// or removes its final newline — <c>dotnet format</c> applies <c>end_of_line</c> and
/// <c>insert_final_newline</c> with formatters of its own. A repository that gates CI on
/// <c>dotnet format --verify-no-changes</c> therefore still failed after <c>formatCode</c>, which
/// described itself as the replacement. The two properties are read here and applied by
/// <see cref="WorkspaceEditApplier"/>; everything else in the file is Roslyn's business.
/// </para>
/// <para>
/// <b>The rules.</b> Every <c>.editorconfig</c> from the file's directory up to the first one whose
/// preamble says <c>root = true</c>, applied from the farthest to the nearest and, within a file,
/// section by section, so the last match wins; <c>unset</c> removes a property. Keys and values are
/// case-insensitive. A section glob without a <c>/</c> matches the file name in any directory below
/// the <c>.editorconfig</c>; one with a <c>/</c> is relative to it, a leading <c>/</c> included.
/// <c>*</c>, <c>**</c>, <c>?</c>, <c>[seq]</c>, <c>[!seq]</c>, <c>{a,b}</c>, <c>{n1..n2}</c> and
/// <c>\</c> are supported; matching is case-sensitive, as Roslyn's own analyzer-config matching is,
/// so this agrees with what <c>dotnet format</c> decides for the same file.
/// </para>
/// <para>
/// A <see cref="Regex"/> per section, unlike <c>Adapter/GlobMatcher</c>: that matcher runs on every
/// file-system event and follows LSP's segment-wise syntax, this runs once per formatted file and
/// follows EditorConfig's, where <c>**</c> may sit inside a segment and a glob without a slash is
/// rooted anywhere. Every literal character goes through <see cref="Regex.Escape"/>.
/// </para>
/// </remarks>
internal static class EditorConfig
{
    /// <summary>The file name the specification looks for.</summary>
    internal const string FileName = ".editorconfig";

    /// <summary>The widest numeric range a <c>{n1..n2}</c> glob is expanded for.</summary>
    private const int MaxRange = 1000;

    /// <summary>The layout rules that apply to one file.</summary>
    /// <param name="path">The file.</param>
    internal static TextLayout LayoutFor(string path)
    {
        var properties = PropertiesFor(path);

        var endOfLine = properties.GetValueOrDefault("end_of_line") switch
        {
            "lf" => "\n",
            "crlf" => "\r\n",
            "cr" => "\r",
            _ => null,
        };

        var finalNewline = properties.GetValueOrDefault("insert_final_newline") switch
        {
            "true" => true,
            "false" => (bool?)false,
            _ => null,
        };

        return new TextLayout(endOfLine, finalNewline);
    }

    /// <summary>Every property the <c>.editorconfig</c> chain sets for one file.</summary>
    /// <param name="path">The file.</param>
    internal static IReadOnlyDictionary<string, string> PropertiesFor(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var full = Path.GetFullPath(path);
        var chain = new List<(string Directory, ParsedFile File)>();

        for (var directory = Path.GetDirectoryName(full); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (Read(Path.Combine(directory, FileName)) is not { } parsed)
            {
                continue;
            }

            chain.Add((directory, parsed));

            if (parsed.IsRoot)
            {
                break;
            }
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = chain.Count - 1; index >= 0; index--)
        {
            var (directory, file) = chain[index];
            var relative = Path.GetRelativePath(directory, full).Replace('\\', '/');

            foreach (var section in file.Sections)
            {
                if (!section.Matches(relative))
                {
                    continue;
                }

                foreach (var (key, value) in section.Properties)
                {
                    if (value == "unset")
                    {
                        result.Remove(key);
                    }
                    else
                    {
                        result[key] = value;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>Parses one <c>.editorconfig</c>, or null when there is none or it cannot be read.</summary>
    /// <remarks>
    /// An unreadable file is treated as absent rather than as a failure: it is somebody else's file,
    /// and formatting without its two layout rules is what every version before this one did.
    /// </remarks>
    private static ParsedFile? Read(string path)
    {
        string text;

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return Parse(text);
    }

    /// <summary>Parses the text of one <c>.editorconfig</c>.</summary>
    /// <param name="text">The file's text.</param>
    internal static ParsedFile Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var isRoot = false;
        var sections = new List<Section>();
        Section? current = null;

        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                current = new Section(line[1..^1]);
                sections.Add(current);
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);

            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim().ToLowerInvariant();
            var value = line[(equals + 1)..].Trim().ToLowerInvariant();

            if (current is null)
            {
                isRoot |= key == "root" && value == "true";
            }
            else
            {
                current.Properties.Add((key, value));
            }
        }

        return new ParsedFile(isRoot, sections);
    }

    /// <summary>One parsed <c>.editorconfig</c>.</summary>
    /// <param name="IsRoot">Whether its preamble stops the search.</param>
    /// <param name="Sections">Its sections, in file order.</param>
    internal sealed record ParsedFile(bool IsRoot, IReadOnlyList<Section> Sections);

    /// <summary>One <c>[glob]</c> section and the properties it sets, in file order.</summary>
    internal sealed class Section
    {
        private readonly Regex? _pattern;

        /// <summary>Creates a section from its glob.</summary>
        /// <param name="glob">The text between the brackets.</param>
        internal Section(string glob)
        {
            Glob = glob;
            _pattern = ToRegex(glob);
        }

        /// <summary>The glob as written.</summary>
        internal string Glob { get; }

        /// <summary>The properties, lower-cased, in file order.</summary>
        internal List<(string Key, string Value)> Properties { get; } = [];

        /// <summary>Whether the section applies to a path relative to its <c>.editorconfig</c>.</summary>
        /// <param name="relativePath">The path, with <c>/</c> separators.</param>
        internal bool Matches(string relativePath) => _pattern?.IsMatch(relativePath) == true;
    }

    /// <summary>
    /// Translates a section glob into an anchored regular expression, or null for a glob that cannot
    /// match anything (an unbalanced or oversized construct is treated as never matching).
    /// </summary>
    /// <param name="glob">The section glob.</param>
    internal static Regex? ToRegex(string glob)
    {
        ArgumentNullException.ThrowIfNull(glob);

        if (glob.Length == 0 || glob.Length > 4096)
        {
            return null;
        }

        // No slash: the file name, in any directory. A leading slash: anchored at this directory.
        var rooted = glob.Contains('/', StringComparison.Ordinal)
            ? glob.TrimStart('/')
            : "**/" + glob;

        var index = 0;
        var body = Sequence(rooted, ref index, inBraces: false);

        return body is null
            ? null
            : new Regex("^" + body + "$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>Translates glob characters until the end, or until a <c>,</c> or <c>}</c> inside braces.</summary>
    private static string? Sequence(string glob, ref int index, bool inBraces)
    {
        var builder = new StringBuilder();

        while (index < glob.Length)
        {
            var character = glob[index];

            if (inBraces && character is ',' or '}')
            {
                return builder.ToString();
            }

            switch (character)
            {
                case '\\' when index + 1 < glob.Length:
                    builder.Append(Regex.Escape(glob[index + 1].ToString()));
                    index += 2;
                    break;

                case '*' when index + 1 < glob.Length && glob[index + 1] == '*':
                    index += 2;

                    if (index < glob.Length && glob[index] == '/')
                    {
                        // `**/` is zero or more whole directories, so `**/*.cs` matches a file
                        // directly in the base directory too.
                        builder.Append("(?:.*/)?");
                        index++;
                    }
                    else
                    {
                        builder.Append(".*");
                    }

                    break;

                case '*':
                    builder.Append("[^/]*");
                    index++;
                    break;

                case '?':
                    builder.Append("[^/]");
                    index++;
                    break;

                case '[':
                    builder.Append(CharacterClass(glob, ref index));
                    break;

                case '{':
                    if (Braces(glob, ref index) is not { } braces)
                    {
                        return null;
                    }

                    builder.Append(braces);
                    break;

                default:
                    builder.Append(Regex.Escape(character.ToString()));
                    index++;
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary><c>[seq]</c> or <c>[!seq]</c>; an unclosed bracket is a literal <c>[</c>.</summary>
    private static string CharacterClass(string glob, ref int index)
    {
        var close = glob.IndexOf(']', index + 1);

        if (close < 0)
        {
            index++;
            return Regex.Escape("[");
        }

        var inner = glob[(index + 1)..close];
        index = close + 1;

        var negated = inner.StartsWith('!');
        var members = negated ? inner[1..] : inner;

        var escaped = new StringBuilder();

        foreach (var member in members)
        {
            escaped.Append(member is '\\' or '^' or '[' or ']' ? "\\" + member : member.ToString());
        }

        return negated ? "[^/" + escaped + "]" : "[" + escaped + "]";
    }

    /// <summary>
    /// <c>{a,b,c}</c>, <c>{n1..n2}</c>, or a literal brace pair when it holds a single item; null when
    /// the braces never close or a range is too wide to expand.
    /// </summary>
    private static string? Braces(string glob, ref int index)
    {
        var range = Regex.Match(glob[index..], @"^\{(-?\d+)\.\.(-?\d+)\}", RegexOptions.CultureInvariant);

        if (range.Success)
        {
            index += range.Length;

            var first = int.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture);
            var last = int.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture);
            var (low, high) = first <= last ? (first, last) : (last, first);

            if (high - low > MaxRange)
            {
                return null;
            }

            return "(?:" + string.Join('|', Enumerable.Range(low, high - low + 1)
                .Select(number => Regex.Escape(number.ToString(CultureInfo.InvariantCulture)))) + ")";
        }

        var start = index;
        index++;

        var alternatives = new List<string>();

        while (true)
        {
            var item = Sequence(glob, ref index, inBraces: true);

            if (item is null)
            {
                return null;
            }

            alternatives.Add(item);

            if (index >= glob.Length)
            {
                // Never closed: the brace is a literal, and the rest is read again as plain text.
                index = start + 1;
                return Regex.Escape("{");
            }

            if (glob[index] == '}')
            {
                index++;
                break;
            }

            index++; // the comma
        }

        return alternatives.Count == 1
            ? Regex.Escape("{") + alternatives[0] + Regex.Escape("}")
            : "(?:" + string.Join('|', alternatives) + ")";
    }
}
