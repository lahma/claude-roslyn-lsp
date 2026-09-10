using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeRoslynLsp.Protocol;

/// <summary>
/// The JSON-RPC constants the LSP base protocol fixes, in one place so no message shape restates
/// them.
/// </summary>
internal static class JsonRpc
{
    /// <summary>The only protocol version JSON-RPC 2.0 messages carry.</summary>
    internal const string Version = "2.0";

    /// <summary>The body was not valid JSON. There is no id to answer under, so the id is null.</summary>
    internal const int ParseError = -32700;

    /// <summary>The method exists in no version of this server. Answered, never ignored.</summary>
    /// <remarks>
    /// A request the peer never gets an answer to is worse than one it is refused: LSP clients wait,
    /// and Claude Code's own client only retries a handful of times within a few seconds before it
    /// gives up on the server entirely.
    /// </remarks>
    internal const int MethodNotFound = -32601;

    /// <summary>The server could not answer. Reserved for the mediation layer (WP4).</summary>
    internal const int InternalError = -32603;

    /// <summary>
    /// A JSON <c>null</c>, for the two places the protocol requires the member to be present and
    /// empty: a response to a request whose result is nothing, and an error answer to a message whose
    /// id could not be read.
    /// </summary>
    /// <remarks>
    /// Cloned out of its document deliberately — a <see cref="JsonElement"/> that still points into a
    /// live <see cref="JsonDocument"/> would be invalid the moment that document was disposed, and
    /// this one outlives the process.
    /// </remarks>
    internal static JsonElement Null { get; } = JsonDocument.Parse("null").RootElement.Clone();
}

/// <summary>
/// Just enough of an inbound message to route it: the method, and whether it carries an id.
/// </summary>
/// <remarks>
/// <para>
/// The <c>params</c> member is deliberately absent. The adapter's design (D1) is that everything
/// which is not a handshake, registration, configuration, progress or diagnostics message crosses as
/// raw bytes, so deserialising parameters into typed shapes would be work done to throw away — and
/// worse, it would put this repository in the business of tracking the parameter shape of every LSP
/// request Roslyn implements.
/// </para>
/// <para>
/// The id is kept as a <see cref="JsonElement"/> so it round-trips exactly. JSON-RPC allows a number
/// or a string, and a response must echo the id it was given, in the form it was given: rewriting
/// <c>"1"</c> as <c>1</c> is a correlation failure the client reports as a request that never came
/// back.
/// </para>
/// </remarks>
internal sealed record IncomingMessage
{
    /// <summary>The <c>jsonrpc</c> member, which must read <c>2.0</c>.</summary>
    [JsonPropertyName("jsonrpc")]
    public string? JsonRpcVersion { get; init; }

    /// <summary>The request id, or an undefined element for a notification.</summary>
    [JsonPropertyName("id")]
    public JsonElement Id { get; init; }

    /// <summary>The method name.</summary>
    [JsonPropertyName("method")]
    public string? Method { get; init; }

    /// <summary>
    /// True when this message expects an answer.
    /// </summary>
    /// <remarks>
    /// An explicit <c>"id": null</c> counts as a notification here rather than as a request: JSON-RPC
    /// 2.0 forbids a null request id, and answering one would produce a response no client can
    /// correlate.
    /// </remarks>
    [JsonIgnore]
    public bool IsRequest => Id.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
}

/// <summary>The answer to <c>initialize</c>.</summary>
internal sealed record InitializeResponse
{
    /// <summary>The JSON-RPC version.</summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpcVersion { get; init; } = JsonRpc.Version;

    /// <summary>The id of the request being answered, echoed exactly.</summary>
    [JsonPropertyName("id")]
    public required JsonElement Id { get; init; }

    /// <summary>The capabilities and identity this server advertises.</summary>
    [JsonPropertyName("result")]
    public required InitializeResult Result { get; init; }
}

/// <summary>What the client learns about this server from the handshake.</summary>
internal sealed record InitializeResult
{
    /// <summary>The capabilities the server advertises.</summary>
    [JsonPropertyName("capabilities")]
    public required ServerCapabilities Capabilities { get; init; }

    /// <summary>The server's name and version.</summary>
    [JsonPropertyName("serverInfo")]
    public required ServerInfo ServerInfo { get; init; }
}

/// <summary>
/// The capability document. In this work package it says only how documents are synchronised.
/// </summary>
/// <remarks>
/// Advertising a capability is a promise to answer the requests it enables, so the stub advertises
/// the minimum that keeps a client's document lifecycle well-formed and nothing else. WP4 replaces
/// this with the authored document — and deliberately still leaves semantic tokens, inlay hints and
/// code lens out, so that Roslyn never registers or refreshes them.
/// </remarks>
internal sealed record ServerCapabilities
{
    /// <summary>How the client should send document contents.</summary>
    [JsonPropertyName("textDocumentSync")]
    public required TextDocumentSyncOptions TextDocumentSync { get; init; }
}

/// <summary>Document synchronisation options.</summary>
internal sealed record TextDocumentSyncOptions
{
    /// <summary>Whether the client should send <c>didOpen</c> and <c>didClose</c>.</summary>
    [JsonPropertyName("openClose")]
    public required bool OpenClose { get; init; }

    /// <summary>
    /// The <c>TextDocumentSyncKind</c>: 0 none, 1 full, 2 incremental.
    /// </summary>
    /// <remarks>
    /// Full (1), not incremental (2). Roslyn accepts full-text <c>didChange</c>, and a mirror the
    /// adapter can replay verbatim after a Roslyn crash is worth far more than the bytes incremental
    /// sync would save — reconstructing a document from a range-edit history that was interrupted
    /// halfway is the kind of bug that shows up as wrong answers rather than as an error.
    /// </remarks>
    [JsonPropertyName("change")]
    public required int Change { get; init; }

    /// <summary>Whether and how the client should send <c>didSave</c>.</summary>
    [JsonPropertyName("save")]
    public required SaveOptions Save { get; init; }
}

/// <summary>Save-notification options.</summary>
internal sealed record SaveOptions
{
    /// <summary>
    /// Whether <c>didSave</c> carries the document text. False: the adapter's mirror already holds
    /// it, so including it would double the bytes on every save for no new information.
    /// </summary>
    [JsonPropertyName("includeText")]
    public required bool IncludeText { get; init; }
}

/// <summary>The server's identity, as reported in the handshake.</summary>
internal sealed record ServerInfo
{
    /// <summary>The server name. Asserted by <c>SmokeTest</c>'s LSP leg on every release RID.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>The server version, which the build stamps from <c>CHANGELOG.md</c>.</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

/// <summary>
/// A successful response whose result is nothing — which is what <c>shutdown</c> answers.
/// </summary>
/// <remarks>
/// The <c>result</c> member has to be <em>present</em> and null, not omitted: JSON-RPC distinguishes
/// a response by the presence of <c>result</c> or <c>error</c>, and a message with neither is not a
/// response at all.
/// </remarks>
internal sealed record NullResultResponse
{
    /// <summary>The JSON-RPC version.</summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpcVersion { get; init; } = JsonRpc.Version;

    /// <summary>The id of the request being answered, echoed exactly.</summary>
    [JsonPropertyName("id")]
    public required JsonElement Id { get; init; }

    /// <summary>Always JSON null.</summary>
    [JsonPropertyName("result")]
    public JsonElement Result { get; init; } = JsonRpc.Null;
}

/// <summary>An error response.</summary>
internal sealed record ErrorResponse
{
    /// <summary>The JSON-RPC version.</summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpcVersion { get; init; } = JsonRpc.Version;

    /// <summary>
    /// The id being answered, or JSON null when the message was too malformed to read one.
    /// </summary>
    [JsonPropertyName("id")]
    public required JsonElement Id { get; init; }

    /// <summary>The failure.</summary>
    [JsonPropertyName("error")]
    public required ResponseError Error { get; init; }
}

/// <summary>The <c>error</c> member of an error response.</summary>
internal sealed record ResponseError
{
    /// <summary>One of the JSON-RPC or LSP error codes.</summary>
    [JsonPropertyName("code")]
    public required int Code { get; init; }

    /// <summary>A short, human-readable explanation.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}
