using System.Buffers;
using System.Globalization;
using System.Text.Json;

using ClaudeRoslynLsp.Protocol;

namespace ClaudeRoslynLsp.Adapter.Sharing;

/// <summary>
/// The one notification the shared host originates rather than relays:
/// <c>claude-roslyn-lsp/backendChanged</c>, sent to every attached client when the Roslyn behind the
/// pipe is launched, recycled or relaunched (D105).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an attached client needs telling at all.</b> Before the idle policy, an attached client
/// learned the backend's process id once, from the handshake the host answered (D88), and a relaunch
/// after a crash left it reporting a pid that no longer existed. With recycles that stops being an
/// edge case: an idle host replaces its Roslyn as a matter of routine, and an attached
/// <c>getWorkspaceStatus</c> that named the dead process — and reported none of the recycles — would
/// be the confidently wrong answer this product exists to remove. An attached LSP session has a
/// second reason: its diagnostics bridge holds result ids issued by the old process (D57).
/// </para>
/// <para>
/// Written and read by hand rather than through a serializer context, because it is three numbers
/// and a word, and it is this product's own vocabulary on its own pipe — giving it a context of its
/// own would be a fifth contract for a shape that fits in one method each way.
/// </para>
/// </remarks>
internal static class BackendChange
{
    /// <summary>The method name. It is in the host's fan-out list, which is where every rule about what an attached client sees lives (D89).</summary>
    internal const string Method = "claude-roslyn-lsp/backendChanged";

    /// <summary>Renders one status as the notification.</summary>
    /// <param name="status">The host's backend status.</param>
    internal static byte[] Build(BackendStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var buffer = new ArrayBufferWriter<byte>(192);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("state"u8, status.StateName);

            if (status.ProcessId is { } processId)
            {
                writer.WriteNumber("roslynProcessId"u8, processId);
            }

            writer.WriteNumber("recycles"u8, status.Recycles);

            if (BackendMemory.Megabytes(status.BaselineBytes) is { } baseline)
            {
                writer.WriteNumber("baselineMegabytes"u8, baseline);
            }

            if (status.LastRecycleAt is { } lastRecycleAt)
            {
                writer.WriteString("lastRecycleAt"u8, lastRecycleAt.ToString("O", CultureInfo.InvariantCulture));
            }

            writer.WriteEndObject();
        }

        return JsonRpcErrors.Notification(Method, buffer.WrittenSpan);
    }

    /// <summary>Reads one notification back, treating anything unreadable as absent.</summary>
    /// <param name="body">The whole message.</param>
    internal static HostBackend? TryParse(ReadOnlySpan<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());

            if (!document.RootElement.TryGetProperty("params", out var parameters)
                || parameters.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            int? processId = parameters.TryGetProperty("roslynProcessId", out var pid)
                              && pid.ValueKind == JsonValueKind.Number
                              && pid.TryGetInt32(out var parsedPid)
                ? parsedPid
                : null;

            var recycles = parameters.TryGetProperty("recycles", out var count)
                           && count.ValueKind == JsonValueKind.Number
                           && count.TryGetInt32(out var parsedCount)
                ? parsedCount
                : 0;

            int? baseline = parameters.TryGetProperty("baselineMegabytes", out var megabytes)
                            && megabytes.ValueKind == JsonValueKind.Number
                            && megabytes.TryGetInt32(out var parsedBaseline)
                ? parsedBaseline
                : null;

            DateTimeOffset? lastRecycleAt = parameters.TryGetProperty("lastRecycleAt", out var at)
                                            && at.ValueKind == JsonValueKind.String
                                            && DateTimeOffset.TryParse(
                                                at.GetString(),
                                                CultureInfo.InvariantCulture,
                                                DateTimeStyles.RoundtripKind,
                                                out var parsedAt)
                ? parsedAt
                : null;

            var state = parameters.TryGetProperty("state", out var word) && word.ValueKind == JsonValueKind.String
                ? word.GetString() ?? "running"
                : "running";

            return new HostBackend(state, processId, recycles, baseline, lastRecycleAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>What an attached client knows about the host's backend, from the last <see cref="BackendChange"/>.</summary>
/// <param name="State">The host's <see cref="BackendStatus.StateName"/>.</param>
/// <param name="ProcessId">Roslyn's process id, or null while there is none.</param>
/// <param name="Recycles">How many times the host has recycled it.</param>
/// <param name="BaselineMegabytes">Its baseline, when it has one.</param>
/// <param name="LastRecycleAt">When the host last recycled it.</param>
internal sealed record HostBackend(
    string State,
    int? ProcessId,
    int Recycles,
    int? BaselineMegabytes,
    DateTimeOffset? LastRecycleAt);
