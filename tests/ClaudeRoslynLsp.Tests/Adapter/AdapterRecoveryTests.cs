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
/// The whole session, end to end, for the two things WP4 added to it: diagnostics reach the client
/// as a push, and a backend that dies is absorbed rather than forwarded.
/// </summary>
/// <remarks>
/// At this level rather than at the bridges' because both claims are about the <em>seam</em>: that a
/// <c>didOpen</c> the client sent turns into a pull on the wire and a <c>publishDiagnostics</c> back,
/// and that a stream that simply ends turns into a relaunch, a replayed mirror and a gate that
/// closes and reopens. A unit test of either bridge can prove neither.
/// </remarks>
public class AdapterRecoveryTests
{
    private const string ProgramUri = "file:///w/Hello.App/Program.cs";

    /// <summary>One CS0029 and one IDE0005, which is what the fixture's Program.cs really produces.</summary>
    private const string Report = """
        {"kind":"full","resultId":"r1","items":[
          {"range":{"start":{"line":21,"character":16},"end":{"line":21,"character":19}},"severity":1,
           "code":"CS0029","message":"Cannot implicitly convert type 'string' to 'int'"},
          {"range":{"start":{"line":0,"character":0},"end":{"line":1,"character":18}},"severity":4,
           "code":"IDE0005","message":"Using directive is unnecessary.","tags":[1,2147483645]}]}
        """;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// A <c>didOpen</c> on a loaded workspace becomes a pull on the wire and a push to the client —
    /// the translation the whole product exists for, proved through the session rather than around
    /// it.
    /// </summary>
    [Fact]
    public async Task ADidOpenTurnsIntoAPullAndAPublish()
    {
        var factory = new RestartableFakeRoslynFactory(
            () => RestartableFakeRoslynFactory.ScriptWithDiagnostics(Report));

        await using var harness = new RecoveryHarness(factory);

        await harness.HandshakeAsync(Cancellation);
        await WaitAsync(() => factory.Latest is not null, Cancellation);
        factory.Latest!.CompleteProjectInitialization();

        await harness.SendAsync(DidOpen(ProgramUri, 1), Cancellation);

        var published = await harness.AwaitPublishAsync(ProgramUri, Cancellation);
        var diagnostics = published.GetProperty("diagnostics");

        Assert.Equal(1, diagnostics.GetArrayLength());
        Assert.Equal("CS0029", diagnostics[0].GetProperty("code").GetString());
        Assert.Equal(1, published.GetProperty("version").GetInt32());

        // The pull carried no identifier (C9) and reached the backend as a real request.
        Assert.Contains(
            factory.Latest.ReceivedMessages,
            x => x.Contains("\"method\":\"textDocument/diagnostic\"", StringComparison.Ordinal)
                 && !x.Contains("\"identifier\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// A backend that dies is relaunched, told about the documents the client has open, and told to
    /// open the solution again — and the client is never sent an error about any of it.
    /// </summary>
    [Fact]
    public async Task AKilledBackendIsRelaunchedWithTheMirrorReplayed()
    {
        var factory = new RestartableFakeRoslynFactory();
        await using var harness = new RecoveryHarness(factory);

        await harness.HandshakeAsync(Cancellation);
        await WaitAsync(() => factory.Latest is not null, Cancellation);

        var first = factory.Latest!;
        first.CompleteProjectInitialization();

        await harness.SendAsync(DidOpen(ProgramUri, 3), Cancellation);
        await WaitAsync(
            () => first.ReceivedMethods.Contains("textDocument/didOpen"),
            Cancellation);

        factory.Kill();

        await WaitAsync(() => factory.ConnectCount == 2, Cancellation);

        var second = factory.Latest!;

        await WaitAsync(
            () => second.ReceivedMethods.Contains("solution/open")
                  && second.ReceivedMethods.Contains("textDocument/didOpen"),
            Cancellation);

        // The replay is one didOpen per document at its latest text, not a queue of notifications.
        var replay = Assert.Single(
            second.ReceivedMessages,
            x => x.Contains("\"method\":\"textDocument/didOpen\"", StringComparison.Ordinal));

        Assert.Contains("\"version\":3", replay, StringComparison.Ordinal);
        Assert.Contains(ProgramUri, replay, StringComparison.Ordinal);

        // And the gate is shut again, which is what stops an empty answer (C27) reaching the client
        // during the gap. Not asserted as Starting specifically: by the time the replay has landed
        // the second backend has usually answered initialize, and RoslynInitialized still holds
        // every request.
        Assert.False(harness.Session.Gate.IsOpen);
        Assert.True(
            harness.Session.Gate.State != ReadinessState.Failed,
            harness.Session.Gate.FailureReason ?? "(no reason recorded)");

        second.CompleteProjectInitialization();
        await WaitAsync(() => harness.Session.Gate.State == ReadinessState.ProjectsLoaded, Cancellation);
    }

    /// <summary>
    /// A request issued during the relaunch is <em>held</em> and then answered — Claude never sees
    /// the crash, only a slow answer.
    /// </summary>
    [Fact]
    public async Task ARequestIssuedDuringTheRelaunchIsHeldAndThenAnswered()
    {
        var factory = new RestartableFakeRoslynFactory();
        await using var harness = new RecoveryHarness(factory);

        await harness.HandshakeAsync(Cancellation);
        await WaitAsync(() => factory.Latest is not null, Cancellation);
        factory.Latest!.CompleteProjectInitialization();

        await harness.SendAsync(DidOpen(ProgramUri, 1), Cancellation);
        factory.Kill();

        await WaitAsync(() => factory.ConnectCount == 2, Cancellation);
        await harness.SendAsync(Definition(7, ProgramUri), Cancellation);

        // Asserted as "the gate is holding it", not as "no answer arrived within N milliseconds".
        // The second formulation is a race with the clock — it fails on a loaded machine for a
        // session that is behaving perfectly — and it is also the weaker claim: a request the gate
        // has queued cannot have been answered, because the queue is what answers it.
        await WaitAsync(() => harness.Session.Gate.HeldCount > 0, Cancellation);

        Assert.Null(harness.TryFindResponse(7));

        factory.Latest!.CompleteProjectInitialization();

        var response = await harness.AwaitResponseAsync(7, Cancellation);

        Assert.False(response.TryGetProperty("error", out _));
        Assert.True(response.GetProperty("result").GetArrayLength() > 0);
    }

    /// <summary>
    /// A request that was <em>already in flight</em> when the backend died is re-held and answered
    /// from the relaunched one, under the id the client is still waiting on (D74).
    /// </summary>
    /// <remarks>
    /// The case the sibling test above cannot reach: there, the request arrives during the gap and
    /// the gate is already shut. Here it was forwarded to a live backend, which then died holding it
    /// — the ordering CI reproduced on Linux and this machine almost never does (C56). Refusing it
    /// with <c>-32603</c> was a crash made visible to the model for no reason: every request the
    /// adapter forwards is a read, and the backend was about to be back.
    /// </remarks>
    [Fact]
    public async Task ARequestInFlightWhenTheBackendDiesIsReplayedRatherThanRefused()
    {
        using var reached = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var connections = 0;

        var factory = new RestartableFakeRoslynFactory(
            () => Interlocked.Increment(ref connections) == 1
                ? ScriptWithHangingDefinition(reached, release)
                : FakeRoslynScript.Roslyn512Startup(projectLoadDelay: null));

        await using var harness = new RecoveryHarness(factory);

        await harness.HandshakeAsync(Cancellation);
        await WaitAsync(() => factory.Latest is not null, Cancellation);

        var first = factory.Latest!;
        first.CompleteProjectInitialization();
        await WaitAsync(() => harness.Session.Gate.IsOpen, Cancellation);

        await harness.SendAsync(Definition(77, ProgramUri), Cancellation);

        // The request is genuinely at the backend, not merely sent: the responder has it and is
        // holding it, which is the only way to make "died mid-request" deterministic.
        await WaitAsync(() => reached.IsSet, Cancellation);

        factory.Kill();

        await WaitAsync(() => factory.ConnectCount == 2, Cancellation);

        // Let the first backend's blocked responder unwind; its answer goes to a closed stream.
        release.Set();

        // Re-held rather than refused: the gate has it, and nothing has been sent to the client.
        await WaitAsync(() => harness.Session.Gate.HeldCount > 0, Cancellation);
        Assert.Null(harness.TryFindResponse(77));

        factory.Latest!.CompleteProjectInitialization();

        var response = await harness.AwaitResponseAsync(77, Cancellation);

        Assert.False(response.TryGetProperty("error", out _));
        Assert.True(response.GetProperty("result").GetArrayLength() > 0);
    }

    /// <summary>
    /// Three deaths inside the window and the session gives up: every request is refused with a
    /// <c>doctor</c> hint, and the client is shown one message rather than a silence.
    /// </summary>
    [Fact]
    public async Task TheFourthDeathFailsTheSessionWithADoctorHint()
    {
        var factory = new RestartableFakeRoslynFactory();
        await using var harness = new RecoveryHarness(factory);

        await harness.HandshakeAsync(Cancellation);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await WaitAsync(() => factory.ConnectCount == attempt, Cancellation);
            factory.Kill();
        }

        await WaitAsync(() => harness.Session.Gate.State == ReadinessState.Failed, Cancellation);

        Assert.Equal(RoslynSupervisor.MaxRestarts, factory.ConnectCount - 1);

        await harness.SendAsync(Definition(9, ProgramUri), Cancellation);
        var response = await harness.AwaitResponseAsync(9, Cancellation);

        var error = response.GetProperty("error");

        Assert.Equal(JsonRpcErrors.InternalError, error.GetProperty("code").GetInt32());
        Assert.Contains("doctor", error.GetProperty("message").GetString()!, StringComparison.Ordinal);

        // The one channel a client actually surfaces, sent once.
        var shown = harness.Notifications("window/showMessage");

        Assert.Single(shown);
        Assert.Equal(1, shown[0].GetProperty("params").GetProperty("type").GetInt32());
        Assert.Contains("doctor", shown[0].GetProperty("params").GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>An orderly shutdown is not a crash, and must not spend a restart.</summary>
    [Fact]
    public async Task AnOrderlyShutdownDoesNotRelaunchAnything()
    {
        var factory = new RestartableFakeRoslynFactory();
        await using var harness = new RecoveryHarness(factory);

        await harness.HandshakeAsync(Cancellation);
        await WaitAsync(() => factory.Latest is not null, Cancellation);
        factory.Latest!.CompleteProjectInitialization();

        await harness.SendAsync("""{"jsonrpc":"2.0","id":50,"method":"shutdown"}""", Cancellation);
        await harness.AwaitResponseAsync(50, Cancellation);
        await harness.SendAsync("""{"jsonrpc":"2.0","method":"exit"}""", Cancellation);

        Assert.Equal(0, await harness.RunTask);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(0, harness.Session.Supervisor.RestartsInWindow);
    }

    /// <summary>
    /// Polls a condition on a short budget.
    /// </summary>
    /// <remarks>
    /// Everything asserted here happens on a task the session started — a relaunch, a replay, a pull
    /// — so "it has happened" is not observable from the call that caused it. Polling is the honest
    /// way to express that without growing a synchronisation primitive in the product for a test.
    /// </remarks>
    /// <param name="condition">What is being waited for.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    private static async Task WaitAsync(Func<bool> condition, CancellationToken cancellationToken)
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

    /// <summary>
    /// The standard startup script with one responder that never answers until the test says so.
    /// </summary>
    /// <remarks>
    /// Blocking the fake's read loop is deliberate: it is the closest thing to a real server that
    /// accepted a request and then died with it, and killing the connection does not depend on that
    /// loop being free — <c>Kill</c> completes the <em>writer</em>, which the adapter sees as end of
    /// stream regardless.
    /// </remarks>
    /// <param name="reached">Set once the responder has the request.</param>
    /// <param name="release">Waited on before the responder returns.</param>
    private static FakeRoslynScript ScriptWithHangingDefinition(
        ManualResetEventSlim reached,
        ManualResetEventSlim release)
    {
        var script = FakeRoslynScript.Roslyn512Startup(projectLoadDelay: null);

        return new FakeRoslynScript
        {
            InitializeResult = script.InitializeResult,
            Steps = script.Steps,
            ProjectLoadDelay = script.ProjectLoadDelay,
            Responders = new Dictionary<string, FakeRoslynResponder>(script.Responders, StringComparer.Ordinal)
            {
                ["textDocument/definition"] = (_, _) =>
                {
                    reached.Set();
                    release.Wait(TimeSpan.FromSeconds(30));

                    return "[]"u8.ToArray();
                },
            },
        };
    }

    private static string DidOpen(string uri, int version) => $$$"""
        {"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":
         {"uri":"{{{uri}}}","languageId":"csharp","version":{{{version}}},"text":"class Program { }"}
        }}
        """;

    private static string Definition(int id, string uri) => $$$"""
        {"jsonrpc":"2.0","id":{{{id}}},"method":"textDocument/definition","params":{
         "textDocument":{"uri":"{{{uri}}}"},"position":{"line":0,"character":6}
        }}
        """;

    /// <summary>A session over in-memory pipes, with everything the client received kept.</summary>
    private sealed class RecoveryHarness : IAsyncDisposable
    {
        private readonly DuplexEnd _testEnd;
        private readonly LspFrameWriter _writer;
        private readonly Task _reading;
        private readonly Lock _gate = new();
        private readonly List<JsonElement> _received = [];
        private readonly CancellationTokenSource _stopping = new();

        internal RecoveryHarness(IRoslynConnectionFactory factory)
        {
            var (sessionEnd, testEnd) = DuplexStreamPair.Create();

            _testEnd = testEnd;
            _writer = new LspFrameWriter(testEnd.Output);

            Session = new AdapterSession(
                sessionEnd.Input,
                sessionEnd.Output,
                factory,
                ClaudeRoslynLspOptions.FromEnvironment(static _ => null),
                () => "/w/HelloSolution.slnx",
                TimeProvider.System,
                NullLogger.Instance);

            RunTask = Task.Run(() => Session.RunAsync(_stopping.Token));
            _reading = Task.Run(ReadLoopAsync);
        }

        internal AdapterSession Session { get; }

        internal Task<int> RunTask { get; }

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
}
