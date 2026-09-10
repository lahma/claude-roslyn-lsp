using System.Globalization;
using System.Text;

namespace ClaudeRoslynLsp.Protocol;

/// <summary>
/// Reads <c>Content-Length</c>-framed LSP messages off a byte stream, one message body at a time.
/// </summary>
/// <remarks>
/// <para>
/// The base protocol (LSP 3.17, <em>Base Protocol</em>) frames every message as ASCII header fields
/// terminated by <c>\r\n</c>, a blank <c>\r\n</c>, and then exactly <c>Content-Length</c> bytes of
/// UTF-8 body. Two things about that are easy to get subtly wrong and are the reason this is a class
/// rather than a helper method: the byte count is of the <em>encoded</em> body, so a message with one
/// non-ASCII character is longer than its character count, and a read from a pipe returns whatever
/// happens to have arrived, so a header, a body, or the four-byte separator itself can be split
/// across any number of reads.
/// </para>
/// <para>
/// The limits are not tuning knobs. A peer that never sends the header terminator would otherwise be
/// able to grow this process's buffer without bound, and a corrupted or hostile
/// <c>Content-Length</c> would otherwise cause a single allocation of whatever it said. Both are
/// refused as protocol errors rather than absorbed.
/// </para>
/// <para>
/// Only <c>Content-Length</c> and <c>Content-Type</c> are accepted, which is the whole header
/// vocabulary the specification defines. An unknown header is refused rather than skipped: this
/// reader sits between two processes that both claim to speak LSP, and a header neither of them
/// should have written means one of them is not writing what we think it is — silently ignoring it
/// would turn a wiring mistake into a mystery further downstream.
/// </para>
/// <para>
/// WP2 extends this (cancellation-aware reads against a real backend, and a byte-exact loopback
/// test at ten thousand messages). It does not replace it.
/// </para>
/// </remarks>
internal sealed class LspFrameReader
{
    /// <summary>
    /// The most bytes a message's header section may occupy before the blank line that ends it.
    /// </summary>
    /// <remarks>
    /// 64 KiB is three orders of magnitude more than the two headers the specification defines can
    /// legitimately need. It exists to bound a peer that sends no terminator at all, not to be a
    /// close fit.
    /// </remarks>
    internal const int MaxHeaderBytes = 64 * 1024;

    /// <summary>The largest <c>Content-Length</c> that will be honoured.</summary>
    /// <remarks>
    /// 32 MiB comfortably exceeds a full-text <c>didChange</c> of any source file a person wrote and
    /// any <c>workspace/diagnostic</c> report a solution can produce, while keeping a bad length from
    /// turning into a bad allocation.
    /// </remarks>
    internal const int MaxContentLength = 32 * 1024 * 1024;

    private const string ContentLengthHeader = "Content-Length";
    private const string ContentTypeHeader = "Content-Type";

    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    private readonly Stream _stream;

    // A plain grow-and-compact buffer rather than System.IO.Pipelines: it only ever holds a header
    // section (bounded by MaxHeaderBytes) plus whatever of the next message arrived with it, because
    // the body is read straight into its own array.
    private byte[] _buffer = new byte[4096];
    private int _start;
    private int _end;

    /// <summary>Creates a reader over <paramref name="stream"/>.</summary>
    /// <param name="stream">The inbound half of the connection. Not disposed by this class.</param>
    internal LspFrameReader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    /// <summary>
    /// Reads the next message body, or returns <see langword="null"/> when the peer closed the
    /// stream cleanly at a frame boundary.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending read.</param>
    /// <returns>The raw UTF-8 body, exactly <c>Content-Length</c> bytes long.</returns>
    /// <exception cref="LspProtocolException">The stream did not carry a well-formed frame.</exception>
    internal async ValueTask<byte[]?> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        var terminator = await FindHeaderTerminatorAsync(cancellationToken).ConfigureAwait(false);

        if (terminator < 0)
        {
            return null;
        }

        var contentLength = ParseHeaders(_buffer.AsSpan(_start, terminator - _start));
        _start = terminator + HeaderTerminator.Length;

        return await ReadBodyAsync(contentLength, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Buffers until the blank line that ends the header section is in hand, and returns the index of
    /// its first byte — or <c>-1</c> for a clean end of stream.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending read.</param>
    private async ValueTask<int> FindHeaderTerminatorAsync(CancellationToken cancellationToken)
    {
        // Relative to _start, because filling the buffer may compact it and move _start.
        var scanned = 0;

        while (true)
        {
            var buffered = _end - _start;

            if (buffered >= HeaderTerminator.Length)
            {
                var index = _buffer.AsSpan(_start + scanned, buffered - scanned).IndexOf(HeaderTerminator);

                if (index >= 0)
                {
                    var headerLength = scanned + index;

                    // Checked here as well as below, because a header that is oversized but *does*
                    // eventually terminate would otherwise slip through whenever the last read
                    // happened to deliver both the excess and the terminator at once.
                    if (headerLength > MaxHeaderBytes)
                    {
                        throw new LspProtocolException(
                            $"The message header was {headerLength} bytes, above the {MaxHeaderBytes}-byte " +
                            "ceiling. Either the peer is not speaking the LSP base protocol, or the stream " +
                            "is out of sync.");
                    }

                    return _start + headerLength;
                }

                // The terminator may straddle the boundary, so keep its length minus one byte.
                scanned = buffered - (HeaderTerminator.Length - 1);
            }

            if (buffered > MaxHeaderBytes)
            {
                throw new LspProtocolException(
                    $"The message header exceeded {MaxHeaderBytes} bytes without the blank line that ends it. " +
                    "Either the peer is not speaking the LSP base protocol, or the stream is out of sync.");
            }

            if (await FillAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                if (_end == _start)
                {
                    // End of stream at a frame boundary: the peer is done, and that is not an error.
                    return -1;
                }

                throw new LspProtocolException(
                    $"The stream ended after {_end - _start} byte(s) of a message header, before the blank " +
                    "line that ends it.");
            }
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="length"/> body bytes, starting with whatever is already
    /// buffered.
    /// </summary>
    /// <remarks>
    /// The remainder is read straight into the result rather than through the internal buffer, so a
    /// 32 MiB message never doubles this reader's own footprint.
    /// </remarks>
    /// <param name="length">The declared <c>Content-Length</c>.</param>
    /// <param name="cancellationToken">Cancels the pending read.</param>
    private async ValueTask<byte[]> ReadBodyAsync(int length, CancellationToken cancellationToken)
    {
        var body = new byte[length];
        var buffered = Math.Min(length, _end - _start);

        _buffer.AsSpan(_start, buffered).CopyTo(body);
        _start += buffered;

        var offset = buffered;

        while (offset < length)
        {
            var read = await _stream.ReadAsync(body.AsMemory(offset), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                throw new LspProtocolException(
                    $"The stream ended {length - offset} byte(s) short of the {length}-byte body its " +
                    "Content-Length header promised.");
            }

            offset += read;
        }

        return body;
    }

    /// <summary>Reads more bytes into the buffer, compacting or growing it first if it is full.</summary>
    /// <param name="cancellationToken">Cancels the pending read.</param>
    /// <returns>The number of bytes read; zero at end of stream.</returns>
    private async ValueTask<int> FillAsync(CancellationToken cancellationToken)
    {
        if (_start > 0)
        {
            _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
        {
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        var read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
        _end += read;

        return read;
    }

    /// <summary>
    /// Parses the header section and returns the declared content length.
    /// </summary>
    /// <param name="headers">The bytes before the blank line, without it.</param>
    private static int ParseHeaders(ReadOnlySpan<byte> headers)
    {
        foreach (var value in headers)
        {
            // Header fields are ASCII by specification. Decoding a non-ASCII byte would silently
            // become '?' and be reported as an unknown header name, which is a confusing way to say
            // "these are not LSP headers at all".
            if (value >= 0x80)
            {
                throw new LspProtocolException(
                    $"The message header contained the non-ASCII byte 0x{value:X2}; LSP header fields are ASCII.");
            }
        }

        var text = Encoding.ASCII.GetString(headers);
        int? contentLength = null;

        foreach (var line in text.Split("\r\n"))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);

            if (colon < 0)
            {
                throw new LspProtocolException(
                    $"The message header line '{line}' has no ':' separating the field name from its value.");
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (name.Equals(ContentLengthHeader, StringComparison.OrdinalIgnoreCase))
            {
                // NumberStyles.None is what rejects "+12", " 12" and "-1": the header is a
                // non-negative decimal integer and nothing else.
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                {
                    throw new LspProtocolException(
                        $"Content-Length was '{value}', which is not a non-negative decimal integer.");
                }

                if (parsed > MaxContentLength)
                {
                    throw new LspProtocolException(
                        $"Content-Length was {parsed}, above the {MaxContentLength}-byte ceiling this adapter " +
                        "will allocate for one message.");
                }

                contentLength = parsed;
            }
            else if (!name.Equals(ContentTypeHeader, StringComparison.OrdinalIgnoreCase))
            {
                throw new LspProtocolException(
                    $"The message carried the header '{name}', which the LSP base protocol does not define. " +
                    $"Only {ContentLengthHeader} and {ContentTypeHeader} are accepted.");
            }
        }

        return contentLength
               ?? throw new LspProtocolException(
                   $"The message header carried no {ContentLengthHeader}, so there is no way to know where its " +
                   "body ends.");
    }
}
