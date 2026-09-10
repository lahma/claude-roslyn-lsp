using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Cli;
using ClaudeRoslynLsp.Lsp;
using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Lsp;

/// <summary>
/// The <c>lsp</c> verb driven end to end over a pair of in-memory streams.
/// </summary>
/// <remarks>
/// <para>
/// This is the same exchange <c>SmokeTest</c> runs against the published Native AOT binary, minus the
/// process. Having it here as well is not duplication: this one fails in two seconds with a stack
/// trace during development, and the smoke test proves the same thing survives AOT compilation on a
/// release runner. The rules under test — echo the id exactly, answer every request, and let
/// <c>shutdown</c> decide the exit code — are the ones whose violation produces a client that hangs
/// rather than an error anybody can read.
/// </para>
/// <para>
/// Streams rather than a real pipe, because a <see cref="MemoryStream"/> makes the exchange
/// deterministic: the script is written in full before the server starts, so there is no timing for a
/// test to be flaky about. The partial-read behaviour a real pipe has is covered where it belongs, in
/// <see cref="Tests.Protocol.LspFramingTests"/>.
/// </para>
/// </remarks>
public class LspStubServerTests
{
    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":null,"capabilities":{}}}""";

    private const string Initialized = """{"jsonrpc":"2.0","method":"initialized","params":{}}""";

    private const string Shutdown = """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""";

    private const string Exit = """{"jsonrpc":"2.0","method":"exit"}""";

    /// <summary>
    /// The test's own cancellation token, threaded through every awaited call.
    /// </summary>
    /// <remarks>
    /// xunit.v3 requires it (xUnit1051): a hung await inside a test would otherwise ignore the
    /// runner's cancellation and keep the whole run alive until its outer timeout.
    /// </remarks>
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InitializeAnswersWithThisServersIdentityAndCapabilities()
    {
        var run = await RunAsync(Initialize, Shutdown, Exit);

        var result = Response(run, id: 1).GetProperty("result");
        var serverInfo = result.GetProperty("serverInfo");

        Assert.Equal(ServerVersion.Name, serverInfo.GetProperty("name").GetString());
        Assert.Equal(ServerVersion.Value, serverInfo.GetProperty("version").GetString());

        var sync = result.GetProperty("capabilities").GetProperty("textDocumentSync");

        Assert.True(sync.GetProperty("openClose").GetBoolean());

        // 1 is TextDocumentSyncKind.Full. Incremental (2) would ask the client for range edits, which
        // the document mirror would then have to replay perfectly to survive a Roslyn restart.
        Assert.Equal(1, sync.GetProperty("change").GetInt32());
        Assert.False(sync.GetProperty("save").GetProperty("includeText").GetBoolean());
    }

    /// <summary>
    /// JSON-RPC allows a string id, and a response has to echo the one it was given <em>in the form
    /// it was given</em>. Answering <c>"7"</c> with <c>7</c> is a correlation failure, and the client
    /// reports it as a request that never came back.
    /// </summary>
    [Fact]
    public async Task ARequestIdIsEchoedInItsOriginalJsonType()
    {
        var run = await RunAsync(
            """{"jsonrpc":"2.0","id":"init-1","method":"initialize","params":{}}""",
            Exit);

        var response = Assert.Single(run.Responses);
        var id = response.GetProperty("id");

        Assert.Equal(JsonValueKind.String, id.ValueKind);
        Assert.Equal("init-1", id.GetString());
    }

    /// <summary>
    /// <c>shutdown</c> answers with a result that is present and null. A response carrying neither
    /// <c>result</c> nor <c>error</c> is not a response at all, and a client waits for the real one.
    /// </summary>
    [Fact]
    public async Task ShutdownAnswersWithAPresentNullResult()
    {
        var run = await RunAsync(Initialize, Shutdown, Exit);

        var response = Response(run, id: 2);

        Assert.True(response.TryGetProperty("result", out var result), "shutdown answered without a result member.");
        Assert.Equal(JsonValueKind.Null, result.ValueKind);
        Assert.False(response.TryGetProperty("error", out _));
        Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
    }

    [Fact]
    public async Task ExitAfterShutdownIsASuccessfulExit()
    {
        var run = await RunAsync(Initialize, Initialized, Shutdown, Exit);

        Assert.Equal(CliDispatcher.ExitSuccess, run.ExitCode);
    }

    /// <summary>
    /// The specification is explicit: <c>exit</c> without a preceding <c>shutdown</c> exits 1. It is
    /// how a client learns that the server was killed rather than asked to stop, and it is the sort of
    /// detail that gets "simplified" to a uniform zero by somebody who has not read the spec.
    /// </summary>
    [Fact]
    public async Task ExitWithoutShutdownIsAFailedExit()
    {
        var run = await RunAsync(Initialize, Exit);

        Assert.Equal(CliDispatcher.ExitFailure, run.ExitCode);
    }

    /// <summary>
    /// A client that was killed closes the stream without saying anything. The same rule applies:
    /// orderly only if <c>shutdown</c> came first.
    /// </summary>
    [Theory]
    [InlineData(false, CliDispatcher.ExitFailure)]
    [InlineData(true, CliDispatcher.ExitSuccess)]
    public async Task EndOfStreamFollowsTheSameExitCodeRule(bool shutdownFirst, int expected)
    {
        var run = shutdownFirst
            ? await RunAsync(Initialize, Shutdown)
            : await RunAsync(Initialize);

        Assert.Equal(expected, run.ExitCode);
    }

    /// <summary>
    /// Every request gets an answer, including the ones this build cannot serve. Silence is the worst
    /// available option: LSP clients wait on an outstanding request, and Claude Code's own client
    /// gives up on the server entirely after a few unanswered retries.
    /// </summary>
    [Fact]
    public async Task AnUnimplementedRequestIsRefusedRatherThanIgnored()
    {
        var run = await RunAsync(
            Initialize,
            """{"jsonrpc":"2.0","id":9,"method":"textDocument/definition","params":{}}""",
            Shutdown,
            Exit);

        var error = Response(run, id: 9).GetProperty("error");

        Assert.Equal(JsonRpc.MethodNotFound, error.GetProperty("code").GetInt32());
        Assert.Contains("textDocument/definition", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>A notification is unanswerable by definition, so the wire stays quiet.</summary>
    [Fact]
    public async Task NotificationsAreIgnoredSilently()
    {
        var run = await RunAsync(
            Initialized,
            """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{}}""",
            """{"jsonrpc":"2.0","method":"$/setTrace","params":{"value":"verbose"}}""",
            Shutdown,
            Exit);

        // Only the shutdown answer.
        var response = Assert.Single(run.Responses);
        Assert.Equal(2, response.GetProperty("id").GetInt32());
    }

    /// <summary>
    /// A well-framed message with an unreadable body is answerable — the frame boundary is still
    /// known — so the connection survives it. The id is null because there was no readable one.
    /// </summary>
    [Fact]
    public async Task AMessageThatIsNotValidJsonIsAnsweredUnderANullId()
    {
        var run = await RunAsync("{ this is not json", Shutdown, Exit);

        Assert.Equal(2, run.Responses.Count);

        var parseError = run.Responses[0];

        Assert.Equal(JsonValueKind.Null, parseError.GetProperty("id").ValueKind);
        Assert.Equal(JsonRpc.ParseError, parseError.GetProperty("error").GetProperty("code").GetInt32());

        // And the server kept going: the shutdown after it was still answered.
        Assert.Equal(CliDispatcher.ExitSuccess, run.ExitCode);
    }

    /// <summary>
    /// Losing framing is not recoverable — there is no way to find where the next message begins — so
    /// the server stops rather than answering from the middle of a stream.
    /// </summary>
    [Fact]
    public async Task AStreamThatIsNotFramedStopsTheServer()
    {
        var input = new MemoryStream("Content-Length: not-a-number\r\n\r\n{}"u8.ToArray());
        using var output = new MemoryStream();

        using var server = new LspStubServer(input, output, NullLogger<LspStubServer>.Instance);

        Assert.Equal(CliDispatcher.ExitFailure, await server.RunAsync(Cancellation));
        Assert.Empty(output.ToArray());
    }

    /// <summary>Finds the response to one request id, failing with the whole exchange if it is absent.</summary>
    private static JsonElement Response(StubRun run, int id)
    {
        foreach (var response in run.Responses)
        {
            if (response.TryGetProperty("id", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.GetInt32() == id)
            {
                return response;
            }
        }

        Assert.Fail($"No response with id {id}. The server wrote: {string.Join(", ", run.Responses)}");
        return default;
    }

    /// <summary>
    /// Writes <paramref name="messages"/> as frames, runs the stub against them, and reads back the
    /// frames it wrote.
    /// </summary>
    private static async Task<StubRun> RunAsync(params string[] messages)
    {
        using var script = new MemoryStream();

        using (var scriptWriter = new LspFrameWriter(script))
        {
            foreach (var message in messages)
            {
                await scriptWriter.WriteFrameAsync(Encoding.UTF8.GetBytes(message), Cancellation);
            }
        }

        var input = new MemoryStream(script.ToArray());
        using var output = new MemoryStream();

        int exitCode;

        using (var server = new LspStubServer(input, output, NullLogger<LspStubServer>.Instance))
        {
            exitCode = await server.RunAsync(Cancellation);
        }

        var reader = new LspFrameReader(new MemoryStream(output.ToArray()));
        var responses = new List<JsonElement>();

        while (await reader.ReadFrameAsync(Cancellation) is { } body)
        {
            using var document = JsonDocument.Parse(body);
            responses.Add(document.RootElement.Clone());
        }

        return new StubRun(exitCode, responses);
    }

    private sealed record StubRun(int ExitCode, IReadOnlyList<JsonElement> Responses);
}
