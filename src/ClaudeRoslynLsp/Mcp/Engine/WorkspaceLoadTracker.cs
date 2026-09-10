using System.Globalization;

namespace ClaudeRoslynLsp.Mcp.Engine;

/// <summary>
/// Reads Roslyn's own narration of a solution load and turns it into the numbers
/// <c>getWorkspaceStatus</c> reports.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no request that asks Roslyn what it loaded.</b> The custom method list (C39) has
/// <c>solution/open</c> and <c>project/open</c> going in and
/// <c>workspace/projectInitializationComplete</c> coming back, and nothing in between that names a
/// project. What does name them is the narration: the work-done progress stream reports
/// <c>Loading N project(s)...</c> against its one token (C31), and
/// <c>LanguageServerProjectSystem</c> logs one <c>Successfully completed load of &lt;path&gt;</c> per
/// project and a <c>Failed to load</c> for each one it could not. So this parses both, and treats
/// every shape it does not recognise as absent rather than as an error — the wording is Roslyn's and
/// may change on a pin bump, and a status tool that threw because a log line was reworded would be
/// worse than one that reports a smaller number.
/// </para>
/// <para>
/// <b>The log lines only arrive if somebody asked for them.</b> The child runs at
/// <c>--logLevel Warning</c> by default (D37), where the per-project lines are simply not emitted —
/// so the project <em>list</em> is normally recovered from the solution file instead
/// (<see cref="WorkspaceProjects"/>), and this class's per-project set enriches it when
/// <c>CLAUDE_ROSLYN_LSP_ROSLYN_LOG_LEVEL=Information</c> is set. The progress stream is not logging
/// and arrives at every level, which is why the total comes from there.
/// </para>
/// </remarks>
internal sealed class WorkspaceLoadTracker
{
    private const string CompletedPrefix = "Successfully completed load of ";
    private const string FailedPrefix = "Failed to load ";
    private const string LoadingProjectsSuffix = " project(s)";
    private const string LoadingPrefix = "Loading ";

    private readonly Lock _lock = new();
    private readonly HashSet<string> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _errors = [];

    private int _reportedTotal;

    /// <summary>How many projects Roslyn said it was loading, or 0 when it never said.</summary>
    internal int ReportedTotal
    {
        get
        {
            lock (_lock)
            {
                return _reportedTotal;
            }
        }
    }

    /// <summary>The project files Roslyn reported as loaded, in no particular order.</summary>
    internal IReadOnlyList<string> LoadedProjects
    {
        get
        {
            lock (_lock)
            {
                return [.. _loaded];
            }
        }
    }

    /// <summary>What Roslyn said went wrong, verbatim and capped.</summary>
    internal IReadOnlyList<string> Errors
    {
        get
        {
            lock (_lock)
            {
                return [.. _errors];
            }
        }
    }

    /// <summary>Forgets everything, for a backend that is being relaunched.</summary>
    internal void Reset()
    {
        lock (_lock)
        {
            _loaded.Clear();
            _errors.Clear();
            _reportedTotal = 0;
        }
    }

    /// <summary>Reads one <c>$/progress</c> message for the project count.</summary>
    /// <param name="message">The progress value's <c>message</c>, when it had one.</param>
    internal void OnProgressMessage(string? message)
    {
        if (ParseProjectCount(message) is not { } count)
        {
            return;
        }

        lock (_lock)
        {
            // The maximum, not the latest: the stream reports the count more than once and a later
            // report of a subset would otherwise shrink the total under a caller's feet.
            _reportedTotal = Math.Max(_reportedTotal, count);
        }
    }

    /// <summary>Reads one <c>window/logMessage</c> for a per-project outcome.</summary>
    /// <param name="message">The log line, prefix and all.</param>
    /// <param name="severity">The LSP <c>MessageType</c>; 1 is an error (C47 numbers the rest oddly).</param>
    internal void OnLogMessage(string? message, int severity)
    {
        if (message is not { Length: > 0 })
        {
            return;
        }

        if (Segment(message, CompletedPrefix) is { Length: > 0 } completed)
        {
            lock (_lock)
            {
                _loaded.Add(completed);
            }

            return;
        }

        if (severity == 1 || message.Contains(FailedPrefix, StringComparison.Ordinal))
        {
            lock (_lock)
            {
                // Capped: a solution whose every project fails would otherwise put two hundred lines
                // into a tool result that a model has to read in one go.
                if (_errors.Count < 10 && !_errors.Contains(message, StringComparer.Ordinal))
                {
                    _errors.Add(message);
                }
            }
        }
    }

    /// <summary>
    /// Reads <c>Loading 30 project(s)...</c> out of a progress message.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The count, or <see langword="null"/> when the message is a different shape.</returns>
    internal static int? ParseProjectCount(string? message)
    {
        if (message is not { Length: > 0 })
        {
            return null;
        }

        var start = message.IndexOf(LoadingPrefix, StringComparison.Ordinal);

        if (start < 0)
        {
            return null;
        }

        start += LoadingPrefix.Length;

        var end = message.IndexOf(LoadingProjectsSuffix, start, StringComparison.Ordinal);

        if (end <= start)
        {
            return null;
        }

        return int.TryParse(
            message.AsSpan(start, end - start),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var count)
            ? count
            : null;
    }

    /// <summary>The text after a prefix, trimmed of the trailing punctuation Roslyn writes.</summary>
    private static string? Segment(string message, string prefix)
    {
        var start = message.IndexOf(prefix, StringComparison.Ordinal);

        return start < 0 ? null : message[(start + prefix.Length)..].Trim().TrimEnd('.');
    }
}
