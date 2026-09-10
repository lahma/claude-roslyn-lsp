using System.Text.Json;

using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// Consumes Roslyn's work-done progress stream into the adapter's own log, and never forwards it.
/// </summary>
/// <remarks>
/// <para>
/// Roslyn asks the client to create a progress token and then reports the solution load against it:
/// one <c>begin</c> titled <c>Loading &lt;solution&gt;...</c>, a handful of <c>report</c>s and one
/// <c>end</c>, all under a bare GUID token (C31). Claude Code refuses
/// <c>window/workDoneProgress/create</c> with <c>-32601</c>, so if the adapter forwarded it Roslyn
/// would be told its progress channel does not exist — and the load would proceed with no account of
/// itself anywhere.
/// </para>
/// <para>
/// So the adapter accepts the token, keeps the stream, and turns it into stderr lines. That stream
/// is the only place the load's own progress appears, and it is what makes "why has this been eight
/// seconds" answerable from a log rather than from a stopwatch. The client hears about it instead
/// through the readiness gate's ten-second notice, which is a sentence rather than a percentage.
/// </para>
/// </remarks>
internal sealed partial class ProgressTracker
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _titles = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    /// <summary>Creates an empty tracker.</summary>
    /// <param name="logger">Where the progress stream is rendered.</param>
    internal ProgressTracker(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>How many progress operations are open.</summary>
    internal int ActiveCount
    {
        get
        {
            lock (_lock)
            {
                return _titles.Count;
            }
        }
    }

    /// <summary>The title of the most recently begun operation, when one is open.</summary>
    internal string? CurrentTitle
    {
        get
        {
            lock (_lock)
            {
                return _titles.Count == 0 ? null : _titles.Values.Last();
            }
        }
    }

    /// <summary>Accepts a <c>window/workDoneProgress/create</c>.</summary>
    /// <param name="parameters">The request's parameters.</param>
    internal void Create(WorkDoneProgressCreateParams? parameters)
    {
        if (TokenOf(parameters?.Token) is not { Length: > 0 } token)
        {
            return;
        }

        lock (_lock)
        {
            _titles[token] = string.Empty;
        }

        Log.Created(_logger, token);
    }

    /// <summary>Renders one <c>$/progress</c> notification into the log.</summary>
    /// <param name="parameters">The notification's parameters.</param>
    internal void Report(ProgressParams? parameters)
    {
        var token = TokenOf(parameters?.Token) ?? string.Empty;
        var value = parameters?.Value;

        if (value is null)
        {
            return;
        }

        string title;

        lock (_lock)
        {
            if (string.Equals(value.Kind, "begin", StringComparison.Ordinal))
            {
                _titles[token] = value.Title ?? string.Empty;
            }
            else if (string.Equals(value.Kind, "end", StringComparison.Ordinal))
            {
                _titles.Remove(token);
            }

            title = _titles.TryGetValue(token, out var known) ? known : value.Title ?? string.Empty;
        }

        Log.Progress(
            _logger,
            title.Length == 0 ? token : title,
            value.Kind ?? "report",
            value.Message ?? string.Empty,
            value.Percentage ?? -1);
    }

    /// <summary>Renders a progress token, which may be a string or a number, as a key.</summary>
    private static string? TokenOf(JsonElement? token) =>
        token?.ValueKind switch
        {
            JsonValueKind.String => token.Value.GetString(),
            JsonValueKind.Number => token.Value.GetRawText(),
            _ => null,
        };

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 800,
            Level = LogLevel.Debug,
            Message = "Accepted a work-done progress token {Token} on Roslyn's behalf; it is consumed here " +
                      "rather than forwarded, because the client refuses to create one.")]
        internal static partial void Created(ILogger logger, string token);

        [LoggerMessage(
            EventId = 801,
            Level = LogLevel.Information,
            Message = "[roslyn progress] {Title}: {Kind} {Message} ({Percentage}%)")]
        internal static partial void Progress(
            ILogger logger,
            string title,
            string kind,
            string message,
            int percentage);
    }
}
