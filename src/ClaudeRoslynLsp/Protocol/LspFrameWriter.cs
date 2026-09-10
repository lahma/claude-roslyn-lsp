using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ClaudeRoslynLsp.Protocol;

/// <summary>
/// Writes <c>Content-Length</c>-framed LSP messages to a byte stream.
/// </summary>
/// <remarks>
/// <para>
/// Two properties are load-bearing and neither is obvious from the specification. First, a frame is
/// written with a <b>single</b> <see cref="Stream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>
/// call: a header written separately from its body can be interleaved with another frame's header by
/// the operating system, and the peer then reads a length that belongs to somebody else. Second,
/// writes are serialised by a gate, because an adapter answers requests concurrently and two
/// half-written frames on one stream are unrecoverable in a way that no amount of care at the message
/// layer can undo.
/// </para>
/// <para>
/// The header is ASCII and the body is UTF-8, and the length is the <em>byte</em> count of the
/// encoded body — never its character count. That is why nothing here goes through a
/// <see cref="TextWriter"/>.
/// </para>
/// </remarks>
internal sealed class LspFrameWriter : IDisposable
{
    private static readonly byte[] HeaderPrefix = "Content-Length: "u8.ToArray();
    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    /// <summary>Enough for any <see cref="int"/>, which is what <c>Content-Length</c> is parsed as.</summary>
    private const int MaxLengthDigits = 10;

    private readonly Stream _stream;
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);

    /// <summary>Creates a writer over <paramref name="stream"/>.</summary>
    /// <param name="stream">The outbound half of the connection. Not disposed by this class.</param>
    internal LspFrameWriter(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    /// <summary>Serialises <paramref name="message"/> and writes it as one frame.</summary>
    /// <typeparam name="T">The message type, which must be declared in <see cref="LspJsonContext"/>.</typeparam>
    /// <param name="message">The message to write.</param>
    /// <param name="typeInfo">
    /// The source-generated contract for <typeparamref name="T"/>. Passed explicitly rather than
    /// resolved from options so that nothing here can fall back to reflection — the product is
    /// published with <c>JsonSerializerIsReflectionEnabledByDefault=false</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    internal ValueTask WriteFrameAsync<T>(
        T message,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        return WriteFrameAsync(JsonSerializer.SerializeToUtf8Bytes(message, typeInfo), cancellationToken);
    }

    /// <summary>Writes an already-encoded UTF-8 body as one frame.</summary>
    /// <param name="body">The message body. Its length is what the header declares.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    internal async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        var capacity = HeaderPrefix.Length + MaxLengthDigits + HeaderTerminator.Length + body.Length;
        var frame = ArrayPool<byte>.Shared.Rent(capacity);

        try
        {
            var offset = 0;

            HeaderPrefix.CopyTo(frame.AsSpan(offset));
            offset += HeaderPrefix.Length;

            // Invariant culture: a digit-grouping locale would otherwise write "1,234" and the peer
            // would read a header it cannot parse. The UTF-8 overload, not the UTF-16 one - the
            // header goes on the wire as bytes.
            if (!body.Length.TryFormat(frame.AsSpan(offset), out var digits, provider: CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException(
                    $"Could not format a Content-Length of {body.Length} into {MaxLengthDigits} digits.");
            }

            offset += digits;

            HeaderTerminator.CopyTo(frame.AsSpan(offset));
            offset += HeaderTerminator.Length;

            body.Span.CopyTo(frame.AsSpan(offset));
            offset += body.Length;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await _stream.WriteAsync(frame.AsMemory(0, offset), cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
