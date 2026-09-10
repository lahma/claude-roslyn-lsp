namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// The subset of glob syntax LSP 3.17 defines for a watcher pattern: <c>**</c>, <c>*</c>, <c>?</c>
/// and <c>{a,b}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled rather than translated into a <see cref="System.Text.RegularExpressions.Regex"/>,
/// for two reasons that both bite in this specific place. The translation would have to escape
/// every regex metacharacter that is legal in a path — a solution directory called
/// <c>Foo (2)+Bar</c> is unusual but perfectly legal — and getting one of those wrong produces a
/// watcher that silently matches nothing, which is exactly the failure mode this bridge exists to
/// remove. And a regex built at run time is compiled at run time, which under Native AOT means the
/// interpreter, on a path that runs for every filesystem event in the workspace.
/// </para>
/// <para>
/// <b>Matching is segment-wise, and <c>**</c> spans zero or more segments.</b> Zero matters:
/// Roslyn's commonest pattern is <c>**/*.cs</c> and a file directly in the base directory has to
/// match it, or a project whose sources sit beside its <c>.csproj</c> gets no watcher at all.
/// </para>
/// <para>
/// <b>Comparison is case-insensitive everywhere.</b> Windows and the default macOS filesystem are
/// case-insensitive, Linux is not — and the cost of the two mistakes is not symmetric. Matching
/// <c>Program.CS</c> against <c>**/*.cs</c> on Linux tells Roslyn about a file it will then ignore,
/// which costs one wasted notification; missing it on Windows leaves the workspace stale, which is
/// the failure with no symptom.
/// </para>
/// <para>
/// Roslyn emits both <c>**/*{.cs,.razor,.cshtml}</c> and <c>**/*{.cs,.cshtml,.razor}</c> for the
/// same intent (C32), which is why the alternation branch exists at all and why the registration
/// tracker compares pattern <em>sets</em> rather than strings.
/// </para>
/// </remarks>
internal static class GlobMatcher
{
    /// <summary>How deep an alternation may nest before the pattern is treated as unmatchable.</summary>
    /// <remarks>
    /// A guard, not a feature. The patterns this sees come from Roslyn and are one level deep; a
    /// pathological one from somewhere else must not be able to turn a filesystem event into an
    /// exponential walk on the thread that delivers it.
    /// </remarks>
    private const int MaxAlternationDepth = 4;

    /// <summary>Whether a workspace-relative path matches a glob.</summary>
    /// <param name="pattern">The glob, with <c>/</c> separators.</param>
    /// <param name="relativePath">The path relative to the glob's base, with either separator.</param>
    internal static bool IsMatch(string pattern, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(relativePath);

        if (pattern.Length == 0)
        {
            return false;
        }

        var patternSegments = Split(pattern);
        var pathSegments = Split(relativePath);

        return MatchSegments(patternSegments, 0, pathSegments, 0);
    }

    /// <summary>Whether any of a watcher's patterns matches.</summary>
    /// <param name="patterns">The collapsed pattern set for one base directory.</param>
    /// <param name="relativePath">The path relative to that base.</param>
    internal static bool IsMatchAny(IReadOnlyList<string> patterns, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        for (var index = 0; index < patterns.Count; index++)
        {
            if (IsMatch(patterns[index], relativePath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a pattern can match anything below its base directory.</summary>
    /// <remarks>
    /// What decides <c>FileSystemWatcher.IncludeSubdirectories</c>. A watcher that recursed when it
    /// did not need to would put the whole of <c>node_modules</c> under observation for a pattern
    /// that only ever matches one file.
    /// </remarks>
    /// <param name="pattern">The glob.</param>
    internal static bool IsRecursive(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return pattern.Contains("**", StringComparison.Ordinal);
    }

    /// <summary>Splits a path on either separator, dropping the empty segments a leading slash makes.</summary>
    private static string[] Split(string value) =>
        value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Matches the remaining pattern segments against the remaining path segments.</summary>
    private static bool MatchSegments(string[] pattern, int patternIndex, string[] path, int pathIndex)
    {
        while (true)
        {
            if (patternIndex == pattern.Length)
            {
                return pathIndex == path.Length;
            }

            if (string.Equals(pattern[patternIndex], "**", StringComparison.Ordinal))
            {
                // Zero or more segments. The zero case is first because it is the common one:
                // `**/*.cs` has to match `Program.cs` sitting directly in the base directory.
                for (var skip = pathIndex; skip <= path.Length; skip++)
                {
                    if (MatchSegments(pattern, patternIndex + 1, path, skip))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (pathIndex == path.Length || !MatchSegment(pattern[patternIndex], path[pathIndex], 0))
            {
                return false;
            }

            patternIndex++;
            pathIndex++;
        }
    }

    /// <summary>Matches one path segment against one pattern segment.</summary>
    /// <param name="pattern">The pattern segment, which may contain <c>*</c>, <c>?</c> and <c>{a,b}</c>.</param>
    /// <param name="text">The path segment.</param>
    /// <param name="depth">How many alternations are already open, so a nested one cannot run away.</param>
    private static bool MatchSegment(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text, int depth)
    {
        while (true)
        {
            if (pattern.IsEmpty)
            {
                return text.IsEmpty;
            }

            switch (pattern[0])
            {
                case '*':
                    // A single star never crosses a separator, and there is none inside a segment,
                    // so this is "any run of characters, including none".
                    for (var take = 0; take <= text.Length; take++)
                    {
                        if (MatchSegment(pattern[1..], text[take..], depth))
                        {
                            return true;
                        }
                    }

                    return false;

                case '?':
                    if (text.IsEmpty)
                    {
                        return false;
                    }

                    pattern = pattern[1..];
                    text = text[1..];
                    continue;

                case '{':
                    return MatchAlternation(pattern, text, depth);

                default:
                    if (text.IsEmpty || !SameCharacter(pattern[0], text[0]))
                    {
                        return false;
                    }

                    pattern = pattern[1..];
                    text = text[1..];
                    continue;
            }
        }
    }

    /// <summary>Tries every branch of a <c>{a,b}</c> group against the text.</summary>
    private static bool MatchAlternation(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text, int depth)
    {
        var close = pattern.IndexOf('}');

        if (close < 0 || depth >= MaxAlternationDepth)
        {
            // An unbalanced brace is not an alternation, it is a literal brace in a file name.
            return !text.IsEmpty
                   && SameCharacter(pattern[0], text[0])
                   && MatchSegment(pattern[1..], text[1..], depth);
        }

        var body = pattern[1..close];
        var tail = pattern[(close + 1)..];

        while (true)
        {
            var comma = body.IndexOf(',');
            var branch = comma < 0 ? body : body[..comma];

            // Concatenated rather than matched in two steps, because a branch may itself end in a
            // wildcard whose extent only the tail can settle: `*{.cs,.razor}` against `A.razor`
            // needs the star to stop in the one place that lets `.razor` line up.
            if (MatchSegment(string.Concat(branch, tail), text, depth + 1))
            {
                return true;
            }

            if (comma < 0)
            {
                return false;
            }

            body = body[(comma + 1)..];
        }
    }

    /// <summary>Compares one character the way a filesystem would.</summary>
    private static bool SameCharacter(char pattern, char text) =>
        pattern == text || char.ToUpperInvariant(pattern) == char.ToUpperInvariant(text);
}
