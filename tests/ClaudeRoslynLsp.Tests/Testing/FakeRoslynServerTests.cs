using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Protocol;
using ClaudeRoslynLsp.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Testing;

/// <summary>
/// The test double, tested.
/// </summary>
/// <remarks>
/// Everything the mediation tests and the smoke test claim rests on this fake behaving like the
/// server it stands in for, so the two properties that carry the most weight are asserted directly
/// rather than inferred: that it answers <c>initialize</c> with Roslyn's real capability document —
/// including the four providers the adapter must filter out (C26) — and that navigation issued
/// before <c>workspace/projectInitializationComplete</c> comes back <em>empty and successful</em>,
/// which is what the readiness gate exists for (C27). If this second one ever regressed to
/// answering correctly from the first millisecond, a broken gate would pass every other test in the
/// suite.
/// </remarks>
public class FakeRoslynServerTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ItAnswersInitializeWithRoslynsOwnCapabilityDocument()
    {
        await using var peer = new FakePeer();

        var result = (await peer.RequestAsync(1, "initialize", "{}", Cancellation)).GetProperty("result");

        Assert.Equal(
            FakeRoslynScript.RoslynServerName,
            result.GetProperty("serverInfo").GetProperty("name").GetString());

        var capabilities = result.GetProperty("capabilities");

        // The four the adapter has to filter. Their presence here is what makes the adapter's own
        // test of that filtering mean something.
        Assert.True(capabilities.TryGetProperty("semanticTokensProvider", out _));
        Assert.True(capabilities.TryGetProperty("codeLensProvider", out _));
        Assert.True(capabilities.TryGetProperty("inlayHintProvider", out _));
        Assert.True(capabilities.TryGetProperty("_vs_onAutoInsertProvider", out _));

        // And Roslyn advertises incremental sync although it accepts full-text changes (C17).
        Assert.Equal(2, capabilities.GetProperty("textDocumentSync").GetProperty("change").GetInt32());
    }

    /// <summary>C27, reproduced: empty and successful, never an error.</summary>
    [Fact]
    public async Task NavigationBeforeReadinessIsEmptyAndSuccessful()
    {
        await using var peer = new FakePeer();

        await peer.RequestAsync(1, "initialize", "{}", Cancellation);

        var early = await peer.RequestAsync(2, "textDocument/definition", "{}", Cancellation);

        Assert.False(early.TryGetProperty("error", out _));
        Assert.Equal(0, early.GetProperty("result").GetArrayLength());

        peer.Server.CompleteProjectInitialization();

        var late = await peer.RequestAsync(3, "textDocument/definition", "{}", Cancellation);

        Assert.Equal(1, late.GetProperty("result").GetArrayLength());
    }

    /// <summary>
    /// The startup sequence, as a whole: registrations, the two configuration requests, the progress
    /// stream and the log lines all arrive off <c>initialized</c>, and the notification the adapter is
    /// waiting for arrives off <c>solution/open</c>.
    /// </summary>
    [Fact]
    public async Task TheStartupSequenceReproducesWhatTheRealServerSends()
    {
        await using var peer = new FakePeer(FakeRoslynScript.Roslyn512Startup(
            projectLoadDelay: TimeSpan.FromMilliseconds(10)));

        await peer.RequestAsync(1, "initialize", "{}", Cancellation);
        await peer.NotifyAsync("initialized", "{}", Cancellation);

        await peer.WaitUntilAsync(
            () => peer.MethodsReceived.Count(x => x == "client/registerCapability") == 4,
            Cancellation);

        var configuration = peer.Received
            .Where(x => x.TryGetProperty("method", out var method) && method.GetString() == "workspace/configuration")
            .ToArray();

        Assert.Equal(2, configuration.Length);
        Assert.Equal(75, configuration[0].GetProperty("params").GetProperty("items").GetArrayLength());
        Assert.Equal(5, configuration[1].GetProperty("params").GetProperty("items").GetArrayLength());

        Assert.Contains("window/workDoneProgress/create", peer.MethodsReceived);
        Assert.Contains("$/progress", peer.MethodsReceived);
        Assert.Contains("window/logMessage", peer.MethodsReceived);

        await peer.NotifyAsync("solution/open", """{"solution":"file:///w/Fixture/Fixture.slnx"}""", Cancellation);

        await peer.WaitUntilAsync(
            () => peer.MethodsReceived.Contains("workspace/projectInitializationComplete"),
            Cancellation);

        Assert.True(peer.Server.ProjectsLoaded);
    }

    [Fact]
    public async Task ShutdownIsAnsweredAndExitStopsTheServer()
    {
        await using var peer = new FakePeer();

        await peer.RequestAsync(1, "initialize", "{}", Cancellation);

        var shutdown = await peer.RequestAsync(2, "shutdown", null, Cancellation);

        Assert.Equal(JsonValueKind.Null, shutdown.GetProperty("result").ValueKind);
        Assert.True(peer.Server.ShutdownRequested);

        await peer.NotifyAsync("exit", null, Cancellation);
        await peer.WaitForStopAsync(Cancellation);
    }

    /// <summary>One end of a connection to a <see cref="FakeRoslynServer"/>, with the frames parsed.</summary>
    private sealed class FakePeer : IAsyncDisposable
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

        private readonly DuplexEnd _peerEnd;
        private readonly DuplexEnd _serverEnd;
        private readonly LspFrameWriter _writer;
        private readonly Task _running;
        private readonly Task _reading;
        private readonly Lock _lock = new();
        private readonly List<JsonElement> _received = [];

        internal FakePeer(FakeRoslynScript? script = null)
        {
            var (serverEnd, peerEnd) = DuplexStreamPair.Create();

            _serverEnd = serverEnd;
            _peerEnd = peerEnd;
            _writer = new LspFrameWriter(peerEnd.Output);

            Server = new FakeRoslynServer(
                script ?? FakeRoslynScript.Roslyn512Startup(projectLoadDelay: null),
                NullLogger.Instance);

            _running = Task.Run(async () =>
            {
                try
                {
                    await Server.RunAsync(serverEnd.Input, serverEnd.Output, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    serverEnd.CompleteOutput();
                }
            });

            _reading = Task.Run(ReadLoopAsync);
        }

        internal FakeRoslynServer Server { get; }

        internal IReadOnlyList<JsonElement> Received
        {
            get
            {
                lock (_lock)
                {
                    return _received.ToArray();
                }
            }
        }

        internal IReadOnlyList<string> MethodsReceived =>
            Received
                .Where(x => x.TryGetProperty("method", out _))
                .Select(x => x.GetProperty("method").GetString() ?? string.Empty)
                .ToArray();

        internal async Task<JsonElement> RequestAsync(
            int id,
            string method,
            string? parameters,
            CancellationToken cancellationToken)
        {
            await SendAsync(Envelope(id, method, parameters), cancellationToken).ConfigureAwait(false);

            var found = default(JsonElement?);

            await WaitUntilAsync(
                () =>
                {
                    foreach (var message in Received)
                    {
                        if (message.TryGetProperty("id", out var value)
                            && value.ValueKind == JsonValueKind.Number
                            && value.GetInt32() == id
                            && !message.TryGetProperty("method", out _))
                        {
                            found = message;
                            return true;
                        }
                    }

                    return false;
                },
                cancellationToken).ConfigureAwait(false);

            return found!.Value;
        }

        internal Task NotifyAsync(string method, string? parameters, CancellationToken cancellationToken) =>
            SendAsync(Envelope(null, method, parameters), cancellationToken);

        internal async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + Patience;

            while (!condition())
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    Assert.Fail($"The condition did not hold within {Patience.TotalSeconds:0} s. Received: [{string.Join(", ", MethodsReceived)}]");
                }

                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }
        }

        internal async Task WaitForStopAsync(CancellationToken cancellationToken) =>
            await _running.WaitAsync(Patience, cancellationToken).ConfigureAwait(false);

        public async ValueTask DisposeAsync()
        {
            _peerEnd.CompleteOutput();

            try
            {
                await _running.WaitAsync(Patience, CancellationToken.None).ConfigureAwait(false);
                await _reading.WaitAsync(Patience, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                // Whatever assertion failed first is the interesting one.
            }

            _writer.Dispose();
        }

        private static string Envelope(int? id, string method, string? parameters)
        {
            var builder = new StringBuilder("""{"jsonrpc":"2.0",""");

            if (id is { } value)
            {
                builder.Append("\"id\":").Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            }

            builder.Append("\"method\":\"").Append(method).Append('"');

            if (parameters is not null)
            {
                builder.Append(",\"params\":").Append(parameters);
            }

            return builder.Append('}').ToString();
        }

        private async Task SendAsync(string message, CancellationToken cancellationToken) =>
            await _writer.WriteFrameAsync(Encoding.UTF8.GetBytes(message), cancellationToken).ConfigureAwait(false);

        private async Task ReadLoopAsync()
        {
            var reader = new LspFrameReader(_peerEnd.Input);

            try
            {
                while (await reader.ReadFrameAsync(CancellationToken.None).ConfigureAwait(false) is { } body)
                {
                    using var document = JsonDocument.Parse(body);

                    lock (_lock)
                    {
                        _received.Add(document.RootElement.Clone());
                    }
                }
            }
            catch (Exception exception) when (exception is LspProtocolException or IOException or ObjectDisposedException)
            {
                // The server stopped writing; what it wrote before that is already recorded.
            }
        }
    }
}
