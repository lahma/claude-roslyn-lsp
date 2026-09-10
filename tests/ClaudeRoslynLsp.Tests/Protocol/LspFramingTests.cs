using System.Text;

using ClaudeRoslynLsp.Protocol;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Protocol;

/// <summary>
/// The <c>Content-Length</c> framing that carries every LSP message.
/// </summary>
/// <remarks>
/// Framing is the one layer where a bug produces no error message: a length that is one byte out
/// leaves the reader parsing the next message from the middle of the previous one, and the symptom is
/// a client that stops getting answers. Every case here is one of the ways that happens — a body
/// whose byte count differs from its character count, a read that returns half a header, a peer
/// sending headers this protocol does not define.
/// </remarks>
public class LspFramingTests
{
    /// <summary>
    /// A body with characters outside the Basic Multilingual Plane, and a real blank line inside it.
    /// </summary>
    /// <remarks>
    /// Both halves are deliberate. <c>Content-Length</c> counts UTF-8 <em>bytes</em>, so a reader that
    /// measured characters would truncate this one; and a body that itself contains the four bytes
    /// that terminate a header section is what catches a reader that goes looking for the separator
    /// again instead of trusting the length it was given.
    /// </remarks>
    private const string NonAsciiBody =
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"näïve — 𝔘𝔫𝔦𝔠𝔬𝔡𝔢\"}\r\n\r\n{\"decoy\":true}";

    /// <summary>
    /// The test's own cancellation token, threaded through every awaited call.
    /// </summary>
    /// <remarks>
    /// xunit.v3 requires it (xUnit1051): a hung await inside a test would otherwise ignore the
    /// runner's cancellation and keep the whole run alive until its outer timeout.
    /// </remarks>
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AWrittenFrameReadsBackByteForByte()
    {
        var body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"initialized","params":{}}""");

        var wire = await WriteAsync(body);
        var reader = new LspFrameReader(new MemoryStream(wire));

        Assert.Equal(body, await reader.ReadFrameAsync(Cancellation));
        Assert.Null(await reader.ReadFrameAsync(Cancellation));
    }

    [Fact]
    public async Task TheHeaderDeclaresTheEncodedByteCountNotTheCharacterCount()
    {
        var body = Encoding.UTF8.GetBytes(NonAsciiBody);
        var wire = await WriteAsync(body);

        var header = Encoding.ASCII.GetString(wire, 0, Encoding.ASCII.GetString(wire).IndexOf("\r\n\r\n", StringComparison.Ordinal));

        Assert.Equal($"Content-Length: {body.Length}", header);
        Assert.NotEqual(NonAsciiBody.Length, body.Length);

        var reader = new LspFrameReader(new MemoryStream(wire));
        Assert.Equal(body, await reader.ReadFrameAsync(Cancellation));
    }

    [Fact]
    public async Task SeveralFramesReadBackInOrder()
    {
        var first = Encoding.UTF8.GetBytes("""{"id":1}""");
        var second = Encoding.UTF8.GetBytes("""{"id":2}""");
        var third = Encoding.UTF8.GetBytes(NonAsciiBody);

        using var buffer = new MemoryStream();
        using (var writer = new LspFrameWriter(buffer))
        {
            await writer.WriteFrameAsync(first, Cancellation);
            await writer.WriteFrameAsync(second, Cancellation);
            await writer.WriteFrameAsync(third, Cancellation);
        }

        var reader = new LspFrameReader(new MemoryStream(buffer.ToArray()));

        Assert.Equal(first, await reader.ReadFrameAsync(Cancellation));
        Assert.Equal(second, await reader.ReadFrameAsync(Cancellation));
        Assert.Equal(third, await reader.ReadFrameAsync(Cancellation));
        Assert.Null(await reader.ReadFrameAsync(Cancellation));
    }

    /// <summary>
    /// A pipe hands over whatever has arrived, not whatever was asked for. One byte per read is the
    /// worst case and the only one that exercises every partial state — a split inside the header
    /// name, inside the length, and inside the four-byte separator.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task AFrameSplitAcrossReadsIsReassembled(int chunkSize)
    {
        var body = Encoding.UTF8.GetBytes(NonAsciiBody);
        var wire = await WriteAsync(body);

        var reader = new LspFrameReader(new ChunkedStream(wire, chunkSize));

        Assert.Equal(body, await reader.ReadFrameAsync(Cancellation));
        Assert.Null(await reader.ReadFrameAsync(Cancellation));
    }

    /// <summary>A stream that ends between messages is a peer that hung up, not a protocol error.</summary>
    [Fact]
    public async Task AnEmptyStreamIsACleanEndOfStream()
    {
        var reader = new LspFrameReader(new MemoryStream([]));

        Assert.Null(await reader.ReadFrameAsync(Cancellation));
    }

    /// <summary>
    /// The separator is CRLF, not LF. Accepting bare LF would make this reader more permissive than
    /// the peers it sits between, so a message it accepted might be one Roslyn rejects — which is the
    /// worst kind of leniency: it moves the failure somewhere else.
    /// </summary>
    [Fact]
    public async Task BareLineFeedsAreNotAHeaderTerminator()
    {
        var wire = Encoding.ASCII.GetBytes("Content-Length: 2\n\n{}");

        await AssertProtocolErrorAsync(wire);
    }

    [Fact]
    public async Task AHeaderTheProtocolDoesNotDefineIsRefused()
    {
        var wire = Encoding.ASCII.GetBytes("Content-Length: 2\r\nX-Trace-Id: 7\r\n\r\n{}");

        var error = await AssertProtocolErrorAsync(wire);
        Assert.Contains("X-Trace-Id", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The one optional header the specification does define is accepted and ignored.</summary>
    [Fact]
    public async Task ContentTypeIsAccepted()
    {
        var wire = Encoding.ASCII.GetBytes(
            "Content-Length: 2\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n{}");

        var reader = new LspFrameReader(new MemoryStream(wire));

        Assert.Equal("{}"u8.ToArray(), await reader.ReadFrameAsync(Cancellation));
    }

    [Theory]
    [InlineData("Content-Length: twelve\r\n\r\n{}")]
    [InlineData("Content-Length: -1\r\n\r\n{}")]
    [InlineData("Content-Length: +2\r\n\r\n{}")]
    [InlineData("Content-Length 2\r\n\r\n{}")]
    [InlineData("Content-Type: application/json\r\n\r\n{}")]
    public async Task AMalformedOrMissingLengthIsRefused(string wire)
    {
        await AssertProtocolErrorAsync(Encoding.ASCII.GetBytes(wire));
    }

    /// <summary>
    /// A length above the ceiling is refused before anything is allocated for it. Without this a
    /// corrupted or hostile header would turn straight into an allocation of whatever it claimed.
    /// </summary>
    [Fact]
    public async Task ALengthAboveTheCeilingIsRefused()
    {
        var wire = Encoding.ASCII.GetBytes($"Content-Length: {LspFrameReader.MaxContentLength + 1}\r\n\r\n");

        var error = await AssertProtocolErrorAsync(wire);
        Assert.Contains("ceiling", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A peer that never sends the blank line must not be able to grow this process's buffer without
    /// bound.
    /// </summary>
    [Fact]
    public async Task AHeaderSectionAboveTheCeilingIsRefused()
    {
        var padding = new string('x', LspFrameReader.MaxHeaderBytes + 1024);
        var wire = Encoding.ASCII.GetBytes($"Content-Length: 2\r\nContent-Type: {padding}\r\n\r\n{{}}");

        var error = await AssertProtocolErrorAsync(wire);
        Assert.Contains("header", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A body shorter than its header promised is the classic symptom of a peer that died mid-write.
    /// Returning what arrived would hand the layer above a truncated JSON document to misparse.
    /// </summary>
    [Fact]
    public async Task ABodyShorterThanItsDeclaredLengthIsRefused()
    {
        var wire = Encoding.ASCII.GetBytes("Content-Length: 20\r\n\r\n{\"id\":1}");

        var error = await AssertProtocolErrorAsync(wire);
        Assert.Contains("short", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A header that ends mid-field is a truncation too, and reads as one.</summary>
    [Fact]
    public async Task AHeaderThatNeverEndsIsRefused()
    {
        await AssertProtocolErrorAsync(Encoding.ASCII.GetBytes("Content-Length: 2\r\n"));
    }

    [Fact]
    public async Task NonAsciiBytesInTheHeaderAreRefused()
    {
        var wire = Encoding.UTF8.GetBytes("Content-Length: 2\r\nContent-Typé: x\r\n\r\n{}");

        var error = await AssertProtocolErrorAsync(wire);
        Assert.Contains("ASCII", error.Message, StringComparison.Ordinal);
    }

    private static async Task<LspProtocolException> AssertProtocolErrorAsync(byte[] wire)
    {
        var reader = new LspFrameReader(new MemoryStream(wire));

        return await Assert.ThrowsAsync<LspProtocolException>(async () => await reader.ReadFrameAsync(Cancellation));
    }

    private static async Task<byte[]> WriteAsync(byte[] body)
    {
        using var buffer = new MemoryStream();

        using (var writer = new LspFrameWriter(buffer))
        {
            await writer.WriteFrameAsync(body, Cancellation);
        }

        return buffer.ToArray();
    }

    /// <summary>A stream that never returns more than <c>chunkSize</c> bytes from one read.</summary>
    /// <remarks>
    /// <see cref="MemoryStream"/> always satisfies a read in full, which is exactly the behaviour a
    /// pipe does not have — so a framing bug that only shows up on a partial read would never fail a
    /// test written against a <see cref="MemoryStream"/> alone.
    /// </remarks>
    private sealed class ChunkedStream(byte[] content, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => content.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var available = Math.Min(Math.Min(chunkSize, buffer.Length), content.Length - _position);

            content.AsSpan(_position, available).CopyTo(buffer);
            _position += available;

            return available;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
