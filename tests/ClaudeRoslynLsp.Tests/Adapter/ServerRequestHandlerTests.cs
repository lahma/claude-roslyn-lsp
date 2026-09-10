using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Protocol;
using ClaudeRoslynLsp.Tests.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// Every row of the table that answers Roslyn on the client's behalf.
/// </summary>
/// <remarks>
/// The rule the whole table serves is that <b>Roslyn is never left waiting</b>: a server-to-client
/// request that goes unanswered stalls the handler that issued it, and several of those handlers are
/// on the solution-load path. So every request here produces something, including the ones nobody
/// has heard of.
/// </remarks>
public class ServerRequestHandlerTests
{
    [Fact]
    public void RegistrationsAreRecordedAndAcknowledgedWithNull()
    {
        var harness = new HandlerHarness();

        var outcome = harness.Handle("""
            {"jsonrpc":"2.0","id":4,"method":"client/registerCapability","params":{"registrations":[{"id":"a","method":"textDocument/diagnostic","registerOptions":{"identifier":"syntax"}}]}}
            """);

        Assert.Equal(ServerMessageOutcome.AnsweredNull, outcome);
        Assert.Equal(1, harness.Registrations.Count);
        Assert.Equal("syntax", Assert.Single(harness.Registrations.DiagnosticSources).Identifier);

        var answer = harness.SingleAnswer();

        Assert.Equal(4, answer.GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("result").ValueKind);
    }

    [Fact]
    public void UnregistrationsAreHonouredAndAcknowledged()
    {
        var harness = new HandlerHarness();

        harness.Handle("""
            {"jsonrpc":"2.0","id":1,"method":"client/registerCapability","params":{"registrations":[{"id":"a","method":"textDocument/diagnostic","registerOptions":{"identifier":"syntax"}}]}}
            """);

        var outcome = harness.Handle("""
            {"jsonrpc":"2.0","id":2,"method":"client/unregisterCapability","params":{"unregisterations":[{"id":"a","method":"textDocument/diagnostic"}]}}
            """);

        Assert.Equal(ServerMessageOutcome.AnsweredNull, outcome);
        Assert.Equal(0, harness.Registrations.Count);
    }

    [Fact]
    public void ConfigurationIsAnsweredByTheAdapterRatherThanForwarded()
    {
        var harness = new HandlerHarness();

        var outcome = harness.Handle("""
            {"jsonrpc":"2.0","id":3,"method":"workspace/configuration","params":{"items":[{"section":"projects.dotnet_enable_automatic_restore"}]}}
            """);

        Assert.Equal(ServerMessageOutcome.AnsweredWithResult, outcome);
        Assert.Empty(harness.Forwarded);

        var result = harness.SingleAnswer().GetProperty("result");

        Assert.Equal(JsonValueKind.True, result[0].ValueKind);
    }

    /// <summary>
    /// Accepted on the client's behalf, because Claude Code refuses it with <c>-32601</c> and the
    /// solution-load progress stream is the only account the load gives of itself (C31).
    /// </summary>
    [Fact]
    public void WorkDoneProgressCreateIsAcceptedAndTheStreamIsConsumed()
    {
        var harness = new HandlerHarness();

        Assert.Equal(ServerMessageOutcome.AnsweredNull, harness.Handle("""
            {"jsonrpc":"2.0","id":5,"method":"window/workDoneProgress/create","params":{"token":"tok-1"}}
            """));

        Assert.Equal(1, harness.Progress.ActiveCount);

        Assert.Equal(ServerMessageOutcome.Consumed, harness.Handle("""
            {"jsonrpc":"2.0","method":"$/progress","params":{"token":"tok-1","value":{"kind":"begin","title":"Loading Fixture.slnx...","percentage":0}}}
            """));

        Assert.Equal("Loading Fixture.slnx...", harness.Progress.CurrentTitle);

        // Never forwarded: the client says the token does not exist.
        Assert.Empty(harness.Forwarded);

        Assert.Equal(ServerMessageOutcome.Consumed, harness.Handle("""
            {"jsonrpc":"2.0","method":"$/progress","params":{"token":"tok-1","value":{"kind":"end","message":"Loaded"}}}
            """));

        Assert.Equal(0, harness.Progress.ActiveCount);
    }

    /// <summary>The one notification the whole adapter waits for. It carries no <c>params</c> at all.</summary>
    [Fact]
    public void ProjectInitializationCompleteOpensTheGate()
    {
        var harness = new HandlerHarness();
        harness.Gate.Start();
        harness.Gate.MarkRoslynInitialized();

        var outcome = harness.Handle("""{"jsonrpc":"2.0","method":"workspace/projectInitializationComplete"}""");

        Assert.Equal(ServerMessageOutcome.Consumed, outcome);
        Assert.Equal(ReadinessState.ProjectsLoaded, harness.Gate.State);
    }

    /// <summary>
    /// Refresh requests are acknowledged immediately and reported as an event. Acting on them is
    /// WP4's; leaving one unanswered would block the handler that asked, which is not.
    /// </summary>
    [Theory]
    [InlineData("workspace/diagnostic/refresh")]
    [InlineData("workspace/codeLens/refresh")]
    [InlineData("workspace/inlayHint/refresh")]
    [InlineData("workspace/semanticTokens/refresh")]
    public void ARefreshRequestIsAcknowledgedAndAnnounced(string method)
    {
        var harness = new HandlerHarness();
        var seen = new List<string>();

        harness.Handler.RefreshRequested += seen.Add;

        Assert.Equal(ServerMessageOutcome.AnsweredNull, harness.Handle($$"""
            {"jsonrpc":"2.0","id":8,"method":"{{method}}"}
            """));

        Assert.Equal([method], seen);
        Assert.Equal(JsonValueKind.Null, harness.SingleAnswer().GetProperty("result").ValueKind);
    }

    [Fact]
    public void ShowMessageCrossesToTheClientUnchanged()
    {
        var harness = new HandlerHarness();
        const string Message = """{"jsonrpc":"2.0","method":"window/showMessage","params":{"type":1,"message":"Project load failed"}}""";

        Assert.Equal(ServerMessageOutcome.ForwardedToClient, harness.Handle(Message));
        Assert.Equal(Message, Encoding.UTF8.GetString(Assert.Single(harness.Forwarded)));
    }

    /// <summary>
    /// Roslyn's twenty-odd startup log lines are its only log channel at the default level (C6).
    /// Unprefixed they would read in the user's transcript as though the adapter had said them, and
    /// the difference matters the moment something is wrong.
    /// </summary>
    [Fact]
    public void LogMessagesReachTheClientMarkedAsRoslynsOwn()
    {
        var harness = new HandlerHarness();

        Assert.Equal(ServerMessageOutcome.ForwardedToClient, harness.Handle("""
            {"jsonrpc":"2.0","method":"window/logMessage","params":{"type":3,"message":"[solution/open] Loading Fixture.slnx..."}}
            """));

        using var document = JsonDocument.Parse(Assert.Single(harness.Forwarded));
        var parameters = document.RootElement.GetProperty("params");

        Assert.Equal("window/logMessage", document.RootElement.GetProperty("method").GetString());
        Assert.Equal(3, parameters.GetProperty("type").GetInt32());
        Assert.Equal("[roslyn] [solution/open] Loading Fixture.slnx...", parameters.GetProperty("message").GetString());
    }

    /// <summary>
    /// A modal question in a client with no user in front of it. Answered null (dismissed) and logged,
    /// so the text is not lost — and, above all, answered, so Roslyn is not left waiting.
    /// </summary>
    [Theory]
    [InlineData("window/showMessageRequest")]
    [InlineData("window/_roslyn_showToast")]
    public void AServerPromptIsDismissedRatherThanForwarded(string method)
    {
        var harness = new HandlerHarness();

        Assert.Equal(ServerMessageOutcome.AnsweredNull, harness.Handle($$$"""
            {"jsonrpc":"2.0","id":9,"method":"{{{method}}}","params":{"type":3,"message":"Restore this project?","actions":[{"title":"Restore"}]}}
            """));

        Assert.Empty(harness.Forwarded);
        Assert.Equal(JsonValueKind.Null, harness.SingleAnswer().GetProperty("result").ValueKind);
    }

    [Fact]
    public void AWorkspaceEditReachesAClientThatSaidItAppliesThem()
    {
        var harness = new HandlerHarness();
        harness.Handler.ClientAppliesEdits = true;

        Assert.Equal(ServerMessageOutcome.ForwardedToClient, harness.Handle("""
            {"jsonrpc":"2.0","id":10,"method":"workspace/applyEdit","params":{"edit":{"documentChanges":[]}}}
            """));

        Assert.Single(harness.Forwarded);
        Assert.Empty(harness.Answers);
    }

    /// <summary>
    /// A refusal has to be a well-formed <c>{applied:false}</c> rather than an error: that is the
    /// protocol's own way of saying "the client declined", which is exactly what happened.
    /// </summary>
    [Fact]
    public void AWorkspaceEditIsDeclinedWhenTheClientCannotApplyOne()
    {
        var harness = new HandlerHarness { };

        Assert.Equal(ServerMessageOutcome.AnsweredWithResult, harness.Handle("""
            {"jsonrpc":"2.0","id":10,"method":"workspace/applyEdit","params":{"edit":{"documentChanges":[]}}}
            """));

        Assert.Empty(harness.Forwarded);

        var result = harness.SingleAnswer().GetProperty("result");

        Assert.False(result.GetProperty("applied").GetBoolean());
        Assert.Contains("applyEdit", result.GetProperty("failureReason").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Telemetry is Roslyn's business. A client that logged it would put the user's solution structure
    /// into a transcript nobody asked to publish.
    /// </summary>
    [Fact]
    public void TelemetryIsDropped()
    {
        var harness = new HandlerHarness();

        Assert.Equal(ServerMessageOutcome.Dropped, harness.Handle("""
            {"jsonrpc":"2.0","method":"telemetry/event","params":{"EventName":"vs/ide/lsp"}}
            """));

        Assert.Empty(harness.Forwarded);
        Assert.Empty(harness.Answers);
    }

    [Fact]
    public void WorkspaceFoldersAreAnsweredFromTheClientsRoot()
    {
        var harness = new HandlerHarness();
        harness.Handler.WorkspaceFolders = [new WorkspaceFolder { Uri = "file:///w/Fixture", Name = "Fixture" }];

        Assert.Equal(ServerMessageOutcome.AnsweredWithResult, harness.Handle("""
            {"jsonrpc":"2.0","id":11,"method":"workspace/workspaceFolders"}
            """));

        var result = harness.SingleAnswer().GetProperty("result");

        Assert.Equal(1, result.GetArrayLength());
        Assert.Equal("file:///w/Fixture", result[0].GetProperty("uri").GetString());
        Assert.Equal("Fixture", result[0].GetProperty("name").GetString());
    }

    [Fact]
    public void WorkspaceFoldersAnswersNullWhenTheClientNamedNoRoot()
    {
        var harness = new HandlerHarness();

        harness.Handle("""{"jsonrpc":"2.0","id":11,"method":"workspace/workspaceFolders"}""");

        Assert.Equal(JsonValueKind.Null, harness.SingleAnswer().GetProperty("result").ValueKind);
    }

    /// <summary>
    /// Roslyn's surface grows between versions (C39). An unknown request gets a refusal rather than
    /// silence, because silence stalls whichever handler asked.
    /// </summary>
    [Fact]
    public void AnUnknownServerRequestIsRefusedWithMethodNotFound()
    {
        var harness = new HandlerHarness();

        Assert.Equal(ServerMessageOutcome.MethodNotFound, harness.Handle("""
            {"jsonrpc":"2.0","id":12,"method":"window/_roslyn_somethingNew","params":{}}
            """));

        var error = harness.SingleAnswer().GetProperty("error");

        Assert.Equal(JsonRpcErrors.MethodNotFound, error.GetProperty("code").GetInt32());
        Assert.Contains("window/_roslyn_somethingNew", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>A notification cannot be answered, so an unknown one is dropped rather than refused.</summary>
    [Fact]
    public void AnUnknownServerNotificationIsDroppedSilently()
    {
        var harness = new HandlerHarness();

        Assert.Equal(ServerMessageOutcome.Dropped, harness.Handle("""
            {"jsonrpc":"2.0","method":"$/somethingNobodyModelled","params":{}}
            """));

        Assert.Empty(harness.Answers);
        Assert.Empty(harness.Forwarded);
    }

    /// <summary>Everything the handler needs, wired to the same components the session uses.</summary>
    private sealed class HandlerHarness
    {
        internal HandlerHarness()
        {
            Registrations = new RegistrationTracker(NullLogger.Instance);
            Progress = new ProgressTracker(NullLogger.Instance);

            Gate = new ReadinessGate(
                new TestTimeProvider(),
                TimeSpan.FromSeconds(120),
                NullLogger.Instance,
                _ => { });

            Handler = new ServerRequestHandler(
                Registrations,
                new ConfigurationResponder(optionsJson: null, NullLogger.Instance),
                Progress,
                Gate,
                NullLogger.Instance);
        }

        internal RegistrationTracker Registrations { get; }

        internal ProgressTracker Progress { get; }

        internal ReadinessGate Gate { get; }

        internal ServerRequestHandler Handler { get; }

        internal List<byte[]> Answers { get; } = [];

        internal List<byte[]> Forwarded { get; } = [];

        internal ServerMessageOutcome Handle(string message)
        {
            var body = Encoding.UTF8.GetBytes(message.Trim());
            var info = LspMessageScanner.Scan(body);

            return Handler.Handle(body, info, Answers.Add, Forwarded.Add);
        }

        internal JsonElement SingleAnswer()
        {
            using var document = JsonDocument.Parse(Assert.Single(Answers));
            return document.RootElement.Clone();
        }
    }
}
