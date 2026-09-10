using System.Globalization;

using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Mcp.Models;

namespace ClaudeRoslynLsp.Mcp.Tools;

/// <summary>Which documents a diagnostic query covers.</summary>
internal enum DiagnosticScope
{
    /// <summary>One file, which has to be opened for Roslyn to say anything about it (C13).</summary>
    File,

    /// <summary>Every file of one project.</summary>
    Project,

    /// <summary>Every file of the solution.</summary>
    Solution,
}

/// <summary>
/// Pulling diagnostics out of Roslyn and reducing them to what a model asked for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two completely different requests wear one name (D67).</b> A file's diagnostics come from
/// <c>textDocument/diagnostic</c>, which returns zero items for a file that is not open — whatever
/// the configured scope, every time (C13). So a file-scoped query opens the document with the bytes
/// on disk, pulls, and closes it again <em>unless it was already open</em>, because closing a
/// document the LSP half is mirroring would take it away from the editor session.
/// </para>
/// <para>
/// A project's or a solution's diagnostics come from <c>workspace/diagnostic</c>, which reports
/// closed files only under a <c>fullSolution</c> compiler scope (C14) — the expensive setting the
/// adapter deliberately does not run with by default (D48). So this raises the scope for the call.
/// The answer then needs three filters that are not options but corrections: it includes generated
/// <c>obj/**/*.cs</c> files and <c>.csproj</c> entries, and it lists a multi-targeted project once
/// per target framework (C15). All three are removed here, because none of them is something a model
/// asked about.
/// </para>
/// <para>
/// <b>It skips open documents.</b> That is C15 as well, and it is the one gap this design cannot
/// close from inside a single request: a solution-scoped pull says nothing about a file the LSP half
/// currently has open. The result's note says so rather than letting it look like a clean file.
/// </para>
/// </remarks>
internal static class DiagnosticQuery
{
    /// <summary>
    /// The caveat on every diagnostic answer. A design-time pass is not a build, and a model that
    /// treats it as one will ship code that does not compile.
    /// </summary>
    internal const string DesignTimeNote =
        "These are Roslyn design-time diagnostics, not a build. They are ~1s where a build is 10-60s and they are "
        + "what an IDE shows you — but they do not run source generators the way a build does, do not include "
        + "MSBuild errors, and do not run tests. Run `dotnet build` before claiming the solution compiles.";

    /// <summary>The extra caveat a workspace-scoped pull carries (C15).</summary>
    internal const string OpenDocumentsNote =
        " Files this server currently has open are omitted from a project or solution pull; ask for them with "
        + "scope: \"file\".";

    /// <summary>Parses the <c>scope</c> argument.</summary>
    /// <param name="scope">file, project or solution.</param>
    internal static DiagnosticScope ParseScope(string? scope) => scope?.Trim().ToLowerInvariant() switch
    {
        null or "" or "file" => DiagnosticScope.File,
        "project" => DiagnosticScope.Project,
        "solution" => DiagnosticScope.Solution,
        _ => throw new ArgumentException(
            $"scope must be file, project or solution, not '{scope}'",
            nameof(scope)),
    };

    /// <summary>
    /// Whether a diagnostic id is the compiler's rather than an analyzer's.
    /// </summary>
    /// <remarks>
    /// <c>CS</c> followed by digits, and nothing else. The compiler's ids are the ones that mean
    /// "this does not build"; IDE, CA, SA and everything else are style and quality rules whose
    /// volume would drown them (C16 — IDE0005 arrives as a Hint over the whole using block, CA1822 as
    /// Information on every method that could be static). So they are opt-in, per call.
    /// </remarks>
    /// <param name="id">The diagnostic id.</param>
    internal static bool IsCompilerDiagnostic(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (id.Length < 3 || id[0] != 'C' || id[1] != 'S')
        {
            return false;
        }

        for (var index = 2; index < id.Length; index++)
        {
            if (!char.IsAsciiDigit(id[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Runs one file's pull, opening and closing the document around it when it is not already open.
    /// </summary>
    /// <param name="context">The tool context.</param>
    /// <param name="path">The absolute file path.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    internal static async Task<IReadOnlyList<(string Path, RawDiagnostic Diagnostic)>> ForFileAsync(
        RoslynToolContext context,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(path);

        await using var session = await DocumentSession
            .OpenAsync(context, "getDiagnostics", path, cancellationToken)
            .ConfigureAwait(false);

        var report = await context.Engine.DocumentDiagnosticAsync(session.Uri, cancellationToken).ConfigureAwait(false);
        var relative = context.Guard.ToRelative(path);

        return [.. report.Items.Select(item => (relative, item))];
    }

    /// <summary>
    /// Runs a workspace-wide pull, raising the compiler scope first and applying C15's three
    /// corrections to the answer.
    /// </summary>
    /// <param name="context">The tool context.</param>
    /// <param name="projectDirectory">When set, only files under this directory are kept.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    internal static async Task<IReadOnlyList<(string Path, RawDiagnostic Diagnostic)>> ForWorkspaceAsync(
        RoslynToolContext context,
        string? projectDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        await context.Engine
            .SetCompilerDiagnosticsScopeAsync(CompilerDiagnosticsScope.FullSolution, cancellationToken)
            .ConfigureAwait(false);

        var report = await context.Engine.WorkspaceDiagnosticAsync([], cancellationToken).ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<(string Path, RawDiagnostic Diagnostic)>();

        foreach (var document in report.Items)
        {
            if (!context.Guard.TryResolve(document.Uri, out var full, out _))
            {
                continue;
            }

            var relative = context.Guard.ToRelative(full);

            if (!IsSourceFile(relative))
            {
                continue;
            }

            if (projectDirectory is not null
                && !full.StartsWith(projectDirectory, context.Guard.PathComparer == StringComparer.OrdinalIgnoreCase
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var diagnostic in document.Items)
            {
                // C15: a multi-targeted project is listed once per TFM, so the same diagnostic on the
                // same line arrives two or three times.
                var key = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{relative}:{diagnostic.Range.Start.Line}:{diagnostic.Range.Start.Character}:{diagnostic.CodeText}");

                if (seen.Add(key))
                {
                    results.Add((relative, diagnostic));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Reduces a pull to what the caller asked for, and reports what it counted on the way.
    /// </summary>
    /// <param name="pulled">Everything the pull returned.</param>
    /// <param name="minSeverity">The severity floor.</param>
    /// <param name="includeAnalyzers">Whether IDE/CA/SA rules are wanted alongside compiler errors.</param>
    /// <param name="ids">An optional list of diagnostic ids to keep.</param>
    /// <param name="maxResults">The cap.</param>
    internal static (IReadOnlyList<DiagnosticEntry> Entries, int TotalCount, DiagnosticCounts Counts) Reduce(
        IReadOnlyList<(string Path, RawDiagnostic Diagnostic)> pulled,
        string? minSeverity,
        bool includeAnalyzers,
        string[]? ids,
        int maxResults)
    {
        ArgumentNullException.ThrowIfNull(pulled);

        var floor = ToolLookup.SeverityFloor(minSeverity);

        var wanted = ids is { Length: > 0 }
            ? ids.Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        var kept = new List<DiagnosticEntry>();
        var error = 0;
        var warning = 0;
        var information = 0;
        var hint = 0;

        foreach (var (path, diagnostic) in pulled)
        {
            var id = diagnostic.CodeText;
            var severity = diagnostic.Severity ?? 3;

            if (severity > floor)
            {
                continue;
            }

            if (wanted is not null && !wanted.Contains(id))
            {
                continue;
            }

            if (!includeAnalyzers && wanted is null && !IsCompilerDiagnostic(id))
            {
                continue;
            }

            switch (severity)
            {
                case 1:
                    error++;
                    break;

                case 2:
                    warning++;
                    break;

                case 4:
                    hint++;
                    break;

                default:
                    information++;
                    break;
            }

            kept.Add(new DiagnosticEntry(
                id,
                ToolLookup.SeverityName(severity),
                diagnostic.Message,
                path,
                diagnostic.Range.Start.OneBasedLine,
                diagnostic.Range.Start.OneBasedColumn,
                diagnostic.Range.End.OneBasedLine,
                diagnostic.Range.End.OneBasedColumn,
                ToolLookup.TagNames(diagnostic.Tags),
                diagnostic.CodeDescription?.Href));
        }

        kept.Sort(static (left, right) =>
        {
            var bySeverity = SeverityRank(left.Severity).CompareTo(SeverityRank(right.Severity));

            if (bySeverity != 0)
            {
                return bySeverity;
            }

            var byPath = string.CompareOrdinal(left.Path, right.Path);

            if (byPath != 0)
            {
                return byPath;
            }

            var byLine = left.Line.CompareTo(right.Line);
            return byLine != 0 ? byLine : left.Column.CompareTo(right.Column);
        });

        var total = kept.Count;
        var page = kept.Count > maxResults ? kept.GetRange(0, maxResults) : kept;

        return (page, total, new DiagnosticCounts(error, warning, information, hint));
    }

    /// <summary>
    /// Whether a path the workspace pull reported is a source file rather than a project file or
    /// generated output (C15).
    /// </summary>
    /// <param name="relativePath">The workspace-relative, forward-slashed path.</param>
    internal static bool IsSourceFile(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        if (!relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !relativePath.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
            && !relativePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            && !relativePath.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
            && !relativePath.Contains("/bin/", StringComparison.OrdinalIgnoreCase);
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "error" => 0,
        "warning" => 1,
        "information" => 2,
        _ => 3,
    };
}
