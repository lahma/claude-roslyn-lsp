using System.Buffers;
using System.Text.Json;

namespace ClaudeRoslynLsp.Protocol;

/// <summary>
/// The JSON-RPC and LSP error codes the adapter uses, and the message builders that write a body
/// straight to UTF-8 without going through a typed shape.
/// </summary>
/// <remarks>
/// <para>
/// The builders exist because most of what the adapter writes is <em>somebody else's</em> JSON with
/// a different envelope around it: a <c>workspace/configuration</c> answer is an array of values
/// that came out of the client's settings, a forwarded <c>window/logMessage</c> keeps a payload this
/// process never modelled. <see cref="Utf8JsonWriter.WriteRawValue(ReadOnlySpan{byte}, bool)"/> puts
/// those through verbatim, which a typed record could only do by holding a
/// <see cref="JsonElement"/> and paying for a parse it does not need.
/// </para>
/// <para>
/// Everything here is source-generator-free and reflection-free, so it stays clean under
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c> and under the AOT analyzers.
/// </para>
/// </remarks>
internal static class JsonRpcErrors
{
    /// <summary>The body was not valid JSON. Answered under a null id, because none could be read.</summary>
    internal const int ParseError = -32700;

    /// <summary>The message was JSON but not a JSON-RPC message.</summary>
    internal const int InvalidRequest = -32600;

    /// <summary>
    /// The method exists in no version of this server. Answered, never ignored: an LSP client waits
    /// on an outstanding request, and Claude Code's own client gives up on the server entirely after
    /// a few unanswered retries.
    /// </summary>
    internal const int MethodNotFound = -32601;

    /// <summary>The parameters did not fit the method.</summary>
    internal const int InvalidParams = -32602;

    /// <summary>The server could not answer. What a failed backend turns every held request into.</summary>
    internal const int InternalError = -32603;

    /// <summary>
    /// LSP's own code for "the client asked for this request to be cancelled". It is what a held
    /// request becomes when <c>$/cancelRequest</c> arrives before the workspace has finished loading.
    /// </summary>
    internal const int RequestCancelled = -32800;

    /// <summary>
    /// LSP's code for "the document changed underneath this request". Deliberately never sent by the
    /// readiness gate: Claude Code retries it only about three times inside three and a half seconds,
    /// which a cold solution load outlasts every time (C31).
    /// </summary>
    internal const int ContentModified = -32801;

    private static readonly byte[] NullToken = "null"u8.ToArray();

    /// <summary>Writes an error response for the given id token.</summary>
    /// <param name="idToken">The raw id token to echo, or <see langword="default"/> for a null id.</param>
    /// <param name="code">One of the codes above.</param>
    /// <param name="message">A short, human-readable explanation. It reaches the user's log.</param>
    internal static byte[] Error(ReadOnlySpan<byte> idToken, int code, string message)
    {
        var buffer = new ArrayBufferWriter<byte>(96 + message.Length);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc"u8, "2.0"u8);
            WriteId(writer, idToken);
            writer.WriteStartObject("error"u8);
            writer.WriteNumber("code"u8, code);
            writer.WriteString("message"u8, message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Writes a successful response whose result is JSON <c>null</c>.
    /// </summary>
    /// <remarks>
    /// The <c>result</c> member has to be <em>present</em> and null rather than omitted: JSON-RPC
    /// identifies a response by the presence of <c>result</c> or <c>error</c>, and a message with
    /// neither is not a response at all — the peer keeps waiting for the real one.
    /// </remarks>
    /// <param name="idToken">The raw id token to echo.</param>
    internal static byte[] NullResult(ReadOnlySpan<byte> idToken) => RawResult(idToken, "null"u8);

    /// <summary>Writes a successful response around an already-encoded result.</summary>
    /// <param name="idToken">The raw id token to echo.</param>
    /// <param name="rawResult">The <c>result</c> member, as UTF-8 JSON.</param>
    internal static byte[] RawResult(ReadOnlySpan<byte> idToken, ReadOnlySpan<byte> rawResult)
    {
        var buffer = new ArrayBufferWriter<byte>(48 + rawResult.Length);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc"u8, "2.0"u8);
            WriteId(writer, idToken);
            writer.WritePropertyName("result"u8);
            writer.WriteRawValue(rawResult, skipInputValidation: true);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Writes a notification around an already-encoded parameter object.</summary>
    /// <param name="method">The method name.</param>
    /// <param name="rawParams">The <c>params</c> member as UTF-8 JSON, or empty to omit it.</param>
    internal static byte[] Notification(string method, ReadOnlySpan<byte> rawParams)
    {
        var buffer = new ArrayBufferWriter<byte>(48 + method.Length + rawParams.Length);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc"u8, "2.0"u8);
            writer.WriteString("method"u8, method);

            if (!rawParams.IsEmpty)
            {
                writer.WritePropertyName("params"u8);
                writer.WriteRawValue(rawParams, skipInputValidation: true);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Writes a request around an already-encoded parameter object.</summary>
    /// <param name="idToken">The raw id token this request is issued under.</param>
    /// <param name="method">The method name.</param>
    /// <param name="rawParams">The <c>params</c> member as UTF-8 JSON, or empty to omit it.</param>
    internal static byte[] Request(ReadOnlySpan<byte> idToken, string method, ReadOnlySpan<byte> rawParams)
    {
        var buffer = new ArrayBufferWriter<byte>(64 + method.Length + rawParams.Length);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc"u8, "2.0"u8);
            WriteId(writer, idToken);
            writer.WriteString("method"u8, method);

            if (!rawParams.IsEmpty)
            {
                writer.WritePropertyName("params"u8);
                writer.WriteRawValue(rawParams, skipInputValidation: true);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Emits the <c>id</c> member, defaulting an absent token to JSON null.</summary>
    private static void WriteId(Utf8JsonWriter writer, ReadOnlySpan<byte> idToken)
    {
        writer.WritePropertyName("id"u8);
        writer.WriteRawValue(idToken.IsEmpty ? NullToken : idToken, skipInputValidation: true);
    }
}
