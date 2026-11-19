using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Live;

/// <summary>
/// The whole adapter, in front of a <b>real</b> Roslyn, answering about a real solution.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in through <c>CLAUDE_ROSLYN_LSP_LIVE_TESTS=1</c>. What it adds over every other test in this
/// repository is the only thing a scripted backend can never supply: whether Roslyn actually answers
/// the questions this adapter promises its client it will. The unit tests prove the mediation is
/// correct <em>given</em> a backend that behaves as the wire logs said; this proves the wire logs
/// still describe the pinned server, and that the fixture's shape produces the answers the design
/// assumed.
/// </para>
/// <para>
/// <b>One test method, run in phases, and that is deliberate.</b> The expensive part is loading the
/// solution — several seconds, a quarter of a gigabyte — so there is one session for everything.
/// Splitting the phases into separate <c>[Test]</c>s over a shared fixture would look tidier and be
/// wrong: NUnit promises no order for tests within a class, and two of these phases (killing Roslyn,
/// shutting the session down) destroy the state the others need. A sequence that must be a sequence
/// is written as one.
/// </para>
/// <para>
/// Every phase reports its timing through <see cref="TestContext.Out"/>, and the run ends with the
/// Roslyn process's peak working set — which is C38 re-measured on a small solution, and the number
/// anybody asking "what does this cost" wants first.
/// </para>
/// </remarks>
[Category("Live")]
public class AdapterLiveTests
{
    /// <summary>How long the solution may take to load before the test gives up (C31 plus slack).</summary>
    private static readonly TimeSpan LoadBudget = TimeSpan.FromSeconds(120);

    /// <summary>How long one navigation answer may take once the workspace is loaded.</summary>
    private static readonly TimeSpan AnswerBudget = TimeSpan.FromSeconds(30);

    /// <summary>How long a corrected file has to stop reporting its error.</summary>
    private static readonly TimeSpan DiagnosticClearBudget = TimeSpan.FromSeconds(2);

    /// <summary>How long a file created on disk has to become findable by <c>workspace/symbol</c>.</summary>
    private static readonly TimeSpan WatcherBudget = TimeSpan.FromSeconds(10);

    /// <summary>How long <c>shutdown</c>/<c>exit</c> may take.</summary>
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The acquired Roslyn and the copied fixture, built once for the class and private to it.
    /// </summary>
    private static AdapterLiveFixture _fixture = null!;

    private readonly TextWriter _output = TestContext.Out;

    [OneTimeSetUp]
    public static async Task AcquireRoslynAsync()
    {
        _fixture = new AdapterLiveFixture();
        await _fixture.InitializeAsync();
    }

    [OneTimeTearDown]
    public static async Task ReleaseRoslynAsync() => await _fixture.DisposeAsync();

    private static CancellationToken Cancellation => TestContext.CurrentContext.CancellationToken;

    /// <summary>Everything the adapter promises, against the real server, in one session.</summary>
    [Test]
    public async Task TheAdapterAnswersEverythingItAdvertisesAgainstTheRealServer()
    {
        _fixture.SkipIfDisabled();

        await using var session = await LiveAdapterSession.StartAsync(_fixture, _output, Cancellation);

        var loaded = Stopwatch.StartNew();
        await session.WaitForWorkspaceAsync(LoadBudget, Cancellation);
        loaded.Stop();

        _output.WriteLine($"projectInitializationComplete   {loaded.Elapsed.TotalSeconds:F1} s");
        _output.WriteLine($"roslyn pid                      {session.RoslynProcessId?.ToString(CultureInfo.InvariantCulture) ?? "(unknown)"}");

        await NavigationPhaseAsync(session);
        await DiagnosticsPhaseAsync(session);
        await WatcherPhaseAsync(session);
        await RecoveryPhaseAsync(session);
        await ShutdownPhaseAsync(session);
    }

    /// <summary>
    /// The six navigation operations Claude Code's LSP tool exposes, each through the adapter.
    /// </summary>
    private async Task NavigationPhaseAsync(LiveAdapterSession session)
    {
        var calculator = session.Uri("Hello.Core", "Calculator.cs");
        var shape = session.Uri("Hello.Core", "IShape.cs");

        await session.OpenAsync(calculator, Cancellation);
        await session.OpenAsync(shape, Cancellation);

        // Compute() on the line the fixture declares it. Found by text rather than hard-coded, so a
        // doc-comment edit in the fixture does not silently move every position in this file.
        var compute = session.Locate(calculator, "public int Compute()", "Compute");
        var area = session.Locate(shape, "double Area();", "Area");

        var definition = await session.RequestAsync(
            "textDocument/definition", Position(calculator, compute), AnswerBudget, Cancellation);

        Assert.That(definition.GetArrayLength() > 0, Is.True, "definition returned nothing");

        var references = await session.RequestAsync(
            "textDocument/references",
            Position(calculator, compute, ""","context":{"includeDeclaration":true}"""),
            AnswerBudget,
            Cancellation);

        _output.WriteLine($"references                      {references.GetArrayLength()}");

        // Caller.CallOnce, Program.Main and ShapeTests all call it, and Hello.Core is multi-targeted,
        // so the only safe assertion is "several".
        Assert.That(references.GetArrayLength() >= 3, Is.True, $"references returned {references.GetArrayLength()}");

        var implementations = await session.RequestAsync(
            "textDocument/implementation", Position(shape, area), AnswerBudget, Cancellation);

        var distinct = implementations.EnumerateArray()
            .Select(x => x.GetProperty("uri").GetString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _output.WriteLine($"implementations                 {implementations.GetArrayLength()} ({distinct.Length} distinct files)");

        // Circle and Square, and nothing else implements IShape.Area.
        Assert.That(distinct.Length, Is.EqualTo(2));

        var symbols = await session.RequestAsync(
            "workspace/symbol", """{"query":"Calculator"}""", AnswerBudget, Cancellation);

        Assert.That(symbols.GetArrayLength() > 0, Is.True, "workspaceSymbol found no Calculator");

        var prepared = await session.RequestAsync(
            "textDocument/prepareCallHierarchy", Position(calculator, compute), AnswerBudget, Cancellation);

        Assert.That(prepared.GetArrayLength() > 0, Is.True, "prepareCallHierarchy returned nothing");

        // C24, asserted end to end: Hello.Core is multi-targeted, so an un-de-duplicated answer
        // would carry the same symbol once per target framework.
        Assert.That(prepared.GetArrayLength(), Is.EqualTo(1));

        var incoming = await session.RequestAsync(
            "callHierarchy/incomingCalls",
            $$$"""{"item":{{{prepared[0].GetRawText()}}} }""",
            AnswerBudget,
            Cancellation);

        var callers = incoming.EnumerateArray()
            .Select(x => x.GetProperty("from").GetProperty("uri").GetString() + " "
                         + x.GetProperty("from").GetProperty("selectionRange").GetRawText())
            .ToArray();

        _output.WriteLine($"incomingCalls                   {callers.Length}");

        Assert.That(callers, Is.Not.Empty);
        Assert.That(callers.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(callers.Length));

        var hover = await session.RequestAsync(
            "textDocument/hover", Position(calculator, compute), AnswerBudget, Cancellation);

        Assert.That(hover.ValueKind, Is.EqualTo(JsonValueKind.Object));
        Assert.That(hover.TryGetProperty("contents", out _), Is.True, "hover carried no contents");
    }

    /// <summary>
    /// The feature the product exists for: a real CS0029 pushed to a client that never asked, and
    /// gone again once it is fixed.
    /// </summary>
    private async Task DiagnosticsPhaseAsync(LiveAdapterSession session)
    {
        var program = session.Uri("Hello.App", "Program.cs");
        var broken = await File.ReadAllTextAsync(session.PathOf("Hello.App", "Program.cs"), Cancellation);

        var appeared = Stopwatch.StartNew();
        await session.OpenAsync(program, Cancellation);

        await session.WaitForDiagnosticsAsync(
            program,
            x => x.Any(d => d.Code == "CS0029"),
            AnswerBudget,
            Cancellation);

        appeared.Stop();
        _output.WriteLine($"CS0029 after didOpen            {appeared.Elapsed.TotalSeconds:F1} s");

        var published = session.LatestDiagnostics(program);

        Assert.That(published, Is.Not.Null);

        // The severity floor and the Unnecessary rule: Calculator.cs's IDE0005 is a Hint and must
        // never reach the client, and what does arrive for Program.cs is errors and warnings only.
        Assert.That(published!.Select(x => x.Severity), Has.All.LessThanOrEqualTo(DiagnosticTranslation.SeverityWarning));

        var fixedText = broken.Replace("int x = \"s\";", "int x = 42;", StringComparison.Ordinal);
        Assert.That(fixedText, Is.Not.EqualTo(broken));

        var cleared = Stopwatch.StartNew();
        await session.ChangeAsync(program, fixedText, Cancellation);

        await session.WaitForDiagnosticsAsync(
            program,
            x => !x.Any(d => d.Code == "CS0029"),
            DiagnosticClearBudget + AnswerBudget,
            Cancellation);

        cleared.Stop();
        _output.WriteLine($"CS0029 gone after didChange     {cleared.Elapsed.TotalSeconds:F2} s");

        Assert.That(
            cleared.Elapsed < DiagnosticClearBudget + AnswerBudget, Is.True,
            $"the corrected file kept reporting CS0029 for {cleared.Elapsed.TotalSeconds:F1} s");

        // Back to broken on disk and in the buffer, so the recovery phase has something to find.
        await session.ChangeAsync(program, broken, Cancellation);
    }

    /// <summary>
    /// A file written by something that is not the editor joins its project — the C33 rule, end to
    /// end, which is the single worst failure mode this adapter removes.
    /// </summary>
    private async Task WatcherPhaseAsync(LiveAdapterSession session)
    {
        var path = session.PathOf("Hello.Core", "Triangle.cs");

        await File.WriteAllTextAsync(
            path,
            """
            namespace Hello.Core;

            /// <summary>Written by the test, not by the editor.</summary>
            public sealed class Triangle : IShape
            {
                /// <inheritdoc />
                public double Area() => 0.5;
            }

            """,
            Cancellation);

        var found = Stopwatch.StartNew();
        var deadline = DateTime.UtcNow + WatcherBudget + AnswerBudget;
        var seen = false;

        while (DateTime.UtcNow < deadline && !seen)
        {
            var symbols = await session.RequestAsync(
                "workspace/symbol", """{"query":"Triangle"}""", AnswerBudget, Cancellation);

            seen = symbols.EnumerateArray().Any(x => x.GetProperty("name").GetString() == "Triangle");

            if (!seen)
            {
                await Task.Delay(250, Cancellation);
            }
        }

        found.Stop();
        _output.WriteLine($"Triangle.cs found by symbol     {found.Elapsed.TotalSeconds:F1} s");

        Assert.That(seen, Is.True, $"workspace/symbol never found Triangle after {found.Elapsed.TotalSeconds:F0} s");
    }

    /// <summary>Killing Roslyn mid-session is absorbed: the next question is still answered.</summary>
    private async Task RecoveryPhaseAsync(LiveAdapterSession session)
    {
        var pid = session.RoslynProcessId;
        Assert.That(pid, Is.Not.Null);

        using (var roslyn = Process.GetProcessById(pid!.Value))
        {
            _output.WriteLine($"peak working set before kill    {roslyn.PeakWorkingSet64 / (1024 * 1024)} MB");
            session.RecordPeakWorkingSet(roslyn.PeakWorkingSet64);
            roslyn.Kill(entireProcessTree: true);
        }

        var recovered = Stopwatch.StartNew();
        var calculator = session.Uri("Hello.Core", "Calculator.cs");
        var compute = session.Locate(calculator, "public int Compute()", "Compute");

        // Issued straight away, so it is held by the gate while the relaunch happens. What is being
        // proved is that the client never sees the crash: no error, and a real answer afterwards.
        var definition = await session.RequestAsync(
            "textDocument/definition", Position(calculator, compute), LoadBudget, Cancellation);

        recovered.Stop();
        _output.WriteLine($"definition after kill           {recovered.Elapsed.TotalSeconds:F1} s");

        Assert.That(definition.GetArrayLength() > 0, Is.True, "the relaunched backend answered definition empty");
        Assert.That(session.RoslynProcessId, Is.Not.EqualTo(pid.Value));
    }

    /// <summary>Shutting down does so promptly; an editor that hangs on quit is a bug users remember.</summary>
    private async Task ShutdownPhaseAsync(LiveAdapterSession session)
    {
        var stopwatch = Stopwatch.StartNew();
        var exitCode = await session.ShutdownAsync(ShutdownBudget, Cancellation);
        stopwatch.Stop();

        _output.WriteLine($"shutdown + exit                 {stopwatch.Elapsed.TotalSeconds:F2} s");
        _output.WriteLine($"peak working set (C38)          {session.PeakWorkingSet / (1024 * 1024)} MB");

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stopwatch.Elapsed < ShutdownBudget, Is.True, $"shutdown took {stopwatch.Elapsed.TotalSeconds:F1} s");
    }

    /// <summary>Renders a <c>TextDocumentPositionParams</c>, with an optional extra member.</summary>
    private static string Position(string uri, (int Line, int Character) at, string extra = "") => $$$"""
        {"textDocument":{"uri":"{{{uri}}}"},"position":{"line":{{{at.Line}}},"character":{{{at.Character}}}}{{{extra}}}
        }
        """;
}
