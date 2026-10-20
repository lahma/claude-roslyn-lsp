using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Adapter.Sharing;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Protocol;
using ClaudeRoslynLsp.Testing;
using ClaudeRoslynLsp.Tests.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Adapter.Sharing;

/// <summary>
/// The idle recycle in a shared engine: the host decides, keeps its pipe and its session file while
/// dormant, and is woken by whoever asks first (D95, D96, D102, D105).
/// </summary>
/// <remarks>
/// Over a real <see cref="SharingRoslynFactory"/>, a real named pipe and a real session directory,
/// for <c>SharingRoslynFactoryTests</c>' reason: the properties under test — the file survives a
/// recycle and is withdrawn by a dormant host's shutdown, the pipe stays open across one — are exactly
/// the kind a fake would get right and the platform would get differently wrong. The host's clock is
/// the hand-moved one; the attached side runs on the real clock, because it never decides anything.
/// </remarks>
public class SharedRecycleTests
{
    private const long Megabyte = 1024 * 1024;
    private const string ProgramUri = "file:///w/Hello.App/Program.cs";

    /// <summary>One CS0029, so a pull has something to publish.</summary>
    private const string Report = """
        {"kind":"full","resultId":"r1","items":[
          {"range":{"start":{"line":21,"character":16},"end":{"line":21,"character":19}},"severity":1,
           "code":"CS0029","message":"Cannot implicitly convert type 'string' to 'int'"}]}
        """;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// A recycle with a client attached: the backend goes, the session file stays and says
    /// <c>dormant</c>, the pipe stays open, and the attached client's next request brings the backend
    /// back and is answered.
    /// </summary>
    [Fact]
    public async Task ARecycleKeepsThePipeAndTheSessionFileAndAnAttachedRequestWakesTheHost()
    {
        await using var host = await HostedSession.StartAsync();
        await using var peer = await host.AttachAsync();

        await peer.HandshakeAsync(1);
        await peer.RequestAsync(2, "textDocument/definition", Position());

        host.Memory = 1500 * Megabyte;
        await host.AdvanceUntilDormantAsync();

        var first = host.Inner.Latest!;

        Assert.Contains("shutdown", first.ReceivedMethods);
        Assert.Equal(1, host.Inner.ConnectCount);
        Assert.NotNull(host.Factory.Host);
        Assert.False(peer.Ended);

        var published = host.Registry.TryRead(host.Key);

        Assert.NotNull(published);
        Assert.Equal("dormant", published.State);
        Assert.Equal(1, published.Recycles);
        Assert.Equal(1, published.AttachedClients);
        Assert.Null(published.RoslynProcessId);

        // The attached client was told (D105).
        await peer.AwaitFirstAsync(message => IsBackendChanged(message, "dormant"));

        await peer.SendAsync(Request(3, "textDocument/definition", Position()));
        await WaitUntilAsync(() => host.Inner.ConnectCount == 2);
        await WaitUntilAsync(() => host.Inner.Latest!.ReceivedMethods.Contains("solution/open"));

        host.Inner.Latest!.CompleteProjectInitialization();

        var answered = await peer.AwaitResponseAsync(3);
        Assert.NotEmpty(answered.GetProperty("result").EnumerateArray());

        await WaitUntilAsync(() => host.Registry.TryRead(host.Key)?.State == "running");
    }

    /// <summary>
    /// A process attaching to a dormant host waits on the host's handshake, so its
    /// <c>initialize</c> has to be what wakes it — or it would wait for ever.
    /// </summary>
    [Fact]
    public async Task AnAttachedInitializeWakesADormantHost()
    {
        await using var host = await HostedSession.StartAsync();

        host.Memory = 1500 * Megabyte;
        await host.AdvanceUntilDormantAsync();
        Assert.Equal(ReadinessState.Dormant, host.Session.Session.Gate.State);

        await using var late = await host.AttachAsync();
        await late.SendAsync(Request(1, "initialize", "{}"));

        await WaitUntilAsync(() => host.Inner.ConnectCount == 2);

        var answered = await late.AwaitResponseAsync(1);

        Assert.Contains(
            "shared engine",
            answered.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString()!,
            StringComparison.Ordinal);

        // And the late attacher learns how many times the backend has been replaced.
        await late.AwaitFirstAsync(message =>
            IsBackendChanged(message, state: null)
            && message.GetProperty("params").GetProperty("recycles").GetInt32() == 1);
    }

    /// <summary>
    /// D95: an attached session's diagnostic pulls are the host's own refreshes coming back, so they
    /// do not keep the shared Roslyn awake.
    /// </summary>
    [Fact]
    public async Task AnAttachedDiagnosticPullDoesNotCountAsActivity()
    {
        await using var host = await HostedSession.StartAsync();
        await using var peer = await host.AttachAsync();

        await peer.HandshakeAsync(1);

        host.Memory = 1500 * Megabyte;
        await host.AdvanceMinutesAsync(10);

        await peer.RequestAsync(
            2,
            "textDocument/diagnostic",
            """{"textDocument":{"uri":"file:///w/Fixture/Fixture.Core/Calculator.cs"}}""");

        // Had the pull counted, the window would run from it and nothing would happen before minute
        // 30. It is recycled on the original schedule instead — minute 21, give or take a deferred look.
        await host.AdvanceUntilDormantAsync(maxMinutes: 14);

        Assert.Contains("shutdown", host.Inner.Latest!.ReceivedMethods);
    }

    /// <summary>
    /// D98: while dormant the retired connection is the only one there is, and the session's own
    /// shutdown disposing it is what withdraws the file — or a dead host would be left advertising a
    /// pipe nobody serves.
    /// </summary>
    [Fact]
    public async Task StoppingADormantHostWithdrawsItsSessionFile()
    {
        var host = await HostedSession.StartAsync();

        try
        {
            host.Memory = 1500 * Megabyte;
            await host.AdvanceUntilDormantAsync();

            Assert.Equal(ReadinessState.Dormant, host.Session.Session.Gate.State);
            Assert.NotNull(host.Registry.TryRead(host.Key));

            await host.Session.DisposeAsync();

            Assert.False(File.Exists(host.Registry.FileFor(host.Key)));
            Assert.Null(host.Factory.Host);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>
    /// D105: an attached engine's <c>getWorkspaceStatus</c> names the Roslyn that is actually running
    /// after a recycle, not the one it met at its own handshake, and counts the recycle.
    /// </summary>
    [Fact]
    public async Task AnAttachedEngineReportsTheHostsNewBackend()
    {
        var connections = 0;

        await using var host = await HostedSession.StartAsync(
            () => WithProcessId(4241 + Interlocked.Increment(ref connections)));

        using var root = TempWorkspace.Create("shared-recycle-engine");

        var attaching = new SharingRoslynFactory(
            new InProcessFakeRoslynFactory(NullLogger.Instance),
            host.Registry,
            () => host.Solution,
            TimeProvider.System,
            NullLogger.Instance);

        await using var engine = new OwnedRoslynEngine(
            attaching,
            ClaudeRoslynLspOptions.FromEnvironment(static _ => null),
            () => WorkspaceSelection.FromPath(host.Solution),
            root.Root,
            TimeProvider.System,
            NullLogger.Instance);

        attaching.Own(engine);

        var before = await engine.EnsureReadyAsync(Patience, Cancellation);

        Assert.Equal(OwnedRoslynEngine.AttachedEngine, before.Engine);
        Assert.Equal(4242, before.ProcessId);
        Assert.Equal(0, before.Recycles);

        host.Memory = 1500 * Megabyte;
        await host.AdvanceUntilDormantAsync();
        Assert.Equal(ReadinessState.Dormant, host.Session.Session.Gate.State);

        // A tool's question wakes the host through the pipe.
        var asked = engine.WorkspaceSymbolAsync("Circle", Cancellation);

        await WaitUntilAsync(() => host.Inner.ConnectCount == 2);
        await WaitUntilAsync(() => host.Inner.Latest!.ReceivedMethods.Contains("solution/open"));
        host.Inner.Latest!.CompleteProjectInitialization();

        Assert.NotEmpty(await asked.WaitAsync(Patience, Cancellation));

        // The announcement of the new backend follows the reload on the pipe; poll the status for it.
        var deadline = DateTimeOffset.UtcNow + Patience;

        while (true)
        {
            var after = await engine.EnsureReadyAsync(Patience, Cancellation);

            if (after.ProcessId == 4243 && after.Recycles == 1)
            {
                break;
            }

            Assert.True(
                DateTimeOffset.UtcNow < deadline,
                $"The attached engine still reports process {after.ProcessId} and {after.Recycles} recycle(s).");

            await Task.Delay(5, Cancellation);
        }
    }

    /// <summary>
    /// A host whose session file another process has taken over keeps serving its own clients but
    /// never writes the file again, and never deletes it when it stops (finding 3 of the 0.2.0
    /// review, D102).
    /// </summary>
    [Fact]
    public async Task AHostWhoseFileWasTakenOverNeverWritesOrDeletesItAgain()
    {
        var host = await HostedSession.StartAsync();

        try
        {
            var path = host.Registry.FileFor(host.Key);
            var ours = host.Registry.TryRead(host.Key)!;

            // Another process found the file gone — a failed attach deletes it — and published itself.
            host.Registry.Delete(host.Key);
            host.Registry.Write(host.Key, ours with { PipeName = "claude-roslyn-lsp-share-newcomer", State = "starting" });

            var newcomer = File.ReadAllBytes(path);

            // A recycle is several state changes, each of which used to be a rewrite.
            host.Memory = 1500 * Megabyte;
            await host.AdvanceUntilDormantAsync();
            await host.AdvanceMinutesAsync(2);

            Assert.Equal(newcomer, File.ReadAllBytes(path));

            await host.Session.DisposeAsync();
            await host.Factory.StopHostingAsync();

            Assert.Equal(newcomer, File.ReadAllBytes(path));
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>
    /// The cached workspace report is about the Roslyn that answered it; when the host recycles that
    /// Roslyn, an attached engine forgets it, or a later solution pull that outlasts its budget would
    /// fall back to a report about a process that has gone (finding 4, C58, D105).
    /// </summary>
    [Fact]
    public async Task AnAttachedEngineForgetsItsWorkspaceReportWhenTheHostRecycles()
    {
        await using var host = await HostedSession.StartAsync(WithWorkspaceReport);
        using var root = TempWorkspace.Create("shared-recycle-report");
        await using var engine = AttachEngine(host, root);

        await engine.EnsureReadyAsync(Patience, Cancellation);
        await engine.WorkspaceDiagnosticAsync([], Cancellation);
        Assert.True(engine.HasCachedWorkspaceReport);

        host.Memory = 1500 * Megabyte;
        await host.AdvanceUntilDormantAsync();

        await WaitUntilAsync(() => !engine.HasCachedWorkspaceReport);
    }

    /// <summary>
    /// An attached engine's tool call is the host's activity even when all it sends is a diagnostic
    /// pull, which the host does not count — it says so with a notification of its own, which Roslyn
    /// never sees (finding 4, D95).
    /// </summary>
    [Fact]
    public async Task AnAttachedEnginesToolCallIsTheHostsActivity()
    {
        await using var host = await HostedSession.StartAsync();
        using var root = TempWorkspace.Create("shared-recycle-activity");
        await using var engine = AttachEngine(host, root);

        await engine.EnsureReadyAsync(Patience, Cancellation);

        host.Memory = 1500 * Megabyte;
        await host.AdvanceMinutesAsync(10);

        await engine.EnsureReadyAsync(Patience, Cancellation);

        var now = host.Time.GetUtcNow();
        await WaitUntilAsync(() => host.Session.Session.Recycler.Status.LastActivityAt == now);

        Assert.DoesNotContain(SharedRoslynHost.ActivityMethod, host.Inner.Latest!.ReceivedMethods);

        // So the window runs from minute 10: at minute 25 the backend is still there.
        await host.AdvanceMinutesAsync(15);

        Assert.Equal(ReadinessState.ProjectsLoaded, host.Session.Session.Gate.State);
        Assert.DoesNotContain("shutdown", host.Inner.Latest!.ReceivedMethods);
    }

    /// <summary>
    /// An attached <c>lsp</c> session's own gate never closes while the host reloads, so the host's
    /// announcement of a new Roslyn is the only thing that can tell it to pull its open documents
    /// again — or their diagnostics would stay the recycled process's until the next edit
    /// (finding 9, D105).
    /// </summary>
    [Fact]
    public async Task AnAttachedSessionPullsItsOpenDocumentsAgainAfterTheHostReloads()
    {
        var connections = 0;

        await using var host = await HostedSession.StartAsync(
            () => WithProcessId(
                4241 + Interlocked.Increment(ref connections),
                RestartableFakeRoslynFactory.ScriptWithDiagnostics(Report)));

        var attaching = new SharingRoslynFactory(
            new InProcessFakeRoslynFactory(NullLogger.Instance),
            host.Registry,
            () => host.Solution,
            TimeProvider.System,
            NullLogger.Instance);

        await using var attached = new SessionHarness(attaching, solution: host.Solution);
        attaching.Own(attached.Session);

        await attached.HandshakeAsync(Cancellation);
        await WaitUntilAsync(() => attached.Session.Gate.IsOpen);

        await attached.SendAsync(DidOpen(ProgramUri), Cancellation);
        await attached.AwaitPublishAsync(ProgramUri, Cancellation);

        var before = Publishes(attached, ProgramUri);

        host.Memory = 1500 * Megabyte;
        await host.AdvanceUntilDormantAsync();

        // Woken by the attached session's own question, and reloaded.
        await attached.SendAsync(Request(30, "textDocument/hover", Position()), Cancellation);
        await WaitUntilAsync(() => host.Inner.ConnectCount == 2);
        await WaitUntilAsync(() => host.Inner.Latest!.ReceivedMethods.Contains("solution/open"));

        host.Inner.Latest!.CompleteProjectInitialization();
        await attached.AwaitResponseAsync(30, Cancellation);

        await WaitUntilAsync(() => Publishes(attached, ProgramUri) > before);
    }

    /// <summary>An engine attached to the host over the real pipe.</summary>
    private static OwnedRoslynEngine AttachEngine(HostedSession host, TempWorkspace root)
    {
        var attaching = new SharingRoslynFactory(
            new InProcessFakeRoslynFactory(NullLogger.Instance),
            host.Registry,
            () => host.Solution,
            TimeProvider.System,
            NullLogger.Instance);

        var engine = new OwnedRoslynEngine(
            attaching,
            ClaudeRoslynLspOptions.FromEnvironment(static _ => null),
            () => WorkspaceSelection.FromPath(host.Solution),
            root.Root,
            TimeProvider.System,
            NullLogger.Instance);

        attaching.Own(engine);

        return engine;
    }

    /// <summary>How many diagnostic sets one client was given for one document.</summary>
    private static int Publishes(SessionHarness harness, string uri) =>
        harness.Notifications("textDocument/publishDiagnostics")
            .Count(x => x.GetProperty("params").GetProperty("uri").GetString() == uri);

    /// <summary>The standard startup with a <c>workspace/diagnostic</c> responder, so a report can be cached.</summary>
    private static FakeRoslynScript WithWorkspaceReport()
    {
        var script = FakeRoslynScript.Roslyn512Startup(projectLoadDelay: null);

        return new FakeRoslynScript
        {
            InitializeResult = script.InitializeResult,
            Steps = script.Steps,
            ProjectLoadDelay = script.ProjectLoadDelay,
            Responders = new Dictionary<string, FakeRoslynResponder>(script.Responders, StringComparer.Ordinal)
            {
                ["workspace/diagnostic"] = (_, _) => """{"items":[]}"""u8.ToArray(),
            },
        };
    }

    private static string DidOpen(string uri) => $$$"""
        {"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":
         {"uri":"{{{uri}}}","languageId":"csharp","version":1,"text":"class Program { }"}
        }}
        """;

    private static bool IsBackendChanged(JsonElement message, string? state) =>
        message.TryGetProperty("method", out var method)
        && method.GetString() == BackendChange.Method
        && (state is null || message.GetProperty("params").GetProperty("state").GetString() == state);

    /// <summary>A startup script with a given <c>_roslyn_processId</c> in its handshake (C45).</summary>
    /// <param name="processId">The id the handshake announces.</param>
    /// <param name="from">The script to change; the standard startup when null.</param>
    private static FakeRoslynScript WithProcessId(int processId, FakeRoslynScript? from = null)
    {
        var script = from ?? FakeRoslynScript.Roslyn512Startup(projectLoadDelay: null);
        var initialize = Encoding.UTF8.GetString(script.InitializeResult.Span)
            .Replace("\"_roslyn_processId\":4242", $"\"_roslyn_processId\":{processId}", StringComparison.Ordinal);

        return new FakeRoslynScript
        {
            InitializeResult = Encoding.UTF8.GetBytes(initialize),
            Steps = script.Steps,
            ProjectLoadDelay = script.ProjectLoadDelay,
            Responders = script.Responders,
        };
    }

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

    private static string Request(int id, string method, string parameters) =>
        $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""";

    private static string Position() =>
        """{"textDocument":{"uri":"file:///w/Fixture/Fixture.Core/Calculator.cs"},"position":{"line":7,"character":18}}""";

    /// <summary>
    /// One <c>lsp</c> session hosting through a real <see cref="SharingRoslynFactory"/>, on the
    /// hand-moved clock, loaded and measured at a 400 MB baseline.
    /// </summary>
    private sealed class HostedSession : IAsyncDisposable
    {
        private readonly TempWorkspace _home;
        private readonly List<IAsyncDisposable> _attached = [];
        private long _memory = 400 * Megabyte;

        private HostedSession(Func<FakeRoslynScript>? script)
        {
            _home = TempWorkspace.Create("shared-recycle");

            Registry = SessionRegistry.ForHome(_home.Root, NullLogger.Instance);
            Solution = _home.Path_("Fixture.slnx");
            Key = SessionRegistry.KeyFor(Solution);
            Inner = new RestartableFakeRoslynFactory(script);
            Factory = new SharingRoslynFactory(Inner, Registry, () => Solution, Time, NullLogger.Instance);

            Session = new SessionHarness(
                Factory,
                Time,
                sampleMemory: pid => pid > 0 ? Memory : null,
                solution: Solution);

            Factory.Own(Session.Session);
        }

        internal TestTimeProvider Time { get; } = new();

        internal SessionRegistry Registry { get; }

        internal string Solution { get; }

        internal string Key { get; }

        internal RestartableFakeRoslynFactory Inner { get; }

        internal SharingRoslynFactory Factory { get; }

        internal SessionHarness Session { get; }

        internal long Memory
        {
            get => Interlocked.Read(ref _memory);
            set => Interlocked.Exchange(ref _memory, value);
        }

        internal static async Task<HostedSession> StartAsync(Func<FakeRoslynScript>? script = null)
        {
            var host = new HostedSession(script);

            await host.Session.HandshakeAsync(Cancellation);

            await WaitUntilAsync(() => host.Factory.Host is not null && host.Inner.Latest is not null);
            await WaitUntilAsync(() => host.Inner.Latest!.ReceivedMethods.Contains("solution/open"));

            host.Inner.Latest!.CompleteProjectInitialization();
            await WaitUntilAsync(() => host.Session.Session.Recycler.Status.Phase == BackendPhase.Running);

            host.Time.Advance(BackendRecycler.BaselineDelay);
            Assert.Equal(400 * Megabyte, host.Session.Session.Recycler.Status.BaselineBytes);

            return host;
        }

        /// <summary>Attaches a second "process" over the real pipe, as an LSP client.</summary>
        internal async Task<Peer> AttachAsync()
        {
            var attaching = new SharingRoslynFactory(
                new InProcessFakeRoslynFactory(NullLogger.Instance),
                Registry,
                () => Solution,
                TimeProvider.System,
                NullLogger.Instance);

            var connection = await attaching.ConnectAsync(Cancellation);
            Assert.True(connection.Attached);

            var peer = new Peer(connection);

            lock (_attached)
            {
                _attached.Add(peer);
            }

            return peer;
        }

        /// <summary>Moves the host's clock a minute at a time, letting each minute's policy work finish.</summary>
        internal async Task AdvanceMinutesAsync(int minutes)
        {
            for (var minute = 0; minute < minutes; minute++)
            {
                Time.Advance(TimeSpan.FromMinutes(1));
                await Session.Session.Recycler.Work.WaitAsync(Patience, Cancellation);
            }
        }

        /// <summary>
        /// Moves the host's clock until its backend is dormant, with real time between minutes for
        /// whatever each one set off — the policy passes over a minute in which a request is in flight.
        /// </summary>
        internal async Task AdvanceUntilDormantAsync(int maxMinutes = 26)
        {
            for (var minute = 0; minute < maxMinutes; minute++)
            {
                Time.Advance(TimeSpan.FromMinutes(1));
                await Session.Session.Recycler.Work.WaitAsync(Patience, Cancellation);

                if (Session.Session.Gate.State == ReadinessState.Dormant)
                {
                    return;
                }

                await Task.Delay(20, Cancellation);
            }

            Assert.Equal(ReadinessState.Dormant, Session.Session.Gate.State);
        }

        public async ValueTask DisposeAsync()
        {
            IAsyncDisposable[] attached;

            lock (_attached)
            {
                attached = [.. _attached];
                _attached.Clear();
            }

            foreach (var peer in attached)
            {
                await peer.DisposeAsync();
            }

            await Session.DisposeAsync();
            await Factory.StopHostingAsync();
            _home.Dispose();
        }
    }

    /// <summary>The attaching process's side of the pipe, driven as an LSP client.</summary>
    private sealed class Peer : IAsyncDisposable
    {
        private readonly Lock _lock = new();
        private readonly List<JsonElement> _received = [];
        private readonly RoslynConnection _connection;
        private readonly LspFrameWriter _writer;
        private readonly Task _reading;

        internal Peer(RoslynConnection connection)
        {
            _connection = connection;
            _writer = new LspFrameWriter(connection.Output);
            _reading = Task.Run(() => ReadLoopAsync(connection.Input), CancellationToken.None);
        }

        /// <summary>Whether the host has closed this connection.</summary>
        internal bool Ended => _connection.Exited.IsCompleted;

        internal Task SendAsync(string message) =>
            _writer.WriteFrameAsync(Encoding.UTF8.GetBytes(message), Cancellation).AsTask();

        internal async Task<JsonElement> RequestAsync(int id, string method, string parameters)
        {
            await SendAsync(Request(id, method, parameters));
            return await AwaitResponseAsync(id);
        }

        internal async Task HandshakeAsync(int id)
        {
            await RequestAsync(id, "initialize", "{}");
            await SendAsync("""{"jsonrpc":"2.0","method":"initialized","params":{}}""");
        }

        internal Task<JsonElement> AwaitResponseAsync(int id) =>
            AwaitFirstAsync(message =>
                message.TryGetProperty("id", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.GetInt32() == id
                && !message.TryGetProperty("method", out _));

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

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();

            try
            {
                await _reading.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
            }

            _writer.Dispose();
        }

        private async Task ReadLoopAsync(Stream input)
        {
            var reader = new LspFrameReader(input);

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
        }
    }
}
