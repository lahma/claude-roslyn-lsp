using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Roslyn;

/// <summary>
/// Drains one of the Roslyn child's text streams into this adapter's logger, a line at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>D36 — every redirected child stream must be read, including the one that "should be empty".</b>
/// A redirected pipe that nobody reads fills at about 4 KB and then blocks the writer inside a
/// <c>write</c> call it cannot return from, which presents as a language server that answered three
/// requests and then stopped — with no error, no exit, and no clue. Roslyn's stderr is empty in
/// <c>--stdio</c> mode (C7), and "empty" is exactly the case where somebody decides the pump is
/// unnecessary.
/// </para>
/// <para>
/// In <c>--pipe</c> mode the child's <em>stdout</em> also has to be pumped, and for a second reason:
/// the pinned build writes a 646-byte startup banner there (C7). That stream is not protocol in pipe
/// mode — the protocol is the pipe — but the banner would still be sitting in a buffer, and if the
/// child's stdout were left inherited it would be written to <em>this</em> process's stdout, which
/// <em>is</em> the protocol channel for the client. That is the corruption hard rule 5 exists to
/// prevent, arriving from the one direction the rule cannot see.
/// </para>
/// </remarks>
internal static partial class RoslynStderrPump
{
    /// <summary>The prefix every forwarded line carries, so it is never mistaken for the adapter's own.</summary>
    internal const string Prefix = "[roslyn]";

    /// <summary>
    /// Reads <paramref name="reader"/> to the end, logging each line, and completes when the stream
    /// closes.
    /// </summary>
    /// <param name="reader">The child's stdout or stderr.</param>
    /// <param name="logger">Where the lines go. Never stdout.</param>
    /// <param name="level">
    /// The level to log at: <c>Information</c> for stderr, which is where a real failure appears, and
    /// <c>Debug</c> for the pipe-mode stdout banner, which is noise on every single start.
    /// </param>
    /// <param name="cancellationToken">Stops the pump.</param>
    internal static async Task PumpAsync(
        TextReader reader,
        ILogger logger,
        LogLevel level,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                Log.ChildLine(logger, level, Prefix, line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // The child exited and the pipe went away underneath the read. That is how a pump ends
            // on a shutdown, and it is the launcher's exit task that reports what it means.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Source-generated log records; see the note in <c>LspStubServer</c> for why (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 130, Message = "{Prefix} {Line}")]
        internal static partial void ChildLine(ILogger logger, LogLevel level, string prefix, string line);
    }
}
