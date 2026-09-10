using System.Text.Json;

namespace ClaudeRoslynLsp.Protocol;

/// <summary>What a scanned message turned out to be.</summary>
internal enum LspMessageKind
{
    /// <summary>Not a JSON object, or an object with neither a method nor a result nor an error.</summary>
    Invalid,

    /// <summary>A method call carrying a correlatable id.</summary>
    Request,

    /// <summary>A method call with no id, which must never be answered.</summary>
    Notification,

    /// <summary>A successful answer: a <c>result</c> member is present.</summary>
    Response,

    /// <summary>A failed answer: an <c>error</c> member is present.</summary>
    ErrorResponse,
}

/// <summary>
/// Everything the adapter needs to route one message, and the byte span of its id so the message can
/// be forwarded with only that token replaced.
/// </summary>
/// <param name="Kind">Request, notification, response or error.</param>
/// <param name="Method">The method name, or <see langword="null"/> for a response.</param>
/// <param name="Id">The id as a comparable value.</param>
/// <param name="IdTokenStart">Byte offset of the id token in the message body, or <c>-1</c>.</param>
/// <param name="IdTokenLength">Byte length of the id token, including a string's quotes.</param>
internal readonly record struct LspMessageInfo(
    LspMessageKind Kind,
    string? Method,
    JsonRpcId Id,
    int IdTokenStart,
    int IdTokenLength)
{
    /// <summary>True when the message carries an id token that can be rewritten.</summary>
    internal bool HasIdToken => IdTokenStart >= 0;

    /// <summary>The id exactly as it was written, for restoring it on the way back.</summary>
    /// <param name="body">The body this info was scanned from.</param>
    internal ReadOnlySpan<byte> IdToken(ReadOnlySpan<byte> body) =>
        HasIdToken ? body.Slice(IdTokenStart, IdTokenLength) : default;
}

/// <summary>
/// A single depth-1 pass over a raw message body that reports what the message is and where its id
/// token sits, without materialising <c>params</c> or <c>result</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece that makes the adapter's pass-through design (D1) affordable. The overwhelming
/// majority of what crosses this process is a navigation request or its answer, and the only thing
/// that has to change on the way is the JSON-RPC id: deserialising a <c>workspace/symbol</c> result
/// into typed shapes so it could be re-serialised identically would put this repository in the
/// business of tracking the parameter and result shape of every request Roslyn implements — and
/// would lose the parts it did not model.
/// </para>
/// <para>
/// <b>Depth is the whole correctness argument.</b> A <c>callHierarchy/incomingCalls</c> request
/// carries an <c>item.data.TextDocument</c> object, a <c>codeAction/resolve</c> carries
/// <c>data.UniqueIdentifier</c>, and Roslyn's own progress payloads nest freely (C18, C24) — any of
/// which may contain a member spelled <c>id</c>. Only a property name read at
/// <see cref="Utf8JsonReader.CurrentDepth"/> 1 is the message's own, so every non-primitive value at
/// that depth is skipped wholesale rather than walked into.
/// </para>
/// <para>
/// The reader is constructed over a span, never a sequence, so
/// <see cref="Utf8JsonReader.TokenStartIndex"/> and <see cref="Utf8JsonReader.BytesConsumed"/> are
/// offsets into the caller's body and the id token can be sliced straight out of it.
/// </para>
/// </remarks>
internal static class LspMessageScanner
{
    /// <summary>Classifies one raw message body.</summary>
    /// <param name="body">The UTF-8 body, exactly as it arrived inside its frame.</param>
    /// <returns>
    /// The routing facts. A body that is not parseable JSON returns
    /// <see cref="LspMessageKind.Invalid"/> rather than throwing: framing is still intact, so the
    /// connection survives it and the peer gets an error answer.
    /// </returns>
    internal static LspMessageInfo Scan(ReadOnlySpan<byte> body)
    {
        try
        {
            return ScanCore(body);
        }
        catch (JsonException)
        {
            return new LspMessageInfo(LspMessageKind.Invalid, null, JsonRpcId.Absent, -1, 0);
        }
    }

    /// <summary>
    /// Produces a copy of <paramref name="body"/> with its id token replaced by
    /// <paramref name="idToken"/> and every other byte identical.
    /// </summary>
    /// <remarks>
    /// Three segments — everything before the token, the new token, everything after — because a
    /// reserialisation would reorder members, renormalise numbers and re-escape strings, and the
    /// peer that receives it has already been told what those bytes are by whoever wrote them.
    /// </remarks>
    /// <param name="body">The original message.</param>
    /// <param name="info">Its scan, which must have an id token.</param>
    /// <param name="idToken">The replacement token, already JSON-encoded.</param>
    internal static byte[] RewriteId(ReadOnlySpan<byte> body, in LspMessageInfo info, ReadOnlySpan<byte> idToken)
    {
        if (!info.HasIdToken)
        {
            throw new InvalidOperationException("The message carries no id token to rewrite.");
        }

        var rewritten = new byte[body.Length - info.IdTokenLength + idToken.Length];

        body[..info.IdTokenStart].CopyTo(rewritten);
        idToken.CopyTo(rewritten.AsSpan(info.IdTokenStart));
        body[(info.IdTokenStart + info.IdTokenLength)..]
            .CopyTo(rewritten.AsSpan(info.IdTokenStart + idToken.Length));

        return rewritten;
    }

    /// <summary>
    /// Reads the <c>params.id</c> of a <c>$/cancelRequest</c>, which is the one id in the protocol
    /// that lives one level below the message's own.
    /// </summary>
    /// <param name="body">The notification body.</param>
    /// <param name="id">The id being cancelled.</param>
    /// <returns>True when a correlatable id was found.</returns>
    internal static bool TryReadCancelRequestId(ReadOnlySpan<byte> body, out JsonRpcId id)
    {
        id = JsonRpcId.Absent;

        try
        {
            var reader = new Utf8JsonReader(body, isFinalBlock: true, state: default);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }

                var isParams = reader.ValueTextEquals("params"u8);

                if (!reader.Read())
                {
                    return false;
                }

                if (!isParams || reader.TokenType != JsonTokenType.StartObject)
                {
                    SkipValue(ref reader);
                    continue;
                }

                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 2)
                    {
                        continue;
                    }

                    var isId = reader.ValueTextEquals("id"u8);

                    if (!reader.Read())
                    {
                        return false;
                    }

                    if (isId)
                    {
                        id = JsonRpcId.Read(ref reader);
                        return id.IsCorrelatable;
                    }

                    SkipValue(ref reader);
                }

                return false;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The scan proper, with JSON errors left to the caller to absorb.</summary>
    /// <param name="body">The UTF-8 body.</param>
    private static LspMessageInfo ScanCore(ReadOnlySpan<byte> body)
    {
        var reader = new Utf8JsonReader(body, isFinalBlock: true, state: default);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return new LspMessageInfo(LspMessageKind.Invalid, null, JsonRpcId.Absent, -1, 0);
        }

        string? method = null;
        var id = JsonRpcId.Absent;
        var idStart = -1;
        var idLength = 0;
        var hasResult = false;
        var hasError = false;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            // Depth 1 is the message object's own members. Anything deeper belongs to params,
            // result or error and is none of this scanner's business — see the class remarks.
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
            {
                continue;
            }

            var isId = reader.ValueTextEquals("id"u8);
            var isMethod = !isId && reader.ValueTextEquals("method"u8);
            var isResult = !isId && !isMethod && reader.ValueTextEquals("result"u8);
            var isError = !isId && !isMethod && !isResult && reader.ValueTextEquals("error"u8);

            if (!reader.Read())
            {
                break;
            }

            if (isId)
            {
                idStart = (int) reader.TokenStartIndex;
                idLength = (int) (reader.BytesConsumed - reader.TokenStartIndex);
                id = JsonRpcId.Read(ref reader);
            }
            else if (isMethod && reader.TokenType == JsonTokenType.String)
            {
                method = reader.GetString();
            }
            else if (isResult)
            {
                hasResult = true;
            }
            else if (isError)
            {
                hasError = true;
            }

            SkipValue(ref reader);
        }

        var kind = method is not null
            ? id.IsCorrelatable ? LspMessageKind.Request : LspMessageKind.Notification
            : hasError ? LspMessageKind.ErrorResponse
            : hasResult ? LspMessageKind.Response
            : LspMessageKind.Invalid;

        return new LspMessageInfo(kind, method, id, idStart, idLength);
    }

    /// <summary>Advances past a composite value; a no-op on a primitive one.</summary>
    /// <param name="reader">The reader, positioned on a value token.</param>
    private static void SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }
    }
}
