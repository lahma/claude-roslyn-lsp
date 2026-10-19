using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

using ClaudeRoslynLsp.Adapter;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Roslyn;

/// <summary>
/// Asks a Roslyn child's runtime for a full, blocking garbage collection over the .NET diagnostics
/// IPC channel, which is what compacts the fragmentation an idle child is mostly made of (D101, C66).
/// </summary>
/// <remarks>
/// <para>
/// <b>The mechanism.</b> Every .NET process listens on a diagnostics endpoint — a named pipe
/// <c>dotnet-diagnostic-{pid}</c> on Windows, a Unix domain socket
/// <c>$TMPDIR/dotnet-diagnostic-{pid}-{key}-socket</c> elsewhere — that speaks the protocol in
/// dotnet/diagnostics' <c>documentation/design-docs/ipc-protocol.md</c>. Opening an EventPipe
/// session (<c>CollectTracing2</c>) that enables only the runtime provider's <c>GCHeapCollect</c>
/// keyword (<c>0x800000</c>) makes the runtime call <c>ETW::GCLog::ForceGCForDiagnostics</c>, which is
/// <c>GarbageCollect(max_generation, blocking)</c> (dotnet/runtime
/// <c>src/coreclr/vm/eventtrace_gcheap.cpp</c>); with gen2 fragmented past its limits the collector
/// decides to compact (<c>src/coreclr/gc/plan_phase.cpp</c>, <c>decide_on_compacting</c>). The
/// collection runs during the session enable, so the <c>OK</c> response arrives after it — about
/// 0.9 s on a 1 GB heap — and the session is stopped again at once.
/// </para>
/// <para>
/// <b>Rundown off, one keyword, nothing kept.</b> A rundown would enumerate every loaded method in a
/// process holding a whole solution, which costs far more than the collection; the trace the runtime
/// streams back is read and thrown away, because it exists only so the runtime's writer never blocks
/// on a full pipe. A connection is used for one command and closed, as the protocol requires, so the
/// <c>StopTracing</c> goes over a second one.
/// </para>
/// <para>
/// <b>Never fatal, always bounded.</b> A child started with <c>DOTNET_EnableDiagnostics=0</c> has no
/// endpoint; a runtime can refuse the session with an <c>HRESULT</c>; a pipe can break. Each of those
/// is <see langword="false"/> with a debug line, inside one 30-second budget, and the policy carries
/// on exactly as it would have with no compactor at all.
/// </para>
/// <para>
/// Pure BCL — a named pipe, a socket and a few little-endian integers — as hard rule 1 and the package
/// budget require. <c>Microsoft.Diagnostics.NETCore.Client</c> would do the same in more code than this
/// file, as a dependency.
/// </para>
/// </remarks>
internal sealed partial class DiagnosticsIpcCompactor : IBackendCompactor
{
    /// <summary>The whole exchange, end to end.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a connect waits for the endpoint.
    /// </summary>
    /// <remarks>
    /// Short, because the runtime's server is listening the whole time it is alive: a pipe that does
    /// not accept within two seconds is one that does not exist, and a Windows client waits out the
    /// whole timeout before it can say so.
    /// </remarks>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long the trace stream is drained for after the session has been stopped.</summary>
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The size of every message header.</summary>
    internal const int HeaderSize = 20;

    /// <summary>The EventPipe command set.</summary>
    internal const byte EventPipeCommandSet = 0x02;

    /// <summary><c>StopTracing</c>: payload <c>ulong sessionId</c>.</summary>
    internal const byte StopTracingCommand = 0x01;

    /// <summary><c>CollectTracing2</c>: buffer size, format, rundown flag and providers.</summary>
    internal const byte CollectTracing2Command = 0x03;

    /// <summary>The command set every server response carries.</summary>
    internal const byte ServerCommandSet = 0xFF;

    /// <summary>A successful response.</summary>
    internal const byte OkCommand = 0x00;

    /// <summary>An error response, carrying an <c>HRESULT</c>.</summary>
    internal const byte ErrorCommand = 0xFF;

    /// <summary>The runtime's own provider.</summary>
    internal const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";

    /// <summary>The <c>GCHeapCollect</c> keyword: enabling it is what forces the collection.</summary>
    internal const ulong GCHeapCollectKeyword = 0x800000;

    /// <summary>EventPipe level 4, Informational.</summary>
    internal const uint InformationalLevel = 4;

    /// <summary>The session's circular buffer, in megabytes; it only ever holds a handful of GC events.</summary>
    internal const uint CircularBufferMegabytes = 16;

    /// <summary>The <c>NetTrace</c> serialization format.</summary>
    internal const uint NetTraceFormat = 1;

    private readonly ILogger _logger;
    private readonly Func<int, CancellationToken, Task<Stream?>> _connect;

    /// <summary>Creates the compactor over the platform's diagnostics endpoint.</summary>
    /// <param name="logger">The stderr log.</param>
    internal DiagnosticsIpcCompactor(ILogger logger)
        : this(logger, ConnectToRuntimeAsync)
    {
    }

    /// <summary>Creates the compactor over an injected transport, which is how the exchange is tested.</summary>
    /// <param name="logger">The stderr log.</param>
    /// <param name="connect">
    /// Opens one connection to a process's endpoint, or answers null when it has none. Called once
    /// for the session and once for the stop.
    /// </param>
    internal DiagnosticsIpcCompactor(ILogger logger, Func<int, CancellationToken, Task<Stream?>> connect)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(connect);

        _logger = logger;
        _connect = connect;
    }

    /// <summary>The magic every header starts with: <c>DOTNET_IPC_V1</c> and a NUL.</summary>
    private static ReadOnlySpan<byte> Magic => "DOTNET_IPC_V1\0"u8;

    /// <inheritdoc />
    public async Task<bool> TryCompactAsync(int processId, CancellationToken cancellationToken)
    {
        if (processId <= 0)
        {
            return false;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);

        var token = budget.Token;

        try
        {
            var session = await _connect(processId, token).ConfigureAwait(false);

            if (session is null)
            {
                Log.NoEndpoint(_logger, processId);
                return false;
            }

            await using (session.ConfigureAwait(false))
            {
                await SendAsync(
                        session,
                        EventPipeCommandSet,
                        CollectTracing2Command,
                        BuildCollectTracingPayload(),
                        token)
                    .ConfigureAwait(false);

                var (commandSet, commandId, body) = await ReceiveAsync(session, token).ConfigureAwait(false);

                if (!TryReadSessionId(commandSet, commandId, body, out var sessionId))
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        var refusal = DescribeRefusal(commandSet, commandId, body);
                        Log.Refused(_logger, processId, refusal);
                    }

                    return false;
                }

                // The collection has run by now: the runtime answers the enable after it. What is
                // left is ending the session cleanly and reading what it streamed meanwhile.
                var drain = DrainAsync(session, token);

                await StopAsync(processId, sessionId, token).ConfigureAwait(false);

                try
                {
                    await drain.WaitAsync(DrainTimeout, token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // The stream did not end; closing the connection below ends the session anyway.
                }

                Log.Collected(_logger, processId);
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or UnauthorizedAccessException
                                              or InvalidDataException or TimeoutException
                                              or OperationCanceledException or ObjectDisposedException)
        {
            Log.Failed(_logger, processId, exception.Message);
            return false;
        }
    }

    /// <summary>Renders one message: the 20-byte header, then the payload.</summary>
    /// <param name="commandSet">The command set.</param>
    /// <param name="commandId">The command within it.</param>
    /// <param name="payload">The payload, already encoded.</param>
    internal static byte[] BuildMessage(byte commandSet, byte commandId, ReadOnlySpan<byte> payload)
    {
        var message = new byte[checked(HeaderSize + payload.Length)];

        Magic.CopyTo(message);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(14), checked((ushort) message.Length));
        message[16] = commandSet;
        message[17] = commandId;

        // Bytes 18-19 are reserved and zero.
        payload.CopyTo(message.AsSpan(HeaderSize));

        return message;
    }

    /// <summary>
    /// The <c>CollectTracing2</c> payload: a 16 MB buffer, NetTrace, no rundown, and the runtime
    /// provider with only <c>GCHeapCollect</c> enabled at Informational.
    /// </summary>
    internal static byte[] BuildCollectTracingPayload()
    {
        using var payload = new MemoryStream(128);

        WriteUInt32(payload, CircularBufferMegabytes);
        WriteUInt32(payload, NetTraceFormat);
        payload.WriteByte(0);
        WriteUInt32(payload, 1);
        WriteUInt64(payload, GCHeapCollectKeyword);
        WriteUInt32(payload, InformationalLevel);
        WriteString(payload, RuntimeProvider);
        WriteString(payload, null);

        return payload.ToArray();
    }

    /// <summary>The <c>StopTracing</c> payload: the session id the enable answered with.</summary>
    /// <param name="sessionId">The session to stop.</param>
    internal static byte[] BuildStopTracingPayload(ulong sessionId)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, sessionId);
        return payload;
    }

    /// <summary>Reads the session id out of a successful response.</summary>
    /// <param name="commandSet">The response's command set.</param>
    /// <param name="commandId">The response's command id.</param>
    /// <param name="body">Its payload.</param>
    /// <param name="sessionId">The session id, when it was one.</param>
    internal static bool TryReadSessionId(byte commandSet, byte commandId, ReadOnlySpan<byte> body, out ulong sessionId)
    {
        if (commandSet == ServerCommandSet && commandId == OkCommand && body.Length >= 8)
        {
            sessionId = BinaryPrimitives.ReadUInt64LittleEndian(body);
            return true;
        }

        sessionId = 0;
        return false;
    }

    /// <summary>Connects to a process's diagnostics endpoint, or answers null when it has none.</summary>
    private static async Task<Stream?> ConnectToRuntimeAsync(int processId, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(
                ".",
                string.Create(CultureInfo.InvariantCulture, $"dotnet-diagnostic-{processId}"),
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.ConnectAsync((int) ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch (TimeoutException)
            {
                // No pipe by that name: not a .NET process, or one started with diagnostics off.
                await pipe.DisposeAsync().ConfigureAwait(false);
                return null;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        if (FindSocket(processId) is not { } path)
        {
            return null;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(ConnectTimeout);

            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), connect.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Finds a process's diagnostics socket. Its name ends in a key the runtime derives from the
    /// process's start time, so it is found by pattern rather than built; the child inherits this
    /// process's <c>TMPDIR</c>, which is what <see cref="Path.GetTempPath"/> reads.
    /// </summary>
    private static string? FindSocket(int processId)
    {
        try
        {
            return Directory
                .GetFiles(
                    Path.GetTempPath(),
                    string.Create(CultureInfo.InvariantCulture, $"dotnet-diagnostic-{processId}-*-socket"))
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Stops the session over a connection of its own; the session stream then ends.</summary>
    private async Task StopAsync(int processId, ulong sessionId, CancellationToken cancellationToken)
    {
        var stop = await _connect(processId, cancellationToken).ConfigureAwait(false);

        if (stop is null)
        {
            // Closing the session connection ends the session too; this is only the tidy way.
            return;
        }

        await using (stop.ConfigureAwait(false))
        {
            await SendAsync(stop, EventPipeCommandSet, StopTracingCommand, BuildStopTracingPayload(sessionId), cancellationToken)
                .ConfigureAwait(false);

            var (commandSet, commandId, body) = await ReceiveAsync(stop, cancellationToken).ConfigureAwait(false);

            if (!TryReadSessionId(commandSet, commandId, body, out _))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    var refusal = DescribeRefusal(commandSet, commandId, body);
                    Log.StopRefused(_logger, processId, refusal);
                }
            }
        }
    }

    /// <summary>Reads and discards the trace stream until it ends.</summary>
    private static async Task DrainAsync(Stream session, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];

        try
        {
            while (await session.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException
                                              or ObjectDisposedException or SocketException)
        {
            // The stream ending badly is still the stream ending.
        }
    }

    private static async Task SendAsync(
        Stream stream,
        byte commandSet,
        byte commandId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(BuildMessage(commandSet, commandId, payload), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(byte CommandSet, byte CommandId, byte[] Body)> ReceiveAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("The diagnostics endpoint answered with something that is not an IPC header.");
        }

        var size = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));

        if (size < HeaderSize)
        {
            throw new InvalidDataException("The diagnostics endpoint answered with a message shorter than its header.");
        }

        var body = new byte[size - HeaderSize];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);

        return (header[16], header[17], body);
    }

    /// <summary>What a non-OK response said, for the log line.</summary>
    private static string DescribeRefusal(byte commandSet, byte commandId, ReadOnlySpan<byte> body) =>
        commandSet == ServerCommandSet && commandId == ErrorCommand && body.Length >= 4
            ? string.Create(CultureInfo.InvariantCulture, $"HRESULT 0x{BinaryPrimitives.ReadUInt32LittleEndian(body):x8}")
            : string.Create(CultureInfo.InvariantCulture, $"response {commandSet:x2}/{commandId:x2} with {body.Length} byte(s)");

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    /// <summary>
    /// The protocol's string: a <c>uint</c> count of UTF-16 code units including the terminating NUL,
    /// then those code units little-endian — or a bare zero for an empty one.
    /// </summary>
    private static void WriteString(Stream stream, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            WriteUInt32(stream, 0);
            return;
        }

        WriteUInt32(stream, checked((uint) (value.Length + 1)));
        stream.Write(Encoding.Unicode.GetBytes(value + "\0"));
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1650,
            Level = LogLevel.Debug,
            Message = "Process {ProcessId} has no diagnostics endpoint (not .NET, or DOTNET_EnableDiagnostics=0); " +
                      "it cannot be asked to compact.")]
        internal static partial void NoEndpoint(ILogger logger, int processId);

        [LoggerMessage(
            EventId = 1651,
            Level = LogLevel.Debug,
            Message = "Process {ProcessId} refused the diagnostics session that forces a collection ({Refusal}).")]
        internal static partial void Refused(ILogger logger, int processId, string refusal);

        [LoggerMessage(
            EventId = 1652,
            Level = LogLevel.Debug,
            Message = "Process {ProcessId} did not stop the diagnostics session cleanly ({Refusal}); closing the " +
                      "connection ends it.")]
        internal static partial void StopRefused(ILogger logger, int processId, string refusal);

        [LoggerMessage(
            EventId = 1653,
            Level = LogLevel.Debug,
            Message = "Asking process {ProcessId} to compact failed ({Reason}); it keeps its memory until the next look.")]
        internal static partial void Failed(ILogger logger, int processId, string reason);

        [LoggerMessage(
            EventId = 1654,
            Level = LogLevel.Debug,
            Message = "Process {ProcessId} ran a full blocking collection on request (D101).")]
        internal static partial void Collected(ILogger logger, int processId);
    }
}
