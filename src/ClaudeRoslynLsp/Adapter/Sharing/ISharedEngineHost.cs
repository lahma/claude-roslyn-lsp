namespace ClaudeRoslynLsp.Adapter.Sharing;

/// <summary>
/// What <see cref="SharedRoslynHost"/> needs from whichever half of this product owns the Roslyn it
/// is multiplexing.
/// </summary>
/// <remarks>
/// <para>
/// <b>D87 — the host is a component of its owner, not a session of its own.</b> Both
/// <see cref="AdapterSession"/> and <c>Mcp/Engine/OwnedRoslynEngine</c> can be the process that
/// launched Roslyn, and D75 declined to extract a common session out of them on the evidence that
/// their glue does different jobs. This interface is the seam that WP5b's note said was the one
/// worth cutting: it is six members wide, every one of them something both classes already do for
/// themselves, and neither class had to be restructured to expose them.
/// </para>
/// <para>
/// The members deliberately do <em>not</em> include anything about serving a peer. The multiplexer
/// owns the pipe, the frames, the per-client id tables and the document ledger; the owner owns the
/// one connection to Roslyn, the readiness gate and the configuration. That split is what keeps a
/// second attached client from being able to do anything the host itself would not do.
/// </para>
/// </remarks>
internal interface ISharedEngineHost
{
    /// <summary>
    /// The gate an attached client's request is held by, exactly as the host's own requests are
    /// (D46).
    /// </summary>
    /// <remarks>
    /// Not a copy and not a second gate: a request forwarded from an attached client before
    /// <c>workspace/projectInitializationComplete</c> would be answered by Roslyn with an empty
    /// successful result (C27), which is the failure the gate exists to prevent and is not made any
    /// less wrong by having arrived over a pipe.
    /// </remarks>
    ReadinessGate Gate { get; }

    /// <summary>
    /// The host's document mirror, which an attached client's opens and edits are recorded in too.
    /// </summary>
    /// <remarks>
    /// So that a Roslyn that dies is replayed with <em>every</em> client's documents, not only the
    /// host's own (D57). The ledger in <see cref="SharedRoslynHost"/> is what decides which of those
    /// entries came from an attached client and may therefore be closed again.
    /// </remarks>
    DocumentMirror Documents { get; }

    /// <summary>
    /// Roslyn's own <c>initialize</c> result, as it arrived, completing when the handshake finishes.
    /// </summary>
    /// <remarks>
    /// The host answers an attached client's <c>initialize</c> out of this rather than forwarding it
    /// (D88): Roslyn has exactly one client and a second <c>initialize</c> on the same connection is
    /// a protocol error. Replaying the real document rather than authoring one keeps the attached
    /// client seeing what a directly-launched one would see, which matters the first time anything in
    /// this repository starts reading a backend capability.
    /// </remarks>
    /// <param name="cancellationToken">Abandons the wait.</param>
    Task<byte[]> BackendInitializeResultAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Forwards one attached client's request to Roslyn and answers it under the client's own id.
    /// </summary>
    /// <remarks>
    /// The id rewrite is D47 applied a second time, for the same reason: the attached client counts
    /// from one and so does this process, so an unrewritten id would deliver one answer as another's.
    /// The reply is built with the client's original id token, byte for byte.
    /// </remarks>
    /// <param name="request">The client's message, exactly as it arrived.</param>
    /// <param name="replyIdToken">The id token the answer must carry.</param>
    /// <param name="cancellationToken">Cancels the request, which also cancels it at Roslyn.</param>
    Task<byte[]> ForwardAsync(byte[] request, byte[] replyIdToken, CancellationToken cancellationToken);

    /// <summary>Sends a notification to Roslyn, dropping it when there is no backend to send it to.</summary>
    /// <param name="body">The raw notification.</param>
    void NotifyBackend(ReadOnlyMemory<byte> body);

    /// <summary>
    /// Applies a runtime configuration override and waits for the pull that makes it real (D77).
    /// </summary>
    /// <remarks>
    /// The reason <c>claude-roslyn-lsp/setOption</c> exists at all. An attached engine's own
    /// <c>ConfigurationResponder</c> is never asked anything — Roslyn asks the host — so an override
    /// set on the attached side would be invisible, and a solution-wide diagnostic pull would answer
    /// emptily and successfully, which is exactly the failure mode this product exists to remove
    /// (C14).
    /// </remarks>
    /// <param name="section">The exact section name.</param>
    /// <param name="rawValue">The raw JSON value.</param>
    /// <param name="cancellationToken">Abandons the round trip.</param>
    Task SetOptionAsync(string section, byte[] rawValue, CancellationToken cancellationToken);

    /// <summary>
    /// Raised for every message Roslyn sends that is not a response — the stream the host filters its
    /// fan-out out of (D89).
    /// </summary>
    /// <remarks>
    /// The owner raises it for everything and decides nothing, because what an attached client should
    /// see is a property of the sharing design rather than of the mediation, and putting the filter
    /// here would give the same list two homes.
    /// </remarks>
    event Action<byte[]>? BackendMessage;
}
