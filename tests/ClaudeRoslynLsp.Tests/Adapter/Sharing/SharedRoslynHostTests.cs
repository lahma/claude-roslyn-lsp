using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Adapter.Sharing;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Protocol;
using ClaudeRoslynLsp.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Adapter.Sharing;

/// <summary>
/// The multiplexing rules (D88), driven against a real <see cref="AdapterSession"/> hosting the
/// scripted Roslyn, with the attached clients on in-memory pipe pairs.
/// </summary>
/// <remarks>
/// <para>
/// A real session rather than a stub owner, because every rule under test is about something the
/// owner already does — the readiness gate holding a request (D46), the id map keeping two peers'
/// numbering apart (D47), the document mirror deciding what a relaunch replays (D57) — and a stub
/// would let each of those rules pass while being wired to nothing.
/// </para>
/// <para>
/// The transport is a pipe <em>pair</em> rather than a named pipe: what these tests are about is the
/// protocol between a host and an attached client, and a named pipe would add a platform's
/// permissions and naming rules to every one of them.
/// <c>SharingRoslynFactoryTests</c> is where the real pipe is exercised.
/// </para>
/// </remarks>
public class SharedRoslynHostTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// Rule 1: the host answers the handshake itself. Roslyn has one client, and a second
    /// <c>initialize</c> on its connection is a protocol error.
    /// </summary>
    [Fact]
    public async Task AnAttachedClientsInitializeIsAnsweredByTheHostAndNeverReachesRoslyn()
    {
        await using var harness = await HostHarness.StartAsync();
        await using var client = harness.Attach();

        var answered = await client.RequestAsync(1, "initialize", "{}");
        var result = answered.GetProperty("result");

        Assert.Equal(
            $"{ServerVersion.Name} shared engine (host process {Environment.ProcessId})",
            result.GetProperty("serverInfo").GetProperty("name").GetString());

        // Everything else is Roslyn's own document, verbatim: the capabilities it advertised and the
        // process that actually holds the workspace (C45).
        Assert.Equal(4242, result.GetProperty("_roslyn_processId").GetInt32());
        Assert.True(result.GetProperty("capabilities").GetProperty("definitionProvider").GetBoolean());

        // One initialize reached the backend, and it was the host's.
        Assert.Single(harness.Backend.ReceivedMethods, method => method == "initialize");
    }

    /// <summary>Rules 2 and 3: held by the host's gate, then answered under the client's own id.</summary>
    [Fact]
    public async Task ARequestIsHeldUntilTheWorkspaceLoadsAndThenAnsweredUnderTheClientsId()
    {
        await using var harness = await HostHarness.StartAsync();
        await using var client = harness.Attach();

        await client.HandshakeAsync(7);
        await client.SendAsync(Request(8, "textDocument/definition", Position()));

        await WaitUntilAsync(() => harness.Session.Gate.HeldCount >= 1);
        Assert.Null(client.TryFind(8));

        harness.Backend.CompleteProjectInitialization();

        var answered = await client.AwaitResponseAsync(8);

        // Non-empty is the proof the gate held it: the scripted backend answers navigation with an
        // empty successful result until it reports the workspace loaded, exactly as the real one does
        // (C27).
        Assert.NotEmpty(answered.GetProperty("result").EnumerateArray());
    }

    /// <summary>
    /// Rule 2 again, with two clients that both count from one: the failure this prevents is a wrong
    /// answer rather than an error.
    /// </summary>
    [Fact]
    public async Task TwoAttachedClientsUsingTheSameIdsGetTheirOwnAnswers()
    {
        await using var harness = await HostHarness.StartAsync();
        await using var first = harness.Attach();
        await using var second = harness.Attach();

        await first.HandshakeAsync(1);
        await second.HandshakeAsync(1);

        harness.Backend.CompleteProjectInitialization();
        await WaitUntilAsync(() => harness.Session.Gate.IsOpen);

        await first.SendAsync(Request(2, "textDocument/definition", Position()));
        await second.SendAsync(Request(2, "textDocument/references", Position()));

        var one = await first.AwaitResponseAsync(2);
        var two = await second.AwaitResponseAsync(2);

        Assert.Single(one.GetProperty("result").EnumerateArray());
        Assert.Equal(3, two.GetProperty("result").GetArrayLength());
    }

    /// <summary>
    /// Rule 5: a client that attached after the load is told so, or it would hold every request until
    /// its own budget ran out on a workspace that finished loading minutes ago.
    /// </summary>
    [Fact]
    public async Task AClientThatAttachesAfterTheLoadIsToldTheWorkspaceIsReady()
    {
        await using var harness = await HostHarness.StartAsync();

        harness.Backend.CompleteProjectInitialization();
        await WaitUntilAsync(() => harness.Session.Gate.IsOpen);

        await using var late = harness.Attach();
        await late.HandshakeAsync(1);

        await late.AwaitFirstAsync(message =>
            message.TryGetProperty("method", out var method)
            && method.GetString() == "workspace/projectInitializationComplete");
    }

    /// <summary>Rule 5: the notifications an attached client needs, and none of the requests.</summary>
    [Fact]
    public async Task LogsAndRefreshesAreFannedOutAndRegistrationsAreNot()
    {
        const string Uri = "file:///w/Fixture/Fixture.Core/Saved.cs";

        await using var harness = await HostHarness.StartAsync(SavingScript());
        await using var client = harness.Attach();

        await client.HandshakeAsync(1);
        harness.Backend.CompleteProjectInitialization();

        await client.AwaitFirstAsync(message =>
            message.TryGetProperty("method", out var method)
            && method.GetString() == "workspace/projectInitializationComplete");

        await client.SendAsync(Notification("textDocument/didSave", TextDocument(Uri)));

        // The log lines cross, because they are what a session that went quiet is diagnosed from.
        await client.AwaitFirstAsync(message =>
            message.TryGetProperty("method", out var method)
            && method.GetString() == "window/logMessage");

        var refresh = await client.AwaitFirstAsync(message =>
            message.TryGetProperty("method", out var method)
            && method.GetString() == "workspace/diagnostic/refresh");

        // Roslyn sent it as a request and the host owes it the answer. What crosses is a
        // notification, so the attached client consumes it without answering under an id its own
        // connection never issued.
        Assert.False(refresh.TryGetProperty("id", out _));

        // The host answers the registrations, the configuration pulls and the progress-create itself
        // (rule 4). An attached client that saw them would be a second answerer for a question that
        // has one correct answer.
        Assert.DoesNotContain(client.Methods, method => method == "client/registerCapability");
        Assert.DoesNotContain(client.Methods, method => method == "workspace/configuration");
        Assert.DoesNotContain(client.Methods, method => method == "window/workDoneProgress/create");
        Assert.DoesNotContain(client.Methods, method => method == "$/progress");
    }

    /// <summary>
    /// Rule 6: Roslyn keeps one buffer per document, so a second <c>didOpen</c> is redundant and a
    /// <c>didClose</c> from one client would revert the other's live buffer to what is on disk.
    /// </summary>
    [Fact]
    public async Task ADocumentTwoClientsOpenIsOpenedOnceAndClosedOnce()
    {
        const string Uri = "file:///w/Fixture/Fixture.Core/Shared.cs";

        await using var harness = await HostHarness.StartAsync();
        await using var first = harness.Attach();
        await using var second = harness.Attach();

        await first.HandshakeAsync(1);
        await second.HandshakeAsync(1);

        harness.Backend.CompleteProjectInitialization();
        await WaitUntilAsync(() => harness.Session.Gate.IsOpen);

        await first.SendAsync(DidOpen(Uri, "class Shared;"));
        await WaitUntilAsync(() => harness.OpensOf(Uri) == 1);

        await second.SendAsync(DidOpen(Uri, "class Shared;"));

        // Fenced on the second client's own connection before the first one closes. Two attached
        // clients are two independent streams and nothing orders one against the other: a close that
        // overtook the second open would be correct for the messages the host had actually seen, and
        // the test would be asserting an interleaving rather than a rule. It failed exactly that way
        // on Linux and never on Windows, which is C56's shape again.
        await second.SendAsync(Request(20, "textDocument/definition", Position()));
        await second.AwaitResponseAsync(20);

        await first.SendAsync(Notification("textDocument/didClose", TextDocument(Uri)));

        // Nothing new should reach Roslyn: the second open is redundant and the first close is not
        // the last one. Proven by a round trip that has to arrive after both of them.
        await first.SendAsync(Request(9, "textDocument/definition", Position()));
        await first.AwaitResponseAsync(9);

        Assert.Equal(1, harness.OpensOf(Uri));
        Assert.Equal(0, harness.ClosesOf(Uri));
        Assert.NotNull(harness.Session.Documents.Find(Uri));

        await second.SendAsync(Notification("textDocument/didClose", TextDocument(Uri)));
        await WaitUntilAsync(() => harness.ClosesOf(Uri) == 1);

        // And the mirror lets go with it, so a relaunch does not replay a document nobody has open.
        await WaitUntilAsync(() => harness.Session.Documents.Find(Uri) is null);
    }

    /// <summary>Rule 6: a client that goes away hands back what only it was holding.</summary>
    [Fact]
    public async Task ADetachingClientsDocumentsAreGivenBack()
    {
        const string Uri = "file:///w/Fixture/Fixture.Core/Orphan.cs";

        await using var harness = await HostHarness.StartAsync();
        var client = harness.Attach();

        await client.HandshakeAsync(1);
        harness.Backend.CompleteProjectInitialization();
        await WaitUntilAsync(() => harness.Session.Gate.IsOpen);

        await client.SendAsync(DidOpen(Uri, "class Orphan;"));
        await WaitUntilAsync(() => harness.OpensOf(Uri) == 1);

        await client.DisposeAsync();

        await WaitUntilAsync(() => harness.ClosesOf(Uri) == 1);
        await WaitUntilAsync(() => harness.Host.ClientCount == 0);
    }

    /// <summary>
    /// Rule 8: an attached client's <c>shutdown</c> is about that client. The Roslyn stays up, and so
    /// does everybody else attached to it.
    /// </summary>
    [Fact]
    public async Task AnAttachedShutdownDetachesOnlyThatClient()
    {
        await using var harness = await HostHarness.StartAsync();
        await using var leaving = harness.Attach();
        await using var staying = harness.Attach();

        await leaving.HandshakeAsync(1);
        await staying.HandshakeAsync(1);

        harness.Backend.CompleteProjectInitialization();
        await WaitUntilAsync(() => harness.Session.Gate.IsOpen);

        await leaving.SendAsync(Request(50, "shutdown", null));
        await leaving.AwaitResponseAsync(50);
        await leaving.SendAsync(Notification("exit", null));

        await WaitUntilAsync(() => harness.Host.ClientCount == 1);

        await staying.SendAsync(Request(51, "textDocument/definition", Position()));
        var answered = await staying.AwaitResponseAsync(51);

        Assert.Single(answered.GetProperty("result").EnumerateArray());
        Assert.False(harness.Backend.ShutdownRequested);
    }

    /// <summary>
    /// Rule 7: the workspace is already open, and opening it a second time starts a second load
    /// underneath the first.
    /// </summary>
    [Fact]
    public async Task ASolutionOpenFromAnAttachedClientIsSwallowed()
    {
        await using var harness = await HostHarness.StartAsync();
        await using var client = harness.Attach();

        await client.HandshakeAsync(1);
        await client.SendAsync(Notification(
            "solution/open",
            """{"solution":"file:///w/Fixture/Fixture.slnx"}"""));

        harness.Backend.CompleteProjectInitialization();
        await client.AwaitFirstAsync(message =>
            message.TryGetProperty("method", out var method)
            && method.GetString() == "workspace/projectInitializationComplete");

        // Exactly one, and it was the host's.
        Assert.Single(harness.Backend.ReceivedMethods, method => method == "solution/open");
    }

    /// <summary>
    /// D90: the attached engine's own responder is never asked anything, so an override has to be
    /// applied in the process Roslyn actually asks.
    /// </summary>
    [Fact]
    public async Task SetOptionIsAppliedInTheHostsOwnConfigurationResponder()
    {
        const string Section = "csharp|background_analysis.dotnet_compiler_diagnostics_scope";

        await using var harness = await HostHarness.StartAsync(RepullingScript(Section));
        await using var client = harness.Attach();

        await client.HandshakeAsync(1);

        await client.SendAsync(Request(
            60,
            SharedRoslynHost.SetOptionMethod,
            $$"""{"section":"{{Section}}","value":"fullSolution"}"""));

        await client.AwaitResponseAsync(60);

        // The proof is on the wire on the *host's* connection: Roslyn asked for the section again and
        // was answered with the override rather than with the standing openFiles default (D48).
        Assert.Contains(
            harness.Backend.ReceivedMessages,
            message => message.Contains("fullSolution", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASetOptionWithoutASectionIsRefusedRatherThanApplied()
    {
        await using var harness = await HostHarness.StartAsync();
        await using var client = harness.Attach();

        await client.HandshakeAsync(1);
        await client.SendAsync(Request(61, SharedRoslynHost.SetOptionMethod, """{"value":"fullSolution"}"""));

        var answered = await client.AwaitResponseAsync(61);

        Assert.Equal(
            JsonRpcErrors.InvalidParams,
            answered.GetProperty("error").GetProperty("code").GetInt32());
    }

    /// <summary>
    /// D91: the host going away is the state an attached client's supervisor already knows what to do
    /// with, so it has to look like a backend that went away rather than like nothing at all.
    /// </summary>
    [Fact]
    public async Task StoppingTheHostClosesEveryAttachedConnection()
    {
        await using var harness = await HostHarness.StartAsync();
        await using var client = harness.Attach();

        await client.HandshakeAsync(1);

        await harness.Host.DisposeAsync();

        await client.WaitForEndOfStreamAsync();
    }

    /// <summary>Rule 4, from the other direction: a client cannot answer a question it was not asked.</summary>
    [Fact]
    public async Task AResponseFromAnAttachedClientIsDroppedRatherThanForwarded()
    {
        await using var harness = await HostHarness.StartAsync();
        await using var client = harness.Attach();

        await client.HandshakeAsync(1);
        await client.SendAsync("""{"jsonrpc":"2.0","id":1,"result":{"applied":true}}""");

        harness.Backend.CompleteProjectInitialization();
        await WaitUntilAsync(() => harness.Session.Gate.IsOpen);

        await client.SendAsync(Request(70, "textDocument/definition", Position()));
        await client.AwaitResponseAsync(70);

        // The backend saw the round trip that followed and nothing that looked like an answer to a
        // question it never asked.
        Assert.DoesNotContain(
            harness.Backend.ReceivedMessages,
            message => message.Contains("\"applied\":true", StringComparison.Ordinal));
    }

    /// <summary>
    /// A script whose Roslyn narrates a save: one log line and one diagnostics refresh.
    /// </summary>
    /// <remarks>
    /// The refresh is deliberately a <em>request</em>, which is what the real server sends
    /// (C9's pull model needs an acknowledgement), so the conversion rule has something to convert.
    /// </remarks>
    private static FakeRoslynScript SavingScript()
    {
        var script = FakeRoslynScript.Roslyn512Startup(projectLoadDelay: null);

        script.Steps.Add(new FakeRoslynStep(
            "textDocument/didSave",
            TimeSpan.Zero,
            Encoding.UTF8.GetBytes(
                """
                {"jsonrpc":"2.0","method":"window/logMessage","params":{"type":3,"message":"[save] noted"}}
                """)));

        script.Steps.Add(new FakeRoslynStep(
            "textDocument/didSave",
            TimeSpan.Zero,
            Encoding.UTF8.GetBytes(
                """
                {"jsonrpc":"2.0","id":950,"method":"workspace/diagnostic/refresh"}
                """)));

        return script;
    }

    /// <summary>A script whose Roslyn re-pulls one section when it is told the configuration changed.</summary>
    /// <remarks>
    /// The real server does exactly this (C46, D77) and the standard startup script does not, because
    /// no earlier test needed it. Without the re-pull the round trip would fall back to its two-second
    /// ceiling and the test would pass slowly for the wrong reason.
    /// </remarks>
    private static FakeRoslynScript RepullingScript(string section)
    {
        var script = FakeRoslynScript.Roslyn512Startup(projectLoadDelay: null);

        script.Steps.Add(new FakeRoslynStep(
            "workspace/didChangeConfiguration",
            TimeSpan.Zero,
            Encoding.UTF8.GetBytes(
                $$$"""
                {"jsonrpc":"2.0","id":900,"method":"workspace/configuration","params":{"items":[{"section":"{{{section}}}"}]}}
                """)));

        return script;
    }

    /// <summary>
    /// Polls until a condition holds, because the conditions are about state several components away
    /// — "the gate is holding one request", "the backend has seen one didClose" — and a signal for
    /// each would be scaffolding in the product.
    /// </summary>
    /// <param name="condition">What has to become true.</param>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;

        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                Assert.Fail($"The condition did not hold within {Patience.TotalSeconds:0} s.");
            }

            await Task.Delay(5, Cancellation).ConfigureAwait(false);
        }
    }

    private static string Request(int id, string method, string? parameters) =>
        parameters is null
            ? $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"}"""
            : $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""";

    private static string Notification(string method, string? parameters) =>
        parameters is null
            ? $$"""{"jsonrpc":"2.0","method":"{{method}}"}"""
            : $$"""{"jsonrpc":"2.0","method":"{{method}}","params":{{parameters}}}""";

    private static string Position() =>
        """{"textDocument":{"uri":"file:///w/Fixture/Fixture.Core/Calculator.cs"},"position":{"line":7,"character":18}}""";

    private static string TextDocument(string uri) => $$$"""{"textDocument":{"uri":"{{{uri}}}"}}""";

    private static string DidOpen(string uri, string text) => Notification(
        "textDocument/didOpen",
        $$$"""{"textDocument":{"uri":"{{{uri}}}","languageId":"csharp","version":1,"text":"{{{text}}}"}}""");

    /// <summary>
    /// One <see cref="AdapterSession"/> hosting the scripted backend, with a
    /// <see cref="SharedRoslynHost"/> over it and a way to attach clients to it.
    /// </summary>
    private sealed class HostHarness : IAsyncDisposable
    {
        private readonly List<AttachedPeer> _attached = [];
        private readonly CancellationTokenSource _stopping = new();
        private readonly DuplexEnd _sessionEnd;
        private readonly DuplexEnd _clientEnd;
        private readonly LspFrameWriter _writer;
        private readonly Task<int> _run;
        private readonly Task _draining;

        private HostHarness(FakeRoslynScript script)
        {
            var (sessionEnd, clientEnd) = DuplexStreamPair.Create();

            _sessionEnd = sessionEnd;
            _clientEnd = clientEnd;
            _writer = new LspFrameWriter(clientEnd.Output);

            Factory = new InProcessFakeRoslynFactory(NullLogger.Instance, script);

            Session = new AdapterSession(
                sessionEnd.Input,
                sessionEnd.Output,
                Factory,
                ClaudeRoslynLspOptions.FromEnvironment(static _ => null),
                () => "/w/Fixture/Fixture.slnx",
                TimeProvider.System,
                NullLogger.Instance);

            Host = new SharedRoslynHost(Session, "test-pipe", NullLogger.Instance);

            _run = Task.Run(() => Session.RunAsync(_stopping.Token), CancellationToken.None);
            _draining = Task.Run(DrainAsync, CancellationToken.None);
        }

        internal AdapterSession Session { get; }

        internal SharedRoslynHost Host { get; }

        internal InProcessFakeRoslynFactory Factory { get; }

        internal FakeRoslynServer Backend => Factory.Server!;

        internal static async Task<HostHarness> StartAsync(FakeRoslynScript? script = null)
        {
            var harness = new HostHarness(script ?? FakeRoslynScript.Roslyn512Startup(projectLoadDelay: null));

            await harness.SendAsync(
                """
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":4242,"rootUri":"file:///w/Fixture","capabilities":{}}}
                """);

            await harness.SendAsync("""{"jsonrpc":"2.0","method":"initialized","params":{}}""");

            // The host cannot answer an attached initialize until it has one of its own to answer it
            // from, so every test starts from there.
            await WaitUntilAsync(() => harness.Factory.Server is not null);
            await WaitUntilAsync(() =>
                harness.Backend.ReceivedMethods.Contains("solution/open", StringComparer.Ordinal));

            return harness;
        }

        internal AttachedPeer Attach()
        {
            var (hostEnd, peerEnd) = DuplexStreamPair.Create();
            var peer = new AttachedPeer(peerEnd);

            Host.Accept(hostEnd.Input, hostEnd.Output, () =>
            {
                hostEnd.CompleteOutput();
                return ValueTask.CompletedTask;
            });

            lock (_attached)
            {
                _attached.Add(peer);
            }

            return peer;
        }

        /// <summary>How many <c>didOpen</c> notifications for one URI reached the backend.</summary>
        internal int OpensOf(string uri) => CountOf("textDocument/didOpen", uri);

        /// <summary>How many <c>didClose</c> notifications for one URI reached the backend.</summary>
        internal int ClosesOf(string uri) => CountOf("textDocument/didClose", uri);

        public async ValueTask DisposeAsync()
        {
            AttachedPeer[] peers;

            lock (_attached)
            {
                peers = [.. _attached];
                _attached.Clear();
            }

            foreach (var peer in peers)
            {
                await peer.DisposeAsync().ConfigureAwait(false);
            }

            await Host.DisposeAsync().ConfigureAwait(false);
            await _stopping.CancelAsync().ConfigureAwait(false);
            _clientEnd.CompleteOutput();

            try
            {
                await _run.WaitAsync(Patience, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
            }

            await Session.DisposeAsync().ConfigureAwait(false);
            _sessionEnd.CompleteOutput();

            try
            {
                await _draining.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
            }

            _writer.Dispose();
            _stopping.Dispose();
        }

        private int CountOf(string method, string uri)
        {
            var count = 0;

            foreach (var message in Backend.ReceivedMessages)
            {
                if (message.Contains($"\"method\":\"{method}\"", StringComparison.Ordinal)
                    && message.Contains(uri, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private Task SendAsync(string message) =>
            _writer.WriteFrameAsync(Encoding.UTF8.GetBytes(message.Trim()), Cancellation).AsTask();

        /// <summary>Reads what the session writes to its own client, so the pipe cannot fill.</summary>
        private async Task DrainAsync()
        {
            var reader = new LspFrameReader(_clientEnd.Input);

            try
            {
                while (await reader.ReadFrameAsync(CancellationToken.None).ConfigureAwait(false) is not null)
                {
                }
            }
            catch (Exception exception) when (exception is LspProtocolException or IOException
                                                  or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }

    /// <summary>One attached client, written and read from the test's side.</summary>
    private sealed class AttachedPeer : IAsyncDisposable
    {
        private readonly Lock _lock = new();
        private readonly List<JsonElement> _received = [];
        private readonly DuplexEnd _end;
        private readonly LspFrameWriter _writer;
        private readonly Task _reading;
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal AttachedPeer(DuplexEnd end)
        {
            _end = end;
            _writer = new LspFrameWriter(end.Output);
            _reading = Task.Run(ReadLoopAsync, CancellationToken.None);
        }

        /// <summary>Every method the host has sent this client.</summary>
        internal IReadOnlyList<string> Methods
        {
            get
            {
                lock (_lock)
                {
                    return
                    [
                        .. _received
                            .Where(static message => message.TryGetProperty("method", out _))
                            .Select(static message => message.GetProperty("method").GetString() ?? string.Empty),
                    ];
                }
            }
        }

        internal Task SendAsync(string message) =>
            _writer.WriteFrameAsync(Encoding.UTF8.GetBytes(message.Trim()), Cancellation).AsTask();

        /// <summary>Sends a request and waits for its answer.</summary>
        internal async Task<JsonElement> RequestAsync(int id, string method, string parameters)
        {
            await SendAsync(Request(id, method, parameters)).ConfigureAwait(false);
            return await AwaitResponseAsync(id).ConfigureAwait(false);
        }

        /// <summary>Does what an attached session does: initialize, then initialized.</summary>
        internal async Task HandshakeAsync(int id)
        {
            await RequestAsync(id, "initialize", "{}").ConfigureAwait(false);
            await SendAsync(Notification("initialized", "{}")).ConfigureAwait(false);
        }

        internal JsonElement? TryFind(int id)
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

        internal Task<JsonElement> AwaitResponseAsync(int id) =>
            AwaitFirstAsync(message =>
                message.TryGetProperty("id", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.GetInt32() == id);

        internal async Task<JsonElement> AwaitFirstAsync(Func<JsonElement, bool> predicate)
        {
            var deadline = DateTimeOffset.UtcNow + Patience;

            while (true)
            {
                lock (_lock)
                {
                    foreach (var message in _received)
                    {
                        if (predicate(message))
                        {
                            return message;
                        }
                    }
                }

                if (DateTimeOffset.UtcNow > deadline)
                {
                    Assert.Fail($"The attached client saw nothing matching within {Patience.TotalSeconds:0} s.");
                }

                await Task.Delay(5, Cancellation).ConfigureAwait(false);
            }
        }

        /// <summary>Waits for the host to close this connection, which is D91's backend-gone signal.</summary>
        internal Task WaitForEndOfStreamAsync() => _closed.Task.WaitAsync(Patience, Cancellation);

        public async ValueTask DisposeAsync()
        {
            _end.CompleteOutput();

            try
            {
                await _reading.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
            }

            _writer.Dispose();
        }

        private async Task ReadLoopAsync()
        {
            var reader = new LspFrameReader(_end.Input);

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
            catch (Exception exception) when (exception is LspProtocolException or IOException
                                                  or ObjectDisposedException or OperationCanceledException)
            {
            }
            finally
            {
                _closed.TrySetResult();
            }
        }
    }
}
