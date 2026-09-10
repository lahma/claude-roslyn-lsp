using System.Buffers;
using System.Text.Json;

using ClaudeRoslynLsp.Protocol;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// The three things every client of a Roslyn backend does with a raw response, in one place.
/// </summary>
/// <remarks>
/// Shared because there are now two of those clients — the mediation (<see cref="AdapterSession"/>)
/// and the MCP half's owned engine (D75) — and each of these is the kind of small function that goes
/// subtly wrong when it is written twice. Reading an error's <em>code</em> in particular: <c>-32801
/// ContentModified</c> means "the world moved, ask again" and every other code means "stop", and a
/// second copy that lost that distinction would produce a bridge that retried forever or one that
/// never retried at all.
/// </remarks>
internal static class RoslynResponses
{
    /// <summary>Delivers a response to the code that asked the question.</summary>
    /// <param name="body">The raw response.</param>
    /// <param name="info">Its scan, for the message kind.</param>
    /// <param name="method">The method that was asked, for the exception's text.</param>
    /// <param name="completion">Where the answer goes.</param>
    internal static void Complete(
        ReadOnlySpan<byte> body,
        in LspMessageInfo info,
        string method,
        TaskCompletionSource<JsonElement> completion)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(completion);

        if (info.Kind == LspMessageKind.ErrorResponse)
        {
            var (code, message) = ErrorOf(body);
            completion.TrySetException(new RoslynRequestException(method, code, message));

            return;
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());

            completion.TrySetResult(document.RootElement.TryGetProperty("result", out var result)
                ? result.Clone()
                : JsonRpc.Null);
        }
        catch (JsonException exception)
        {
            completion.TrySetException(exception);
        }
    }

    /// <summary>Pulls the code and the message out of an error response.</summary>
    /// <param name="body">The raw response.</param>
    internal static (int Code, string Message) ErrorOf(ReadOnlySpan<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());

            if (!document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return (JsonRpcErrors.InternalError, "(no error object)");
            }

            var code = error.TryGetProperty("code", out var codeValue)
                       && codeValue.ValueKind == JsonValueKind.Number
                       && codeValue.TryGetInt32(out var parsed)
                ? parsed
                : JsonRpcErrors.InternalError;

            var message = error.TryGetProperty("message", out var messageValue)
                          && messageValue.ValueKind == JsonValueKind.String
                ? messageValue.GetString() ?? "(no message)"
                : "(no message)";

            return (code, message);
        }
        catch (JsonException)
        {
            return (JsonRpcErrors.InternalError, "(unreadable)");
        }
    }

    /// <summary>Builds a <c>$/cancelRequest</c> naming the id Roslyn knows the request by.</summary>
    /// <param name="outboundId">The id the request was sent under.</param>
    internal static byte[] BuildCancelRequest(int outboundId)
    {
        var buffer = new ArrayBufferWriter<byte>(32);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id"u8, outboundId);
            writer.WriteEndObject();
        }

        return JsonRpcErrors.Notification("$/cancelRequest", buffer.WrittenSpan);
    }
}
