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
/// Attach-or-host over a <em>real</em> named pipe and a real session directory (D92).
/// </summary>
/// <remarks>
/// <para>
/// Two <see cref="SharingRoslynFactory"/> instances in one process stand in for the two processes
/// Claude Code starts at the same instant. The pipe, the file and the lock are all the real ones,
/// because each of them is exactly the kind of thing a fake would get subtly right and the platform
/// would get differently wrong — a pipe name's length, a <see cref="FileShare.None"/> handle, a
/// delete that races a read.
/// </para>
/// <para>
/// The load-bearing assertion in the first test is the negative one: the attaching factory's own
/// backend was never started. That is what "one Roslyn per solution" means, stated in a way that
/// cannot pass by accident.
/// </para>
/// </remarks>
public class SharingRoslynFactoryTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSecondProcessAttachesToTheFirstsRoslynAndStartsNoneOfItsOwn()
    {
        using var home = TempWorkspace.Create("share-two");
        var registry = SessionRegistry.ForHome(home.Root, NullLogger.Instance);
        var solution = home.Path_("Fixture.slnx");

        await using var host = await SharedHostSession.StartAsync(registry, solution);

        var key = SessionRegistry.KeyFor(solution);
        var published = registry.TryRead(key);

        Assert.NotNull(published);
        Assert.Equal(Environment.ProcessId, published.ProcessId);
        Assert.Equal(ServerVersion.Value, published.Version);
        Assert.Equal(solution, published.Solution);
        Assert.NotNull(host.Factory.Host);

        // The second process. Its own launcher is a scripted backend that must never be asked for a
        // connection, which is the whole claim of this work package.
        var attaching = new InProcessFakeRoslynFactory(NullLogger.Instance);

        var factory = new SharingRoslynFactory(
            attaching,
            registry,
            () => solution,
            TimeProvider.System,
            NullLogger.Instance);

        await using var connection = await factory.ConnectAsync(Cancellation);

        Assert.True(connection.Attached);
        Assert.Equal(Environment.ProcessId, connection.HostProcessId);
        Assert.True(factory.IsAttached);
        Assert.Null(factory.Host);
        Assert.Null(attaching.Server);

        await using var peer = new AttachedConnection(connection);

        var initialized = await peer.RequestAsync(1, "initialize", "{}");

        Assert.Contains(
            "shared engine",
            initialized.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString()!,
            StringComparison.Ordinal);

        // Readiness reaches the attached process over the pipe, so its own gate opens on the host's
        // load rather than on a load it never started (D89).
        host.Backend.CompleteProjectInitialization();

        await peer.AwaitFirstAsync(message =>
            message.TryGetProperty("method", out var method)
            && method.GetString() == "workspace/projectInitializationComplete");

        var answered = await peer.RequestAsync(
            2,
            "textDocument/definition",
            """{"textDocument":{"uri":"file:///w/Fixture/Fixture.Core/Calculator.cs"},"position":{"line":7,"character":18}}""");

        Assert.Single(answered.GetProperty("result").EnumerateArray());
        Assert.Null(attaching.Server);
    }

    /// <summary>
    /// A machine that rebooted leaves a file naming a process that no longer exists; the reader
    /// removes it and takes over (D86).
    /// </summary>
    [Fact]
    public async Task ASessionNamingADeadProcessIsReplacedByTheProcessThatFoundIt()
    {
        using var home = TempWorkspace.Create("share-stale");
        var registry = SessionRegistry.ForHome(home.Root, NullLogger.Instance);
        var solution = home.Path_("Fixture.slnx");
        var key = SessionRegistry.KeyFor(solution);

        registry.Write(key, new SharedSession(
            int.MaxValue - 7,
            ServerVersion.Value,
            SharedSession.PipeTransport,
            "claude-roslyn-lsp-share-nobody",
            solution,
            DateTimeOffset.UtcNow.AddHours(-3)));

        await using var host = await SharedHostSession.StartAsync(registry, solution);

        var published = registry.TryRead(key);

        Assert.NotNull(published);
        Assert.Equal(Environment.ProcessId, published.ProcessId);
        Assert.NotEqual("claude-roslyn-lsp-share-nobody", published.PipeName);
        Assert.NotNull(host.Factory.Host);
    }

    /// <summary>
    /// The pid check is a filter, not a proof: a reused process id passes it and the connect is what
    /// catches it.
    /// </summary>
    [Fact]
    public async Task ASessionWhosePipeIsNotThereIsRemovedAndTheReaderHosts()
    {
        using var home = TempWorkspace.Create("share-nopipe");
        var registry = SessionRegistry.ForHome(home.Root, NullLogger.Instance);
        var solution = home.Path_("Fixture.slnx");
        var key = SessionRegistry.KeyFor(solution);

        // This process is alive and is not serving this pipe, which is exactly what a reused pid
        // looks like from the outside.
        registry.Write(key, new SharedSession(
            Environment.ProcessId,
            ServerVersion.Value,
            SharedSession.PipeTransport,
            "claude-roslyn-lsp-share-" + Guid.NewGuid().ToString("N"),
            solution,
            DateTimeOffset.UtcNow));

        await using var host = await SharedHostSession.StartAsync(registry, solution);

        var published = registry.TryRead(key);

        Assert.NotNull(published);
        Assert.NotNull(host.Factory.Host);
        Assert.Equal(host.Factory.Host.PipeName, published.PipeName);
    }

    /// <summary>
    /// Misc-files mode (C28): with no solution there is nothing two processes could agree they are
    /// both looking at, so nothing is published and the inner factory is used unchanged.
    /// </summary>
    [Fact]
    public async Task WithNoSolutionNothingIsPublishedAndNothingIsShared()
    {
        using var home = TempWorkspace.Create("share-none");
        var registry = SessionRegistry.ForHome(home.Root, NullLogger.Instance);
        var inner = new InProcessFakeRoslynFactory(NullLogger.Instance);

        var factory = new SharingRoslynFactory(
            inner,
            registry,
            static () => null,
            TimeProvider.System,
            NullLogger.Instance);

        await using var connection = await factory.ConnectAsync(Cancellation);

        Assert.False(connection.Attached);
        Assert.Null(factory.Host);
        Assert.Null(factory.Key);
        Assert.Empty(registry.List());
    }

    /// <summary>
    /// A host that goes away withdraws its file, so the next process to look hosts rather than
    /// spending an attach cycle on a pipe that is being torn down.
    /// </summary>
    [Fact]
    public async Task DisposingTheHostsConnectionWithdrawsItsSessionFile()
    {
        using var home = TempWorkspace.Create("share-withdraw");
        var registry = SessionRegistry.ForHome(home.Root, NullLogger.Instance);
        var solution = home.Path_("Fixture.slnx");
        var key = SessionRegistry.KeyFor(solution);

        var host = await SharedHostSession.StartAsync(registry, solution);

        Assert.NotNull(registry.TryRead(key));

        await host.DisposeAsync();

        Assert.Null(registry.TryRead(key));
        Assert.False(File.Exists(registry.FileFor(key)));
    }

    /// <summary>
    /// One session, hosting through a real <see cref="SharingRoslynFactory"/> over the scripted
    /// backend.
    /// </summary>
    private sealed class SharedHostSession : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly DuplexEnd _sessionEnd;
        private readonly DuplexEnd _clientEnd;
        private readonly LspFrameWriter _writer;
        private readonly InProcessFakeRoslynFactory _inner;
        private readonly Task<int> _run;
        private readonly Task _draining;

        private SharedHostSession(SessionRegistry registry, string solution)
        {
            var (sessionEnd, clientEnd) = DuplexStreamPair.Create();

            _sessionEnd = sessionEnd;
            _clientEnd = clientEnd;
            _writer = new LspFrameWriter(clientEnd.Output);
            _inner = new InProcessFakeRoslynFactory(
                NullLogger.Instance,
                FakeRoslynScript.Roslyn512Startup(projectLoadDelay: null));

            Factory = new SharingRoslynFactory(
                _inner,
                registry,
                () => solution,
                TimeProvider.System,
                NullLogger.Instance);

            Session = new AdapterSession(
                sessionEnd.Input,
                sessionEnd.Output,
                Factory,
                ClaudeRoslynLspOptions.FromEnvironment(static _ => null),
                () => solution,
                TimeProvider.System,
                NullLogger.Instance);

            Factory.Own(Session);

            _run = Task.Run(() => Session.RunAsync(_stopping.Token), CancellationToken.None);
            _draining = Task.Run(DrainAsync, CancellationToken.None);
        }

        internal AdapterSession Session { get; }

        internal SharingRoslynFactory Factory { get; }

        internal FakeRoslynServer Backend => _inner.Server!;

        internal static async Task<SharedHostSession> StartAsync(SessionRegistry registry, string solution)
        {
            var host = new SharedHostSession(registry, solution);

            await host.SendAsync(
                """
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":4242,"rootUri":"file:///w/Fixture","capabilities":{}}}
                """);

            await host.SendAsync("""{"jsonrpc":"2.0","method":"initialized","params":{}}""");

            await WaitUntilAsync(() => host.Factory.Host is not null && host._inner.Server is not null);
            await WaitUntilAsync(() =>
                host.Backend.ReceivedMethods.Contains("solution/open", StringComparer.Ordinal));

            return host;
        }

        public async ValueTask DisposeAsync()
        {
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
            await Factory.StopHostingAsync().ConfigureAwait(false);

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

        private Task SendAsync(string message) =>
            _writer.WriteFrameAsync(Encoding.UTF8.GetBytes(message.Trim()), Cancellation).AsTask();

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

    /// <summary>The attaching process's side of the pipe, driven as an LSP client.</summary>
    private sealed class AttachedConnection : IAsyncDisposable
    {
        private readonly Lock _lock = new();
        private readonly List<JsonElement> _received = [];
        private readonly LspFrameWriter _writer;
        private readonly Task _reading;

        internal AttachedConnection(RoslynConnection connection)
        {
            _writer = new LspFrameWriter(connection.Output);
            _reading = Task.Run(() => ReadLoopAsync(connection.Input), CancellationToken.None);
        }

        internal async Task<JsonElement> RequestAsync(int id, string method, string parameters)
        {
            await _writer.WriteFrameAsync(
                    Encoding.UTF8.GetBytes(
                        $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}"""),
                    Cancellation)
                .ConfigureAwait(false);

            return await AwaitFirstAsync(message =>
                message.TryGetProperty("id", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.GetInt32() == id).ConfigureAwait(false);
        }

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
                    Assert.Fail($"The attached connection saw nothing matching within {Patience.TotalSeconds:0} s.");
                }

                await Task.Delay(5, Cancellation).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _reading.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>Polls until a condition holds, with a diagnosis rather than a hang when it does not.</summary>
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
}
