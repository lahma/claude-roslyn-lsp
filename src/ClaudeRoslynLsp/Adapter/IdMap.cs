using System.Globalization;
using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Protocol;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>One request this adapter is waiting on an answer for.</summary>
/// <remarks>
/// Exactly one of <see cref="OriginalIdToken"/> and <see cref="Completion"/> is set. The first means
/// the request came from a peer and its answer has to go back with the id that peer used; the second
/// means the adapter asked the question itself and wants the answer in code.
/// </remarks>
internal sealed class PendingRequest
{
    /// <summary>The method, for log lines and for deciding what a failure means.</summary>
    internal required string Method { get; init; }

    /// <summary>The originating peer's id token, byte for byte, or null for an adapter request.</summary>
    internal byte[]? OriginalIdToken { get; init; }

    /// <summary>The originating peer's id as a value, for cancellation lookup.</summary>
    internal JsonRpcId OriginalId { get; init; }

    /// <summary>
    /// The peer's message exactly as it arrived, kept so the request can be sent again under a fresh
    /// id after the backend was relaunched (D74).
    /// </summary>
    /// <remarks>
    /// Only forwarded, peer-originated requests carry one. An adapter-originated request has a
    /// <see cref="Completion"/> instead and simply fails: every one of them — a diagnostic pull, a
    /// handshake — is something the adapter re-issues on its own schedule once the workspace is back,
    /// whereas a peer request has a client waiting on it that will wait forever if it is not answered
    /// and will get a wrong answer if it is answered <c>-32603</c> for a backend that came back.
    /// </remarks>
    internal byte[]? Body { get; init; }

    /// <summary>Where an adapter-originated request's answer goes, or null for a forwarded one.</summary>
    internal TaskCompletionSource<JsonElement>? Completion { get; init; }
}

/// <summary>
/// The correlation table for one direction: peer ids in, adapter ids out, and back again.
/// </summary>
/// <remarks>
/// <para>
/// Two peers that have never met are both counting from one. Claude Code's <c>initialize</c> is id
/// 0 or 1; so is the adapter's own <c>initialize</c> to Roslyn. Forwarding an id unchanged would
/// therefore let a client request and an adapter request collide on the same number, and the answer
/// to one would be delivered as the answer to the other — a failure that produces a wrong answer,
/// not an error. So every request that leaves this adapter leaves under an id this table minted, and
/// the original is restored on the way back from the bytes it arrived in
/// (<see cref="LspMessageScanner.RewriteId"/>).
/// </para>
/// <para>
/// One counter serves both kinds of outbound request — forwarded and adapter-originated — which is
/// what makes collisions structurally impossible rather than merely unlikely.
/// </para>
/// <para>
/// There is one of these per direction. Roslyn asks the client questions too
/// (<c>workspace/applyEdit</c>), and those ids need exactly the same treatment mirrored.
/// </para>
/// </remarks>
internal sealed class IdMap
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, PendingRequest> _byOutboundId = [];
    private readonly Dictionary<JsonRpcId, int> _byOriginalId = [];

    private int _nextOutboundId;

    /// <summary>How many requests are currently outstanding. Used by shutdown and by tests.</summary>
    internal int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _byOutboundId.Count;
            }
        }
    }

    /// <summary>
    /// Records a request arriving from a peer and returns the id it should be forwarded under.
    /// </summary>
    /// <param name="originalId">The peer's id, as a value.</param>
    /// <param name="originalIdToken">The peer's id, as the bytes it wrote.</param>
    /// <param name="method">The method being forwarded.</param>
    /// <param name="body">
    /// The peer's message as it arrived, so a backend that dies mid-request can be relaunched and the
    /// request replayed rather than refused (D74). Null keeps the entry un-replayable, which is what
    /// the client-bound direction wants: a client that has gone away is the end of the session.
    /// </param>
    internal int Forward(
        JsonRpcId originalId,
        ReadOnlySpan<byte> originalIdToken,
        string method,
        byte[]? body = null)
    {
        ArgumentNullException.ThrowIfNull(method);

        var pending = new PendingRequest
        {
            Method = method,
            OriginalIdToken = originalIdToken.ToArray(),
            OriginalId = originalId,
            Body = body,
        };

        lock (_lock)
        {
            var outbound = ++_nextOutboundId;
            _byOutboundId[outbound] = pending;

            // A peer that reuses an id it has an answer outstanding for is out of contract. The last
            // one wins for cancellation lookup; both still get their own answer back, because the
            // answers are keyed on the outbound id.
            _byOriginalId[originalId] = outbound;

            return outbound;
        }
    }

    /// <summary>Records a request this adapter is issuing itself and returns its id.</summary>
    /// <param name="method">The method being sent.</param>
    /// <param name="completion">Where the answer is delivered.</param>
    internal int Register(string method, TaskCompletionSource<JsonElement> completion)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(completion);

        var pending = new PendingRequest { Method = method, Completion = completion };

        lock (_lock)
        {
            var outbound = ++_nextOutboundId;
            _byOutboundId[outbound] = pending;
            return outbound;
        }
    }

    /// <summary>Takes the pending request an answer belongs to, removing it from the table.</summary>
    /// <param name="outboundId">The id the answer came back under.</param>
    /// <param name="pending">The request, when one was outstanding.</param>
    internal bool TryComplete(int outboundId, out PendingRequest pending)
    {
        lock (_lock)
        {
            if (!_byOutboundId.Remove(outboundId, out var found))
            {
                pending = null!;
                return false;
            }

            if (found.OriginalIdToken is not null
                && _byOriginalId.TryGetValue(found.OriginalId, out var mapped)
                && mapped == outboundId)
            {
                _byOriginalId.Remove(found.OriginalId);
            }

            pending = found;
            return true;
        }
    }

    /// <summary>
    /// Finds the outbound id a peer's id was forwarded under, without removing the entry.
    /// </summary>
    /// <remarks>
    /// What <c>$/cancelRequest</c> needs: the cancellation has to name the id the <em>other</em>
    /// peer knows, and the request itself stays outstanding until it is actually answered.
    /// </remarks>
    /// <param name="originalId">The id the peer used.</param>
    /// <param name="outboundId">The id it was forwarded under.</param>
    internal bool TryResolveOutboundId(JsonRpcId originalId, out int outboundId)
    {
        lock (_lock)
        {
            return _byOriginalId.TryGetValue(originalId, out outboundId);
        }
    }

    /// <summary>Empties the table and returns everything that was outstanding.</summary>
    /// <remarks>
    /// The backend going away has to leave no request unanswered: a peer waits on an outstanding
    /// request indefinitely, so every entry here becomes an error answer rather than silence.
    /// </remarks>
    internal IReadOnlyList<PendingRequest> DrainAll()
    {
        lock (_lock)
        {
            var drained = _byOutboundId.Values.ToArray();
            _byOutboundId.Clear();
            _byOriginalId.Clear();
            return drained;
        }
    }

    /// <summary>Renders an outbound id as the UTF-8 bytes of a JSON number token.</summary>
    /// <param name="outboundId">The id.</param>
    internal static byte[] TokenFor(int outboundId) =>
        Encoding.UTF8.GetBytes(outboundId.ToString(CultureInfo.InvariantCulture));
}
