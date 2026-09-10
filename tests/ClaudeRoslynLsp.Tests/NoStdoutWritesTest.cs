using System.Text;

using Xunit;

namespace ClaudeRoslynLsp.Tests;

/// <summary>
/// In both server modes stdout <em>is</em> the JSON-RPC channel - <c>Content-Length</c>-framed for
/// LSP, newline-delimited for MCP: one stray write corrupts the stream, and the client sees a server
/// that has silently stopped working. Only <c>Cli/</c>, which owns the non-protocol modes, may use
/// the console at all, and within it only <c>CliRuntime</c> actually does.
/// </summary>
/// <remarks>
/// <para>
/// The check is a source scan rather than a runtime assertion because the failure it guards against
/// is a line of code somebody adds later — probably a debugging <c>Console.WriteLine</c> — that no
/// functional test would fail on.
/// </para>
/// <para>
/// <b>Calibration.</b> A naive text search would fire on the phrase <c>Console.Write</c> inside a
/// comment (this codebase discusses the rule in several of them) and on
/// <c>logging.AddConsole(...)</c>, which is the correct way to log to stderr. So the scan first
/// strips comments, string literals and character literals, and only then looks for
/// <c>Console.</c> followed by a stdout-capable member. <c>AddConsole</c> is not matched because
/// the pattern requires a word boundary before <c>Console</c>. Two self-checks keep the test from
/// passing vacuously: the scan must find source files at all, and it must still find real usages
/// inside <c>Cli/</c>.
/// </para>
/// <para>
/// The known limitation of stripping literals is that a <c>Console.Write</c> hidden inside an
/// interpolation hole would be missed. That is not a way anyone writes to stdout by accident,
/// and the alternative — a scan that reports every comment discussing the rule — would be ignored
/// within a week.
/// </para>
/// </remarks>
public class NoStdoutWritesTest
{
    /// <summary>The only directory allowed to touch the console, relative to the repository root.</summary>
    private const string AllowedDirectory = "src/ClaudeRoslynLsp/Cli/";

    /// <summary>The file that identifies the repository root when walking up from the test assembly.</summary>
    private const string RootMarker = "claude-roslyn-lsp.slnx";

    /// <summary>
    /// Console members that can reach a standard stream. <c>Console.Error</c> is included even
    /// though stderr is safe: the rule is "the CLI owns the console", and a stderr write outside
    /// the CLI belongs in the logger, which already goes to stderr.
    /// </summary>
    private static readonly string[] ForbiddenMembers =
    [
        "Console.Write",
        "Console.Out",
        "Console.Error",
        "Console.In",
        "Console.OpenStandardOutput",
        "Console.OpenStandardError",
        "Console.SetOut",
        "Console.SetError",
    ];

    [Fact]
    public void OnlyTheCliWritesToTheConsole()
    {
        var root = FindRepositoryRoot();
        var sourceFiles = EnumerateProductionSources(root).ToList();

        // Self-check: a broken root lookup must fail loudly rather than scan nothing.
        Assert.True(sourceFiles.Count > 8, $"Expected to scan the whole product; found {sourceFiles.Count} files.");

        var offenders = new List<string>();
        var cliUsages = 0;

        foreach (var (relativePath, code) in sourceFiles)
        {
            var usages = CountConsoleUsages(code);

            if (usages == 0)
            {
                continue;
            }

            if (relativePath.StartsWith(AllowedDirectory, StringComparison.Ordinal))
            {
                cliUsages += usages;
                continue;
            }

            offenders.Add(relativePath);
        }

        Assert.True(
            offenders.Count == 0,
            "stdout is the protocol channel in both server modes; only " + AllowedDirectory + " may use the console. Offending files: "
            + string.Join(", ", offenders));

        // Self-check: the CLI does write to the console, so a detector that matches nothing is
        // broken rather than reassuring.
        Assert.True(cliUsages > 0, "The scan found no console usage even in " + AllowedDirectory + ".");
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RootMarker)))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find {RootMarker} above {AppContext.BaseDirectory}.");
    }

    /// <summary>Every hand-written server source file, as (repository-relative path, source text).</summary>
    private static IEnumerable<(string RelativePath, string Code)> EnumerateProductionSources(DirectoryInfo root)
    {
        var source = Path.Combine(root.FullName, "src");

        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root.FullName, file).Replace('\\', '/');

            // Generated output, not source: obj/ holds the JSON source generator's files and the
            // assembly-info the SDK writes.
            if (relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (relative, File.ReadAllText(file));
        }
    }

    private static int CountConsoleUsages(string code)
    {
        var stripped = StripCommentsAndLiterals(code);
        var usages = 0;

        foreach (var member in ForbiddenMembers)
        {
            var index = 0;

            while ((index = stripped.IndexOf(member, index, StringComparison.Ordinal)) >= 0)
            {
                // A word boundary before "Console" is what keeps AddConsole(...) out of the count.
                if (index == 0 || !IsIdentifierCharacter(stripped[index - 1]))
                {
                    usages++;
                }

                index += member.Length;
            }
        }

        return usages;
    }

    private static bool IsIdentifierCharacter(char value) => char.IsLetterOrDigit(value) || value is '_' or '.';

    /// <summary>
    /// Replaces comments, string literals and character literals with nothing, leaving the code
    /// around them intact. Handles line and block comments, raw string literals of any quote
    /// count, verbatim strings and ordinary escaped strings.
    /// </summary>
    private static string StripCommentsAndLiterals(string source)
    {
        var code = new StringBuilder(source.Length);
        var index = 0;

        while (index < source.Length)
        {
            var current = source[index];

            if (current == '/' && Peek(source, index + 1) == '/')
            {
                while (index < source.Length && source[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && Peek(source, index + 1) == '*')
            {
                index += 2;

                while (index + 1 < source.Length && !(source[index] == '*' && source[index + 1] == '/'))
                {
                    index++;
                }

                index = Math.Min(index + 2, source.Length);
                continue;
            }

            if (current == '"' && Peek(source, index + 1) == '"' && Peek(source, index + 2) == '"')
            {
                index = SkipRawString(source, index);
                continue;
            }

            if (current == '@' && Peek(source, index + 1) == '"')
            {
                index = SkipVerbatimString(source, index + 2);
                continue;
            }

            if (current is '"' or '\'')
            {
                index = SkipSimpleLiteral(source, index + 1, current);
                continue;
            }

            code.Append(current);
            index++;
        }

        return code.ToString();
    }

    private static char Peek(string source, int index) => index < source.Length ? source[index] : '\0';

    /// <summary>Skips a raw string literal, which ends at a quote run at least as long as its opener.</summary>
    private static int SkipRawString(string source, int index)
    {
        var opening = 0;

        while (index < source.Length && source[index] == '"')
        {
            opening++;
            index++;
        }

        while (index < source.Length)
        {
            if (source[index] != '"')
            {
                index++;
                continue;
            }

            var run = 0;

            while (index < source.Length && source[index] == '"')
            {
                run++;
                index++;
            }

            if (run >= opening)
            {
                break;
            }
        }

        return index;
    }

    /// <summary>Skips a verbatim string, in which the only escape is a doubled quote.</summary>
    private static int SkipVerbatimString(string source, int index)
    {
        while (index < source.Length)
        {
            if (source[index] != '"')
            {
                index++;
                continue;
            }

            if (Peek(source, index + 1) == '"')
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return index;
    }

    /// <summary>Skips an ordinary string or character literal, honouring backslash escapes.</summary>
    private static int SkipSimpleLiteral(string source, int index, char terminator)
    {
        while (index < source.Length)
        {
            var current = source[index];

            if (current == '\\')
            {
                index += 2;
                continue;
            }

            if (current == terminator)
            {
                return index + 1;
            }

            // An unterminated literal cannot span a line; bail rather than swallow the rest of the
            // file (which would hide every usage below it).
            if (current == '\n')
            {
                return index;
            }

            index++;
        }

        return index;
    }
}
