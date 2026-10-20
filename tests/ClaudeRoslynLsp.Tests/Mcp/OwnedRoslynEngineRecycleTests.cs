using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Testing;
using ClaudeRoslynLsp.Tests.Adapter;
using ClaudeRoslynLsp.Tests.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Mcp;

/// <summary>
/// The idle recycle in the MCP half: a tool call is the activity, and the next one relaunches the
/// backend (D95, D96, D98, D99).
/// </summary>
/// <remarks>
/// The first unit tests to construct an <see cref="OwnedRoslynEngine"/>: its constructor has taken
/// any connection factory and any clock since WP5b, and the recycle is the first behaviour of it that
/// is worth proving without a real Roslyn, because the interesting part is time.
/// </remarks>
public class OwnedRoslynEngineRecycleTests
{
    private const int RoslynProcessId = 4242;
    private const long Megabyte = 1024 * 1024;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// Idle and grown, the backend is stopped; the next tool call — any tool's, since they all start
    /// with <see cref="OwnedRoslynEngine.EnsureReadyAsync"/> — relaunches it and reports why it is
    /// loading, and the recycle shows up in the status.
    /// </summary>
    [Fact]
    public async Task TheNextToolCallRelaunchesARecycledBackendAndTheStatusSaysSo()
    {
        await using var harness = await EngineHarness.StartAsync();

        harness.Memory = 1500 * Megabyte;
        await harness.AdvanceUntilDormantAsync();

        var first = harness.Factory.Latest!;

        Assert.Contains("shutdown", first.ReceivedMethods);
        Assert.Contains("exit", first.ReceivedMethods);
        Assert.Equal(1, harness.Factory.ConnectCount);
        Assert.Equal(ReadinessState.Dormant, harness.Engine.Gate.State);

        // A tool call. Its wait is on the engine's clock, so the five seconds pass when the test
        // says, and what comes back is the loading answer every tool gives.
        var loading = harness.Engine.EnsureReadyAsync(TimeSpan.FromSeconds(5), Cancellation);

        await SessionHarness.WaitAsync(() => harness.Factory.ConnectCount == 2, Cancellation);
        harness.Time.Advance(TimeSpan.FromSeconds(5));

        var state = await loading.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);

        Assert.Equal(WorkspaceLoadStatus.Loading, state.Status);
        Assert.Contains("stopped while this session was idle", state.Message!, StringComparison.Ordinal);
        Assert.Equal(1, state.Recycles);
        Assert.NotNull(state.LastRecycleAt);

        var second = harness.Factory.Latest!;

        await SessionHarness.WaitAsync(() => second.ReceivedMethods.Contains("solution/open"), Cancellation);
        second.CompleteProjectInitialization();

        var ready = await harness.Engine
            .EnsureReadyAsync(TimeSpan.FromMinutes(5), Cancellation)
            .WaitAsync(TimeSpan.FromSeconds(30), Cancellation);

        Assert.Equal(WorkspaceLoadStatus.Ready, ready.Status);
        Assert.Null(ready.Message);
        Assert.Equal(RoslynProcessId, ready.ProcessId);
        Assert.Equal(1, ready.Recycles);

        // The new generation's baseline, a minute after its load (D100).
        await SessionHarness.WaitAsync(
            () => harness.Engine.Recycler.Status.Phase == BackendPhase.Running,
            Cancellation);

        harness.Memory = 380 * Megabyte;
        harness.Time.Advance(BackendRecycler.BaselineDelay);

        var settled = await harness.Engine.EnsureReadyAsync(TimeSpan.FromMinutes(5), Cancellation);
        Assert.Equal(380 * Megabyte, settled.BaselineBytes);
    }

    /// <summary>
    /// After a wake the cached workspace report belongs to a process that has gone: a pull the new one
    /// holds means "nothing since I last told you", and it has told this engine nothing (C58, D98).
    /// </summary>
    [Fact]
    public async Task AWakeForgetsTheWorkspaceReportOfTheRecycledBackend()
    {
        await using var harness = await EngineHarness.StartAsync(WithWorkspaceReport());

        await harness.Engine.WorkspaceDiagnosticAsync([], Cancellation);
        Assert.True(harness.Engine.HasCachedWorkspaceReport);

        harness.Memory = 1500 * Megabyte;
        await harness.AdvanceUntilDormantAsync();
        Assert.Equal(ReadinessState.Dormant, harness.Engine.Gate.State);

        _ = harness.Engine.EnsureReadyAsync(TimeSpan.FromMinutes(5), Cancellation);
        await SessionHarness.WaitAsync(() => harness.Factory.ConnectCount == 2, Cancellation);
        await SessionHarness.WaitAsync(
            () => harness.Factory.Latest!.ReceivedMethods.Contains("solution/open"),
            Cancellation);

        Assert.False(harness.Engine.HasCachedWorkspaceReport);
    }

    /// <summary>A tool call inside the window keeps the backend; one call is enough.</summary>
    [Fact]
    public async Task AToolCallInsideTheWindowKeepsTheBackend()
    {
        await using var harness = await EngineHarness.StartAsync();

        harness.Memory = 1500 * Megabyte;

        await harness.AdvanceMinutesAsync(15);
        await harness.Engine.EnsureReadyAsync(TimeSpan.FromMinutes(1), Cancellation);
        await harness.AdvanceMinutesAsync(15);

        Assert.DoesNotContain("shutdown", harness.Factory.Latest!.ReceivedMethods);
        Assert.Equal(ReadinessState.ProjectsLoaded, harness.Engine.Gate.State);
    }

    /// <summary>
    /// A recycle that throws after closing the gate and replacing <c>_opened</c> still leaves the
    /// engine dormant and wakeable: the next tool call relaunches it and is answered (finding 2).
    /// </summary>
    [Fact]
    public async Task AStopThatFailsPartWayStillLeavesTheEngineWakeable()
    {
        await using var harness = await EngineHarness.StartAsync(
            wrap: static inner => new TweakedRoslynFactory(inner) { ThrowAtEndOfStream = static attempt => attempt == 1 });

        harness.Memory = 1500 * Megabyte;
        await harness.AdvanceUntilDormantAsync();
        await SessionHarness.WaitAsync(
            () => harness.Engine.Recycler.Status.Phase == BackendPhase.Dormant,
            Cancellation);

        var ready = harness.Engine.EnsureReadyAsync(TimeSpan.FromMinutes(5), Cancellation);

        await SessionHarness.WaitAsync(() => harness.Factory.ConnectCount == 2, Cancellation);
        await SessionHarness.WaitAsync(
            () => harness.Factory.Latest!.ReceivedMethods.Contains("solution/open"),
            Cancellation);

        harness.Factory.Latest!.CompleteProjectInitialization();

        var state = await ready.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        Assert.Equal(WorkspaceLoadStatus.Ready, state.Status);
    }

    /// <summary>
    /// A wake that fails answers the call that asked for it with a loading status that says why and
    /// that the next call tries again — and the next call does (finding 5, D96).
    /// </summary>
    [Fact]
    public async Task AWakeThatFailsAnswersLoadingAndTheNextCallTriesAgain()
    {
        TweakedRoslynFactory? tweaked = null;

        await using var harness = await EngineHarness.StartAsync(
            wrap: inner => tweaked = new TweakedRoslynFactory(inner) { FailAttempt = static attempt => attempt == 2 });

        harness.Memory = 1500 * Megabyte;
        await harness.AdvanceUntilDormantAsync();
        await SessionHarness.WaitAsync(
            () => harness.Engine.Recycler.Status.Phase == BackendPhase.Dormant,
            Cancellation);

        var failed = await harness.Engine
            .EnsureReadyAsync(TimeSpan.FromMinutes(5), Cancellation)
            .WaitAsync(TimeSpan.FromSeconds(30), Cancellation);

        Assert.Equal(WorkspaceLoadStatus.Loading, failed.Status);
        Assert.Contains("relaunching it failed", failed.Message!, StringComparison.Ordinal);
        Assert.Equal(ReadinessState.Dormant, harness.Engine.Gate.State);
        Assert.Equal(1, harness.Factory.ConnectCount);

        await SessionHarness.WaitAsync(
            () => harness.Engine.Recycler.Status.Phase == BackendPhase.Dormant,
            Cancellation);

        var retried = harness.Engine.EnsureReadyAsync(TimeSpan.FromMinutes(5), Cancellation);

        await SessionHarness.WaitAsync(() => harness.Factory.ConnectCount == 2, Cancellation);
        await SessionHarness.WaitAsync(
            () => harness.Factory.Latest!.ReceivedMethods.Contains("solution/open"),
            Cancellation);

        harness.Factory.Latest!.CompleteProjectInitialization();

        var state = await retried.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);

        Assert.Equal(WorkspaceLoadStatus.Ready, state.Status);
        Assert.Null(state.Message);
        Assert.Equal(3, tweaked!.Attempts);
    }

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

    /// <summary>One engine over the restartable fake, on the hand-moved clock, loaded and measured.</summary>
    private sealed class EngineHarness : IAsyncDisposable
    {
        private readonly TempWorkspace _workspace;
        private long _memory = 400 * Megabyte;

        private EngineHarness(
            FakeRoslynScript? script,
            Func<RestartableFakeRoslynFactory, IRoslynConnectionFactory>? wrap)
        {
            _workspace = TempWorkspace.Create("engine-recycle");

            var solution = _workspace.File_("Fixture.slnx", "<Solution />");

            Factory = new RestartableFakeRoslynFactory(
                () => script ?? FakeRoslynScript.Roslyn512Startup(projectLoadDelay: null));

            Engine = new OwnedRoslynEngine(
                wrap?.Invoke(Factory) ?? Factory,
                ClaudeRoslynLspOptions.FromEnvironment(static _ => null),
                () => WorkspaceSelection.FromPath(solution),
                _workspace.Root,
                Time,
                NullLogger.Instance,
                sampleMemory: pid => pid == RoslynProcessId ? Memory : null);
        }

        internal TestTimeProvider Time { get; } = new();

        internal RestartableFakeRoslynFactory Factory { get; }

        internal OwnedRoslynEngine Engine { get; }

        internal long Memory
        {
            get => Interlocked.Read(ref _memory);
            set => Interlocked.Exchange(ref _memory, value);
        }

        /// <summary>Starts the engine, loads the workspace, and takes the baseline at 400 MB.</summary>
        /// <param name="script">The backend's script; the standard startup when null.</param>
        /// <param name="wrap">Puts something between the engine and the fake, for the fault tests.</param>
        internal static async Task<EngineHarness> StartAsync(
            FakeRoslynScript? script = null,
            Func<RestartableFakeRoslynFactory, IRoslynConnectionFactory>? wrap = null)
        {
            var harness = new EngineHarness(script, wrap);

            harness.Engine.Start();
            await SessionHarness.WaitAsync(() => harness.Factory.Latest is not null, Cancellation);
            await SessionHarness.WaitAsync(
                () => harness.Factory.Latest!.ReceivedMethods.Contains("solution/open"),
                Cancellation);

            harness.Factory.Latest!.CompleteProjectInitialization();

            var ready = await harness.Engine.EnsureReadyAsync(TimeSpan.FromMinutes(5), Cancellation);
            Assert.Equal(WorkspaceLoadStatus.Ready, ready.Status);

            await SessionHarness.WaitAsync(
                () => harness.Engine.Recycler.Status.Phase == BackendPhase.Running,
                Cancellation);

            harness.Time.Advance(BackendRecycler.BaselineDelay);
            Assert.Equal(400 * Megabyte, harness.Engine.Recycler.Status.BaselineBytes);

            return harness;
        }

        /// <summary>Moves the clock a minute at a time, letting each minute's policy work finish.</summary>
        internal async Task AdvanceMinutesAsync(int minutes)
        {
            for (var minute = 0; minute < minutes; minute++)
            {
                Time.Advance(TimeSpan.FromMinutes(1));
                await Engine.Recycler.Work.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
            }
        }

        /// <summary>
        /// Moves the clock until the backend is dormant: never before the window (the first eighteen
        /// minutes are asserted to change nothing), and within a few looks after it.
        /// </summary>
        internal async Task AdvanceUntilDormantAsync()
        {
            await AdvanceMinutesAsync(18);
            Assert.Equal(ReadinessState.ProjectsLoaded, Engine.Gate.State);

            for (var minute = 0; minute < 8; minute++)
            {
                Time.Advance(TimeSpan.FromMinutes(1));
                await Engine.Recycler.Work.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);

                if (Engine.Gate.State == ReadinessState.Dormant)
                {
                    return;
                }

                await Task.Delay(20, Cancellation);
            }

            Assert.Equal(ReadinessState.Dormant, Engine.Gate.State);
        }

        public async ValueTask DisposeAsync()
        {
            // The engine's own shutdown waits on its clock; a fake that answers promptly never lets it.
            await Engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
            _workspace.Dispose();
        }
    }
}
