using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Protocol;
using ClaudeRoslynLsp.Tests.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

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
    [Test]
    public void RegistrationsAreRecordedAndAcknowledgedWithNull()
    {
        var harness = new HandlerHarness();

        var outcome = harness.Handle("""
            {"jsonrpc":"2.0","id":4,"method":"client/registerCapability","params":{"registrations":[{"id":"a","method":"textDocument/diagnostic","registerOptions":{"identifier":"syntax"}}]}}
            """);

        Assert.That(outcome, Is.EqualTo(ServerMessageOutcome.AnsweredNull));
        Assert.That(harness.Registrations.Count, Is.EqualTo(1));
        Assert.That(harness.Registrations.DiagnosticSources, Has.Exactly(1).Items);
        Assert.That(harness.Registrations.DiagnosticSources.Single().Identifier, Is.EqualTo("syntax"));

        var answer = harness.SingleAnswer();

        Assert.That(answer.GetProperty("id").GetInt32(), Is.EqualTo(4));
        Assert.That(answer.GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public void UnregistrationsAreHonouredAndAcknowledged()
    {
        var harness = new HandlerHarness();

        harness.Handle("""
            {"jsonrpc":"2.0","id":1,"method":"client/registerCapability","params":{"registrations":[{"id":"a","method":"textDocument/diagnostic","registerOptions":{"identifier":"syntax"}}]}}
            """);

        var outcome = harness.Handle("""
            {"jsonrpc":"2.0","id":2,"method":"client/unregisterCapability","params":{"unregisterations":[{"id":"a","method":"textDocument/diagnostic"}]}}
            """);

        Assert.That(outcome, Is.EqualTo(ServerMessageOutcome.AnsweredNull));
        Assert.That(harness.Registrations.Count, Is.EqualTo(0));
    }

    [Test]
    public void ConfigurationIsAnsweredByTheAdapterRatherThanForwarded()
    {
        var harness = new HandlerHarness();

        var outcome = harness.Handle("""
            {"jsonrpc":"2.0","id":3,"method":"workspace/configuration","params":{"items":[{"section":"projects.dotnet_enable_automatic_restore"}]}}
            """);

        Assert.That(outcome, Is.EqualTo(ServerMessageOutcome.AnsweredWithResult));
        Assert.That(harness.Forwarded, Is.Empty);

        var result = harness.SingleAnswer().GetProperty("result");

        Assert.That(result[0].ValueKind, Is.EqualTo(JsonValueKind.True));
    }

    /// <summary>
    /// Accepted on the client's behalf, because Claude Code refuses it with <c>-32601</c> and the
    /// solution-load progress stream is the only account the load gives of itself (C31).
    /// </summary>
    [Test]
    public void WorkDoneProgressCreateIsAcceptedAndTheStreamIsConsumed()
    {
        var harness = new HandlerHarness();

        Assert.That(harness.Handle("""
            {"jsonrpc":"2.0","id":5,"method":"window/workDoneProgress/create","params":{"token":"tok-1"}}
            """), Is.EqualTo(ServerMessageOutcome.AnsweredNull));

        Assert.That(harness.Progress.ActiveCount, Is.EqualTo(1));

        Assert.That(harness.Handle("""
            {"jsonrpc":"2.0","method":"$/progress","params":{"token":"tok-1","value":{"kind":"begin","title":"Loading Fixture.slnx...","percentage":0}}}
            """), Is.EqualTo(ServerMessageOutcome.Consumed));

        Assert.That(harness.Progress.CurrentTitle, Is.EqualTo("Loading Fixture.slnx..."));

        // Never forwarded: the client says the token does not exist.
        Assert.That(harness.Forwarded, Is.Empty);

        Assert.That(harness.Handle("""
            {"jsonrpc":"2.0","method":"$/progress","params":{"token":"tok-1","value":{"kind":"end","message":"Loaded"}}}
            """), Is.EqualTo(ServerMessageOutcome.Consumed));

        Assert.That(harness.Progress.ActiveCount, Is.EqualTo(0));
    }

    /// <summary>The one notification the whole adapter waits for. It carries no <c>params</c> at all.</summary>
    [Test]
    public void ProjectInitializationCompleteOpensTheGate()
    {
        var harness = new HandlerHarness();
        harness.Gate.Start();
        harness.Gate.MarkRoslynInitialized();

        var outcome = harness.Handle("""{"jsonrpc":"2.0","method":"workspace/projectInitializationComplete"}""");

        Assert.That(outcome, Is.EqualTo(ServerMessageOutcome.Consumed));
        Assert.That(harness.Gate.State, Is.EqualTo(ReadinessState.ProjectsLoaded));
    }

    /// <summary>
    /// Refresh requests are acknowledged immediately and reported as an event. Acting on them is
    /// WP4's; leaving one unanswered would block the handler that asked, which is not.
    /// </summary>
    [TestCase("workspace/diagnostic/refresh")]
    [TestCase("workspace/codeLens/refresh")]
    [TestCase("workspace/inlayHint/refresh")]
    [TestCase("workspace/semanticTokens/refresh")]
    public void ARefreshRequestIsAcknowledgedAndAnnounced(string method)
    {
        var harness = new HandlerHarness();
        var seen = new List<string>();

        harness.Handler.RefreshRequested += seen.Add;

        Assert.That(harness.Handle($$"""
            {"jsonrpc":"2.0","id":8,"method":"{{method}}"}
            """), Is.EqualTo(ServerMessageOutcome.AnsweredNull));

        Assert.That(seen, Is.EqualTo([method]));
        Assert.That(harness.SingleAnswer().GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public void ShowMessageCrossesToTheClientUnchanged()
    {
        var harness = new HandlerHarness();
        const string Message = """{"jsonrpc":"2.0","method":"window/showMessage","params":{"type":1,"message":"Project load failed"}}""";

        Assert.That(harness.Handle(Message), Is.EqualTo(ServerMessageOutcome.ForwardedToClient));
        Assert.That(harness.Forwarded, Has.Exactly(1).Items);
        Assert.That(Encoding.UTF8.GetString(harness.Forwarded.Single()), Is.EqualTo(Message));
    }

    /// <summary>
    /// Roslyn's twenty-odd startup log lines are its only log channel at the default level (C6).
    /// Unprefixed they would read in the user's transcript as though the adapter had said them, and
    /// the difference matters the moment something is wrong.
    /// </summary>
    [Test]
    public void LogMessagesReachTheClientMarkedAsRoslynsOwn()
    {
        var harness = new HandlerHarness();

        Assert.That(harness.Handle("""
            {"jsonrpc":"2.0","method":"window/logMessage","params":{"type":3,"message":"[solution/open] Loading Fixture.slnx..."}}
            """), Is.EqualTo(ServerMessageOutcome.ForwardedToClient));

        Assert.That(harness.Forwarded, Has.Exactly(1).Items);
        using var document = JsonDocument.Parse(harness.Forwarded.Single());
        var parameters = document.RootElement.GetProperty("params");

        Assert.That(document.RootElement.GetProperty("method").GetString(), Is.EqualTo("window/logMessage"));
        Assert.That(parameters.GetProperty("type").GetInt32(), Is.EqualTo(3));
        Assert.That(parameters.GetProperty("message").GetString(), Is.EqualTo("[roslyn] [solution/open] Loading Fixture.slnx..."));
    }

    /// <summary>
    /// A modal question in a client with no user in front of it. Answered null (dismissed) and logged,
    /// so the text is not lost — and, above all, answered, so Roslyn is not left waiting.
    /// </summary>
    [TestCase("window/showMessageRequest")]
    [TestCase("window/_roslyn_showToast")]
    public void AServerPromptIsDismissedRatherThanForwarded(string method)
    {
        var harness = new HandlerHarness();

        Assert.That(harness.Handle($$$"""
            {"jsonrpc":"2.0","id":9,"method":"{{{method}}}","params":{"type":3,"message":"Restore this project?","actions":[{"title":"Restore"}]}}
            """), Is.EqualTo(ServerMessageOutcome.AnsweredNull));

        Assert.That(harness.Forwarded, Is.Empty);
        Assert.That(harness.SingleAnswer().GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public void AWorkspaceEditReachesAClientThatSaidItAppliesThem()
    {
        var harness = new HandlerHarness();
        harness.Handler.ClientAppliesEdits = true;

        Assert.That(harness.Handle("""
            {"jsonrpc":"2.0","id":10,"method":"workspace/applyEdit","params":{"edit":{"documentChanges":[]}}}
            """), Is.EqualTo(ServerMessageOutcome.ForwardedToClient));

        Assert.That(harness.Forwarded, Has.Exactly(1).Items);
        Assert.That(harness.Answers, Is.Empty);
    }

    /// <summary>
    /// A refusal has to be a well-formed <c>{applied:false}</c> rather than an error: that is the
    /// protocol's own way of saying "the client declined", which is exactly what happened.
    /// </summary>
    [Test]
    public void AWorkspaceEditIsDeclinedWhenTheClientCannotApplyOne()
    {
        var harness = new HandlerHarness { };

        Assert.That(harness.Handle("""
            {"jsonrpc":"2.0","id":10,"method":"workspace/applyEdit","params":{"edit":{"documentChanges":[]}}}
            """), Is.EqualTo(ServerMessageOutcome.AnsweredWithResult));

        Assert.That(harness.Forwarded, Is.Empty);

        var result = harness.SingleAnswer().GetProperty("result");

        Assert.That(result.GetProperty("applied").GetBoolean(), Is.False);
        Assert.That(result.GetProperty("failureReason").GetString()!, Contains.Substring("applyEdit").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// Telemetry is Roslyn's business. A client that logged it would put the user's solution structure
    /// into a transcript nobody asked to publish.
    /// </summary>
    [Test]
    public void TelemetryIsDropped()
    {
        var harness = new HandlerHarness();

        Assert.That(harness.Handle("""
            {"jsonrpc":"2.0","method":"telemetry/event","params":{"EventName":"vs/ide/lsp"}}
            """), Is.EqualTo(ServerMessageOutcome.Dropped));

        Assert.That(harness.Forwarded, Is.Empty);
        Assert.That(harness.Answers, Is.Empty);
    }

    [Test]
    public void WorkspaceFoldersAreAnsweredFromTheClientsRoot()
    {
        var harness = new HandlerHarness();
        harness.Handler.WorkspaceFolders = [new WorkspaceFolder { Uri = "file:///w/Fixture", Name = "Fixture" }];

        Assert.That(harness.Handle("""
            {"jsonrpc":"2.0","id":11,"method":"workspace/workspaceFolders"}
            """), Is.EqualTo(ServerMessageOutcome.AnsweredWithResult));

        var result = harness.SingleAnswer().GetProperty("result");

        Assert.That(result.GetArrayLength(), Is.EqualTo(1));
        Assert.That(result[0].GetProperty("uri").GetString(), Is.EqualTo("file:///w/Fixture"));
        Assert.That(result[0].GetProperty("name").GetString(), Is.EqualTo("Fixture"));
    }

    [Test]
    public void WorkspaceFoldersAnswersNullWhenTheClientNamedNoRoot()
    {
        var harness = new HandlerHarness();

        harness.Handle("""{"jsonrpc":"2.0","id":11,"method":"workspace/workspaceFolders"}""");

        Assert.That(harness.SingleAnswer().GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    /// <summary>
    /// Roslyn's surface grows between versions (C39). An unknown request gets a refusal rather than
    /// silence, because silence stalls whichever handler asked.
    /// </summary>
    [Test]
    public void AnUnknownServerRequestIsRefusedWithMethodNotFound()
    {
        var harness = new HandlerHarness();

        Assert.That(harness.Handle("""
            {"jsonrpc":"2.0","id":12,"method":"window/_roslyn_somethingNew","params":{}}
            """), Is.EqualTo(ServerMessageOutcome.MethodNotFound));

        var error = harness.SingleAnswer().GetProperty("error");

        Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo(JsonRpcErrors.MethodNotFound));
        Assert.That(
            error.GetProperty("message").GetString()!,
            Contains.Substring("window/_roslyn_somethingNew").Using(StringComparison.Ordinal));
    }

    /// <summary>A notification cannot be answered, so an unknown one is dropped rather than refused.</summary>
    [Test]
    public void AnUnknownServerNotificationIsDroppedSilently()
    {
        var harness = new HandlerHarness();

        Assert.That(harness.Handle("""
            {"jsonrpc":"2.0","method":"$/somethingNobodyModelled","params":{}}
            """), Is.EqualTo(ServerMessageOutcome.Dropped));

        Assert.That(harness.Answers, Is.Empty);
        Assert.That(harness.Forwarded, Is.Empty);
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
            Assert.That(Answers, Has.Exactly(1).Items);
            using var document = JsonDocument.Parse(Answers.Single());
            return document.RootElement.Clone();
        }
    }
}
