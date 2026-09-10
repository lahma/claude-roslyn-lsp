using System.Buffers;
using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// Answers Roslyn's <c>workspace/configuration</c> requests, because nobody else in the chain can.
/// </summary>
/// <remarks>
/// <para>
/// Roslyn asks for eighty sections in two batches during startup. Their names look like
/// <c>csharp|background_analysis.dotnet_compiler_diagnostics_scope</c> — a pipe between the language
/// and the option group — and Claude Code's configuration lookup is a dotted path walk over a JSON
/// object, which cannot address a key containing a pipe at all. Forwarding the request would
/// therefore get every section answered <c>null</c> at best, and at worst nothing: Claude Code only
/// answers <c>workspace/configuration</c> when a <c>settings</c> block happens to be present in the
/// plugin configuration. So the adapter answers them.
/// </para>
/// <para>
/// <b>The defaults are tuned for an agent, not for an editor.</b> Answering <c>null</c> everywhere
/// is valid and gives Roslyn's own defaults, which assume a human with a screen: full-solution
/// background analysis, inlay hints, code lens, reference-assembly symbol search. An agent reads
/// none of that, and the ones that cost real CPU cost it on a machine that is simultaneously running
/// a model's tool calls. The four choices worth stating outright:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Diagnostic scopes at <c>openFiles</c>, because the diagnostics that matter are the ones on the
/// file the agent just edited, and <c>fullSolution</c> is the setting that makes a large solution
/// unusable. WP4's opt-in workspace mode raises it deliberately for the runs that need it (C14).
/// </description></item>
/// <item><description>
/// Automatic restore <b>on</b>. Roslyn restores server-side when <c>obj/</c> is missing and never
/// asks the client (C30); with restore off, a freshly cloned repository loads into a workspace with
/// no references and answers confidently about nothing.
/// </description></item>
/// <item><description>
/// Decompiled-source navigation <b>on</b>, so go-to-definition into a BCL symbol reaches real source
/// rather than dead-ending (C25).
/// </description></item>
/// <item><description>
/// Reference-assembly symbol search <b>off</b>, and inlay hints, code lens and auto-insert off: the
/// adapter does not advertise those capabilities to its client (C26), so any work Roslyn does for
/// them is work whose output is discarded.
/// </description></item>
/// </list>
/// <para>
/// Precedence is client settings, then <c>CLAUDE_ROSLYN_LSP_OPTIONS</c>, then these defaults, then
/// <c>null</c>. The client wins because a user who wrote a value in their own configuration meant
/// it; the environment variable is the escape hatch for a client with no settings channel; and an
/// unknown section falls through to <c>null</c> so a Roslyn version that asks about something new
/// gets its own default rather than an error.
/// </para>
/// </remarks>
internal sealed partial class ConfigurationResponder
{
    /// <summary>Sections whose exact name has an agent-tuned answer.</summary>
    private static readonly Dictionary<string, byte[]> ExactDefaults = new(StringComparer.Ordinal)
    {
        ["csharp|background_analysis.dotnet_analyzer_diagnostics_scope"] = "\"openFiles\""u8.ToArray(),
        ["csharp|background_analysis.dotnet_compiler_diagnostics_scope"] = "\"openFiles\""u8.ToArray(),
        ["projects.dotnet_enable_automatic_restore"] = "true"u8.ToArray(),
        ["csharp|symbol_search.dotnet_search_reference_assemblies"] = "false"u8.ToArray(),
        ["navigation.dotnet_navigate_to_decompiled_sources"] = "true"u8.ToArray(),
        ["csharp|auto_insert.dotnet_enable_auto_insert"] = "false"u8.ToArray(),
    };

    /// <summary>
    /// Section prefixes whose whole family is answered <c>false</c>.
    /// </summary>
    /// <remarks>
    /// A prefix rather than an enumeration because Roslyn adds hint kinds between versions, and a
    /// list that was complete on the pinned build would quietly stop being complete on the next one
    /// — turning a feature back on that this adapter does not carry.
    /// </remarks>
    private static readonly string[] FalseByPrefix =
    [
        "csharp|inlay_hints.",
        "csharp|code_lens.",
    ];

    private static readonly byte[] NullValue = "null"u8.ToArray();

    /// <summary>The section the opt-in workspace mode has to raise (C14).</summary>
    internal const string CompilerDiagnosticsScopeSection =
        "csharp|background_analysis.dotnet_compiler_diagnostics_scope";

    private static readonly byte[] FullSolutionValue = "\"fullSolution\""u8.ToArray();

    private readonly Lock _lock = new();
    private readonly ILogger _logger;
    private readonly Dictionary<string, byte[]> _fromEnvironment;
    private readonly bool _fullSolutionCompilerScope;

    private JsonElement _clientSettings;

    /// <summary>Creates a responder over the client's settings and the environment override.</summary>
    /// <param name="optionsJson">
    /// The raw <c>CLAUDE_ROSLYN_LSP_OPTIONS</c> value: a JSON object whose keys are exact section
    /// names. Malformed content is a logged warning, never a startup failure (D10).
    /// </param>
    /// <param name="logger">The stderr log.</param>
    /// <param name="fullSolutionCompilerScope">
    /// Whether the compiler diagnostics scope is raised to <c>fullSolution</c>. Set only by
    /// <c>CLAUDE_ROSLYN_LSP_WORKSPACE_DIAGNOSTICS=errors</c>: closed files are unreachable at any
    /// lower scope (C13, C14), and this is the setting that makes a large solution expensive, so it
    /// moves with the feature that needs it and with nothing else. The analyser scope stays at
    /// <c>openFiles</c> — the mode reports compile errors, and raising both is what turns a
    /// hundred-project solution into a machine that is busy for minutes.
    /// </param>
    internal ConfigurationResponder(string? optionsJson, ILogger logger, bool fullSolutionCompilerScope = false)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _fromEnvironment = ParseOptions(optionsJson, logger);
        _fullSolutionCompilerScope = fullSolutionCompilerScope;

        if (fullSolutionCompilerScope)
        {
            Log.FullSolutionScope(logger);
        }
    }

    /// <summary>How many sections the environment override supplies.</summary>
    internal int EnvironmentSectionCount => _fromEnvironment.Count;

    /// <summary>
    /// Records the client's <c>settings.roslyn</c> object, from <c>initializationOptions</c> or from
    /// <c>workspace/didChangeConfiguration</c>.
    /// </summary>
    /// <param name="settings">
    /// Either the <c>settings</c> wrapper or the <c>roslyn</c> object itself — both spellings are in
    /// circulation and telling a user their configuration was silently ignored is not an option.
    /// </param>
    internal void UpdateClientSettings(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var resolved = settings;

        if (settings.TryGetProperty("settings", out var nested) && nested.ValueKind == JsonValueKind.Object)
        {
            resolved = nested;
        }

        if (resolved.TryGetProperty("roslyn", out var roslyn) && roslyn.ValueKind == JsonValueKind.Object)
        {
            resolved = roslyn;
        }

        var count = 0;

        foreach (var _ in resolved.EnumerateObject())
        {
            count++;
        }

        lock (_lock)
        {
            _clientSettings = resolved.Clone();
        }

        Log.ClientSettings(_logger, count);
    }

    /// <summary>The answer for one section, as raw UTF-8 JSON.</summary>
    /// <param name="section">The section name, or null when the request named none.</param>
    internal ReadOnlyMemory<byte> Answer(string? section)
    {
        if (section is not { Length: > 0 })
        {
            return NullValue;
        }

        lock (_lock)
        {
            if (_clientSettings.ValueKind == JsonValueKind.Object
                && _clientSettings.TryGetProperty(section, out var fromClient))
            {
                return Encoding.UTF8.GetBytes(fromClient.GetRawText());
            }
        }

        if (_fromEnvironment.TryGetValue(section, out var fromEnvironment))
        {
            return fromEnvironment;
        }

        if (_fullSolutionCompilerScope
            && string.Equals(section, CompilerDiagnosticsScopeSection, StringComparison.Ordinal))
        {
            return FullSolutionValue;
        }

        if (ExactDefaults.TryGetValue(section, out var exact))
        {
            return exact;
        }

        foreach (var prefix in FalseByPrefix)
        {
            if (section.StartsWith(prefix, StringComparison.Ordinal))
            {
                return "false"u8.ToArray();
            }
        }

        return NullValue;
    }

    /// <summary>Builds the whole response to one <c>workspace/configuration</c> request.</summary>
    /// <param name="idToken">Roslyn's request id, echoed exactly.</param>
    /// <param name="parameters">The sections asked about, in the order the array must follow.</param>
    internal byte[] BuildResponse(ReadOnlySpan<byte> idToken, ConfigurationParams? parameters)
    {
        var items = parameters?.Items ?? [];
        var buffer = new ArrayBufferWriter<byte>(64 + (items.Count * 8));

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();

            foreach (var item in items)
            {
                writer.WriteRawValue(Answer(item.Section).Span, skipInputValidation: true);
            }

            writer.WriteEndArray();
        }

        Log.Answered(_logger, items.Count);

        return JsonRpcErrors.RawResult(idToken, buffer.WrittenSpan);
    }

    /// <summary>Parses the environment override into exact section answers.</summary>
    private static Dictionary<string, byte[]> ParseOptions(string? optionsJson, ILogger logger)
    {
        var parsed = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        if (optionsJson is not { Length: > 0 })
        {
            return parsed;
        }

        try
        {
            using var document = JsonDocument.Parse(optionsJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                Log.OptionsNotAnObject(logger, document.RootElement.ValueKind.ToString());
                return parsed;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                parsed[property.Name] = Encoding.UTF8.GetBytes(property.Value.GetRawText());
            }
        }
        catch (JsonException exception)
        {
            // Never a startup failure: stdout is the protocol channel, so a server that refuses to
            // start leaves its client with a dead process and nowhere to read the reason (D10).
            Log.OptionsUnparsable(logger, exception);
        }

        return parsed;
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 700,
            Level = LogLevel.Debug,
            Message = "Answered a workspace/configuration request for {Count} section(s).")]
        internal static partial void Answered(ILogger logger, int count);

        [LoggerMessage(
            EventId = 701,
            Level = LogLevel.Information,
            Message = "The client supplied {Count} Roslyn setting(s); they take precedence over this " +
                      "adapter's own defaults.")]
        internal static partial void ClientSettings(ILogger logger, int count);

        [LoggerMessage(
            EventId = 702,
            Level = LogLevel.Warning,
            Message = "CLAUDE_ROSLYN_LSP_OPTIONS is not valid JSON and is being ignored; Roslyn will get " +
                      "this adapter's defaults instead.")]
        internal static partial void OptionsUnparsable(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 703,
            Level = LogLevel.Warning,
            Message = "CLAUDE_ROSLYN_LSP_OPTIONS is a JSON {Kind}, not an object of section names, and is " +
                      "being ignored.")]
        internal static partial void OptionsNotAnObject(ILogger logger, string kind);

        [LoggerMessage(
            EventId = 704,
            Level = LogLevel.Information,
            Message = "The compiler diagnostics scope is raised to fullSolution because " +
                      "CLAUDE_ROSLYN_LSP_WORKSPACE_DIAGNOSTICS asked for closed-file errors (C14). " +
                      "Roslyn will hold every project's compilation in memory.")]
        internal static partial void FullSolutionScope(ILogger logger);
    }
}
