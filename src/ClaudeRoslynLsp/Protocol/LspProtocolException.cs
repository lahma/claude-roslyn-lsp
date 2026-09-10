namespace ClaudeRoslynLsp.Protocol;

/// <summary>
/// The stream carried something that is not a well-formed LSP message frame.
/// </summary>
/// <remarks>
/// This is deliberately not recoverable at the message level. Framing is a byte-stream contract: once
/// a header is unreadable or a declared length is not believable, there is no way to know where the
/// next frame begins, so the only honest response is to stop reading and let the caller decide
/// whether to fail the process or to restart the peer. A message that is well framed but whose
/// <em>body</em> is bad JSON is a different thing entirely, and is answered with a JSON-RPC error
/// rather than raised as this.
/// </remarks>
internal sealed class LspProtocolException : IOException
{
    /// <summary>Creates the exception with a message naming what was wrong with the stream.</summary>
    /// <param name="message">What was read, and what was expected instead.</param>
    internal LspProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception from an underlying I/O failure.</summary>
    /// <param name="message">What was read, and what was expected instead.</param>
    /// <param name="innerException">The failure that stopped the read.</param>
    internal LspProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
