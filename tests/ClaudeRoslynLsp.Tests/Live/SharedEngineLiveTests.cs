using System.Diagnostics;
using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Adapter.Sharing;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Edits;
using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Mcp.Models;
using ClaudeRoslynLsp.Mcp.Tools;
using ClaudeRoslynLsp.Protocol;
using ClaudeRoslynLsp.Roslyn;
using ClaudeRoslynLsp.Testing;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Live;

/// <summary>
/// The claim of the shared engine, against a <b>real</b> Roslyn: an <c>lsp</c> session and an
/// <c>mcp</c> engine on one solution are <em>one</em> language server, and both of them keep working
/// when it dies or when the process holding it goes away (D23, D85-D94).
/// </summary>
/// <remarks>
/// <para>
/// Opt-in through <c>CLAUDE_ROSLYN_LSP_LIVE_TESTS=1</c>, and one test method in phases for D59's
/// reason, more sharply here than anywhere else: the phases <em>are</em> a lifecycle. Killing the
/// host's Roslyn, and then stopping the host itself, destroy exactly the preconditions the earlier
/// phases needed, and NUnit promises no order for tests within a class.
/// </para>
/// <para>
/// The load-bearing assertion is a negative one: the attaching half's own launcher never started a
/// process. Counting <c>Microsoft.CodeAnalysis.LanguageServer</c> processes on the machine would be
/// the obvious check and the wrong one — a developer box running Visual Studio, VS Code or another
/// Claude session has several, none of them this test's. What this asserts instead is that both
/// halves report the <em>same</em> Roslyn process id, that it is alive, and that the second launcher
/// has no process of its own to report.
/// </para>
/// <para>
/// Its own copy of the fixture and its own adapter home, because the session directory is part of
/// what is under test and the two other live fixtures edit the same files differently.
/// </para>
/// </remarks>
[Category("Live")]
public class SharedEngineLiveTests
{
    private static readonly string[] SkippedDirectories = ["bin", "obj", ".vs"];

    private readonly TextWriter _output = TestContext.Out;

    private TempWorkspace? _home;
    private TempWorkspace? _workspace;
    private ClaudeRoslynLspOptions _options = new();
    private AdapterPaths? _paths;

    private static CancellationToken Cancellation => TestContext.CurrentContext.CancellationToken;

    /// <summary>Whether the live tests were asked for.</summary>
    private bool Enabled { get; } =
        Environment.GetEnvironmentVariable(AdapterLiveFixture.EnableVariable) is "1" or "true" or "TRUE";

    private string WorkspaceRoot => _workspace?.Root ?? throw new InvalidOperationException("not initialised");

    private string SolutionPath => Path.Combine(WorkspaceRoot, "HelloSolution.slnx");

    /// <summary>The workspace root as a <c>file:</c> URI, which is what the client's handshake carries.</summary>
    private string RootUri => new Uri(WorkspaceRoot + Path.DirectorySeparatorChar).AbsoluteUri;

    /// <summary>Copies the fixture and acquires Roslyn; does nothing when the live tests are off.</summary>
    [SetUp]
    public async Task SetUpAsync()
    {
        if (!Enabled)
        {
            return;
        }

        _workspace = TempWorkspace.Create("live-shared");
        CopyTree(RepositoryLayout.Path_("tests", "fixtures", "HelloSolution"), _workspace.Root);

        foreach (var name in new[] { "Directory.Build.props", "Directory.Packages.props" })
        {
            File.Copy(RepositoryLayout.Path_("tests", "fixtures", name), Path.Combine(_workspace.Root, name), true);
        }

        var configuredHome = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_LSP_HOME");
        string home;

        if (string.IsNullOrWhiteSpace(configuredHome))
        {
            _home = TempWorkspace.Create("live-shared-home");
            home = _home.Root;
        }
        else
        {
            home = configuredHome;
        }

        _options = new ClaudeRoslynLspOptions
        {
            Home = home,
            Solution = SolutionPath,
            SolutionVariable = "CLAUDE_ROSLYN_LSP_SOLUTION",
            RoslynLogLevel = LogLevel.Information,
        };

        _paths = AdapterPaths.Resolve(_options, static _ => null);

        using var client = NuGetPayloadDownloader.CreateHttpClient();

        var locator = new RoslynServerLocator(
            _options,
            _paths,
            NullLogger.Instance,
            downloaderFactory: () => new NuGetPayloadDownloader(client, _paths, NullLogger.Instance));

        var resolution = await locator.ResolveAsync(allowDownload: true, cancellationToken: Cancellation);

        Assert.That(resolution.IsResolved, Is.True, resolution.Failure);
    }

    /// <summary>Deletes the copy and the temporary home.</summary>
    [TearDown]
    public void TearDown()
    {
        _workspace?.Dispose();
        _home?.Dispose();
    }

    /// <summary>The whole lifecycle, in the one order that lets every phase have its preconditions.</summary>
    [Test]
    public async Task OneRoslynServesBothVerbsAndSurvivesLosingEitherHalf()
    {
        if (!Enabled)
        {
            Assert.Ignore(
                $"Set {AdapterLiveFixture.EnableVariable}=1 to run the shared-engine live test "
                + "(it starts a real Roslyn).");
        }

        // The session directory is private to this run: the key is derived from the solution path,
        // which is a temporary copy, so nothing else on the machine can collide with it.
        var registry = SessionRegistry.ForHome(_paths!.Home, new TestOutputLogger(_output, LogLevel.Warning));

        var hostRoslyn = await LspHostsAndMcpAttachesAsync(registry);
        await McpHostsAndLspAttachesAsync(registry);

        _output.WriteLine($"one Roslyn served both verbs; its peak working set was {hostRoslyn} MB");
    }

    /// <summary>Phases 1-5: the <c>lsp</c> verb gets there first, and the <c>mcp</c> verb attaches.</summary>
    private async Task<long> LspHostsAndMcpAttachesAsync(SessionRegistry registry)
    {
        var started = Stopwatch.StartNew();

        var hostLauncher = new LaunchedRoslynFactory(_options, _paths!, Logger("lsp-host"));
        var hostFactory = Sharing(hostLauncher, registry, "lsp-host");

        await using var host = LspHost.Start(hostFactory, _options, SolutionPath, Logger("lsp-host"));

        await host.HandshakeAsync(RootUri);
        await WaitUntilAsync(() => host.Session.Gate.IsOpen, TimeSpan.FromSeconds(_options.ReadyTimeoutSeconds));

        _output.WriteLine($"lsp host loaded                 {started.Elapsed.TotalSeconds:F1} s");

        var published = registry.TryRead(SessionRegistry.KeyFor(SolutionPath));

        Assert.That(published, Is.Not.Null);
        Assert.That(published!.ProcessId, Is.EqualTo(Environment.ProcessId));
        Assert.That(hostFactory.Host, Is.Not.Null);

        // ------------------------------------------------------------------ the attaching engine
        var attachLauncher = new LaunchedRoslynFactory(_options, _paths!, Logger("mcp-attached"));
        var attachFactory = Sharing(attachLauncher, registry, "mcp-attached");

        var attaching = Stopwatch.StartNew();
        await using var engine = Engine(attachFactory, "mcp-attached");
        attachFactory.Own(engine);

        engine.Start();

        var state = await engine.EnsureReadyAsync(
            TimeSpan.FromSeconds(_options.ReadyTimeoutSeconds),
            Cancellation);

        _output.WriteLine($"mcp attached and ready          {attaching.Elapsed.TotalSeconds:F1} s");

        Assert.That(state.Status, Is.EqualTo(WorkspaceLoadStatus.Ready));
        Assert.That(state.Engine, Is.EqualTo(OwnedRoslynEngine.AttachedEngine));
        Assert.That(state.HostProcessId, Is.EqualTo(Environment.ProcessId));
        Assert.That(attachFactory.IsAttached, Is.True);

        // The claim, stated so it cannot pass by accident: the attaching half never launched
        // anything, and the process it reports is the one the host launched.
        Assert.That(attachLauncher.ProcessId, Is.Null);
        Assert.That(state.ProcessId, Is.Not.Null);
        Assert.That(IsAlive(state.ProcessId!.Value), Is.True, "the shared Roslyn process is not running");

        var roslynProcessId = state.ProcessId.Value;
        _output.WriteLine($"shared Roslyn                   pid {roslynProcessId}");

        // ------------------------------------------------------------------ both halves answer
        var definition = await host.RequestAsync(
            "textDocument/definition",
            Position("Hello.Core/Caller.cs", "calculator.Compute()", offset: 12));

        Assert.That(definition.GetProperty("result").EnumerateArray(), Is.Not.Empty);
        _output.WriteLine($"lsp definition through the host {definition.GetProperty("result").GetArrayLength()} location(s)");

        var context = ToolContext(engine);
        var resolved = await SymbolTools.ResolveSymbolAsync(context, "IShape", cancellationToken: Cancellation);

        Assert.That(resolved.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(resolved.Matches!, Is.Not.Empty);
        _output.WriteLine($"mcp resolveSymbol over the pipe {resolved.Matches!.Count} match(es)");

        // A solution-wide pull needs a fullSolution compiler scope (C14), which an attached engine
        // can only get by asking the host to set it (D90). If the routing were wrong this would come
        // back empty and successful, which is the failure this product exists to remove.
        var diagnostics = await DiagnosticTools.GetDiagnosticsAsync(
            context,
            scope: "solution",
            cancellationToken: Cancellation);

        Assert.That(diagnostics.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(diagnostics.Diagnostics ?? [], Has.Some.Matches<DiagnosticEntry>(entry => entry!.Id == "CS0029"));
        _output.WriteLine($"mcp solution diagnostics        {diagnostics.Diagnostics!.Count} entries, CS0029 present");

        await StaleClientTextIsRefusedAsync(context, host);

        var peak = WorkingSetOf(roslynProcessId);

        // ------------------------------------------------------------------ the backend dies
        var killed = Stopwatch.StartNew();
        Kill(roslynProcessId);

        var afterKill = await host.RequestAsync(
            "textDocument/definition",
            Position("Hello.Core/Caller.cs", "calculator.Compute()", offset: 12),
            TimeSpan.FromSeconds(_options.ReadyTimeoutSeconds));

        Assert.That(afterKill.GetProperty("result").EnumerateArray(), Is.Not.Empty);
        _output.WriteLine($"lsp answered after a kill -9    {killed.Elapsed.TotalSeconds:F1} s");

        var recovered = await SymbolTools.ResolveSymbolAsync(context, "IShape", cancellationToken: Cancellation);

        Assert.That(recovered.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(recovered.Matches!, Is.Not.Empty);
        _output.WriteLine($"mcp answered after a kill -9    {killed.Elapsed.TotalSeconds:F1} s, still attached");

        // The attached half never noticed the relaunch as anything but a pause: it is still using the
        // host's engine rather than having started one.
        Assert.That(attachLauncher.ProcessId, Is.Null);

        // ------------------------------------------------------------------ the host goes away
        var takeover = Stopwatch.StartNew();
        await host.DisposeAsync();
        await hostFactory.StopHostingAsync();

        WorkspaceState owned;

        while (true)
        {
            owned = await engine.EnsureReadyAsync(TimeSpan.FromSeconds(30), Cancellation);

            if (owned.Status == WorkspaceLoadStatus.Ready
                && string.Equals(owned.Engine, OwnedRoslynEngine.Engine, StringComparison.Ordinal))
            {
                break;
            }

            Assert.That(
                takeover.Elapsed < TimeSpan.FromSeconds(_options.ReadyTimeoutSeconds), Is.True,
                $"the attached engine did not take ownership within {_options.ReadyTimeoutSeconds} s "
                + $"(status {owned.Status}, engine {owned.Engine})");

            await Task.Delay(250, Cancellation);
        }

        _output.WriteLine($"mcp became the owner            {takeover.Elapsed.TotalSeconds:F1} s");

        Assert.That(attachLauncher.ProcessId, Is.Not.Null);
        Assert.That(attachFactory.Host, Is.Not.Null);

        var afterTakeover = await SymbolTools.ResolveSymbolAsync(context, "IShape", cancellationToken: Cancellation);

        Assert.That(afterTakeover.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(afterTakeover.Matches!, Is.Not.Empty);

        await attachFactory.StopHostingAsync();

        return peak;
    }

    /// <summary>Phase 6: the same thing the other way round, which is the order the plugin uses.</summary>
    /// <remarks>
    /// Claude Code starts the <c>mcp</c> server at session start and the <c>lsp</c> server lazily, so
    /// this is the ordering that actually happens; the first phase is the one that has to be
    /// arranged. Both are here because the rendezvous is symmetric by construction and a change that
    /// broke the symmetry would otherwise be found by a user.
    /// </remarks>
    private async Task McpHostsAndLspAttachesAsync(SessionRegistry registry)
    {
        var started = Stopwatch.StartNew();

        var hostLauncher = new LaunchedRoslynFactory(_options, _paths!, Logger("mcp-host"));
        var hostFactory = Sharing(hostLauncher, registry, "mcp-host");

        await using var engine = Engine(hostFactory, "mcp-host");
        hostFactory.Own(engine);

        engine.Start();

        var hosted = await engine.EnsureReadyAsync(
            TimeSpan.FromSeconds(_options.ReadyTimeoutSeconds),
            Cancellation);

        Assert.That(hosted.Status, Is.EqualTo(WorkspaceLoadStatus.Ready));
        Assert.That(hosted.Engine, Is.EqualTo(OwnedRoslynEngine.Engine));
        Assert.That(hostFactory.Host, Is.Not.Null);

        _output.WriteLine($"mcp host loaded                 {started.Elapsed.TotalSeconds:F1} s");

        var attachLauncher = new LaunchedRoslynFactory(_options, _paths!, Logger("lsp-attached"));
        var attachFactory = Sharing(attachLauncher, registry, "lsp-attached");

        var attaching = Stopwatch.StartNew();
        await using var attached = LspHost.Start(attachFactory, _options, SolutionPath, Logger("lsp-attached"));

        await attached.HandshakeAsync(RootUri);
        await WaitUntilAsync(
            () => attached.Session.Gate.IsOpen,
            TimeSpan.FromSeconds(_options.ReadyTimeoutSeconds));

        _output.WriteLine($"lsp attached and ready          {attaching.Elapsed.TotalSeconds:F1} s");

        Assert.That(attachFactory.IsAttached, Is.True);
        Assert.That(attachLauncher.ProcessId, Is.Null);

        var definition = await attached.RequestAsync(
            "textDocument/definition",
            Position("Hello.Core/Caller.cs", "calculator.Compute()", offset: 12));

        Assert.That(definition.GetProperty("result").EnumerateArray(), Is.Not.Empty);

        await ByteOrderMarkSurvivesFormattingAsync(engine, attached);

        await attached.DisposeAsync();
        await hostFactory.StopHostingAsync();
    }

    /// <summary>
    /// D112, end to end over the pipe: the LSP client hosts and holds a document whose text is not the
    /// file on disk, and the attached MCP engine — which can only learn that from the host
    /// (`claude-roslyn-lsp/documentText`) — refuses to write a formatting edit Roslyn computed against
    /// the client's text, and leaves the file as it was.
    /// </summary>
    private async Task StaleClientTextIsRefusedAsync(RoslynToolContext context, LspHost host)
    {
        const string Relative = "Hello.Core/Pending.cs";
        const string OnDisk = "namespace Hello.Core\n{\n    public class Pending\n    {\n    }\n}\n";
        const string Unsaved = "namespace Hello.Core\n{\n    public class Pending\n    {\n        public int  Count;\n    }\n}\n";

        var path = Path.Combine(WorkspaceRoot, Relative.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(path, OnDisk, Cancellation);

        var uri = new Uri(path).AbsoluteUri;
        await host.NotifyAsync(
            "textDocument/didOpen",
            "{\"textDocument\":{\"uri\":\"" + uri + "\",\"languageId\":\"csharp\",\"version\":1,\"text\":"
            + JsonSerializer.Serialize(Unsaved, TestJson.String) + "}}");
        await WaitUntilAsync(() => host.Session.Documents.Find(uri) is not null, TimeSpan.FromSeconds(30));

        var failure = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(async () =>
            await EditTools.FormatCodeAsync(context, paths: [Relative], preview: true, cancellationToken: Cancellation));

        Assert.That(failure!.Message, Contains.Substring("differs from the file on disk").Using(StringComparison.Ordinal));
        Assert.That(await File.ReadAllTextAsync(path, Cancellation), Is.EqualTo(OnDisk));

        await host.NotifyAsync("textDocument/didClose", "{\"textDocument\":{\"uri\":\"" + uri + "\"}}");
        _output.WriteLine("mcp formatCode on stale client text   refused through the host, file untouched");
    }

    /// <summary>
    /// Issue #3, end to end, in the reporter's arrangement: the MCP server hosts, the LSP client
    /// attaches and opens a file that starts with a byte order mark the way Claude Code does — the mark
    /// as the text's first character (C67) — and `formatCode` on that already formatted file must
    /// find nothing to change, where it used to delete the file's first real character.
    /// </summary>
    private async Task ByteOrderMarkSurvivesFormattingAsync(OwnedRoslynEngine engine, LspHost attached)
    {
        const string Relative = "Hello.Core/Settings.cs";
        const string Text =
            "using System;\r\n\r\nnamespace Hello.Core\r\n{\r\n    public class Settings\r\n    {\r\n"
            + "        public TimeSpan Timeout { get; set; }\r\n    }\r\n}\r\n";

        var path = Path.Combine(WorkspaceRoot, Relative.Replace('/', Path.DirectorySeparatorChar));
        var bytes = (byte[])[0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Text)];
        await File.WriteAllBytesAsync(path, bytes, Cancellation);

        var uri = new Uri(path).AbsoluteUri;
        var item = "{\"uri\":\"" + uri + "\",\"languageId\":\"csharp\",\"version\":1,\"text\":"
                   + JsonSerializer.Serialize("\uFEFF" + Text, TestJson.String) + "}";

        await attached.NotifyAsync("textDocument/didOpen", "{\"textDocument\":" + item + "}");
        await WaitUntilAsync(() => engine.IsDocumentOpen(uri), TimeSpan.FromSeconds(30));

        var context = ToolContext(engine);

        foreach (var preview in new[] { true, false })
        {
            var result = await EditTools.FormatCodeAsync(
                context,
                paths: [Relative],
                preview: preview,
                cancellationToken: Cancellation);

            Assert.That(result.Status, Is.EqualTo(ToolStatus.Ok), result.Note);
            Assert.That(result.FilesChanged, Is.EqualTo(0), result.Diff);
            Assert.That(result.Note, Is.EqualTo(EditResult.NothingToDoNote));
        }

        Assert.That(await File.ReadAllBytesAsync(path, Cancellation), Is.EqualTo(bytes));
        _output.WriteLine("formatCode on an LSP-opened BOM file  nothing to change, bytes unchanged");
    }

    private SharingRoslynFactory Sharing(IRoslynConnectionFactory inner, SessionRegistry registry, string name) =>
        new(inner, registry, () => SolutionPath, TimeProvider.System, Logger(name));

    private OwnedRoslynEngine Engine(IRoslynConnectionFactory factory, string name) =>
        new(
            factory,
            _options,
            () => new WorkspaceSelection(SolutionPath, [], "the shared-engine live fixture"),
            WorkspaceRoot,
            TimeProvider.System,
            Logger(name),
            () => new RoslynBackendIdentity(RoslynServerManifest.Version));

    private RoslynToolContext ToolContext(IRoslynEngine engine)
    {
        var guard = new WorkspacePathGuard(WorkspaceRoot);

        return new RoslynToolContext(engine, _options, guard, new WorkspaceEditApplier(guard), new EditCache());
    }

    private PrefixedLogger Logger(string name) => new(new TestOutputLogger(_output, LogLevel.Information), name);

    /// <summary>A <c>textDocument/*</c> position, found by the text on the line rather than by a number.</summary>
    private string Position(string relativePath, string needle, int offset)
    {
        var path = Path.Combine(WorkspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var lines = File.ReadAllLines(path);

        for (var index = 0; index < lines.Length; index++)
        {
            var column = lines[index].IndexOf(needle, StringComparison.Ordinal);

            if (column >= 0)
            {
                var uri = new Uri(path).AbsoluteUri;

                return $$$"""
                    {"textDocument":{"uri":"{{{uri}}}"},"position":{"line":{{{index}}},"character":{{{column + offset}}}}}
                    """;
            }
        }

        Assert.Fail($"'{needle}' is not in {relativePath}; the fixture moved and this position is stale.");
        return string.Empty;
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static long WorkingSetOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Refresh();

            return process.WorkingSet64 / (1024 * 1024);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return 0;
        }
    }

    private static void Kill(int processId)
    {
        using var process = Process.GetProcessById(processId);
        process.Kill(entireProcessTree: false);
        process.WaitForExit(10_000);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan patience)
    {
        var deadline = DateTimeOffset.UtcNow + patience;

        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                Assert.Fail($"The condition did not hold within {patience.TotalSeconds:0} s.");
            }

            await Task.Delay(50, Cancellation).ConfigureAwait(false);
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(directory);

            if (SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyTree(directory, Path.Combine(destination, name));
        }
    }

    /// <summary>An <c>lsp</c> session driven from the client side over in-memory pipes.</summary>
    private sealed class LspHost : IAsyncDisposable
    {
        private readonly Lock _lock = new();
        private readonly List<JsonElement> _received = [];
        private readonly CancellationTokenSource _stopping = new();
        private readonly DuplexEnd _sessionEnd;
        private readonly DuplexEnd _clientEnd;
        private readonly LspFrameWriter _writer;
        private readonly Task<int> _run;
        private readonly Task _reading;

        private int _nextId = 100;
        private int _disposed;

        private LspHost(
            IRoslynConnectionFactory factory,
            ClaudeRoslynLspOptions options,
            string solutionPath,
            ILogger logger)
        {
            var (sessionEnd, clientEnd) = DuplexStreamPair.Create();

            _sessionEnd = sessionEnd;
            _clientEnd = clientEnd;
            _writer = new LspFrameWriter(clientEnd.Output);

            Session = new AdapterSession(
                sessionEnd.Input,
                sessionEnd.Output,
                factory,
                options,
                () => solutionPath,
                TimeProvider.System,
                logger);

            (factory as SharingRoslynFactory)?.Own(Session);

            _run = Task.Run(() => Session.RunAsync(_stopping.Token), CancellationToken.None);
            _reading = Task.Run(ReadLoopAsync, CancellationToken.None);
        }

        internal AdapterSession Session { get; }

        internal static LspHost Start(
            IRoslynConnectionFactory factory,
            ClaudeRoslynLspOptions options,
            string solutionPath,
            ILogger logger) =>
            new(factory, options, solutionPath, logger);

        /// <summary>
        /// Sends <c>initialize</c> with a real <c>rootUri</c>, then <c>initialized</c>.
        /// </summary>
        /// <remarks>
        /// The root is not decoration here: the file-watch bridge takes it from the client's
        /// handshake, and without a watcher the server-side restore's <c>project.assets.json</c>
        /// never reaches Roslyn and the load never completes at all (C48).
        /// </remarks>
        /// <param name="rootUri">The workspace root as a <c>file:</c> URI.</param>
        internal async Task HandshakeAsync(string rootUri)
        {
            const string Handshake = """
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":null,"rootUri":"#ROOT#","capabilities":{}}}
                """;

            await SendAsync(Handshake.Replace("#ROOT#", rootUri, StringComparison.Ordinal)).ConfigureAwait(false);

            await AwaitAsync(
                message => message.TryGetProperty("id", out var id)
                           && id.ValueKind == JsonValueKind.Number
                           && id.GetInt32() == 1,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            await SendAsync("""{"jsonrpc":"2.0","method":"initialized","params":{}}""").ConfigureAwait(false);
        }

        internal Task NotifyAsync(string method, string parameters) =>
            SendAsync($$"""{"jsonrpc":"2.0","method":"{{method}}","params":{{parameters}}}""");

        internal async Task<JsonElement> RequestAsync(string method, string parameters, TimeSpan? patience = null)
        {
            var id = Interlocked.Increment(ref _nextId);

            await SendAsync($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""")
                .ConfigureAwait(false);

            return await AwaitAsync(
                message => message.TryGetProperty("id", out var value)
                           && value.ValueKind == JsonValueKind.Number
                           && value.GetInt32() == id,
                patience ?? TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            // The phases dispose the host on purpose, and the `await using` disposes it again on the
            // way out. Idempotent rather than guarded at every call site, because "stop the host" is
            // exactly what both of them mean.
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _stopping.CancelAsync().ConfigureAwait(false);
            _clientEnd.CompleteOutput();

            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
            }

            await Session.DisposeAsync().ConfigureAwait(false);
            _sessionEnd.CompleteOutput();

            try
            {
                await _reading.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
            }

            _writer.Dispose();
            _stopping.Dispose();
        }

        private Task SendAsync(string message) =>
            _writer.WriteFrameAsync(Encoding.UTF8.GetBytes(message.Trim()), Cancellation).AsTask();

        private async Task<JsonElement> AwaitAsync(Func<JsonElement, bool> predicate, TimeSpan patience)
        {
            var deadline = DateTimeOffset.UtcNow + patience;

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
                    Assert.Fail($"The session answered nothing matching within {patience.TotalSeconds:0} s.");
                }

                await Task.Delay(25, Cancellation).ConfigureAwait(false);
            }
        }

        private async Task ReadLoopAsync()
        {
            var reader = new LspFrameReader(_clientEnd.Input);

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

    /// <summary>A logger that says which of the four sessions in this test a line came from.</summary>
    /// <param name="inner">Where the lines go.</param>
    /// <param name="prefix">Which half is speaking.</param>
    private sealed class PrefixedLogger(ILogger inner, string prefix) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            inner.Log(
                logLevel,
                eventId,
                state,
                exception,
                (value, error) => $"[{prefix}] {formatter(value, error)}");
        }
    }
}
