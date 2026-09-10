namespace ClaudeRoslynLsp.Roslyn;

/// <summary>
/// Presents a read stream and a write stream as one bidirectional <see cref="Stream"/>.
/// </summary>
/// <remarks>
/// <para>
/// A named pipe is already duplex; a child process's stdio is two separate one-way pipes. The
/// mediation layer above (WP2/WP4) should not have to know which transport it got, so the difference
/// is absorbed here rather than becoming a second code path through every read and write in the
/// adapter.
/// </para>
/// <para>
/// Only the asynchronous members are implemented beyond what <see cref="Stream"/> derives for itself:
/// the framing reader and writer are async throughout, and a synchronous read on a child's stdout is
/// exactly the call that cannot be cancelled when the child stops answering.
/// </para>
/// </remarks>
internal sealed class DuplexStream : Stream
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly bool _ownsStreams;

    /// <summary>Creates a duplex view over two one-way streams.</summary>
    /// <param name="input">The stream to read from — the child's stdout.</param>
    /// <param name="output">The stream to write to — the child's stdin.</param>
    /// <param name="ownsStreams">Whether disposing this also disposes both halves.</param>
    internal DuplexStream(Stream input, Stream output, bool ownsStreams = true)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        _input = input;
        _output = output;
        _ownsStreams = ownsStreams;
    }

    /// <inheritdoc />
    public override bool CanRead => _input.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => _output.CanWrite;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() => _output.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _output.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => _input.Read(buffer);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _input.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => _output.Write(buffer);

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _output.WriteAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsStreams)
        {
            _input.Dispose();
            _output.Dispose();
        }

        base.Dispose(disposing);
    }
}
