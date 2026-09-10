using ClaudeRoslynLsp.Edits;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;

namespace ClaudeRoslynLsp.Mcp.Tools;

/// <summary>
/// The single place an exception becomes something a model can act on.
/// </summary>
/// <remarks>
/// <para>
/// Every tool body runs inside <see cref="ExecuteAsync"/>, so <b>only <see cref="McpException"/> ever
/// escapes a tool method</b> — anything else reaches the client as the SDK's generic "An error
/// occurred", which throws away the one chance to say what to do differently.
/// </para>
/// <para>
/// The messages are written for a model mid-task: each says what happened, names the argument or the
/// environment variable to change, and where a retry is plausible says which call to make next.
/// </para>
/// </remarks>
internal static partial class ToolErrors
{
    /// <summary>
    /// Where the unexpected-exception branch logs its stack trace. Assigned once from
    /// <c>McpServerSetup</c>; a null logger keeps the funnel usable from tests, where nothing has
    /// wired up logging.
    /// </summary>
    private static ILogger _logger = NullLogger.Instance;

    /// <summary>Points the funnel's diagnostic logging at the server's stderr logger.</summary>
    /// <param name="loggerFactory">The server's logger factory.</param>
    internal static void UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger(typeof(ToolErrors).Namespace!);
    }

    /// <summary>
    /// Runs a tool body, converting anything it throws into an <see cref="McpException"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="OperationCanceledException"/> is rethrown untouched: the client cancelled, and the
    /// SDK's cancellation path — not an error result — is the correct answer. An
    /// <see cref="McpException"/> from argument validation is already the finished product and passes
    /// through unchanged.
    /// </remarks>
    /// <typeparam name="T">The tool's result type.</typeparam>
    /// <param name="tool">The tool's MCP name, for the log line.</param>
    /// <param name="operation">The tool body.</param>
    internal static async Task<T> ExecuteAsync<T>(string tool, Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpException)
        {
            throw;
        }
        catch (WorkspaceEditRefusedException exception)
        {
            throw new McpException(
                $"{tool} refused to write the edit Roslyn resolved: {exception.Message}. "
                + "Nothing was changed on disk.");
        }
        catch (ArgumentException exception)
        {
            throw new McpException($"{tool} was called with an argument it cannot use: {exception.Message}.");
        }
        catch (FileNotFoundException exception)
        {
            throw new McpException(
                $"{tool} could not read '{exception.FileName ?? "a file"}'. "
                + "Paths are relative to the workspace root and use forward slashes.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new McpException(
                $"{tool} was given a path whose directory does not exist. "
                + "Paths are relative to the workspace root and use forward slashes.");
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new McpException(
                $"{tool} was not allowed to write a file it had to change: {exception.Message}. "
                + "The edit may have been applied in part; re-read the affected files.");
        }
        catch (IOException exception)
        {
            throw new McpException(
                $"{tool} failed while reading or writing a file: {exception.Message}. "
                + "Re-read the affected files before editing them.");
        }
        catch (Exception exception)
        {
            LogUnexpectedFailure(_logger, tool, exception);

            throw new McpException(
                $"{tool} failed unexpectedly: {exception.Message}. "
                + "Run `claude-roslyn-lsp doctor` if this keeps happening; the server's stderr log has the stack trace.");
        }
    }

    /// <summary>The workspace-is-not-ready case that a tool cannot express in its result.</summary>
    /// <param name="tool">The tool's MCP name.</param>
    /// <param name="what">What the caller asked for.</param>
    internal static McpException NotFound(string tool, string what) =>
        new($"{tool} found no {what}.");

    /// <summary>
    /// The "your argument matched several things" case, which lists what it matched so the next call
    /// can be more specific.
    /// </summary>
    /// <param name="tool">The tool's MCP name.</param>
    /// <param name="what">What was ambiguous.</param>
    /// <param name="candidates">The candidates, already formatted one per entry.</param>
    internal static McpException Ambiguous(string tool, string what, IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var listed = candidates.Take(20).ToArray();

        return new McpException(
            $"{tool} matched {listed.Length} candidates for {what}, and will not guess between them:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, listed.Select(candidate => "  - " + candidate))
            + Environment.NewLine
            + "Pass a longer name, or a path:line:col address, to pick one.");
    }

    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Error,
        Message = "The MCP tool {Tool} failed with an exception no branch of the error funnel expected.")]
    private static partial void LogUnexpectedFailure(ILogger logger, string tool, Exception exception);
}
