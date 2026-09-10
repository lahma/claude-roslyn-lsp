using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Cli;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Protocol;
using ClaudeRoslynLsp.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// The whole mediation, driven over in-memory pipes against the scripted Roslyn 5.12 startup.
/// </summary>
/// <remarks>
/// <para>
/// These are the tests that hold the product's central claims: that the handshake does not wait for
/// the backend, that a request issued during the load is <em>held</em> rather than answered empty
/// (C27), that what comes back carries the client's own id, and that shutting down does so in order.
/// Each of those is a failure a user would experience as "the language server does not work" with no
/// error anywhere, which is precisely why they are asserted at this level rather than left to the
/// units underneath.
/// </para>
/// <para>
/// The pipes are real <see cref="System.IO.Pipelines.Pipe"/>s, so a read blocks until the other side
/// writes and completing a writer produces a genuine end of stream — the same shapes a socket or a
/// child's stdio would produce, without a process.
/// </para>
/// </remarks>
public class AdapterSessionTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// The handshake is answered out of this repository's own capability document, before anything
    /// has been connected. Claude Code holds <c>initialize</c> open forever otherwise.
    /// </summary>
    [Fact]
    public async Task InitializeIsAnsweredBeforeTheBackendIsEvenConnected()
    {
        await using var harness = new SessionHarness();

        await harness.SendAsync(Initialize(1), Cancellation);
        var response = await harness.AwaitResponseAsync(1, Cancellation);

        var result = response.GetProperty("result");
        var serverInfo = result.GetProperty("serverInfo");

        Assert.Equal(ServerVersion.Name, serverInfo.GetProperty("name").GetString());
        Assert.Equal(ServerVersion.Value, serverInfo.GetProperty("version").GetString());

        // Nothing was connected to answer it with.
        Assert.Null(harness.Factory!.Server);
        Assert.Equal(ReadinessState.Starting, harness.Session.Gate.State);

        var sync = result.GetProperty("capabilities").GetProperty("textDocumentSync");

        Assert.True(sync.GetProperty("openClose").GetBoolean());
        Assert.Equal(1, sync.GetProperty("change").GetInt32());
        Assert.False(sync.GetProperty("save").GetProperty("includeText").GetBoolean());
    }

    /// <summary>
    /// C26, asserted end to end: the fake answers with Roslyn's real document, which advertises
    /// semantic tokens, code lens, inlay hints and a <c>_vs_</c> provider. None of them may reach the
    /// client, because advertising a capability is a promise to answer the requests it enables and
    /// this adapter bridges none of those four.
    /// </summary>
    [Fact]
    public async Task RoslynsOwnCapabilitiesAreNeverForwardedToTheClient()
    {
        await using var harness = new SessionHarness();

        await harness.SendAsync(Initialize(1), Cancellation);
        var response = await harness.AwaitResponseAsync(1, Cancellation);

        await harness.SendAsync(Notification("initialized"), Cancellation);
        await harness.AwaitBackendAsync(Cancellation);

        var capabilities = response.GetProperty("result").GetProperty("capabilities");

        Assert.False(capabilities.TryGetProperty("semanticTokensProvider", out _));
        Assert.False(capabilities.TryGetProperty("codeLensProvider", out _));
        Assert.False(capabilities.TryGetProperty("inlayHintProvider", out _));
        Assert.False(capabilities.TryGetProperty("_vs_onAutoInsertProvider", out _));
        Assert.False(capabilities.TryGetProperty("documentOnTypeFormattingProvider", out _));

        // And what it does advertise is what the mediation actually carries.
        Assert.True(capabilities.GetProperty("definitionProvider").GetBoolean());
        Assert.True(capabilities.GetProperty("callHierarchyProvider").GetBoolean());
        Assert.True(capabilities.GetProperty("renameProvider").GetProperty("prepareProvider").GetBoolean());
        Assert.True(capabilities.GetProperty("codeActionProvider").GetProperty("resolveProvider").GetBoolean());
    }

    /// <summary>
    /// What the client said about itself is carried into the handshake with Roslyn: its root becomes
    /// Roslyn's <c>rootUri</c> and its workspace folder, and the adapter names its own process id so
    /// Roslyn exits with the adapter rather than being orphaned holding a whole solution.
    /// </summary>
    /// <remarks>
    /// Asserted end to end because the parameters live one level down, inside <c>params</c>, and
    /// reading them from the wrong level produces a server that works in every respect except that
    /// it silently opens nothing.
    /// </remarks>
    [Fact]
    public async Task TheClientsRootReachesRoslynsInitialize()
    {
        await using var harness = new SessionHarness();

        await harness.HandshakeAsync(Cancellation);
        var backend = await harness.AwaitBackendAsync(Cancellation);
        await harness.AwaitBackendMethodAsync(backend, "initialized", Cancellation);

        var initialize = Assert.Single(
            backend.ReceivedMessages,
            x => x.Contains("\"method\":\"initialize\"", StringComparison.Ordinal));

        using var document = JsonDocument.Parse(initialize);
        var parameters = document.RootElement.GetProperty("params");

        Assert.Equal("file:///w/Fixture", parameters.GetProperty("rootUri").GetString());
        Assert.Equal(Environment.ProcessId, parameters.GetProperty("processId").GetInt32());
        Assert.Equal("file:///w/Fixture", parameters.GetProperty("workspaceFolders")[0].GetProperty("uri").GetString());
        Assert.Equal(ServerVersion.Name, parameters.GetProperty("clientInfo").GetProperty("name").GetString());

        // And the authored document, not the client's: Claude Code declares almost none of this.
        var capabilities = parameters.GetProperty("capabilities");

        Assert.True(capabilities.GetProperty("workspace").GetProperty("configuration").GetBoolean());
        Assert.True(capabilities.GetProperty("workspace").GetProperty("didChangeWatchedFiles")
            .GetProperty("dynamicRegistration").GetBoolean());
        Assert.True(capabilities.GetProperty("textDocument").GetProperty("diagnostic")
            .GetProperty("dynamicRegistration").GetBoolean());
        Assert.True(capabilities.GetProperty("window").GetProperty("workDoneProgress").GetBoolean());
        Assert.Equal("utf-16", capabilities.GetProperty("general").GetProperty("positionEncodings")[0].GetString());

        // linkSupport false everywhere: Claude Code renders a LocationLink as nothing at all.
        Assert.False(capabilities.GetProperty("textDocument").GetProperty("definition")
            .GetProperty("linkSupport").GetBoolean());

        // And the three the adapter refuses to carry are absent, so Roslyn never registers them.
        Assert.False(capabilities.GetProperty("textDocument").TryGetProperty("semanticTokens", out _));
        Assert.False(capabilities.GetProperty("textDocument").TryGetProperty("inlayHint", out _));
        Assert.False(capabilities.GetProperty("textDocument").TryGetProperty("codeLens", out _));
    }

    /// <summary>
    /// The product's central behaviour. Roslyn answers a definition issued before
    /// <c>projectInitializationComplete</c> with an empty successful result (C27) — indistinguishable
    /// from "no definition found". So the adapter holds it, and answers when the workspace is up.
    /// </summary>
    [Fact]
    public async Task ADefinitionIssuedDuringTheLoadIsHeldAndThenAnswered()
    {
        await using var harness = new SessionHarness();

        await harness.HandshakeAsync(Cancellation);
        var backend = await harness.AwaitBackendAsync(Cancellation);
        await harness.AwaitBackendMethodAsync(backend, "solution/open", Cancellation);

        await harness.SendAsync(Request(2, "textDocument/definition", """
            {"textDocument":{"uri":"file:///w/Fixture/Fixture.App/Program.cs"},"position":{"line":12,"character":9}}
            """), Cancellation);

        await harness.WaitUntilAsync(() => harness.Session.Gate.HeldCount == 1, Cancellation);
        Assert.Null(harness.TryFindResponse(2));

        backend.CompleteProjectInitialization();

        var response = await harness.AwaitResponseAsync(2, Cancellation);
        var result = response.GetProperty("result");

        Assert.Equal(ReadinessState.ProjectsLoaded, harness.Session.Gate.State);
        Assert.Equal(1, result.GetArrayLength());
        Assert.Equal("file:///w/Fixture/Fixture.Core/IShape.cs", result[0].GetProperty("uri").GetString());
    }

    /// <summary>
    /// Once the workspace is up a request crosses immediately, and the answer comes back under the
    /// client's own id — in the form the client wrote it. Answering <c>"nav-7"</c> with <c>7</c> is a
    /// correlation failure the client reports as a request that never came back.
    /// </summary>
    [Fact]
    public async Task AfterReadinessARequestPassesThroughAndItsAnswerCarriesTheClientsOwnId()
    {
        await using var harness = new SessionHarness();

        await harness.HandshakeAsync(Cancellation);
        var backend = await harness.AwaitBackendAsync(Cancellation);
        await harness.AwaitBackendMethodAsync(backend, "solution/open", Cancellation);

        backend.CompleteProjectInitialization();
        await harness.WaitUntilAsync(() => harness.Session.Gate.State == ReadinessState.ProjectsLoaded, Cancellation);

        await harness.SendAsync("""
            {"jsonrpc":"2.0","id":"nav-7","method":"textDocument/hover","params":{"textDocument":{"uri":"file:///w/a.cs"},"position":{"line":0,"character":0}}}
            """, Cancellation);

        var response = await harness.AwaitResponseAsync("nav-7", Cancellation);

        Assert.Equal(JsonValueKind.String, response.GetProperty("id").ValueKind);
        Assert.Equal("nav-7", response.GetProperty("id").GetString());
        Assert.Equal("markdown", response.GetProperty("result").GetProperty("contents").GetProperty("kind").GetString());

        // And Roslyn saw an id this adapter minted, not the client's.
        var forwarded = Assert.Single(backend.ReceivedMessages, x => x.Contains("textDocument/hover", StringComparison.Ordinal));
        Assert.DoesNotContain("nav-7", forwarded, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cancellation for a request that has never reached Roslyn cannot be mapped through — Roslyn
    /// has never seen the id. The gate answers it instead, with LSP's <c>RequestCancelled</c>.
    /// </summary>
    [Fact]
    public async Task CancellingAHeldRequestAnswersItWithRequestCancelled()
    {
        await using var harness = new SessionHarness();

        await harness.HandshakeAsync(Cancellation);
        var backend = await harness.AwaitBackendAsync(Cancellation);
        await harness.AwaitBackendMethodAsync(backend, "solution/open", Cancellation);

        await harness.SendAsync(Request(3, "textDocument/references", "{}"), Cancellation);
        await harness.WaitUntilAsync(() => harness.Session.Gate.HeldCount == 1, Cancellation);

        await harness.SendAsync(Notification("$/cancelRequest", """{"id":3}"""), Cancellation);

        var response = await harness.AwaitResponseAsync(3, Cancellation);
        var error = response.GetProperty("error");

        Assert.Equal(JsonRpcErrors.RequestCancelled, error.GetProperty("code").GetInt32());
        Assert.Equal(0, harness.Session.Gate.HeldCount);

        // And the request never reached Roslyn at all.
        Assert.DoesNotContain("textDocument/references", backend.ReceivedMethods);
    }

    /// <summary>
    /// The scripted startup includes a request nothing in the table knows. Roslyn must get
    /// <c>-32601</c> rather than silence: an unanswered server-to-client request stalls whichever
    /// handler issued it, and several of those are on the solution-load path.
    /// </summary>
    [Fact]
    public async Task AnUnknownRoslynRequestIsRefusedRatherThanIgnored()
    {
        await using var harness = new SessionHarness();

        await harness.HandshakeAsync(Cancellation);
        var backend = await harness.AwaitBackendAsync(Cancellation);

        await harness.WaitUntilAsync(
            () => backend.ReceivedMessages.Any(x => x.Contains("-32601", StringComparison.Ordinal)),
            Cancellation);

        var refusal = Assert.Single(backend.ReceivedMessages, x => x.Contains("-32601", StringComparison.Ordinal));

        Assert.Contains("window/_roslyn_unknownFutureRequest", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// The registrations and configuration requests Claude Code refuses are answered here, so Roslyn
    /// ends up with its watchers, its diagnostic sources and this adapter's tuned options.
    /// </summary>
    [Fact]
    public async Task RoslynsRegistrationsAndConfigurationAreAnsweredByTheAdapter()
    {
        await using var harness = new SessionHarness();

        await harness.HandshakeAsync(Cancellation);
        var backend = await harness.AwaitBackendAsync(Cancellation);

        await harness.WaitUntilAsync(
            () => harness.Session.Registrations.DiagnosticSources.Count >= 10,
            Cancellation);

        var sources = harness.Session.Registrations.DiagnosticSources;

        Assert.Contains(sources, x => x.Identifier == "DocumentCompilerSemantic");
        Assert.Contains(sources, x => x.Identifier == "DocumentAnalyzerSemantic");
        Assert.Contains(sources, x => x.Identifier is null);

        // 135 registrations collapsed onto two real directories in the script's shape.
        Assert.Equal(2, harness.Session.Registrations.Watchers.Count);

        var configuration = backend.ReceivedMessages
            .Where(x => x.Contains("\"result\":[", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, configuration.Length);
        Assert.Contains(configuration, x => x.Contains("openFiles", StringComparison.Ordinal));
    }

    /// <summary>
    /// Documents opened before the backend is up are not queued as raw notifications; the mirror
    /// holds the latest text and replays one <c>didOpen</c> per document once Roslyn is initialised.
    /// Three edits before the backend arrived become one notification at the third text.
    /// </summary>
    [Fact]
    public async Task DocumentsOpenedBeforeTheBackendIsUpAreReplayedFromTheMirror()
    {
        await using var harness = new SessionHarness();

        await harness.SendAsync(Initialize(1), Cancellation);
        await harness.AwaitResponseAsync(1, Cancellation);

        await harness.SendAsync(DidOpen("file:///w/a.cs", 1, "one"), Cancellation);
        await harness.SendAsync(DidChange("file:///w/a.cs", 2, "two"), Cancellation);
        await harness.SendAsync(DidChange("file:///w/a.cs", 3, "three"), Cancellation);

        await harness.WaitUntilAsync(() => harness.Session.Documents.Count == 1, Cancellation);

        await harness.SendAsync(Notification("initialized"), Cancellation);
        var backend = await harness.AwaitBackendAsync(Cancellation);

        await harness.WaitUntilAsync(
            () => backend.ReceivedMethods.Count(x => x == "textDocument/didOpen") == 1,
            Cancellation);

        var replay = Assert.Single(backend.ReceivedMessages, x => x.Contains("textDocument/didOpen", StringComparison.Ordinal));

        using var document = JsonDocument.Parse(replay);
        var item = document.RootElement.GetProperty("params").GetProperty("textDocument");

        Assert.Equal(3, item.GetProperty("version").GetInt32());
        Assert.Equal("three", item.GetProperty("text").GetString());
        Assert.DoesNotContain("textDocument/didChange", backend.ReceivedMethods);
    }

    /// <summary>
    /// <c>shutdown</c> reaches Roslyn before the client is answered, and <c>exit</c> reaches it before
    /// the process stops — the ordering an editor's quit depends on.
    /// </summary>
    [Fact]
    public async Task ShutdownAndExitPropagateToRoslynInOrderAndExitZero()
    {
        await using var harness = new SessionHarness();

        await harness.HandshakeAsync(Cancellation);
        var backend = await harness.AwaitBackendAsync(Cancellation);
        await harness.AwaitBackendMethodAsync(backend, "solution/open", Cancellation);

        await harness.SendAsync(Request(9, "shutdown", null), Cancellation);
        var response = await harness.AwaitResponseAsync(9, Cancellation);

        Assert.True(response.TryGetProperty("result", out var result));
        Assert.Equal(JsonValueKind.Null, result.ValueKind);
        Assert.Contains("shutdown", backend.ReceivedMethods);
        Assert.True(backend.ShutdownRequested);

        await harness.SendAsync(Notification("exit"), Cancellation);

        Assert.Equal(CliDispatcher.ExitSuccess, await harness.RunToCompletionAsync(Cancellation));
        Assert.Contains("exit", backend.ReceivedMethods);
    }

    /// <summary>
    /// The specification's rule, and the one that gets "simplified" to a uniform zero by somebody who
    /// has not read it: <c>exit</c> without a preceding <c>shutdown</c> is a failure the client is
    /// entitled to notice.
    /// </summary>
    [Fact]
    public async Task ExitWithoutShutdownIsAFailedExit()
    {
        await using var harness = new SessionHarness();

        await harness.SendAsync(Initialize(1), Cancellation);
        await harness.AwaitResponseAsync(1, Cancellation);
        await harness.SendAsync(Notification("exit"), Cancellation);

        Assert.Equal(CliDispatcher.ExitFailure, await harness.RunToCompletionAsync(Cancellation));
    }

    /// <summary>
    /// A client that was killed closes the stream without saying anything. Same rule: orderly only if
    /// <c>shutdown</c> came first.
    /// </summary>
    [Theory]
    [InlineData(false, CliDispatcher.ExitFailure)]
    [InlineData(true, CliDispatcher.ExitSuccess)]
    public async Task ClientEndOfStreamFollowsTheSameExitCodeRule(bool shutdownFirst, int expected)
    {
        await using var harness = new SessionHarness();

        await harness.SendAsync(Initialize(1), Cancellation);
        await harness.AwaitResponseAsync(1, Cancellation);

        if (shutdownFirst)
        {
            await harness.SendAsync(Request(2, "shutdown", null), Cancellation);
            await harness.AwaitResponseAsync(2, Cancellation);
        }

        harness.CloseClientStream();

        Assert.Equal(expected, await harness.RunToCompletionAsync(Cancellation));
    }

    /// <summary>
    /// Losing framing is not recoverable — there is no way to find where the next message begins — so
    /// the session stops rather than answering from the middle of a stream.
    /// </summary>
    [Fact]
    public async Task AStreamThatIsNotFramedStopsTheSession()
    {
        await using var harness = new SessionHarness();

        await harness.SendRawAsync("Content-Length: not-a-number\r\n\r\n{}", Cancellation);

        Assert.Equal(CliDispatcher.ExitFailure, await harness.RunToCompletionAsync(Cancellation));
    }

    /// <summary>
    /// Well framed, badly written. The frame boundary is still known, so the connection survives it —
    /// and the answer carries a null id, because there was no readable one.
    /// </summary>
    [Fact]
    public async Task AMessageThatIsNotJsonIsAnsweredUnderANullIdAndTheSessionContinues()
    {
        await using var harness = new SessionHarness();

        await harness.SendAsync("{ this is not json", Cancellation);
        await harness.SendAsync(Initialize(1), Cancellation);

        var parseError = await harness.AwaitFirstAsync(
            x => x.TryGetProperty("error", out var error)
                 && error.GetProperty("code").GetInt32() == JsonRpcErrors.ParseError,
            Cancellation);

        Assert.Equal(JsonValueKind.Null, parseError.GetProperty("id").ValueKind);

        // And the session kept going.
        await harness.AwaitResponseAsync(1, Cancellation);
    }

    /// <summary>
    /// With no backend to be had, every request is refused with an explanation and a <c>doctor</c>
    /// hint. That is a state a user can act on, unlike a server that answers everything with nothing.
    /// </summary>
    [Fact]
    public async Task WithNoBackendEveryRequestIsRefusedWithADoctorHint()
    {
        await using var harness = new SessionHarness(new FailingFactory());

        await harness.HandshakeAsync(Cancellation);
        await harness.WaitUntilAsync(() => harness.Session.Gate.State == ReadinessState.Failed, Cancellation);

        await harness.SendAsync(Request(2, "textDocument/definition", "{}"), Cancellation);

        var error = (await harness.AwaitResponseAsync(2, Cancellation)).GetProperty("error");

        Assert.Equal(JsonRpcErrors.InternalError, error.GetProperty("code").GetInt32());
        Assert.Contains("doctor", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// With nothing configured there is nothing that will ever report the workspace loaded, so holding
    /// requests would be a hang. Misc-files mode is degraded, not broken (C28).
    /// </summary>
    [Fact]
    public async Task WithNoSolutionConfiguredTheGateOpensImmediately()
    {
        await using var harness = new SessionHarness(solutionPath: null);

        await harness.HandshakeAsync(Cancellation);
        await harness.AwaitBackendAsync(Cancellation);

        await harness.WaitUntilAsync(() => harness.Session.Gate.State == ReadinessState.ProjectsLoaded, Cancellation);

        await harness.SendAsync(Request(2, "textDocument/definition", "{}"), Cancellation);
        var response = await harness.AwaitResponseAsync(2, Cancellation);

        Assert.True(response.TryGetProperty("result", out _));
    }

    // -----------------------------------------------------------------------------------------
    // Message literals
    // -----------------------------------------------------------------------------------------

    private static string Initialize(int id) => Fill(
        """
        {"jsonrpc":"2.0","id":#ID#,"method":"initialize","params":{"processId":4242,"rootUri":"file:///w/Fixture","capabilities":{"workspace":{"applyEdit":false,"configuration":false}}}}
        """,
        ("#ID#", Text(id)));

    private static string Request(int id, string method, string? parameters) =>
        parameters is null
            ? $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"}"""
            : $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters.Trim()}}}""";

    private static string Notification(string method, string? parameters = null) =>
        parameters is null
            ? $$"""{"jsonrpc":"2.0","method":"{{method}}"}"""
            : $$"""{"jsonrpc":"2.0","method":"{{method}}","params":{{parameters.Trim()}}}""";

    private static string DidOpen(string uri, int version, string text) => Fill(
        """
        {"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"#URI#","languageId":"csharp","version":#VERSION#,"text":"#TEXT#"}}}
        """,
        ("#URI#", uri),
        ("#VERSION#", Text(version)),
        ("#TEXT#", text));

    private static string DidChange(string uri, int version, string text) => Fill(
        """
        {"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"#URI#","version":#VERSION#},"contentChanges":[{"text":"#TEXT#"}]}}
        """,
        ("#URI#", uri),
        ("#VERSION#", Text(version)),
        ("#TEXT#", text));

    private static string Text(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Substitutes into a plain raw-string JSON template.
    /// </summary>
    /// <remarks>
    /// An interpolated raw string cannot carry these payloads legibly: an LSP message ends in a run
    /// of closing braces long enough that the number of <c>$</c> characters needed to escape it stops
    /// being readable, and one brace out of step is a compile error somewhere else entirely. A
    /// placeholder the JSON itself can never contain keeps the literal looking like the message it is.
    /// </remarks>
    /// <param name="template">The JSON, with <c>#NAME#</c> placeholders.</param>
    /// <param name="values">What to put in them.</param>
    private static string Fill(string template, params (string Token, string Value)[] values)
    {
        foreach (var (token, value) in values)
        {
            template = template.Replace(token, value, StringComparison.Ordinal);
        }

        return template;
    }


    /// <summary>A factory that always refuses, for the "no backend" state.</summary>
    private sealed class FailingFactory : IRoslynConnectionFactory
    {
        public string Description => "a backend that cannot be started";

        public Task<RoslynConnection> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromException<RoslynConnection>(new InvalidOperationException("no server here"));
    }

    /// <summary>
    /// A session on one end of a pipe pair, with the test on the other, plus the polling helpers that
    /// let an assertion wait for something without a fixed sleep.
    /// </summary>
    private sealed class SessionHarness : IAsyncDisposable
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

        private readonly DuplexEnd _testEnd;
        private readonly DuplexEnd _sessionEnd;
        private readonly LspFrameWriter _writer;
        private readonly Task<int> _run;
        private readonly Task _reading;
        private readonly Lock _lock = new();
        private readonly List<JsonElement> _received = [];
        private readonly CancellationTokenSource _stopping = new();

        internal SessionHarness(
            IRoslynConnectionFactory? factory = null,
            string? solutionPath = "/w/Fixture/Fixture.slnx")
        {
            var (sessionEnd, testEnd) = DuplexStreamPair.Create();

            _sessionEnd = sessionEnd;
            _testEnd = testEnd;
            _writer = new LspFrameWriter(testEnd.Output);

            // The default script leaves projectInitializationComplete manual, because a test that
            // asserts "held, then answered" has to own the moment in between.
            Factory = factory as InProcessFakeRoslynFactory;

            if (factory is null)
            {
                Factory = new InProcessFakeRoslynFactory(
                    NullLogger.Instance,
                    FakeRoslynScript.Roslyn512Startup(projectLoadDelay: null));

                factory = Factory;
            }

            Session = new AdapterSession(
                sessionEnd.Input,
                sessionEnd.Output,
                factory,
                ClaudeRoslynLspOptions.FromEnvironment(static _ => null),
                () => solutionPath,
                TimeProvider.System,
                NullLogger.Instance);

            _run = Task.Run(() => Session.RunAsync(_stopping.Token));
            _reading = Task.Run(ReadLoopAsync);
        }

        internal AdapterSession Session { get; }

        /// <summary>The in-process fake's factory, when this harness is using one.</summary>
        internal InProcessFakeRoslynFactory? Factory { get; }

        internal async Task SendAsync(string message, CancellationToken cancellationToken) =>
            await _writer.WriteFrameAsync(Encoding.UTF8.GetBytes(message.Trim()), cancellationToken)
                .ConfigureAwait(false);

        internal async Task SendRawAsync(string wire, CancellationToken cancellationToken) =>
            await _testEnd.Output.WriteAsync(Encoding.UTF8.GetBytes(wire), cancellationToken).ConfigureAwait(false);

        internal void CloseClientStream() => _testEnd.CompleteOutput();

        /// <summary>Drives <c>initialize</c> and <c>initialized</c>, and waits for the answer.</summary>
        internal async Task HandshakeAsync(CancellationToken cancellationToken)
        {
            await SendAsync(Initialize(1), cancellationToken).ConfigureAwait(false);
            await AwaitResponseAsync(1, cancellationToken).ConfigureAwait(false);
            await SendAsync(Notification("initialized"), cancellationToken).ConfigureAwait(false);
        }

        internal async Task<FakeRoslynServer> AwaitBackendAsync(CancellationToken cancellationToken)
        {
            Assert.NotNull(Factory);
            await WaitUntilAsync(() => Factory.Server is not null, cancellationToken).ConfigureAwait(false);
            return Factory.Server!;
        }

        internal Task AwaitBackendMethodAsync(FakeRoslynServer backend, string method, CancellationToken cancellationToken) =>
            WaitUntilAsync(() => backend.ReceivedMethods.Contains(method, StringComparer.Ordinal), cancellationToken);

        internal Task<JsonElement> AwaitResponseAsync(int id, CancellationToken cancellationToken) =>
            AwaitFirstAsync(
                x => x.TryGetProperty("id", out var value)
                     && value.ValueKind == JsonValueKind.Number
                     && value.GetInt32() == id,
                cancellationToken);

        internal Task<JsonElement> AwaitResponseAsync(string id, CancellationToken cancellationToken) =>
            AwaitFirstAsync(
                x => x.TryGetProperty("id", out var value)
                     && value.ValueKind == JsonValueKind.String
                     && value.GetString() == id,
                cancellationToken);

        internal JsonElement? TryFindResponse(int id)
        {
            lock (_lock)
            {
                foreach (var message in _received)
                {
                    if (message.TryGetProperty("id", out var value)
                        && value.ValueKind == JsonValueKind.Number
                        && value.GetInt32() == id)
                    {
                        return message;
                    }
                }
            }

            return null;
        }

        internal async Task<JsonElement> AwaitFirstAsync(
            Func<JsonElement, bool> predicate,
            CancellationToken cancellationToken)
        {
            var found = default(JsonElement?);

            await WaitUntilAsync(
                () =>
                {
                    lock (_lock)
                    {
                        foreach (var message in _received)
                        {
                            if (predicate(message))
                            {
                                found = message;
                                return true;
                            }
                        }
                    }

                    return false;
                },
                cancellationToken).ConfigureAwait(false);

            return found!.Value;
        }

        /// <summary>
        /// Polls until a condition holds. Polling rather than signalling because the conditions are
        /// about state several components away — "the gate is holding one request", "Roslyn has been
        /// sent solution/open" — and a signal for each would be scaffolding in the product.
        /// </summary>
        internal async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + Patience;

            while (!condition())
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    Assert.Fail($"The condition did not hold within {Patience.TotalSeconds:0} s. The session wrote: {Transcript()}");
                }

                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }
        }

        internal async Task<int> RunToCompletionAsync(CancellationToken cancellationToken) =>
            await _run.WaitAsync(Patience, cancellationToken).ConfigureAwait(false);

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
            _testEnd.CompleteOutput();

            try
            {
                await _run.WaitAsync(Patience, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                // The assertion that failed is the interesting one; a session that will not stop is
                // reported by the test host's own timeout.
            }

            await Session.DisposeAsync().ConfigureAwait(false);

            // The session does not own its output stream — in the product that stream is stdout, and
            // closing it belongs to the process. Here the test owns both ends, so it is what tells
            // the reader that nothing more is coming.
            _sessionEnd.CompleteOutput();

            try
            {
                await _reading.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
            }

            _writer.Dispose();
            _stopping.Dispose();
        }

        /// <summary>Reads everything the session writes into a list the assertions search.</summary>
        private async Task ReadLoopAsync()
        {
            var reader = new LspFrameReader(_testEnd.Input);

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
                // The session stopped writing. Whatever it wrote before that is already recorded.
            }
        }

        private string Transcript()
        {
            lock (_lock)
            {
                return _received.Count == 0 ? "(nothing)" : string.Join(" | ", _received.Select(x => x.GetRawText()));
            }
        }
    }
}
