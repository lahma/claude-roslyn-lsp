using System.Text;
using System.Text.RegularExpressions;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Testing;
using ClaudeRoslynLsp.Tests.Testing;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// The idle recycle through a whole <c>lsp</c> session: the backend stopped by protocol, the gate
/// dormant, the next request relaunching it with the mirror replayed (D95-D100).
/// </summary>
/// <remarks>
/// <para>
/// Over <see cref="RestartableFakeRoslynFactory"/>, because the claims are about what reaches the
/// backend — a <c>shutdown</c> and an <c>exit</c> rather than a closed stream; a second connection
/// that is told about the client's <em>unsaved</em> text — and about what the client never sees,
/// which is an error.
/// </para>
/// <para>
/// The session runs on <see cref="TestTimeProvider"/> and the clock is moved a minute at a time with
/// the policy's work awaited after each step. Everything else on that clock — the readiness budget,
/// the diagnostics bridge's debounces, the watch bridge's rebuilds — moves with it, which is the
/// point: a twenty-minute idle period must not disturb any of them.
/// </para>
/// </remarks>
public class AdapterIdleRecycleTests
{
    private const string ProgramUri = "file:///w/Hello.App/Program.cs";
    private const int RoslynProcessId = 4242;
    private const long Megabyte = 1024 * 1024;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// The whole cycle: idle and grown, stopped by protocol and not relaunched; then one request, and
    /// the backend comes back with the client's unsaved buffer and answers it.
    /// </summary>
    [Fact]
    public async Task AnIdleGrownBackendIsStoppedByProtocolAndTheNextRequestBringsItBack()
    {
        var factory = new RestartableFakeRoslynFactory();
        var memory = new MemoryScript();
        var time = new TestTimeProvider();

        await using var harness = new SessionHarness(factory, time, sampleMemory: memory.Sample);

        await LoadAsync(harness, factory, time, memory);

        await harness.SendAsync(DidOpen(ProgramUri, 1, "class Program { }"), Cancellation);
        await harness.SendAsync(DidChange(ProgramUri, 2, "class Program { int Unsaved; }"), Cancellation);
        await SessionHarness.WaitAsync(
            () => factory.Latest!.ReceivedMethods.Contains("textDocument/didChange"),
            Cancellation);

        memory.Bytes = 1500 * Megabyte;

        // Not before the window has passed...
        await AdvanceMinutesAsync(time, harness, 18);
        Assert.DoesNotContain("shutdown", factory.Latest!.ReceivedMethods);

        // ...and soon after it has.
        await AdvanceUntilAsync(time, harness, () => harness.Session.Gate.State == ReadinessState.Dormant, 6);

        var first = factory.Latest!;

        Assert.Contains("shutdown", first.ReceivedMethods);
        Assert.Contains("exit", first.ReceivedMethods);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(ReadinessState.Dormant, harness.Session.Gate.State);
        Assert.Equal(1, harness.Session.Recycler.Status.Recycles);

        // Dormant stays dormant: nothing relaunches it but a request.
        await AdvanceMinutesAsync(time, harness, 30);
        Assert.Equal(1, factory.ConnectCount);

        await harness.SendAsync(Definition(7, ProgramUri), Cancellation);
        await SessionHarness.WaitAsync(() => factory.ConnectCount == 2, Cancellation);

        var second = factory.Latest!;

        await SessionHarness.WaitAsync(
            () => second.ReceivedMethods.Contains("solution/open")
                  && second.ReceivedMethods.Contains("textDocument/didOpen"),
            Cancellation);

        // One didOpen at the latest text — the buffer the client had not saved — not the history.
        var replay = Assert.Single(
            second.ReceivedMessages,
            x => x.Contains("\"method\":\"textDocument/didOpen\"", StringComparison.Ordinal));

        Assert.Contains("int Unsaved;", replay, StringComparison.Ordinal);
        Assert.Contains("\"version\":2", replay, StringComparison.Ordinal);

        // Held, not answered empty: the reloaded workspace has not reported itself loaded yet.
        Assert.Null(harness.TryFindResponse(7));

        second.CompleteProjectInitialization();

        var response = await harness.AwaitResponseAsync(7, Cancellation);

        Assert.False(response.TryGetProperty("error", out _));
        Assert.True(response.GetProperty("result").GetArrayLength() > 0);

        // A recycle is not a crash (D99), and the client was told why the answer was slow.
        Assert.Equal(0, harness.Session.Supervisor.RestartsInWindow);
        Assert.Contains(
            harness.Notifications("window/logMessage"),
            x => x.GetProperty("params").GetProperty("message").GetString()!
                .Contains("stopped while this session was idle", StringComparison.Ordinal));
    }

    /// <summary>Any client message inside the window starts it again (D95).</summary>
    [Fact]
    public async Task ActivityInsideTheWindowPreventsTheRecycle()
    {
        var factory = new RestartableFakeRoslynFactory();
        var memory = new MemoryScript();
        var time = new TestTimeProvider();

        await using var harness = new SessionHarness(factory, time, sampleMemory: memory.Sample);

        await LoadAsync(harness, factory, time, memory);
        await harness.SendAsync(DidOpen(ProgramUri, 1, "class Program { }"), Cancellation);

        memory.Bytes = 1500 * Megabyte;
        await AdvanceMinutesAsync(time, harness, 15);

        await harness.SendAsync(DidChange(ProgramUri, 2, "class Program { int Edited; }"), Cancellation);
        await SessionHarness.WaitAsync(
            () => factory.Latest!.ReceivedMethods.Contains("textDocument/didChange"),
            Cancellation);

        // Without the edit the recycle would have come at minute 21; with it, not at minute 35.
        await AdvanceMinutesAsync(time, harness, 19);
        Assert.DoesNotContain("shutdown", factory.Latest!.ReceivedMethods);

        await AdvanceUntilAsync(time, harness, () => factory.Latest!.ReceivedMethods.Contains("shutdown"), 6);
    }

    /// <summary>
    /// D99: the recycle did not spend the crash budget, so a backend that keeps dying after the wake
    /// gets exactly the two relaunches any other session would.
    /// </summary>
    [Fact]
    public async Task ACrashAfterARecycleStillGetsTheWholeRestartBudget()
    {
        var factory = new RestartableFakeRoslynFactory();
        var memory = new MemoryScript();
        var time = new TestTimeProvider();

        await using var harness = new SessionHarness(factory, time, sampleMemory: memory.Sample);

        await RecycleAsync(harness, factory, time, memory);

        await harness.SendAsync(Definition(9, ProgramUri), Cancellation);
        await SessionHarness.WaitAsync(() => factory.ConnectCount == 2, Cancellation);

        for (var connection = 2; connection <= 4; connection++)
        {
            var expected = connection;
            await SessionHarness.WaitAsync(() => factory.ConnectCount == expected, Cancellation);
            factory.Kill();
        }

        await SessionHarness.WaitAsync(() => harness.Session.Gate.State == ReadinessState.Failed, Cancellation);

        Assert.Equal(4, factory.ConnectCount);
        Assert.Equal(RoslynSupervisor.MaxRestarts, harness.Session.Supervisor.RestartsInWindow);
    }

    /// <summary>
    /// D98: a new backend registers everything again, under ids of its own, so without the clear the
    /// table would hold one copy of every registration per backend the session has ever had.
    /// </summary>
    [Fact]
    public async Task RecyclesDoNotMultiplyRegistrations()
    {
        var connections = 0;
        var factory = new RestartableFakeRoslynFactory(() => WithFreshRegistrationIds(Interlocked.Increment(ref connections)));
        var memory = new MemoryScript();
        var time = new TestTimeProvider();

        await using var harness = new SessionHarness(factory, time, sampleMemory: memory.Sample);

        await LoadAsync(harness, factory, time, memory);
        await WaitForStartupAsync(factory.Latest!);

        var registrations = harness.Session.Registrations.Count;
        var watched = harness.Session.Registrations.Watchers.Count;

        Assert.True(registrations > 0, "The scripted startup registers nothing; the test would prove nothing.");

        for (var recycle = 1; recycle <= 3; recycle++)
        {
            memory.Bytes = 1500 * Megabyte;
            await AdvanceUntilAsync(time, harness, () => harness.Session.Gate.State == ReadinessState.Dormant, 26);

            await harness.SendAsync(Definition(100 + recycle, ProgramUri), Cancellation);

            var expected = recycle + 1;
            await SessionHarness.WaitAsync(() => factory.ConnectCount == expected, Cancellation);
            await WaitForStartupAsync(factory.Latest!);

            Assert.Equal(registrations, harness.Session.Registrations.Count);
            Assert.Equal(watched, harness.Session.Registrations.Watchers.Count);

            await CompleteLoadAsync(harness, factory, time, memory);
            await harness.AwaitResponseAsync(100 + recycle, Cancellation);
        }
    }

    /// <summary>
    /// Never during a load: a request held for a workspace that has not finished loading is a request
    /// somebody is waiting on, however long it has been waiting.
    /// </summary>
    [Fact]
    public async Task NoRecycleHappensWhileARequestIsHeldForTheLoad()
    {
        var factory = new RestartableFakeRoslynFactory();
        var memory = new MemoryScript { Bytes = 3000 * Megabyte };
        var time = new TestTimeProvider();

        // A load budget longer than the test, so the gate is still holding at the end of it.
        var options = ClaudeRoslynLspOptions.FromEnvironment(static _ => null) with { ReadyTimeoutSeconds = 3600 };

        await using var harness = new SessionHarness(factory, time, options, memory.Sample);

        await harness.HandshakeAsync(Cancellation);
        await SessionHarness.WaitAsync(() => factory.Latest is not null, Cancellation);

        await harness.SendAsync(Definition(5, ProgramUri), Cancellation);
        await SessionHarness.WaitAsync(() => harness.Session.Gate.HeldCount > 0, Cancellation);

        await AdvanceMinutesAsync(time, harness, 40);

        Assert.DoesNotContain("shutdown", factory.Latest!.ReceivedMethods);
        Assert.Equal(1, harness.Session.Gate.HeldCount);
        Assert.Equal(0, harness.Session.Recycler.Status.Recycles);
    }

    /// <summary>
    /// Never under a request that is in flight: stopping Roslyn under it would answer it with an error
    /// for no reason. Once it is answered, the recycle goes ahead.
    /// </summary>
    [Fact]
    public async Task NoRecycleHappensWhileARequestIsInFlight()
    {
        using var reached = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var factory = new RestartableFakeRoslynFactory(() => ScriptWithHangingDefinition(reached, release));
        var memory = new MemoryScript();
        var time = new TestTimeProvider();

        await using var harness = new SessionHarness(factory, time, sampleMemory: memory.Sample);

        await LoadAsync(harness, factory, time, memory);
        memory.Bytes = 3000 * Megabyte;

        await harness.SendAsync(Definition(11, ProgramUri), Cancellation);
        await SessionHarness.WaitAsync(() => reached.IsSet, Cancellation);

        await AdvanceMinutesAsync(time, harness, 40);
        Assert.DoesNotContain("shutdown", factory.Latest!.ReceivedMethods);

        release.Set();
        await harness.AwaitResponseAsync(11, Cancellation);

        // The idle period started with that request, long ago; the compaction step comes first.
        await AdvanceUntilAsync(time, harness, () => factory.Latest!.ReceivedMethods.Contains("shutdown"), 6);
    }

    /// <summary>A session whose policy is off keeps its backend however long it idles and however large it is.</summary>
    [Fact]
    public async Task AZeroWindowKeepsTheBackendForEver()
    {
        var factory = new RestartableFakeRoslynFactory();
        var memory = new MemoryScript();
        var time = new TestTimeProvider();
        var options = ClaudeRoslynLspOptions.FromEnvironment(static _ => null) with { IdleRecycleMinutes = 0 };

        await using var harness = new SessionHarness(factory, time, options, memory.Sample);

        await LoadAsync(harness, factory, time, memory);
        memory.Bytes = 9000 * Megabyte;

        await AdvanceMinutesAsync(time, harness, 90);

        Assert.DoesNotContain("shutdown", factory.Latest!.ReceivedMethods);
        Assert.Equal(1, factory.ConnectCount);
    }

    /// <summary>Handshake, load, and the baseline a minute later at 400 MB.</summary>
    private static async Task LoadAsync(
        SessionHarness harness,
        RestartableFakeRoslynFactory factory,
        TestTimeProvider time,
        MemoryScript memory)
    {
        await harness.HandshakeAsync(Cancellation);
        await SessionHarness.WaitAsync(() => factory.Latest is not null, Cancellation);
        await CompleteLoadAsync(harness, factory, time, memory);
    }

    /// <summary>Completes the current backend's load and takes its baseline.</summary>
    private static async Task CompleteLoadAsync(
        SessionHarness harness,
        RestartableFakeRoslynFactory factory,
        TestTimeProvider time,
        MemoryScript memory)
    {
        factory.Latest!.CompleteProjectInitialization();
        await SessionHarness.WaitAsync(
            () => harness.Session.Recycler.Status.Phase == BackendPhase.Running,
            Cancellation);

        memory.Bytes = 400 * Megabyte;
        time.Advance(BackendRecycler.BaselineDelay);

        await SessionHarness.WaitAsync(
            () => harness.Session.Recycler.Status.BaselineBytes == 400 * Megabyte,
            Cancellation);
    }

    /// <summary>A load and a recycle, for the tests about what happens after one.</summary>
    private static async Task RecycleAsync(
        SessionHarness harness,
        RestartableFakeRoslynFactory factory,
        TestTimeProvider time,
        MemoryScript memory)
    {
        await LoadAsync(harness, factory, time, memory);

        memory.Bytes = 1500 * Megabyte;
        await AdvanceUntilAsync(time, harness, () => harness.Session.Gate.State == ReadinessState.Dormant, 26);
    }

    /// <summary>Moves the clock a minute at a time, letting each minute's policy work finish.</summary>
    private static async Task AdvanceMinutesAsync(TestTimeProvider time, SessionHarness harness, int minutes)
    {
        for (var minute = 0; minute < minutes; minute++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await harness.Session.Recycler.Work.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        }
    }

    /// <summary>
    /// Moves the clock a minute at a time until a condition holds, failing if it has not within a cap.
    /// </summary>
    /// <remarks>
    /// The deadline side of every "it is recycled" claim. The policy correctly passes over a minute in
    /// which something is in flight — a diagnostics pull the test's own edit set off, say — and the
    /// test clock moves twenty minutes in a few real milliseconds, so which exact minute the recycle
    /// lands in is a race with the session's own background work. What is not a race is the "not
    /// before" side, which each test asserts separately with a fixed advance.
    /// </remarks>
    private static async Task AdvanceUntilAsync(
        TestTimeProvider time,
        SessionHarness harness,
        Func<bool> condition,
        int maxMinutes)
    {
        for (var minute = 0; minute < maxMinutes; minute++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await harness.Session.Recycler.Work.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);

            if (condition())
            {
                return;
            }

            // Real time for whatever this minute set off to finish before the next decision.
            await Task.Delay(20, Cancellation);
        }

        Assert.True(condition(), $"The condition did not hold within {maxMinutes} more minute(s) of the session's clock.");
    }

    /// <summary>
    /// Waits until a backend has sent everything it sends at startup and been answered: the adapter
    /// answering the script's last request (108) means every registration before it was recorded.
    /// </summary>
    private static Task WaitForStartupAsync(FakeRoslynServer backend) =>
        SessionHarness.WaitAsync(
            () => backend.ReceivedMessages.Any(x =>
                x.Contains("\"id\":108", StringComparison.Ordinal)
                && !x.Contains("\"method\"", StringComparison.Ordinal)),
            Cancellation);

    /// <summary>
    /// The standard startup with every registration id made unique to one connection, which is what
    /// the real server does: a new process mints new GUIDs.
    /// </summary>
    private static FakeRoslynScript WithFreshRegistrationIds(int connection)
    {
        var script = FakeRoslynScript.Roslyn512Startup(projectLoadDelay: null);
        var guid = new Regex("\"id\":\"([0-9a-f-]{36})\"", RegexOptions.None, TimeSpan.FromSeconds(1));

        return new FakeRoslynScript
        {
            InitializeResult = script.InitializeResult,
            ProjectLoadDelay = script.ProjectLoadDelay,
            Responders = script.Responders,
            Steps =
            [
                .. script.Steps.Select(step => step with
                {
                    Body = Encoding.UTF8.GetBytes(guid.Replace(
                        Encoding.UTF8.GetString(step.Body),
                        match => $"\"id\":\"{match.Groups[1].Value}-{connection}\"")),
                }),
            ],
        };
    }

    /// <summary>The standard startup script with a definition responder that blocks until released.</summary>
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

    private static string DidOpen(string uri, int version, string text) => $$$"""
        {"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":
         {"uri":"{{{uri}}}","languageId":"csharp","version":{{{version}}},"text":"{{{text}}}"}
        }}
        """;

    private static string DidChange(string uri, int version, string text) => $$$"""
        {"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":
         {"uri":"{{{uri}}}","version":{{{version}}}},"contentChanges":[{"text":"{{{text}}}"}]
        }}
        """;

    private static string Definition(int id, string uri) => $$$"""
        {"jsonrpc":"2.0","id":{{{id}}},"method":"textDocument/definition","params":{
         "textDocument":{"uri":"{{{uri}}}"},"position":{"line":0,"character":6}
        }}
        """;

    /// <summary>What the scripted Roslyn process "holds", by the policy's metric.</summary>
    private sealed class MemoryScript
    {
        private long _bytes = 400 * Megabyte;

        internal long Bytes
        {
            get => Interlocked.Read(ref _bytes);
            set => Interlocked.Exchange(ref _bytes, value);
        }

        internal long? Sample(int processId) => processId == RoslynProcessId ? Bytes : null;
    }
}
