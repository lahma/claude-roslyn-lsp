using System.Buffers;
using System.Text.Json;

using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// Turns one of Roslyn's pull reports into the <c>publishDiagnostics</c> set an agent should see.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a policy, and the policy is "what would change what the model does next".</b> Roslyn
/// reports everything an IDE would paint in a gutter, which for one edited file is routinely dozens
/// of entries at Hint and Information severity; Claude Code renders diagnostics as a text
/// attachment on the next turn and cuts it, so an unfiltered set spends the whole budget on
/// IDE0005-shaped advice and pushes the CS error that actually broke the build off the end. Every
/// step below removes something that would have crowded out something else.
/// </para>
/// <list type="number">
/// <item><description>
/// <b>A severity floor, Warning by default.</b> Hint and Information are style, and the agent did
/// not ask about style. <c>CLAUDE_ROSLYN_LSP_DIAGNOSTIC_MIN_SEVERITY</c> lowers it for somebody who
/// did.
/// </description></item>
/// <item><description>
/// <b>Anything tagged <c>Unnecessary</c> that is not an error goes.</b> C16: IDE0005 arrives at
/// severity 4 <em>at position 0:0 for the whole using block</em>, so it is both the least useful
/// entry and the one most likely to be read as "there is a problem at the top of the file". An
/// unnecessary-tagged <em>error</em> is kept, because that is a real one carrying a fade hint.
/// </description></item>
/// <item><description>
/// <b>Tags at or above 2147483640 are stripped.</b> Those are Visual Studio's private tag values
/// (C16), and no client outside VS knows what they mean; several treat an unknown tag as a reason
/// to drop the whole diagnostic.
/// </description></item>
/// <item><description>
/// <b>Sorted by severity and then by line, and capped at fifty.</b> The cap is against a file that
/// genuinely has hundreds — a generated file, or one edited into nonsense mid-keystroke — where the
/// first fifty say everything the next four hundred would.
/// </description></item>
/// </list>
/// <para>
/// Each surviving diagnostic is rewritten rather than modelled: every member is copied verbatim and
/// only <c>tags</c> is filtered. Roslyn puts opaque payloads on a diagnostic (a <c>data</c> member
/// that a code action later round-trips, C18) and this repository has no business having an opinion
/// about their shape.
/// </para>
/// </remarks>
internal static partial class DiagnosticTranslation
{
    /// <summary>The most diagnostics that will ever be published for one file.</summary>
    internal const int MaxPerFile = 50;

    /// <summary>The lowest tag value Visual Studio reserves for itself (C16).</summary>
    internal const int PrivateTagFloor = 2147483640;

    /// <summary>The LSP <c>Unnecessary</c> tag.</summary>
    internal const int UnnecessaryTag = 1;

    /// <summary>LSP severity: an error.</summary>
    internal const int SeverityError = 1;

    /// <summary>LSP severity: a warning. The default floor.</summary>
    internal const int SeverityWarning = 2;

    /// <summary>LSP severity: information.</summary>
    internal const int SeverityInformation = 3;

    /// <summary>LSP severity: a hint.</summary>
    internal const int SeverityHint = 4;

    /// <summary>
    /// Parses <c>CLAUDE_ROSLYN_LSP_DIAGNOSTIC_MIN_SEVERITY</c>, falling back to Warning.
    /// </summary>
    /// <remarks>
    /// Names rather than numbers, because the numbers run the wrong way round for a "minimum" —
    /// 1 is the most severe — and a user who wrote <c>2</c> meaning "at least two levels" would get
    /// something defensible and unintended. The numbers are accepted anyway, since they are what the
    /// protocol uses and somebody reading a wire log will try them.
    /// </remarks>
    /// <param name="value">The configured value, or null.</param>
    /// <param name="logger">Where an unrecognised value is reported.</param>
    internal static int ParseSeverityFloor(string? value, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        switch (value?.ToUpperInvariant())
        {
            case null:
                return SeverityWarning;

            case "ERROR" or "ERRORS" or "1":
                return SeverityError;

            case "WARNING" or "WARNINGS" or "WARN" or "2":
                return SeverityWarning;

            case "INFORMATION" or "INFO" or "3":
                return SeverityInformation;

            case "HINT" or "HINTS" or "ALL" or "4":
                return SeverityHint;

            default:
                Log.UnknownSeverity(logger, value);
                return SeverityWarning;
        }
    }

    /// <summary>
    /// Builds a <c>textDocument/publishDiagnostics</c> notification from a pull report's items.
    /// </summary>
    /// <param name="uri">The document.</param>
    /// <param name="version">
    /// The mirror version the pull was started at, so a client can discard a set that an edit has
    /// already overtaken. Null when the document is not open.
    /// </param>
    /// <param name="items">The report's <c>items</c> array, or a default element for "none".</param>
    /// <param name="severityFloor">The lowest severity to keep, as an LSP severity number.</param>
    /// <param name="cap">The most diagnostics to publish.</param>
    /// <returns>The encoded notification, and how many diagnostics survived.</returns>
    internal static (byte[] Body, int Count) BuildPublish(
        string uri,
        int? version,
        JsonElement items,
        int severityFloor,
        int cap = MaxPerFile)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var kept = Select(items, severityFloor, cap);
        var buffer = new ArrayBufferWriter<byte>(256 + (kept.Count * 256));

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("uri"u8, uri);

            if (version is { } number)
            {
                writer.WriteNumber("version"u8, number);
            }

            writer.WritePropertyName("diagnostics"u8);
            writer.WriteStartArray();

            foreach (var diagnostic in kept)
            {
                WriteWithoutPrivateTags(writer, diagnostic);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return (JsonRpcErrors.Notification("textDocument/publishDiagnostics", buffer.WrittenSpan), kept.Count);
    }

    /// <summary>Applies the floor, the tag rule, the ordering and the cap.</summary>
    /// <param name="items">The report's <c>items</c> array.</param>
    /// <param name="severityFloor">The lowest severity to keep.</param>
    /// <param name="cap">The most to keep.</param>
    internal static List<JsonElement> Select(JsonElement items, int severityFloor, int cap = MaxPerFile)
    {
        var kept = new List<JsonElement>();

        if (items.ValueKind != JsonValueKind.Array)
        {
            return kept;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var severity = SeverityOf(item);

            if (severity > severityFloor)
            {
                continue;
            }

            if (severity != SeverityError && HasUnnecessaryTag(item))
            {
                continue;
            }

            kept.Add(item);
        }

        kept.Sort(CompareForDelivery);

        if (kept.Count > cap)
        {
            kept.RemoveRange(cap, kept.Count - cap);
        }

        return kept;
    }

    /// <summary>The diagnostic's severity, defaulting to Error when it carries none.</summary>
    /// <remarks>
    /// The specification makes <c>severity</c> optional and says the client decides. Defaulting to
    /// the most severe is the safe direction: the cost of showing an unclassified diagnostic is one
    /// extra line, and the cost of hiding one is silence about a real problem.
    /// </remarks>
    internal static int SeverityOf(JsonElement diagnostic) =>
        diagnostic.TryGetProperty("severity", out var severity)
        && severity.ValueKind == JsonValueKind.Number
        && severity.TryGetInt32(out var value)
            ? value
            : SeverityError;

    /// <summary>Whether the diagnostic carries the LSP <c>Unnecessary</c> tag.</summary>
    internal static bool HasUnnecessaryTag(JsonElement diagnostic)
    {
        if (!diagnostic.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var tag in tags.EnumerateArray())
        {
            if (tag.ValueKind == JsonValueKind.Number && tag.TryGetInt32(out var value) && value == UnnecessaryTag)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Severity first, then position: the order somebody reading the first five wants.</summary>
    private static int CompareForDelivery(JsonElement left, JsonElement right)
    {
        var bySeverity = SeverityOf(left).CompareTo(SeverityOf(right));

        if (bySeverity != 0)
        {
            return bySeverity;
        }

        var (leftLine, leftCharacter) = StartOf(left);
        var (rightLine, rightCharacter) = StartOf(right);

        var byLine = leftLine.CompareTo(rightLine);
        return byLine != 0 ? byLine : leftCharacter.CompareTo(rightCharacter);
    }

    /// <summary>The diagnostic's start position, or the origin when it has an unreadable range.</summary>
    private static (int Line, int Character) StartOf(JsonElement diagnostic)
    {
        if (!diagnostic.TryGetProperty("range", out var range)
            || range.ValueKind != JsonValueKind.Object
            || !range.TryGetProperty("start", out var start)
            || start.ValueKind != JsonValueKind.Object)
        {
            return (0, 0);
        }

        var line = start.TryGetProperty("line", out var lineValue) && lineValue.TryGetInt32(out var parsedLine)
            ? parsedLine
            : 0;

        var character = start.TryGetProperty("character", out var characterValue)
                        && characterValue.TryGetInt32(out var parsedCharacter)
            ? parsedCharacter
            : 0;

        return (line, character);
    }

    /// <summary>
    /// Copies one diagnostic through, filtering <c>tags</c> and dropping the member when nothing is
    /// left.
    /// </summary>
    private static void WriteWithoutPrivateTags(Utf8JsonWriter writer, JsonElement diagnostic)
    {
        writer.WriteStartObject();

        foreach (var property in diagnostic.EnumerateObject())
        {
            if (!string.Equals(property.Name, "tags", StringComparison.Ordinal))
            {
                property.WriteTo(writer);
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var visible = new List<JsonElement>();

            foreach (var tag in property.Value.EnumerateArray())
            {
                if (tag.ValueKind == JsonValueKind.Number
                    && tag.TryGetInt32(out var value)
                    && value >= PrivateTagFloor)
                {
                    continue;
                }

                visible.Add(tag);
            }

            if (visible.Count == 0)
            {
                // An empty array is legal but pointless, and one client in three treats a present
                // member as "the server had something to say here".
                continue;
            }

            writer.WritePropertyName("tags"u8);
            writer.WriteStartArray();

            foreach (var tag in visible)
            {
                tag.WriteTo(writer);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1300,
            Level = LogLevel.Warning,
            Message = "CLAUDE_ROSLYN_LSP_DIAGNOSTIC_MIN_SEVERITY='{Value}' is not error, warning, " +
                      "information or hint; using warning.")]
        internal static partial void UnknownSeverity(ILogger logger, string value);
    }
}
