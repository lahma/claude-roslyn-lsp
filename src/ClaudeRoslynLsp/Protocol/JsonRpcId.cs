using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClaudeRoslynLsp.Protocol;

/// <summary>Which of the three shapes JSON-RPC allows a message id carries.</summary>
internal enum JsonRpcIdKind
{
    /// <summary>There was no <c>id</c> member at all — the message is a notification.</summary>
    Absent,

    /// <summary>A JSON number.</summary>
    Number,

    /// <summary>A JSON string.</summary>
    Text,

    /// <summary>An explicit <c>null</c>, which JSON-RPC 2.0 forbids on a request.</summary>
    Null,
}

/// <summary>
/// A JSON-RPC message id: a number, a string, or null — compared and hashed so it can key the
/// adapter's correlation tables.
/// </summary>
/// <remarks>
/// <para>
/// The adapter needs an id as a <em>value</em> in exactly two places: to find a held request again
/// when <c>$/cancelRequest</c> names it, and to key the map from Claude's ids to the ids this
/// adapter gives Roslyn. Everywhere else the id crosses as the original bytes, which is why
/// <see cref="LspMessageScanner"/> reports a token span alongside this: re-encoding <c>"7"</c> as
/// <c>7</c>, or <c>1e2</c> as <c>100</c>, would be a correlation failure the peer experiences as a
/// request that never came back.
/// </para>
/// <para>
/// A number id is kept as <see cref="long"/>. JSON-RPC does not bound the numeric range, but every
/// client in practice counts up from one, and a peer that sends a fractional or astronomically large
/// id still round-trips correctly because the <em>bytes</em> are what get restored — this value is
/// only used for lookup, and <see cref="JsonRpcIdKind.Absent"/> is the honest answer when the token
/// will not fit.
/// </para>
/// </remarks>
internal readonly struct JsonRpcId : IEquatable<JsonRpcId>
{
    private readonly long _number;
    private readonly string? _text;

    private JsonRpcId(JsonRpcIdKind kind, long number, string? text)
    {
        Kind = kind;
        _number = number;
        _text = text;
    }

    /// <summary>The shape this id carries.</summary>
    internal JsonRpcIdKind Kind { get; }

    /// <summary>An id that was not present.</summary>
    internal static JsonRpcId Absent => default;

    /// <summary>An explicit JSON <c>null</c> id.</summary>
    internal static JsonRpcId Null { get; } = new(JsonRpcIdKind.Null, 0, null);

    /// <summary>
    /// True when this id can correlate a request with a response — that is, it is a number or a
    /// string. An absent id is a notification and an explicit null is not a legal request id.
    /// </summary>
    internal bool IsCorrelatable => Kind is JsonRpcIdKind.Number or JsonRpcIdKind.Text;

    /// <summary>Creates a numeric id.</summary>
    /// <param name="value">The number.</param>
    internal static JsonRpcId FromNumber(long value) => new(JsonRpcIdKind.Number, value, null);

    /// <summary>Creates a string id.</summary>
    /// <param name="value">The string.</param>
    internal static JsonRpcId FromText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new JsonRpcId(JsonRpcIdKind.Text, 0, value);
    }

    /// <summary>
    /// Reads this id as the <see cref="int"/> the adapter mints its own ids as.
    /// </summary>
    /// <param name="value">The number, when this is one that fits.</param>
    /// <returns>
    /// False for a string, a null, an absent id, or a number outside <see cref="int"/> — none of
    /// which this adapter can have issued, so a response carrying one is not ours to route.
    /// </returns>
    internal bool TryGetInt32(out int value)
    {
        if (Kind == JsonRpcIdKind.Number && _number is >= int.MinValue and <= int.MaxValue)
        {
            value = (int) _number;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>Reads an id from a reader positioned on the value of an <c>id</c> member.</summary>
    /// <param name="reader">The reader, positioned on the id's value token.</param>
    internal static JsonRpcId Read(ref Utf8JsonReader reader) =>
        reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt64(out var number) => FromNumber(number),

            // A number that is not an Int64 — fractional, or wider than the type. It is still a
            // legal id and still crosses byte-exactly; it simply cannot be looked up, which only
            // costs a cancellation that finds nothing.
            JsonTokenType.Number => Absent,
            JsonTokenType.String => FromText(reader.GetString()!),
            JsonTokenType.Null => Null,
            _ => Absent,
        };

    /// <summary>Reads an id out of an already-parsed element.</summary>
    /// <param name="element">The <c>id</c> member.</param>
    internal static JsonRpcId FromElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var number) => FromNumber(number),
            JsonValueKind.Number => Absent,
            JsonValueKind.String => FromText(element.GetString()!),
            JsonValueKind.Null => Null,
            _ => Absent,
        };

    /// <summary>
    /// Renders this id as the UTF-8 bytes of a JSON token, ready to be spliced into a message body.
    /// </summary>
    /// <remarks>
    /// Only ever used for ids this adapter <em>invented</em> (which are always numbers) and for the
    /// rare error answer to a peer id that was parsed rather than captured. An id that came off the
    /// wire is restored from its original bytes instead — see <see cref="LspMessageScanner"/>.
    /// </remarks>
    internal byte[] ToTokenBytes() =>
        Kind switch
        {
            JsonRpcIdKind.Number => Encoding.UTF8.GetBytes(_number.ToString(CultureInfo.InvariantCulture)),
            JsonRpcIdKind.Text => EncodeText(_text!),
            _ => "null"u8.ToArray(),
        };

    /// <inheritdoc />
    public bool Equals(JsonRpcId other) =>
        Kind == other.Kind
        && _number == other._number
        && string.Equals(_text, other._text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is JsonRpcId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        Kind switch
        {
            JsonRpcIdKind.Number => HashCode.Combine(Kind, _number),
            JsonRpcIdKind.Text => HashCode.Combine(Kind, StringComparer.Ordinal.GetHashCode(_text!)),
            _ => (int) Kind,
        };

    /// <summary>A short rendering for log lines. Never used on the wire.</summary>
    public override string ToString() =>
        Kind switch
        {
            JsonRpcIdKind.Number => _number.ToString(CultureInfo.InvariantCulture),
            JsonRpcIdKind.Text => "\"" + _text + "\"",
            JsonRpcIdKind.Null => "null",
            _ => "(none)",
        };

    /// <summary>Value equality.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public static bool operator ==(JsonRpcId left, JsonRpcId right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public static bool operator !=(JsonRpcId left, JsonRpcId right) => !left.Equals(right);

    /// <summary>Encodes a string id as a quoted, escaped JSON token.</summary>
    private static byte[] EncodeText(string text)
    {
        // Through the writer rather than by hand, because a quote, a backslash or a control
        // character in an id has to come out escaped and getting that wrong produces a message the
        // peer cannot parse at all.
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(text.Length + 8);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(text);
        }

        return buffer.WrittenSpan.ToArray();
    }
}
