using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests;

/// <summary>
/// No C# source holds a raw control character: a literal that needs one writes its escape
/// (<c>'\0'</c>, <c>"\t"</c>, <c>'\u001B'</c>) instead.
/// </summary>
/// <remarks>
/// <para>
/// A raw NUL makes a file binary to git, which decides from its first 8,000 bytes: <c>git diff</c>
/// prints "Binary files differ", <c>git grep</c> prints "Binary file matches", and GitHub's pull
/// request view hides the file, so a change to it cannot be reviewed line by line. Four sources once
/// carried one — each the separator of a composite key, typed as the character rather than its escape
/// — and nothing noticed, because the compiled program is identical either way. One of the four sat
/// past the first 8,000 bytes, where git does not look and a reader still cannot see it, which is why
/// this scans the whole text rather than asking git what it thinks.
/// </para>
/// <para>
/// Tab is the one control character allowed inside a line; line feed and carriage return are what
/// separates lines, and which of them a file uses is <c>.gitattributes</c>' business.
/// </para>
/// </remarks>
public class NoControlCharactersTest
{
    /// <summary>The directories holding this repository's C# sources, relative to the root.</summary>
    private static readonly string[] SourceDirectories = ["src", "tests", "build"];

    [Test]
    public void NoSourceFileHoldsARawControlCharacter()
    {
        var files = SourceDirectories
            .SelectMany(directory => Directory.EnumerateFiles(RepositoryLayout.Path_(directory), "*.cs", SearchOption.AllDirectories))
            .Select(file => (Path: file, Relative: Path.GetRelativePath(RepositoryLayout.Root, file).Replace('\\', '/')))
            .Where(file => !file.Relative.Contains("/bin/", StringComparison.Ordinal) && !file.Relative.Contains("/obj/", StringComparison.Ordinal))
            .ToList();

        // Self-check: a broken root lookup must fail loudly rather than scan nothing.
        Assert.That(files.Count > 100, Is.True, $"Expected to scan every C# source; found {files.Count} files.");

        var offenders = new List<string>();

        foreach (var (path, relative) in files)
        {
            var lines = File.ReadAllLines(path);

            for (var line = 0; line < lines.Length; line++)
            {
                for (var column = 0; column < lines[line].Length; column++)
                {
                    var character = lines[line][column];
                    if (char.IsControl(character) && character != '\t')
                    {
                        offenders.Add($"{relative}:{line + 1}:{column + 1} holds U+{(int)character:X4}");
                    }
                }
            }
        }

        Assert.That(
            offenders.Count == 0, Is.True,
            "A raw control character in a source file makes git and GitHub treat the file as binary, so "
            + "its changes cannot be reviewed; write the escape instead ('\\0', '\\u001B'):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
