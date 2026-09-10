using System.Text.Json;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// Roslyn answered a request the adapter asked on its own behalf with an error, and the code
/// matters to the caller.
/// </summary>
/// <remarks>
/// The diagnostics bridge is the reason this is a type rather than an
/// <see cref="InvalidOperationException"/> with a formatted message: <c>-32801 ContentModified</c>
/// and <c>-32800 RequestCancelled</c> mean "ask again in a moment", and every other code means
/// "stop". Parsing that distinction back out of a sentence would be a decision made twice, once by
/// whoever wrote the sentence and once by whoever read it.
/// </remarks>
internal sealed class RoslynRequestException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="method">The method that was refused.</param>
    /// <param name="code">The JSON-RPC error code.</param>
    /// <param name="message">Roslyn's own message.</param>
    internal RoslynRequestException(string method, int code, string message)
        : base($"Roslyn refused '{method}' with {code}: {message}")
    {
        Method = method;
        Code = code;
    }

    /// <summary>The method that was refused.</summary>
    internal string Method { get; }

    /// <summary>The JSON-RPC error code Roslyn answered with.</summary>
    internal int Code { get; }

    /// <summary>Whether the failure means "the world moved; ask again", rather than "no".</summary>
    internal bool IsRetryable =>
        Code is Protocol.JsonRpcErrors.ContentModified or Protocol.JsonRpcErrors.RequestCancelled;
}

/// <summary>
/// The narrow view of the session that the diagnostics, watching and recovery bridges get.
/// </summary>
/// <remarks>
/// <para>
/// Three verbs and two facts, and deliberately nothing else. A bridge may ask Roslyn a question on
/// the adapter's own behalf, tell Roslyn something, and tell the client something; it may not reach
/// the id maps, the endpoints or the read loops. That is what keeps a bridge testable against a
/// hand-written channel, and what stops "the diagnostics bridge posts a frame" from becoming a
/// second writer racing <see cref="OutboundQueue"/>.
/// </para>
/// <para>
/// <see cref="AskAsync"/> is the interesting one. It mints an outbound id from the same counter
/// every forwarded request uses (D47), so an adapter-originated pull cannot collide with a client
/// request; and cancelling its token both removes the pending entry and sends Roslyn a
/// <c>$/cancelRequest</c>, because a pull the bridge abandoned is a pull Roslyn should stop
/// computing.
/// </para>
/// </remarks>
internal interface IAdapterChannel
{
    /// <summary>Whether there is a backend to talk to right now.</summary>
    bool BackendConnected { get; }

    /// <summary>How far the workspace has got, so a bridge can decline to ask too early (C28).</summary>
    ReadinessState Readiness { get; }

    /// <summary>The workspace root as a local path, or <see langword="null"/> when the client sent none.</summary>
    string? WorkspaceRoot { get; }

    /// <summary>Sends a notification to Roslyn, dropping it when there is no backend.</summary>
    /// <param name="body">The UTF-8 message.</param>
    void NotifyServer(ReadOnlyMemory<byte> body);

    /// <summary>Sends a notification to the client.</summary>
    /// <param name="body">The UTF-8 message.</param>
    void NotifyClient(ReadOnlyMemory<byte> body);

    /// <summary>Sends a <c>window/logMessage</c> the client will render.</summary>
    /// <param name="type">1 error, 2 warning, 3 info, 4 log.</param>
    /// <param name="message">The text.</param>
    void LogToClient(int type, string message);

    /// <summary>Asks Roslyn a question on the adapter's own behalf.</summary>
    /// <param name="method">The method name.</param>
    /// <param name="rawParams">The already-encoded <c>params</c> value.</param>
    /// <param name="cancellationToken">Abandons the request and cancels it at Roslyn.</param>
    /// <returns>The <c>result</c> member.</returns>
    /// <exception cref="RoslynRequestException">Roslyn answered with an error.</exception>
    Task<JsonElement> AskAsync(string method, ReadOnlyMemory<byte> rawParams, CancellationToken cancellationToken);
}
