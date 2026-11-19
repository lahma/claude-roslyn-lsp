using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Tests.Testing;

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// The pull-to-push bridge, driven against a scripted channel and a hand-moved clock.
/// </summary>
/// <remarks>
/// The clock matters: every trigger in this class is a debounce, and the debounces are the design.
/// A test that slept for 400 ms per case would be a test somebody eventually deletes.
/// </remarks>
public class DiagnosticsBridgeTests
{
    private const string Uri = "file:///w/Hello.App/Program.cs";

    /// <summary>A full report carrying the fixture's CS0029 and an IDE0005 that must not survive.</summary>
    private const string FullReport = """
        {"kind":"full","resultId":"r1","items":[
          {"range":{"start":{"line":21,"character":16},"end":{"line":21,"character":19}},"severity":1,
           "code":"CS0029","message":"Cannot implicitly convert type 'string' to 'int'"},
          {"range":{"start":{"line":0,"character":0},"end":{"line":1,"character":18}},"severity":4,
           "code":"IDE0005","message":"Using directive is unnecessary.","tags":[1,2147483645]}]}
        """;

    private static CancellationToken Cancellation => TestContext.CurrentContext.CancellationToken;

    /// <summary>A <c>didOpen</c> on a loaded workspace pulls at once and publishes what survives.</summary>
    [Test]
    public async Task DidOpenPullsImmediatelyAndPublishesTheTranslatedSet()
    {
        using var harness = new BridgeHarness();
        harness.Open(Uri, "class C { }");
        harness.Channel.Answer(FullReport);

        harness.Bridge.OnDidOpen(Uri);
        await harness.Channel.WaitForClientAsync(1, Cancellation);

        Assert.That(harness.Channel.Asked, Has.Exactly(1).Items);
        var asked = harness.Channel.Asked.Single();
        Assert.That(asked.Method, Is.EqualTo("textDocument/diagnostic"));

        // C9: no identifier, because one pull with none gives the union of every source.
        Assert.That(asked.Params.TryGetProperty("identifier", out _), Is.False);
        Assert.That(asked.Params.GetProperty("textDocument").GetProperty("uri").GetString(), Is.EqualTo(Uri));

        var published = Published(harness, 0);

        Assert.That(published.GetProperty("uri").GetString(), Is.EqualTo(Uri));
        Assert.That(published.GetProperty("version").GetInt32(), Is.EqualTo(1));

        var diagnostics = published.GetProperty("diagnostics");

        Assert.That(diagnostics.GetArrayLength(), Is.EqualTo(1));
        Assert.That(diagnostics[0].GetProperty("code").GetString(), Is.EqualTo("CS0029"));
    }

    /// <summary>
    /// C28: a document opened before the workspace has loaded is not pulled — misc-files answers are
    /// wrong in a way that reads as correct — and is pulled the moment it has.
    /// </summary>
    [Test]
    public async Task ADidOpenBeforeTheWorkspaceLoadsIsDeferredUntilItHas()
    {
        using var harness = new BridgeHarness();
        harness.Channel.Readiness = ReadinessState.RoslynInitialized;
        harness.Open(Uri, "class C { }");

        harness.Bridge.OnDidOpen(Uri);

        Assert.That(harness.Channel.Asked, Is.Empty);

        harness.Channel.Readiness = ReadinessState.ProjectsLoaded;
        harness.Channel.Answer(FullReport);
        harness.Bridge.OnProjectsLoaded();

        await harness.Channel.WaitForClientAsync(1, Cancellation);
        Assert.That(harness.Channel.Asked, Is.Not.Empty);
    }

    /// <summary>A <c>didChange</c> waits out the trailing debounce, and a burst is one pull.</summary>
    [Test]
    public async Task DidChangeIsDebouncedAndABurstCollapsesToOnePull()
    {
        using var harness = new BridgeHarness();
        harness.Open(Uri, "class C { }");
        harness.Channel.Answer(FullReport);

        harness.Bridge.OnDidChange(Uri);
        harness.Time.Advance(TimeSpan.FromMilliseconds(200));
        harness.Bridge.OnDidChange(Uri);
        harness.Time.Advance(TimeSpan.FromMilliseconds(200));
        harness.Bridge.OnDidChange(Uri);

        Assert.That(harness.Channel.Asked, Is.Empty);

        harness.Time.Advance(DiagnosticsBridge.ChangeDebounce);
        await harness.Channel.WaitForClientAsync(1, Cancellation);

        Assert.That(harness.Channel.Asked, Has.Exactly(1).Items);
    }

    /// <summary>A save pulls at once: it is the strongest "does this compile" signal an agent gives.</summary>
    [Test]
    public async Task DidSavePullsWithoutWaiting()
    {
        using var harness = new BridgeHarness();
        harness.Open(Uri, "class C { }");
        harness.Channel.Answer(FullReport);

        harness.Bridge.OnDidSave(Uri);
        await harness.Channel.WaitForClientAsync(1, Cancellation);

        Assert.That(harness.Channel.Asked, Has.Exactly(1).Items);
    }

    /// <summary>
    /// C12: the second pull presents the first's <c>resultId</c>, and an <c>unchanged</c> report
    /// publishes nothing — which is what keeps an idle session quiet.
    /// </summary>
    [Test]
    public async Task ThePreviousResultIdIsSentAndAnUnchangedReportPublishesNothing()
    {
        using var harness = new BridgeHarness();
        harness.Open(Uri, "class C { }");

        harness.Channel.Answer(FullReport);
        harness.Bridge.OnDidSave(Uri);
        await harness.Channel.WaitForClientAsync(1, Cancellation);

        harness.Channel.Answer("""{"kind":"unchanged","resultId":"r2"}""");
        harness.Bridge.OnDidSave(Uri);
        await harness.Channel.WaitForAskedAsync(2, Cancellation);

        Assert.That(harness.Channel.Asked[1].Params.GetProperty("previousResultId").GetString(), Is.EqualTo("r1"));

        // Nothing new reached the client: still the one publish from the first pull.
        await Task.Delay(50, Cancellation);
        Assert.That(harness.Channel.ToClient, Has.Exactly(1).Items);
    }

    /// <summary>A close clears the client's set, because a client keeps the last one forever.</summary>
    [Test]
    public void DidCloseClearsTheDiagnosticsAndForgetsTheDocument()
    {
        using var harness = new BridgeHarness();
        harness.Open(Uri, "class C { }");

        harness.Bridge.OnDidClose(Uri);

        var published = Published(harness, 0);

        Assert.That(published.GetProperty("uri").GetString(), Is.EqualTo(Uri));
        Assert.That(published.GetProperty("diagnostics").GetArrayLength(), Is.EqualTo(0));
    }

    /// <summary>
    /// <c>-32801 ContentModified</c> means the document moved under the pull. One retry, because the
    /// world settles or it does not, and a retry loop against a fast typist would never return.
    /// </summary>
    [Test]
    public async Task AContentModifiedRefusalIsRetriedExactlyOnce()
    {
        using var harness = new BridgeHarness();
        harness.Open(Uri, "class C { }");

        harness.Channel.Refuse(-32801);
        harness.Channel.Answer(FullReport);

        harness.Bridge.OnDidSave(Uri);
        await harness.Channel.WaitForAskedAsync(1, Cancellation);

        // "The pull was made" is not yet "the retry is armed": the bridge schedules it in the
        // continuation that observes the refusal, and advancing the clock before that fires nothing
        // at all (C62). Windows usually wins that race; Linux reliably loses it.
        await harness.Time.WaitForTimerAsync(DiagnosticsBridge.RetryDelay, Cancellation);

        harness.Time.Advance(DiagnosticsBridge.RetryDelay);
        await harness.Channel.WaitForClientAsync(1, Cancellation);

        Assert.That(harness.Channel.Asked.Count, Is.EqualTo(2));
    }

    /// <summary>Any other refusal is reported and dropped; there is nothing a second ask would fix.</summary>
    [Test]
    public async Task AnOrdinaryRefusalIsNotRetried()
    {
        using var harness = new BridgeHarness();
        harness.Open(Uri, "class C { }");
        harness.Channel.Refuse(-32603);

        harness.Bridge.OnDidSave(Uri);
        await harness.Channel.WaitForAskedAsync(1, Cancellation);
        await Task.Delay(50, Cancellation);

        Assert.That(harness.Channel.Asked, Has.Exactly(1).Items);
        Assert.That(harness.Channel.ToClient, Is.Empty);
    }

    /// <summary>
    /// A refresh re-pulls every open document, after its own debounce — Roslyn sends several in a row.
    /// </summary>
    [Test]
    public async Task ARefreshRePullsEveryOpenDocument()
    {
        using var harness = new BridgeHarness();
        harness.Open(Uri, "class C { }");
        harness.Open("file:///w/Hello.Core/Calculator.cs", "class D { }");

        harness.Bridge.OnRefreshRequested("workspace/diagnostic/refresh");
        harness.Bridge.OnRefreshRequested("workspace/diagnostic/refresh");

        Assert.That(harness.Channel.Asked, Is.Empty);

        harness.Time.Advance(DiagnosticsBridge.RefreshDebounce);
        await harness.Channel.WaitForAskedAsync(2, Cancellation);

        Assert.That(harness.Channel.Asked.Count, Is.EqualTo(2));
    }

    /// <summary>
    /// The other three refreshes are for capabilities the adapter does not advertise (D45), so
    /// acting on them would be work whose output nobody reads.
    /// </summary>
    [TestCase("workspace/codeLens/refresh")]
    [TestCase("workspace/inlayHint/refresh")]
    [TestCase("workspace/semanticTokens/refresh")]
    public void TheRefreshesForCapabilitiesTheAdapterDoesNotCarryAreIgnored(string method)
    {
        using var harness = new BridgeHarness();
        harness.Open(Uri, "class C { }");

        harness.Bridge.OnRefreshRequested(method);
        harness.Time.Advance(DiagnosticsBridge.RefreshDebounce * 2);

        Assert.That(harness.Channel.Asked, Is.Empty);
    }

    /// <summary>
    /// A project file changed on disk: every open document is re-pulled a second later, because the
    /// event is the start of Roslyn's work, not the end of it (C33).
    /// </summary>
    [Test]
    public async Task AProjectFileChangeRePullsAfterASecond()
    {
        using var harness = new BridgeHarness();
        harness.Open(Uri, "class C { }");

        harness.Bridge.OnProjectFilesChanged();
        harness.Time.Advance(TimeSpan.FromMilliseconds(900));

        Assert.That(harness.Channel.Asked, Is.Empty);

        harness.Time.Advance(TimeSpan.FromMilliseconds(200));
        await harness.Channel.WaitForAskedAsync(1, Cancellation);
    }

    /// <summary>A reset forgets the result ids, because the server that issued them is gone.</summary>
    [Test]
    public async Task ResetForgetsTheResultIds()
    {
        using var harness = new BridgeHarness();
        harness.Open(Uri, "class C { }");

        harness.Channel.Answer(FullReport);
        harness.Bridge.OnDidSave(Uri);
        await harness.Channel.WaitForClientAsync(1, Cancellation);

        harness.Bridge.Reset();

        harness.Channel.Answer(FullReport);
        harness.Bridge.OnDidSave(Uri);
        await harness.Channel.WaitForAskedAsync(2, Cancellation);

        Assert.That(harness.Channel.Asked[1].Params.TryGetProperty("previousResultId", out _), Is.False);
    }

    /// <summary>Nothing is asked while there is no backend to ask.</summary>
    [Test]
    public void NothingIsPulledWithoutABackend()
    {
        using var harness = new BridgeHarness();
        harness.Channel.BackendConnected = false;
        harness.Open(Uri, "class C { }");

        harness.Bridge.OnDidSave(Uri);

        Assert.That(harness.Channel.Asked, Is.Empty);
    }

    /// <summary>The opt-in workspace mode is off unless it is asked for by name.</summary>
    [TestCase(null, false)]
    [TestCase("errors", true)]
    [TestCase("ERRORS", true)]
    [TestCase("off", false)]
    [TestCase("true", false)]
    [TestCase("everything", false)]
    public void TheWorkspaceModeIsOptInByName(string? value, bool expected) =>
        Assert.That(DiagnosticsBridge.IsWorkspaceModeRequested(value, NullLogger.Instance), Is.EqualTo(expected));

    /// <summary>
    /// With the mode on, a save runs a <c>workspace/diagnostic</c> after its own longer debounce, with
    /// no identifier and the result ids it knows (C14).
    /// </summary>
    [Test]
    public async Task TheWorkspaceModeAsksAfterASaveWithNoIdentifier()
    {
        using var harness = new BridgeHarness(workspaceDiagnostics: "errors");
        harness.Open(Uri, "class C { }");

        Assert.That(harness.Bridge.WorkspaceMode, Is.True);

        harness.Channel.Answer("""{"kind":"full","resultId":"r1","items":[]}""");
        harness.Bridge.OnDidSave(Uri);
        await harness.Channel.WaitForAskedAsync(1, Cancellation);

        harness.Channel.Answer("""{"items":[]}""");
        harness.Time.Advance(DiagnosticsBridge.WorkspaceSaveDebounce);
        await harness.Channel.WaitForAskedAsync(2, Cancellation);

        var workspace = harness.Channel.Asked[1];

        Assert.That(workspace.Method, Is.EqualTo("workspace/diagnostic"));
        Assert.That(workspace.Params.TryGetProperty("identifier", out _), Is.False);
        Assert.That(workspace.Params.GetProperty("previousResultIds").GetArrayLength(), Is.EqualTo(0));
    }

    /// <summary>
    /// C15's noise, filtered: open documents, <c>obj/</c>, <c>.csproj</c> entries and the per-TFM
    /// duplicate all go, and what is published is errors only.
    /// </summary>
    [Test]
    public async Task TheWorkspaceModePublishesErrorsForClosedSourceFilesOnly()
    {
        using var harness = new BridgeHarness(workspaceDiagnostics: "errors");
        harness.Open(Uri, "class C { }");

        const string report = """
            {"items":[
              {"kind":"full","uri":"file:///w/Hello.App/Program.cs","resultId":"a","items":[
                {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"severity":1,"code":"OPEN"}]},
              {"kind":"full","uri":"file:///w/Hello.Core/obj/Debug/Generated.cs","resultId":"b","items":[
                {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"severity":1,"code":"OBJ"}]},
              {"kind":"full","uri":"file:///w/Hello.Core/Hello.Core.csproj","resultId":"c","items":[
                {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"severity":1,"code":"PROJ"}]},
              {"kind":"full","uri":"file:///w/Hello.Core/Caller.cs","resultId":"d","items":[
                {"range":{"start":{"line":3,"character":0},"end":{"line":3,"character":1}},"severity":1,"code":"REAL"},
                {"range":{"start":{"line":4,"character":0},"end":{"line":4,"character":1}},"severity":2,"code":"WARN"}]},
              {"kind":"full","uri":"file:///w/Hello.Core/Caller.cs","resultId":"e","items":[
                {"range":{"start":{"line":3,"character":0},"end":{"line":3,"character":1}},"severity":1,"code":"REAL"}]}]}
            """;

        harness.Channel.Answer("""{"kind":"full","resultId":"r1","items":[]}""");
        harness.Channel.Answer(report);

        harness.Bridge.OnDidSave(Uri);
        await harness.Channel.WaitForAskedAsync(1, Cancellation);

        harness.Time.Advance(DiagnosticsBridge.WorkspaceSaveDebounce);
        await harness.Channel.WaitForClientAsync(2, Cancellation);

        var closed = harness.Channel.ToClient
            .Select(x => x.GetProperty("params"))
            .Where(x => x.GetProperty("uri").GetString()!.EndsWith("Caller.cs", StringComparison.Ordinal))
            .ToArray();

        Assert.That(closed, Has.Exactly(1).Items);
        var only = closed.Single();
        var diagnostics = only.GetProperty("diagnostics");

        Assert.That(diagnostics.GetArrayLength(), Is.EqualTo(1));
        Assert.That(diagnostics[0].GetProperty("code").GetString(), Is.EqualTo("REAL"));

        // No version on a file nobody has open: a fabricated one would make the client discard it.
        Assert.That(only.TryGetProperty("version", out _), Is.False);

        // And nothing at all for the open file, the obj/ file or the project file.
        Assert.That(
            harness.Channel.ToClient,
            Has.None.Matches<JsonElement>(x => x.GetProperty("params").GetProperty("uri").GetString()!.Contains("obj/", StringComparison.Ordinal)));

        Assert.That(
            harness.Channel.ToClient,
            Has.None.Matches<JsonElement>(x => x.GetProperty("params").GetProperty("uri").GetString()!.EndsWith(".csproj", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A closed file that was reported and is now clean gets an explicit empty set, or it stays
    /// broken in the transcript for the rest of the session.
    /// </summary>
    [Test]
    public async Task AClosedFileThatWasFixedIsClearedExplicitly()
    {
        using var harness = new BridgeHarness(workspaceDiagnostics: "errors");
        harness.Open(Uri, "class C { }");

        const string broken = """
            {"items":[{"kind":"full","uri":"file:///w/Hello.Core/Caller.cs","resultId":"a","items":[
              {"range":{"start":{"line":3,"character":0},"end":{"line":3,"character":1}},"severity":1,"code":"REAL"}]}]}
            """;

        harness.Channel.Answer("""{"kind":"full","resultId":"r1","items":[]}""");
        harness.Channel.Answer(broken);

        harness.Bridge.OnDidSave(Uri);
        await harness.Channel.WaitForAskedAsync(1, Cancellation);
        harness.Time.Advance(DiagnosticsBridge.WorkspaceSaveDebounce);
        await harness.Channel.WaitForClientAsync(2, Cancellation);

        harness.Channel.Answer("""{"kind":"full","resultId":"r2","items":[]}""");
        harness.Channel.Answer("""{"items":[]}""");

        harness.Bridge.OnDidSave(Uri);
        await harness.Channel.WaitForAskedAsync(3, Cancellation);
        harness.Time.Advance(DiagnosticsBridge.WorkspaceSaveDebounce);
        await harness.Channel.WaitForClientAsync(4, Cancellation);

        var last = harness.Channel.ToClient[^1].GetProperty("params");

        Assert.That(last.GetProperty("uri").GetString()!, Does.EndWith("Caller.cs").Using(StringComparison.Ordinal));
        Assert.That(last.GetProperty("diagnostics").GetArrayLength(), Is.EqualTo(0));
    }

    private static JsonElement Published(BridgeHarness harness, int index) =>
        harness.Channel.ToClient[index].GetProperty("params");

    /// <summary>A bridge over a scripted channel, a real mirror and a hand-moved clock.</summary>
    private sealed class BridgeHarness : IDisposable
    {
        internal BridgeHarness(string? workspaceDiagnostics = null)
        {
            Mirror = new DocumentMirror(NullLogger.Instance);

            Bridge = new DiagnosticsBridge(
                Mirror,
                Channel,
                new ClaudeRoslynLspOptions { WorkspaceDiagnostics = workspaceDiagnostics },
                Time,
                NullLogger.Instance);
        }

        internal StubAdapterChannel Channel { get; } = new() { WorkspaceRoot = "/w" };

        internal TestTimeProvider Time { get; } = new();

        internal DocumentMirror Mirror { get; }

        internal DiagnosticsBridge Bridge { get; }

        /// <summary>Puts a document in the mirror the way a real <c>didOpen</c> would.</summary>
        /// <remarks>
        /// <see cref="JsonEncodedText"/> rather than a serializer call: the test project runs with
        /// reflection-free serialization too (D6), so escaping a bare string is the honest way to do
        /// it without adding a contract for <c>string</c>.
        /// </remarks>
        internal void Open(string uri, string text)
        {
            var escaped = JsonEncodedText.Encode(text);

            Mirror.Open(Encoding.UTF8.GetBytes($$$"""
                {"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":
                 {"uri":"{{{uri}}}","languageId":"csharp","version":1,"text":"{{{escaped}}}"}
                }}
                """));
        }

        public void Dispose() => Bridge.Dispose();
    }
}
