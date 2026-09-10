using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// Changing one Roslyn setting and waiting for the moment it is actually in effect (D77).
/// </summary>
/// <remarks>
/// <para>
/// Roslyn does not read what a client pushes in <c>workspace/didChangeConfiguration</c>. It treats
/// the notification as "ask me again" and answers itself with a fresh <c>workspace/configuration</c>
/// request, which <see cref="ConfigurationResponder"/> answers from the override — so the only
/// observable instant at which the new value exists is "that pull has been answered" (C46, D48).
/// Firing and forgetting races the very call the change was made for: a solution-wide diagnostic pull
/// issued before the pull comes back reports nothing, which reads exactly like a clean solution.
/// </para>
/// <para>
/// Shared because there are two callers with the same problem and one of them arrived with WP9: the
/// MCP engine setting its own scope, and the shared host applying an <em>attached</em> engine's
/// <c>claude-roslyn-lsp/setOption</c> (D90) into the responder Roslyn actually asks.
/// </para>
/// </remarks>
internal static partial class ConfigurationRoundTrip
{
    /// <summary>How long the round trip is waited for before the value is assumed to be in flight.</summary>
    /// <remarks>
    /// Two seconds is far more than the round trip costs and is a ceiling rather than a wait: a build
    /// that stopped re-pulling would make every option change two seconds slower, which is worth
    /// noticing and is not worth failing a call over — the override stands either way.
    /// </remarks>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The notification, written out rather than serialised.
    /// </summary>
    /// <remarks>
    /// Its <c>settings</c> object is empty on purpose and is always the same bytes. A constant is
    /// both cheaper and immune to the modelling accident that cost a live run: a default
    /// <see cref="System.Text.Json.JsonElement"/> has
    /// <see cref="System.Text.Json.JsonValueKind.Undefined"/> and throws when it is written.
    /// </remarks>
    internal static readonly byte[] Notification =
        JsonRpcErrors.Notification("workspace/didChangeConfiguration", """{"settings":{}}"""u8);

    /// <summary>Sets an override, tells Roslyn to re-read, and waits for it to have done so.</summary>
    /// <param name="responder">The responder Roslyn asks for configuration.</param>
    /// <param name="section">The exact section name.</param>
    /// <param name="rawValue">The raw JSON value, or empty to drop the override.</param>
    /// <param name="notify">Sends the notification to Roslyn.</param>
    /// <param name="time">The clock, so the budget is testable.</param>
    /// <param name="logger">The stderr log.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    internal static async Task ApplyAsync(
        ConfigurationResponder responder,
        string section,
        byte[] rawValue,
        Action<ReadOnlyMemory<byte>> notify,
        TimeProvider time,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(responder);
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(rawValue);
        ArgumentNullException.ThrowIfNull(notify);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        responder.SetOverride(section, rawValue);

        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnAnswered(IReadOnlyList<string> sections)
        {
            if (sections.Contains(section, StringComparer.Ordinal))
            {
                answered.TrySetResult();
            }
        }

        responder.Answered += OnAnswered;

        try
        {
            notify(Notification);

            await answered.Task.WaitAsync(Budget, time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The override stands whatever happened; a Roslyn that did not re-pull is a slower answer
            // rather than a wrong one, and saying so once is more useful than failing the call.
            Log.NotConfirmed(logger, section, Budget.TotalSeconds);
        }
        finally
        {
            responder.Answered -= OnAnswered;
        }
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1700,
            Level = LogLevel.Warning,
            Message = "Roslyn did not re-read {Section} within {Seconds:0} s of being told the configuration " +
                      "changed; the answer stands but the next pull may use the previous value.")]
        internal static partial void NotConfirmed(ILogger logger, string section, double seconds);
    }
}
