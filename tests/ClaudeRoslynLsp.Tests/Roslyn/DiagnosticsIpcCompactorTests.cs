using System.Buffers.Binary;
using System.Text;

using ClaudeRoslynLsp.Roslyn;
using ClaudeRoslynLsp.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Roslyn;

/// <summary>
/// The compactor behind the idle policy's seam: the diagnostics IPC bytes it writes, the exchange it
/// conducts, and — against this very process — that a collection actually happens (D101, C66).
/// </summary>
/// <remarks>
/// The encoding tests are against bytes worked out by hand from dotnet/diagnostics'
/// <c>documentation/design-docs/ipc-protocol.md</c>, not against the encoder's own output, so a change
/// to the encoder cannot quietly redefine what "right" means. The exchange tests run against a scripted
/// endpoint over an in-memory pipe pair; the last two run against real processes.
/// </remarks>
public class DiagnosticsIpcCompactorTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Fourteen bytes of magic, a little-endian total size, the command, two reserved zeros.</summary>
    [Fact]
    public void AMessageIsTheTwentyByteHeaderAndThePayload()
    {
        var message = DiagnosticsIpcCompactor.BuildMessage(0x02, 0x01, [1, 2, 3, 4, 5, 6, 7, 8]);

        byte[] expected =
        [
            (byte) 'D', (byte) 'O', (byte) 'T', (byte) 'N', (byte) 'E', (byte) 'T', (byte) '_',
            (byte) 'I', (byte) 'P', (byte) 'C', (byte) '_', (byte) 'V', (byte) '1', 0x00,
            0x1C, 0x00,
            0x02,
            0x01,
            0x00, 0x00,
            1, 2, 3, 4, 5, 6, 7, 8,
        ];

        Assert.Equal(expected, message);
    }

    /// <summary>
    /// <c>CollectTracing2</c>: buffer, format, rundown off, one provider with only <c>GCHeapCollect</c>
    /// at Informational, its name as a counted, NUL-terminated UTF-16 string, and no filter data.
    /// </summary>
    [Fact]
    public void TheCollectTracingPayloadAsksForTheCollectionAndNothingElse()
    {
        var expected = new List<byte>();

        expected.AddRange([0x10, 0x00, 0x00, 0x00]);                         // circularBufferMB = 16
        expected.AddRange([0x01, 0x00, 0x00, 0x00]);                         // format = NetTrace
        expected.Add(0x00);                                                  // requestRundown = false
        expected.AddRange([0x01, 0x00, 0x00, 0x00]);                         // one provider
        expected.AddRange([0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00]); // keywords = 0x800000
        expected.AddRange([0x04, 0x00, 0x00, 0x00]);                         // level = Informational
        expected.AddRange([0x20, 0x00, 0x00, 0x00]);                         // 31 characters + NUL

        foreach (var character in "Microsoft-Windows-DotNETRuntime")
        {
            expected.Add((byte) character);
            expected.Add(0x00);
        }

        expected.AddRange([0x00, 0x00]);                                     // the terminating NUL
        expected.AddRange([0x00, 0x00, 0x00, 0x00]);                         // empty filter data

        var payload = DiagnosticsIpcCompactor.BuildCollectTracingPayload();

        Assert.Equal(97, payload.Length);
        Assert.Equal(expected, payload);
    }

    [Fact]
    public void TheStopPayloadIsTheSessionIdLittleEndian()
    {
        byte[] expected = [0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01];

        Assert.Equal(expected, DiagnosticsIpcCompactor.BuildStopTracingPayload(0x0102030405060708));
    }

    [Fact]
    public void OnlyAnOkResponseCarriesASession()
    {
        Assert.True(DiagnosticsIpcCompactor.TryReadSessionId(0xFF, 0x00, [42, 0, 0, 0, 0, 0, 0, 0], out var session));
        Assert.Equal(42UL, session);

        Assert.False(DiagnosticsIpcCompactor.TryReadSessionId(0xFF, 0xFF, [0x05, 0x00, 0x07, 0x80], out _));
        Assert.False(DiagnosticsIpcCompactor.TryReadSessionId(0xFF, 0x00, [1, 2, 3], out _));
    }

    /// <summary>
    /// The whole exchange against a scripted endpoint: enable on one connection, stop on a second,
    /// drain the first until it ends.
    /// </summary>
    [Fact]
    public async Task ASessionIsOpenedStoppedOnASecondConnectionAndDrained()
    {
        var endpoint = new ScriptedEndpoint(accept: true);
        var compactor = new DiagnosticsIpcCompactor(NullLogger.Instance, endpoint.ConnectAsync);

        Assert.True(await compactor.TryCompactAsync(4242, Cancellation));

        Assert.Equal(2, endpoint.Connections);
        Assert.Equal(
            DiagnosticsIpcCompactor.BuildMessage(0x02, 0x03, DiagnosticsIpcCompactor.BuildCollectTracingPayload()),
            endpoint.Requests[0]);
        Assert.Equal(
            DiagnosticsIpcCompactor.BuildMessage(0x02, 0x01, DiagnosticsIpcCompactor.BuildStopTracingPayload(ScriptedEndpoint.SessionId)),
            endpoint.Requests[1]);
    }

    /// <summary>A refusal is an answer, not an error: false, and no stop for a session that never opened.</summary>
    [Fact]
    public async Task ARefusedSessionIsFalseAndIsNotStopped()
    {
        var endpoint = new ScriptedEndpoint(accept: false);
        var compactor = new DiagnosticsIpcCompactor(NullLogger.Instance, endpoint.ConnectAsync);

        Assert.False(await compactor.TryCompactAsync(4242, Cancellation));
        Assert.Equal(1, endpoint.Connections);
    }

    [Fact]
    public async Task NoEndpointIsFalse()
    {
        var compactor = new DiagnosticsIpcCompactor(NullLogger.Instance, static (_, _) => Task.FromResult<Stream?>(null));

        Assert.False(await compactor.TryCompactAsync(4242, Cancellation));
        Assert.False(await compactor.TryCompactAsync(0, Cancellation));
    }

    /// <summary>
    /// The real thing, against the one .NET process a test can always reach: this one. Its own
    /// diagnostics server forces a blocking gen2 collection when the session is enabled.
    /// </summary>
    [Fact]
    public async Task ThisProcessIsCollectedWhenAsked()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("DOTNET_EnableDiagnostics") == "0",
            "The diagnostics server is switched off in this test run.");

        Assert.SkipUnless(
            OperatingSystem.IsWindows()
            || Directory.GetFiles(Path.GetTempPath(), $"dotnet-diagnostic-{Environment.ProcessId}-*-socket").Length > 0,
            "This platform exposes no diagnostics socket for the test process.");

        var before = GC.CollectionCount(2);

        Assert.True(await new DiagnosticsIpcCompactor(NullLogger.Instance).TryCompactAsync(Environment.ProcessId, Cancellation));
        Assert.True(GC.CollectionCount(2) > before, "No gen2 collection ran while the session was being enabled.");
    }

    /// <summary>A process id nothing is listening for answers false, and quickly.</summary>
    [Fact]
    public async Task AProcessWithNoEndpointIsFalseWithinAShortBound()
    {
        var started = DateTime.UtcNow;

        Assert.False(await new DiagnosticsIpcCompactor(NullLogger.Instance).TryCompactAsync(int.MaxValue - 7, Cancellation));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// A diagnostics endpoint in a script: the first connection answers the enable and streams a little
    /// trace until it is stopped; the second answers the stop.
    /// </summary>
    private sealed class ScriptedEndpoint(bool accept)
    {
        internal const ulong SessionId = 0x1122334455667788;

        private readonly Lock _lock = new();
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connections;

        internal int Connections => Volatile.Read(ref _connections);

        internal List<byte[]> Requests { get; } = [];

        internal Task<Stream?> ConnectAsync(int processId, CancellationToken cancellationToken)
        {
            var (client, server) = DuplexStreamPair.Create();
            var index = Interlocked.Increment(ref _connections);

            _ = Task.Run(() => ServeAsync(index, server), CancellationToken.None);

            return Task.FromResult<Stream?>(new DuplexStream(client.Input, client.Output));
        }

        private async Task ServeAsync(int index, DuplexEnd server)
        {
            var request = await ReadMessageAsync(server.Input);

            lock (_lock)
            {
                Requests.Add(request);
            }

            if (index == 1)
            {
                if (!accept)
                {
                    await WriteAsync(server, 0xFF, 0xFF, [0x05, 0x00, 0x07, 0x80]);
                    server.CompleteOutput();
                    return;
                }

                await WriteAsync(server, 0xFF, 0x00, DiagnosticsIpcCompactor.BuildStopTracingPayload(SessionId));
                await server.Output.WriteAsync(Encoding.ASCII.GetBytes("Nettrace!FastSerialization..."));

                // The trace stream ends when the session is stopped, as the runtime's does.
                await _stopped.Task;
                server.CompleteOutput();
                return;
            }

            await WriteAsync(server, 0xFF, 0x00, DiagnosticsIpcCompactor.BuildStopTracingPayload(SessionId));
            server.CompleteOutput();
            _stopped.TrySetResult();
        }

        private static async Task WriteAsync(DuplexEnd server, byte commandSet, byte commandId, byte[] payload)
        {
            await server.Output.WriteAsync(DiagnosticsIpcCompactor.BuildMessage(commandSet, commandId, payload));
            await server.Output.FlushAsync();
        }

        private static async Task<byte[]> ReadMessageAsync(Stream input)
        {
            var header = new byte[DiagnosticsIpcCompactor.HeaderSize];
            await input.ReadExactlyAsync(header);

            var size = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
            var message = new byte[size];

            header.CopyTo(message, 0);
            await input.ReadExactlyAsync(message.AsMemory(DiagnosticsIpcCompactor.HeaderSize));

            return message;
        }
    }
}
