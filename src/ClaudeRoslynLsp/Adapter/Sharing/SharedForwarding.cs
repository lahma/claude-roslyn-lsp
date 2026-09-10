using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Protocol;

namespace ClaudeRoslynLsp.Adapter.Sharing;

/// <summary>
/// Turning one attached client's request into a question for Roslyn and its answer back into a reply.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the two owners because it is the one piece of <see cref="ISharedEngineHost"/> that is
/// identical in both and easy to get subtly wrong in two places: the reply has to carry the
/// <em>client's</em> id token byte for byte (D47), a Roslyn error has to come back as a JSON-RPC
/// error rather than as an exception the multiplexer would have to reinterpret, and a
/// <c>null</c> result has to be a JSON <c>null</c> rather than an absent member.
/// </para>
/// <para>
/// The <c>params</c> value is copied out of the client's message and put back verbatim, so an opaque
/// payload — a code action's <c>data</c>, a call-hierarchy item (C18, C24) — reaches Roslyn exactly
/// as the attached client wrote it. The result is copied back the same way, out of
/// <see cref="JsonElement.GetRawText"/>, which returns the slice of the document it was parsed from.
/// </para>
/// </remarks>
internal static class SharedForwarding
{
    /// <summary>Asks Roslyn one attached client's question and builds its reply.</summary>
    /// <param name="request">The client's message, exactly as it arrived.</param>
    /// <param name="replyIdToken">The id token the reply must carry.</param>
    /// <param name="ask">The owner's own "send this and wait" — its id map, its endpoint.</param>
    /// <param name="cancellationToken">Cancels the request, at Roslyn as well as here.</param>
    internal static async Task<byte[]> ForwardAsync(
        byte[] request,
        byte[] replyIdToken,
        Func<string, ReadOnlyMemory<byte>, CancellationToken, Task<JsonElement>> ask,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(replyIdToken);
        ArgumentNullException.ThrowIfNull(ask);

        var info = LspMessageScanner.Scan(request);
        var method = info.Method ?? string.Empty;

        try
        {
            var result = await ask(method, RawParams(request), cancellationToken).ConfigureAwait(false);

            return JsonRpcErrors.RawResult(
                replyIdToken,
                result.ValueKind == JsonValueKind.Undefined
                    ? "null"u8
                    : Encoding.UTF8.GetBytes(result.GetRawText()));
        }
        catch (RoslynRequestException failure)
        {
            // Roslyn said no, and "no" is an answer. Rebuilding it as an error response rather than
            // letting the exception escape is what keeps a -32801 ContentModified meaning the same
            // thing to an attached client as it does to a direct one.
            return JsonRpcErrors.Error(replyIdToken, failure.Code, failure.Message);
        }
    }

    /// <summary>The raw bytes of a request's <c>params</c> member, or nothing when it has none.</summary>
    private static ReadOnlyMemory<byte> RawParams(byte[] request)
    {
        try
        {
            using var document = JsonDocument.Parse(request);

            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("params", out var parameters)
                   && parameters.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
                ? Encoding.UTF8.GetBytes(parameters.GetRawText())
                : default;
        }
        catch (JsonException)
        {
            // The scanner already accepted this as a JSON-RPC message, so an unreadable body here is
            // a message with no params rather than a reason to refuse the call.
            return default;
        }
    }
}
