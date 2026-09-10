using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Protocol;
using ClaudeRoslynLsp.Roslyn;
using ClaudeRoslynLsp.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Live;

/// <summary>One diagnostic, reduced to what the live tests assert on.</summary>
/// <param name="Code">The diagnostic id, e.g. <c>CS0029</c>.</param>
/// <param name="Severity">The LSP severity.</param>
/// <param name="Message">The text.</param>
internal sealed record LiveDiagnostic(string Code, int Severity, string Message);

/// <summary>
/// A real <see cref="AdapterSession"/> in front of a real Roslyn, driven from the client side over
/// in-memory pipes.
/// </summary>
/// <remarks>
/// <para>
/// The client half is written here rather than reused from the build's smoke harness because it
/// needs things a handshake does not: to wait for a <em>notification</em> (the diagnostics push is
/// the whole point), to keep the latest set per URI, and to find a position in a file by text so a
/// doc-comment edit in the fixture cannot silently move every assertion.
/// </para>
/// <para>
/// The backend is <see cref="LaunchedRoslynFactory"/> — the same class the <c>lsp</c> verb uses, not
/// a test double — so what is under test is the shipped path, launcher and supervisor included.
/// </para>
/// </remarks>
internal sealed class LiveAdapterSession : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly List<JsonElement> _received = [];
    private readonly Dictionary<string, IReadOnlyList<LiveDiagnostic>> _diagnostics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _lines = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();

    private readonly AdapterLiveFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly DuplexEnd _testEnd;
    private readonly LspFrameWriter _writer;
    private readonly LaunchedRoslynFactory _factory;
    private readonly Task _reading;

    private int _nextId = 100;
    private int _version = 1;
    private bool _stopped;

    private LiveAdapterSession(AdapterLiveFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;

        var (sessionEnd, testEnd) = DuplexStreamPair.Create();

        _testEnd = testEnd;
        _writer = new LspFrameWriter(testEnd.Output);

        _factory = new LaunchedRoslynFactory(fixture.Options, fixture.Paths!, NullLogger.Instance);

        Session = new AdapterSession(
            sessionEnd.Input,
            sessionEnd.Output,
            _factory,
            fixture.Options,
            () => fixture.SolutionPath,
            TimeProvider.System,
            NullLogger.Instance);

        RunTask = Task.Run(() => Session.RunAsync(_stopping.Token));
        _reading = Task.Run(ReadLoopAsync);
    }

    /// <summary>The session under test.</summary>
    internal AdapterSession Session { get; }

    /// <summary>The session's own run loop, which completes at <c>exit</c>.</summary>
    internal Task<int> RunTask { get; }

    /// <summary>The launched Roslyn's process id, or null before it has started.</summary>
    internal int? RoslynProcessId => _factory.ProcessId;

    /// <summary>The largest working set seen for the Roslyn process (C38, re-measured).</summary>
    internal long PeakWorkingSet { get; private set; }

    /// <summary>Starts a session and completes its client-side handshake.</summary>
    /// <param name="fixture">The acquired Roslyn and the copied solution.</param>
    /// <param name="output">Where timings go.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    internal static async Task<LiveAdapterSession> StartAsync(
        AdapterLiveFixture fixture,
        ITestOutputHelper output,
        CancellationToken cancellationToken)
    {
        var session = new LiveAdapterSession(fixture, output);

        var rootUri = new Uri(fixture.WorkspaceRoot + Path.DirectorySeparatorChar).AbsoluteUri;

        await session.SendAsync(
            $$$"""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":null,
             "rootUri":"{{{rootUri}}}","capabilities":{}}
            }
            """,
            cancellationToken);

        await session.AwaitResponseAsync(1, TimeSpan.FromSeconds(10), cancellationToken);
        await session.SendAsync("""{"jsonrpc":"2.0","method":"initialized","params":{}}""", cancellationToken);

        return session;
    }

    /// <summary>Records a working-set measurement, keeping the largest.</summary>
    /// <param name="bytes">The measurement.</param>
    internal void RecordPeakWorkingSet(long bytes) => PeakWorkingSet = Math.Max(PeakWorkingSet, bytes);

    /// <summary>The <c>file:</c> URI of one fixture file.</summary>
    /// <param name="parts">Path segments below the workspace root.</param>
    internal string Uri(params string[] parts) => new Uri(PathOf(parts)).AbsoluteUri;

    /// <summary>The local path of one fixture file.</summary>
    /// <param name="parts">Path segments below the workspace root.</param>
    internal string PathOf(params string[] parts) => Path.Combine([_fixture.WorkspaceRoot, .. parts]);

    /// <summary>
    /// Finds a zero-based position by searching the file's text.
    /// </summary>
    /// <remarks>
    /// Hard-coded line numbers in a live test are a trap: the fixture carries doc comments that
    /// explain why each part of it must not be tidied away, and any edit to one would move every
    /// position silently, turning a passing test into a test that asserts about whitespace.
    /// </remarks>
    /// <param name="uri">The document, which must already be open.</param>
    /// <param name="line">The line to find, matched as a substring.</param>
    /// <param name="token">The token within it whose first character is the position.</param>
    internal (int Line, int Character) Locate(string uri, string line, string token)
    {
        string[] lines;

        lock (_gate)
        {
            lines = _lines[uri];
        }

        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].Contains(line, StringComparison.Ordinal))
            {
                continue;
            }

            var character = lines[index].IndexOf(token, StringComparison.Ordinal);

            if (character >= 0)
            {
                return (index, character);
            }
        }

        Assert.Fail($"'{line}' is not in {uri}");
        return default;
    }

    /// <summary>Opens a document, sending its real text.</summary>
    /// <param name="uri">The document.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    internal async Task OpenAsync(string uri, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(new Uri(uri).LocalPath, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _lines[uri] = text.ReplaceLineEndings("\n").Split('\n');
        }

        var escaped = JsonEncodedText.Encode(text);

        await SendAsync(
            $$$"""
            {"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":
             {"uri":"{{{uri}}}","languageId":"csharp","version":1,"text":"{{{escaped}}}"}
            }}
            """,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces a document's whole text (D13: full synchronisation).</summary>
    /// <param name="uri">The document.</param>
    /// <param name="text">The new text.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    internal async Task ChangeAsync(string uri, string text, CancellationToken cancellationToken)
    {
        int version;

        lock (_gate)
        {
            _lines[uri] = text.ReplaceLineEndings("\n").Split('\n');
            version = ++_version;
        }

        var escaped = JsonEncodedText.Encode(text);

        await SendAsync(
            $$$"""
            {"jsonrpc":"2.0","method":"textDocument/didChange","params":{
             "textDocument":{"uri":"{{{uri}}}","version":{{{version}}}},
             "contentChanges":[{"text":"{{{escaped}}}"}]}
            }
            """,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends one request and waits for its answer, failing the test on an error response.</summary>
    /// <param name="method">The method.</param>
    /// <param name="parameters">The already-rendered <c>params</c>.</param>
    /// <param name="budget">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    internal async Task<JsonElement> RequestAsync(
        string method,
        string parameters,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        int id;

        lock (_gate)
        {
            id = ++_nextId;
        }

        await SendAsync(
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"{{{method}}}","params":{{{parameters.Trim()}}} }""",
            cancellationToken).ConfigureAwait(false);

        var response = await AwaitResponseAsync(id, budget, cancellationToken).ConfigureAwait(false);

        if (response.TryGetProperty("error", out var error))
        {
            Assert.Fail($"{method} failed: {error.GetRawText()}");
        }

        return response.GetProperty("result");
    }

    /// <summary>Waits until the readiness gate reports the workspace loaded.</summary>
    /// <param name="budget">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    internal async Task WaitForWorkspaceAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        await WaitAsync(
            () => Session.Gate.State is ReadinessState.ProjectsLoaded,
            budget,
            () => $"the workspace was still {Session.Gate.State} after {budget.TotalSeconds:F0} s",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits until a document's published diagnostics satisfy a predicate.</summary>
    /// <param name="uri">The document.</param>
    /// <param name="predicate">What is being waited for.</param>
    /// <param name="budget">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    internal async Task WaitForDiagnosticsAsync(
        string uri,
        Func<IReadOnlyList<LiveDiagnostic>, bool> predicate,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        await WaitAsync(
            () => LatestDiagnostics(uri) is { } published && predicate(published),
            budget,
            () =>
            {
                var published = LatestDiagnostics(uri);

                return published is null
                    ? $"nothing was ever published for {uri}"
                    : $"the last set for {uri} was [{string.Join(", ", published.Select(x => x.Code))}]";
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The most recent diagnostics published for a document, or null when there are none.</summary>
    /// <param name="uri">The document.</param>
    internal IReadOnlyList<LiveDiagnostic>? LatestDiagnostics(string uri)
    {
        lock (_gate)
        {
            return _diagnostics.GetValueOrDefault(uri);
        }
    }

    /// <summary>Sends <c>shutdown</c> and <c>exit</c> and returns the session's exit code.</summary>
    /// <param name="budget">How long the whole thing may take.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    internal async Task<int> ShutdownAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        int id;

        lock (_gate)
        {
            id = ++_nextId;
            _stopped = true;
        }

        await SendAsync($$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"shutdown"}""", cancellationToken)
            .ConfigureAwait(false);

        await AwaitResponseAsync(id, budget, cancellationToken).ConfigureAwait(false);
        await SendAsync("""{"jsonrpc":"2.0","method":"exit"}""", cancellationToken).ConfigureAwait(false);

        return await RunTask.WaitAsync(budget, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_stopped)
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
            _testEnd.CompleteOutput();
        }

        try
        {
            await RunTask.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            _output.WriteLine("the session did not stop within 20 s");
        }

        await Session.DisposeAsync().ConfigureAwait(false);
        _writer.Dispose();
        _stopping.Dispose();

        try
        {
            await _reading.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or IOException)
        {
        }
    }

    private async Task SendAsync(string message, CancellationToken cancellationToken) =>
        await _writer.WriteFrameAsync(Encoding.UTF8.GetBytes(message.Trim()), cancellationToken)
            .ConfigureAwait(false);

    private async Task<JsonElement> AwaitResponseAsync(int id, TimeSpan budget, CancellationToken cancellationToken)
    {
        JsonElement? found = null;

        await WaitAsync(
            () =>
            {
                found = TryFindResponse(id);
                return found is not null;
            },
            budget,
            () => $"request {id} was never answered",
            cancellationToken).ConfigureAwait(false);

        return found!.Value;
    }

    private JsonElement? TryFindResponse(int id)
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

    private async Task WaitAsync(
        Func<bool> condition,
        TimeSpan budget,
        Func<string> describeFailure,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + budget;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            SampleWorkingSet();
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        Assert.Fail(describeFailure());
    }

    /// <summary>Samples the Roslyn process's working set while waiting, for the C38 re-measure.</summary>
    private void SampleWorkingSet()
    {
        if (RoslynProcessId is not { } pid)
        {
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            RecordPeakWorkingSet(process.PeakWorkingSet64);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // It exited, which the recovery phase does on purpose.
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

                Record(element);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                              or LspProtocolException or JsonException)
        {
        }
    }

    /// <summary>Keeps the latest published set per URI, and echoes the server's messages.</summary>
    private void Record(JsonElement message)
    {
        if (!message.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
        {
            return;
        }

        switch (method.GetString())
        {
            case "textDocument/publishDiagnostics":
                RecordDiagnostics(message.GetProperty("params"));
                break;

            case "window/logMessage":
            case "window/showMessage":
                // Echoed so a failing run's output carries what the adapter was telling its client
                // at the time, which is the first thing anybody reads.
                _output.WriteLine("[client] " + message.GetProperty("params").GetProperty("message").GetString());
                break;

            default:
                break;
        }
    }

    private void RecordDiagnostics(JsonElement parameters)
    {
        var uri = parameters.GetProperty("uri").GetString();

        if (uri is null)
        {
            return;
        }

        var published = parameters.GetProperty("diagnostics").EnumerateArray()
            .Select(x => new LiveDiagnostic(
                x.TryGetProperty("code", out var code) ? code.ToString() : "(none)",
                x.TryGetProperty("severity", out var severity) ? severity.GetInt32() : 1,
                x.TryGetProperty("message", out var text) ? text.GetString() ?? string.Empty : string.Empty))
            .ToArray();

        lock (_gate)
        {
            _diagnostics[uri] = published;
        }
    }
}
