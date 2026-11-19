using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Protocol;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Protocol;

/// <summary>
/// The depth-1 scan and the three-segment id rewrite that the whole pass-through design rests on.
/// </summary>
/// <remarks>
/// Two properties are load-bearing and neither is visible from a passing feature test. First, an
/// <c>id</c> member nested inside <c>params</c> must never be mistaken for the message's own — Roslyn
/// puts <c>TextDocument</c>, <c>data</c> and <c>SymbolKeyData</c> blocks inside requests (C18, C24),
/// and picking the wrong token would rewrite somebody's payload. Second, a forwarded message must
/// differ from the original in exactly the id token and nowhere else: a reserialisation would
/// renormalise numbers, reorder members and re-escape strings, and the peer has already been told
/// what those bytes are.
/// </remarks>
public class LspMessageScannerTests
{
    [Test]
    public void ANumericRequestIdIsReadAndLocated()
    {
        var body = Utf8("""{"jsonrpc":"2.0","id":42,"method":"textDocument/definition","params":{}}""");
        var info = LspMessageScanner.Scan(body);

        Assert.That(info.Kind, Is.EqualTo(LspMessageKind.Request));
        Assert.That(info.Method, Is.EqualTo("textDocument/definition"));
        Assert.That(info.Id, Is.EqualTo(JsonRpcId.FromNumber(42)));
        Assert.That(Encoding.UTF8.GetString(info.IdToken(body)), Is.EqualTo("42"));
    }

    [Test]
    public void AStringRequestIdKeepsItsQuotesInTheToken()
    {
        var body = Utf8("""{"jsonrpc":"2.0","id":"init-1","method":"initialize"}""");
        var info = LspMessageScanner.Scan(body);

        Assert.That(info.Kind, Is.EqualTo(LspMessageKind.Request));
        Assert.That(info.Id, Is.EqualTo(JsonRpcId.FromText("init-1")));

        // With the quotes: what gets spliced back in has to be a complete JSON token.
        Assert.That(Encoding.UTF8.GetString(info.IdToken(body)), Is.EqualTo("\"init-1\""));
    }

    /// <summary>
    /// An escaped character makes the token longer than the string it denotes, which is exactly the
    /// case a naive "length plus two" would get wrong.
    /// </summary>
    [Test]
    public void AnEscapedStringIdIsLocatedByItsEncodedLength()
    {
        var body = Utf8("""{"jsonrpc":"2.0","id":"a\"b\\c","method":"shutdown"}""");
        var info = LspMessageScanner.Scan(body);

        Assert.That(info.Id, Is.EqualTo(JsonRpcId.FromText("a\"b\\c")));
        Assert.That(Encoding.UTF8.GetString(info.IdToken(body)), Is.EqualTo("\"a\\\"b\\\\c\""));
    }

    /// <summary>
    /// JSON-RPC 2.0 forbids a null request id, so a message carrying one is a notification here — and
    /// answering it would produce a response no client can correlate.
    /// </summary>
    [Test]
    public void AnExplicitNullIdIsANotification()
    {
        var info = LspMessageScanner.Scan(Utf8("""{"jsonrpc":"2.0","id":null,"method":"exit"}"""));

        Assert.That(info.Kind, Is.EqualTo(LspMessageKind.Notification));
        Assert.That(info.Id.Kind, Is.EqualTo(JsonRpcIdKind.Null));
    }

    [Test]
    public void ANotificationHasNoIdToken()
    {
        var info = LspMessageScanner.Scan(Utf8("""{"jsonrpc":"2.0","method":"initialized","params":{}}"""));

        Assert.That(info.Kind, Is.EqualTo(LspMessageKind.Notification));
        Assert.That(info.HasIdToken, Is.False);
    }

    /// <summary>
    /// A response is told from an error response by which member it carries, and from a request by
    /// carrying neither a method nor one of those two.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="isError">Whether it is the error form.</param>
    [TestCase("""{"jsonrpc":"2.0","id":1,"result":[]}""", false)]
    [TestCase("""{"jsonrpc":"2.0","id":1,"result":null}""", false)]
    [TestCase("""{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"no"}}""", true)]
    public void AResponseIsRecognisedByWhichMemberItCarries(string message, bool isError)
    {
        var info = LspMessageScanner.Scan(Utf8(message));

        Assert.That(info.Kind, Is.EqualTo(isError ? LspMessageKind.ErrorResponse : LspMessageKind.Response));
        Assert.That(info.Method, Is.Null);
    }

    [TestCase("not json at all")]
    [TestCase("[1,2,3]")]
    [TestCase("{\"jsonrpc\":\"2.0\"}")]
    public void SomethingThatIsNotAJsonRpcMessageIsInvalidRatherThanThrowing(string message)
    {
        Assert.That(LspMessageScanner.Scan(Utf8(message)).Kind, Is.EqualTo(LspMessageKind.Invalid));
    }

    /// <summary>
    /// The one that matters. <c>params</c> here carries three nested <c>id</c> members, one of them
    /// before the message's own — a scanner that walked into objects would pick the wrong token and
    /// rewrite a call-hierarchy payload instead of the request id.
    /// </summary>
    [Test]
    public void AnIdNestedInsideParamsIsNotMistakenForTheMessagesOwn()
    {
        var body = Utf8("""
            {"jsonrpc":"2.0","params":{"id":999,"item":{"data":{"id":"inner","TextDocument":{"id":7}}}},"id":42,"method":"callHierarchy/incomingCalls"}
            """);

        var info = LspMessageScanner.Scan(body);

        Assert.That(info.Id, Is.EqualTo(JsonRpcId.FromNumber(42)));
        Assert.That(Encoding.UTF8.GetString(info.IdToken(body)), Is.EqualTo("42"));

        // And the rewrite lands on the right one.
        var rewritten = Encoding.UTF8.GetString(LspMessageScanner.RewriteId(body, info, "5"u8));

        Assert.That(rewritten, Contains.Substring("\"id\":999").Using(StringComparison.Ordinal));
        Assert.That(rewritten, Contains.Substring("\"id\":\"inner\"").Using(StringComparison.Ordinal));
        Assert.That(rewritten, Contains.Substring("\"id\":5,\"method\"").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>$/cancelRequest</c> is the one place the protocol puts an id one level down, so it gets its
    /// own reader — and it must not be confused by the notification's absence of an id of its own.
    /// </summary>
    [Test]
    public void TheCancelledIdIsReadOutOfParams()
    {
        Assert.That(LspMessageScanner.TryReadCancelRequestId(
            Utf8("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":17}}"""),
            out var numeric), Is.True);

        Assert.That(numeric, Is.EqualTo(JsonRpcId.FromNumber(17)));

        Assert.That(LspMessageScanner.TryReadCancelRequestId(
            Utf8("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":"abc"}}"""),
            out var text), Is.True);

        Assert.That(text, Is.EqualTo(JsonRpcId.FromText("abc")));

        Assert.That(LspMessageScanner.TryReadCancelRequestId(
            Utf8("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{}}"""),
            out _), Is.False);
    }

    /// <summary>
    /// A rewritten message differs from the original in the id token and in nothing else — asserted
    /// byte for byte, because "looks the same when reparsed" is a weaker claim than the design makes.
    /// </summary>
    [TestCase("""{"jsonrpc":"2.0","id":1,"method":"m","params":{"a":1.50,"b":"é"}}""", "1", "1000")]
    [TestCase("""{"jsonrpc":"2.0","id":"x","method":"m"}""", "\"x\"", "7")]
    [TestCase("""{"id":123456,"jsonrpc":"2.0","method":"m","params":[]}""", "123456", "1")]
    public void RewritingAnIdChangesOnlyTheIdToken(string message, string oldToken, string newToken)
    {
        var body = Utf8(message);
        var info = LspMessageScanner.Scan(body);
        var rewritten = LspMessageScanner.RewriteId(body, info, Utf8(newToken));

        var expected = Utf8(ReplaceFirst(message, "\"id\":" + oldToken, "\"id\":" + newToken));

        Assert.That(rewritten, Is.EqualTo(expected));
    }

    /// <summary>
    /// Ten thousand messages through the forward-and-restore cycle, comparing bytes. The adapter's
    /// central claim is that a request and its answer cross unchanged apart from one token, and this
    /// is the only test that can hold that claim at the scale where an off-by-one in the offsets
    /// would still be rare enough to look like flakiness.
    /// </summary>
    [Test]
    public void TenThousandMessagesSurviveAForwardAndRestoreCycleByteForByte()
    {
        for (var index = 0; index < 10_000; index++)
        {
            var original = Utf8(MessageNumber(index));
            var scanned = LspMessageScanner.Scan(original);

            Assert.That(scanned.Kind, Is.EqualTo(LspMessageKind.Request));

            // Forward: the peer's id token is remembered and replaced with the adapter's own.
            var remembered = scanned.IdToken(original).ToArray();
            var forwarded = LspMessageScanner.RewriteId(original, scanned, Encoding.UTF8.GetBytes((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));

            // Restore: the answer comes back under the adapter's id and is rewritten to the peer's.
            var forwardedInfo = LspMessageScanner.Scan(forwarded);
            var restored = LspMessageScanner.RewriteId(forwarded, forwardedInfo, remembered);

            Assert.That(restored, Is.EqualTo(original));
        }
    }

    /// <summary>
    /// Builds a message whose id shape and payload vary with the index.
    /// </summary>
    /// <remarks>
    /// Plain raw strings with placeholders rather than interpolated ones: an LSP message ends in a
    /// run of closing braces long enough that the number of <c>$</c> characters needed to escape it
    /// stops being readable, and the literal has to stay recognisable as the message it is.
    /// </remarks>
    /// <param name="index">Which message to build.</param>
    private static string MessageNumber(int index)
    {
        var id = Number(index);

        return (index % 4) switch
        {
            0 => Fill(
                """{"jsonrpc":"2.0","id":#N#,"method":"textDocument/definition","params":{"textDocument":{"uri":"file:///w/File#N#.cs"},"position":{"line":#L#,"character":3}}}""",
                ("#N#", id),
                ("#L#", Number(index % 97))),

            1 => Fill(
                """{"jsonrpc":"2.0","id":"req-#N#","method":"textDocument/hover","params":{"id":#N#,"nested":{"id":"decoy"}}}""",
                ("#N#", id)),

            2 => Fill(
                """{"method":"workspace/symbol","id":#N#,"jsonrpc":"2.0","params":{"query":"Circleé#N#"}}""",
                ("#N#", id)),

            _ => Fill(
                """{"jsonrpc":"2.0","id":#N#,"method":"codeAction/resolve","params":{"data":{"UniqueIdentifier":"a\b\"c","Range":{"start":{"line":1,"character":0}}}}}""",
                ("#N#", id)),
        };
    }

    /// <summary>Renders an integer the way a JSON number token is written.</summary>
    private static string Number(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Substitutes into a plain raw-string JSON template.</summary>
    /// <param name="template">The JSON, with <c>#NAME#</c> placeholders.</param>
    /// <param name="values">What to put in them.</param>
    private static string Fill(string template, params (string Token, string Value)[] values)
    {
        foreach (var (token, value) in values)
        {
            template = template.Replace(token, value, StringComparison.Ordinal);
        }

        return template;
    }

    /// <summary>Replaces the first occurrence, so a decoy later in the message is left alone.</summary>
    private static string ReplaceFirst(string text, string search, string replacement)
    {
        var index = text.IndexOf(search, StringComparison.Ordinal);
        Assert.That(index >= 0, Is.True, $"'{search}' is not in '{text}'.");

        return string.Concat(text.AsSpan(0, index), replacement, text.AsSpan(index + search.Length));
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}

/// <summary>The id value type: how it compares, hashes and renders.</summary>
public class JsonRpcIdTests
{
    [Test]
    public void NumbersAndStringsAreDistinctEvenWhenTheyLookAlike()
    {
        Assert.That(JsonRpcId.FromText("7"), Is.Not.EqualTo(JsonRpcId.FromNumber(7)));
        Assert.That(JsonRpcId.FromNumber(7), Is.EqualTo(JsonRpcId.FromNumber(7)));
        Assert.That(JsonRpcId.FromText("7"), Is.EqualTo(JsonRpcId.FromText("7")));
    }

    /// <summary>
    /// Both forms key the correlation tables, so a dictionary has to keep them apart. A client using
    /// string ids and an adapter using numeric ones is the ordinary case, not an exotic one.
    /// </summary>
    [Test]
    public void BothFormsCanKeyADictionaryWithoutColliding()
    {
        var table = new Dictionary<JsonRpcId, string>
        {
            [JsonRpcId.FromNumber(1)] = "number",
            [JsonRpcId.FromText("1")] = "text",
            [JsonRpcId.Null] = "null",
        };

        Assert.That(table[JsonRpcId.FromNumber(1)], Is.EqualTo("number"));
        Assert.That(table[JsonRpcId.FromText("1")], Is.EqualTo("text"));
        Assert.That(table[JsonRpcId.Null], Is.EqualTo("null"));
    }

    [Test]
    public void OnlyNumbersAndStringsCanCorrelateARequest()
    {
        Assert.That(JsonRpcId.FromNumber(1).IsCorrelatable, Is.True);
        Assert.That(JsonRpcId.FromText("a").IsCorrelatable, Is.True);
        Assert.That(JsonRpcId.Null.IsCorrelatable, Is.False);
        Assert.That(JsonRpcId.Absent.IsCorrelatable, Is.False);
    }

    [Test]
    public void OnlyAnIdThisAdapterCouldHaveIssuedReadsBackAsAnInteger()
    {
        Assert.That(JsonRpcId.FromNumber(11).TryGetInt32(out var value), Is.True);
        Assert.That(value, Is.EqualTo(11));

        Assert.That(JsonRpcId.FromText("11").TryGetInt32(out _), Is.False);
        Assert.That(JsonRpcId.FromNumber(long.MaxValue).TryGetInt32(out _), Is.False);
        Assert.That(JsonRpcId.Absent.TryGetInt32(out _), Is.False);
    }

    [Test]
    public void AStringIdIsEncodedAsAQuotedEscapedToken()
    {
        var token = Encoding.UTF8.GetString(JsonRpcId.FromText("a\"b").ToTokenBytes());

        using var document = JsonDocument.Parse(token);

        Assert.That(document.RootElement.ValueKind, Is.EqualTo(JsonValueKind.String));
        Assert.That(document.RootElement.GetString(), Is.EqualTo("a\"b"));
    }
}
