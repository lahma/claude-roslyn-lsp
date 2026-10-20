using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Protocol;
using ClaudeRoslynLsp.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// One <see cref="AdapterSession"/> over in-memory pipes, with everything the client received kept.
/// </summary>
/// <remarks>
/// Shared by the recovery tests and the idle-recycle tests. The clock, the configuration and the
/// memory sampler are parameters because the recycle tests need all three under their control — a
/// twenty-minute window on the real clock is a test nobody runs, and a real Roslyn's memory is not
/// something a test can arrange — while the recovery tests keep the real clock they were written
/// against.
/// </remarks>
internal sealed class SessionHarness : IAsyncDisposable
{
    private readonly DuplexEnd _sessionEnd;
    private readonly DuplexEnd _testEnd;
    private readonly LspFrameWriter _writer;
    private readonly Task _reading;
    private readonly Lock _gate = new();
    private readonly List<JsonElement> _received = [];
    private readonly CancellationTokenSource _stopping = new();

    private int _disposed;

    /// <summary>Starts a session; nothing is sent until the test sends it.</summary>
    /// <param name="factory">The backend.</param>
    /// <param name="time">The session's clock; the real one when null.</param>
    /// <param name="options">The configuration; every default when null.</param>
    /// <param name="sampleMemory">The idle policy's memory sampler; the real process when null.</param>
    /// <param name="solution">What the session opens.</param>
    /// <param name="compactor">What the idle policy asks to give memory back; nothing when null.</param>
    internal SessionHarness(
        IRoslynConnectionFactory factory,
        TimeProvider? time = null,
        ClaudeRoslynLspOptions? options = null,
        Func<int, long?>? sampleMemory = null,
        string solution = "/w/HelloSolution.slnx",
        IBackendCompactor? compactor = null)
    {
        var (sessionEnd, testEnd) = DuplexStreamPair.Create();

        _sessionEnd = sessionEnd;
        _testEnd = testEnd;
        _writer = new LspFrameWriter(testEnd.Output);

        Session = new AdapterSession(
            sessionEnd.Input,
            sessionEnd.Output,
            factory,
            options ?? ClaudeRoslynLspOptions.FromEnvironment(static _ => null),
            () => solution,
            time ?? TimeProvider.System,
            NullLogger.Instance,
            sampleMemory: sampleMemory,
            compactor: compactor);

        RunTask = Task.Run(() => Session.RunAsync(_stopping.Token));
        _reading = Task.Run(ReadLoopAsync);
    }

    internal AdapterSession Session { get; }

    internal Task<int> RunTask { get; }

    /// <summary>Polls a condition on a short budget, failing with a diagnosis rather than hanging.</summary>
    /// <param name="condition">What is being waited for.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    internal static async Task WaitAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }

        Assert.Fail("The session did not reach the expected state within 30 s.");
    }

    internal async Task SendAsync(string message, CancellationToken cancellationToken) =>
        await _writer.WriteFrameAsync(Encoding.UTF8.GetBytes(message.Trim()), cancellationToken)
            .ConfigureAwait(false);

    internal async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        await SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":null,"rootUri":"file:///w","capabilities":{}}}""",
            cancellationToken);

        await AwaitResponseAsync(1, cancellationToken);
        await SendAsync("""{"jsonrpc":"2.0","method":"initialized","params":{}}""", cancellationToken);
    }

    /// <summary>The response with an id, or null when it has not arrived.</summary>
    internal JsonElement? TryFindResponse(int id)
    {
        lock (_gate)
        {
            foreach (var message in _received)
            {
                if (message.TryGetProperty("id", out var value)
                    && value.ValueKind == JsonValueKind.Number
                    && value.GetInt32() == id
                    && !message.TryGetProperty("method", out _))
                {
                    return message;
                }
            }
        }

        return null;
    }

    internal async Task<JsonElement> AwaitResponseAsync(int id, CancellationToken cancellationToken)
    {
        await WaitAsync(() => TryFindResponse(id) is not null, cancellationToken).ConfigureAwait(false);
        return TryFindResponse(id)!.Value;
    }

    internal async Task<JsonElement> AwaitPublishAsync(string uri, CancellationToken cancellationToken)
    {
        JsonElement? found = null;

        await WaitAsync(
            () =>
            {
                found = Notifications("textDocument/publishDiagnostics")
                    .Select(x => x.GetProperty("params"))
                    .Cast<JsonElement?>()
                    .FirstOrDefault(x => x!.Value.GetProperty("uri").GetString() == uri);

                return found is not null;
            },
            cancellationToken).ConfigureAwait(false);

        return found!.Value;
    }

    /// <summary>Every notification of one method the client was sent.</summary>
    internal JsonElement[] Notifications(string method)
    {
        lock (_gate)
        {
            return _received
                .Where(x => x.TryGetProperty("method", out var value) && value.GetString() == method)
                .ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Idempotent: a test that stops the session itself still has the harness disposed after it.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        _testEnd.CompleteOutput();

        try
        {
            await RunTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
        }

        await Session.DisposeAsync().ConfigureAwait(false);

        // The session's writer does not close the stream it writes to, so the reader below would
        // otherwise wait out its whole budget for an end of stream nothing is going to send.
        _sessionEnd.CompleteOutput();
        _writer.Dispose();
        _stopping.Dispose();

        try
        {
            await _reading.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException
                                              or IOException)
        {
        }
    }

    private async Task ReadLoopAsync()
    {
        var reader = new LspFrameReader(_testEnd.Input);

        try
        {
            while (await reader.ReadFrameAsync(CancellationToken.None).ConfigureAwait(false) is { } body)
            {
                var element = JsonDocument.Parse(body).RootElement.Clone();

                lock (_gate)
                {
                    _received.Add(element);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                              or LspProtocolException or JsonException)
        {
        }
    }
}
