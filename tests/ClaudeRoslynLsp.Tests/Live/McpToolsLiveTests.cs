using System.Diagnostics;
using System.Globalization;
using System.Text;

using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Mcp.Models;
using ClaudeRoslynLsp.Mcp.Tools;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Live;

/// <summary>
/// The ten MCP tools, against a <b>real</b> Roslyn and a real solution, through
/// <see cref="OwnedRoslynEngine"/>.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in through <c>CLAUDE_ROSLYN_LSP_LIVE_TESTS=1</c>. What this adds over
/// <c>Tools/ToolBehaviourTests</c> is the half those cannot reach: those prove the judgement above
/// the engine seam against payloads transcribed from the WP0 spikes, and this proves the payloads
/// still describe the pinned server — that <c>workspace/diagnostic</c> really does report a closed
/// file once the compiler scope is raised (C14), that <c>codeAction/resolveFixAll</c> really accepts
/// the plain action's data (C20), that <c>Move type to X.cs</c> really resolves to a create followed
/// by edits into a file that does not exist yet (C21), and that a file written back after a rename
/// keeps its CRLF endings and its byte-order mark.
/// </para>
/// <para>
/// <b>One test method, run in phases (D59, D81).</b> Loading the solution is the expensive part and
/// several phases destroy what the others need — the fix-all deletes the using that the code-action
/// phase asks about, the rename changes the symbol the navigation phases resolve. NUnit promises no
/// order for tests within a class, so a sequence that must be a sequence is written as one.
/// </para>
/// <para>
/// Every phase prints its timing, and the run ends with the Roslyn process's working set. Those
/// numbers are the answer to "what does this cost", and they are the reason the phases report even
/// when they pass.
/// </para>
/// </remarks>
[Category("Live")]
public class McpToolsLiveTests : IDisposable
{
    /// <summary>
    /// The one MCP engine, over the class's own copy of the fixture, built once and loaded once.
    /// </summary>
    private static McpToolsLiveFixture _fixture = null!;

    private readonly TextWriter _output = TestContext.Out;

    public McpToolsLiveTests()
    {
        if (_fixture.Enabled)
        {
            _fixture.Relay.PointAt(new TestOutputLogger(_output, Microsoft.Extensions.Logging.LogLevel.Information));
        }
    }

    [OneTimeSetUp]
    public static async Task LoadWorkspaceAsync()
    {
        _fixture = new McpToolsLiveFixture();
        await _fixture.InitializeAsync();
    }

    [OneTimeTearDown]
    public static async Task ReleaseWorkspaceAsync() => await _fixture.DisposeAsync();

    private static CancellationToken Cancellation => TestContext.CurrentContext.CancellationToken;

    private static RoslynToolContext Context => _fixture.Context;

    /// <inheritdoc />
    public void Dispose()
    {
        _fixture.Relay.Detach();
        GC.SuppressFinalize(this);
    }

    /// <summary>Every tool, in the one order that lets them all run against one workspace.</summary>
    [Test]
    public async Task TheToolsAnswerFromARealRoslynAgainstARealSolution()
    {
        _fixture.SkipIfDisabled();

        _output.WriteLine($"acquisition                     {_fixture.AcquisitionElapsed.TotalSeconds:F1} s");
        _output.WriteLine($"workspace load                  {_fixture.LoadElapsed.TotalSeconds:F1} s");

        await StatusPhaseAsync();
        await NavigationPhaseAsync();
        await DiagnosticsPhaseAsync();
        await CodeActionPhaseAsync();
        await RenamePhaseAsync();
        await FixAllPhaseAsync();
        await MoveTypePhaseAsync();
        await FormatPhaseAsync();

        ReportWorkingSet();
    }

    /// <summary>The tool every "loading" note points at, once the workspace is loaded.</summary>
    private async Task StatusPhaseAsync()
    {
        var status = await TimedAsync(
            "getWorkspaceStatus",
            () => WorkspaceTools.GetWorkspaceStatusAsync(Context, Cancellation));

        Assert.That(status.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(status.Engine, Is.EqualTo(OwnedRoslynEngine.Engine));
        Assert.That(status.Solution, Is.EqualTo("HelloSolution.slnx"));

        // Three projects in the solution file, and Roslyn's own progress stream says how many it is
        // loading — the two numbers agree or the load did not cover the solution (D78).
        Assert.That(status.ProjectsTotal, Is.EqualTo(3));
        Assert.That(status.ProjectsLoaded, Is.EqualTo(status.ProjectsTotal));
        Assert.That(status.Projects, Is.Not.Null);
        Assert.That(status.Projects!, Has.Some.Matches<ProjectSummary>(project => project!.Name == "Hello.Core"));

        // Hello.Core is multi-targeted on purpose; that is what produces C15's and C24's duplicates.
        var core = status.Projects!.Single(project => project.Name == "Hello.Core");
        Assert.That(core.TargetFrameworks, Is.EqualTo(["net10.0", "netstandard2.0"]));

        Assert.That(status.ProcessId, Is.Not.Null);
        Assert.That(status.RoslynVersion, Is.Not.Null);

        _output.WriteLine($"  solution                      {status.Solution}");
        _output.WriteLine($"  projects                      {status.ProjectsLoaded}/{status.ProjectsTotal}");
        _output.WriteLine($"  roslyn pid                    {status.ProcessId}");
        _output.WriteLine($"  roslyn working set            {status.MemoryMegabytes} MB");
    }

    /// <summary>Name-addressed lookup, and the cross-project references that prove the solution loaded.</summary>
    private async Task NavigationPhaseAsync()
    {
        var resolved = await TimedAsync(
            "resolveSymbol(Calculator.Compute)",
            () => SymbolTools.ResolveSymbolAsync(Context, "Calculator.Compute", cancellationToken: Cancellation));

        Assert.That(resolved.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(resolved.Matches, Is.Not.Null);

        Assert.That(resolved.Matches!, Has.Exactly(1).Matches<SymbolMatch>(match => match!.Path == "Hello.Core/Calculator.cs"));
        var compute = resolved.Matches!.Single(match => match.Path == "Hello.Core/Calculator.cs");

        Assert.That(compute.Name, Is.EqualTo("Compute"));
        Assert.That(compute.Kind, Is.EqualTo("method"));
        Assert.That(compute.Line, Is.EqualTo(Line("Hello.Core/Calculator.cs", "public int Compute()")));
        Assert.That(compute.Signature ?? string.Empty, Contains.Substring("Compute").Using(StringComparison.Ordinal));

        _output.WriteLine($"  {compute.Path}:{compute.Line}:{compute.Column}   {compute.Signature}");

        var references = await TimedAsync(
            "findReferences(Calculator.Compute)",
            () => SymbolTools.FindReferencesAsync(Context, "Calculator.Compute", cancellationToken: Cancellation));

        Assert.That(references.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(references.ByFile, Is.Not.Null);

        var files = references.ByFile!.Select(file => file.Path).ToArray();

        _output.WriteLine("  byFile                        " + string.Join(", ", files));

        // The three call sites live in three different projects, which is how "loaded the solution"
        // is told apart from "loaded the two projects the app references".
        Assert.That(files, Does.Contain("Hello.Core/Caller.cs"));
        Assert.That(files, Does.Contain("Hello.App/Program.cs"));
        Assert.That(files, Does.Contain("Hello.Tests/ShapeTests.cs"));
    }

    /// <summary>Both halves of <c>getDiagnostics</c>: the workspace pull and the file pull (D67).</summary>
    private async Task DiagnosticsPhaseAsync()
    {
        // C14: a closed file is reported only under a fullSolution compiler scope, which the engine
        // raises for the call and Roslyn keeps for the session.
        var solution = await TimedAsync(
            "getDiagnostics(scope: solution, minSeverity: error)",
            () => DiagnosticTools.GetDiagnosticsAsync(
                Context,
                scope: "solution",
                minSeverity: "error",
                cancellationToken: Cancellation));

        Assert.That(solution.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(solution.Diagnostics, Is.Not.Null);

        Assert.That(solution.Diagnostics!, Has.Exactly(1).Matches<DiagnosticEntry>(entry => entry!.Id == "CS0029"));
        var cs0029 = solution.Diagnostics!.Single(entry => entry.Id == "CS0029");

        Assert.That(cs0029.Path, Is.EqualTo("Hello.App/Program.cs"));
        Assert.That(cs0029.Severity, Is.EqualTo("error"));
        Assert.That(cs0029.Line, Is.EqualTo(Line("Hello.App/Program.cs", "int x = \"s\";")));

        _output.WriteLine($"  {cs0029.Path}:{cs0029.Line}  {cs0029.Id}  {cs0029.Message}");

        // C13: the same file, asked about by name, is not open — so the tool has to open it, pull and
        // close it again, and a build where that stopped working answers zero items successfully.
        var file = await TimedAsync(
            "getDiagnostics(scope: file, closed file)",
            () => DiagnosticTools.GetDiagnosticsAsync(
                Context,
                scope: "file",
                path: "Hello.App/Program.cs",
                minSeverity: "error",
                cancellationToken: Cancellation));

        Assert.That(file.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(file.Diagnostics ?? [], Has.Some.Matches<DiagnosticEntry>(entry => entry!.Id == "CS0029"));

        // Opened for the call and put back, so the workspace pull above keeps working (C15 skips
        // open documents).
        Assert.That(Context.Engine.IsDocumentOpen(Uri("Hello.App/Program.cs")), Is.False);
    }

    /// <summary>The lightbulb list at the IDE0005 site, with its fix-all scopes (D64).</summary>
    private async Task CodeActionPhaseAsync()
    {
        // C16: IDE0005 is reported at 0:0 for the whole using block, so 1:1 is where the fix is
        // offered.
        var actions = await TimedAsync(
            "getCodeActions(Calculator.cs:1:1)",
            () => CodeActionTools.GetCodeActionsAsync(
                Context,
                "Hello.Core/Calculator.cs",
                line: 1,
                col: 1,
                cancellationToken: Cancellation));

        Assert.That(actions.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(actions.Actions, Is.Not.Null);

        foreach (var action in actions.Actions!)
        {
            _output.WriteLine(
                $"  {action.Id}  \"{action.Title}\""
                + (action.FixAllScopes is { Count: > 0 } scopes ? "  fixAll: " + string.Join(",", scopes) : string.Empty));
        }

        Assert.That(
            actions.Actions!,
            Has.Exactly(1).Matches<CodeActionEntry>(action => action!.Title.Contains("Remove unnecessary usings", StringComparison.Ordinal)));
        var remove = actions.Actions!.Single(action => action.Title.Contains("Remove unnecessary usings", StringComparison.Ordinal));

        // C18's Fix All entry is folded onto the plain action rather than offered as a sibling (D64).
        Assert.That(remove.FixAllScopes, Is.Not.Null);
        Assert.That(remove.FixAllScopes!, Is.EqualTo(["document", "project", "solution"]));
    }

    /// <summary>A solution-wide semantic rename, previewed and then applied (D62).</summary>
    private async Task RenamePhaseAsync()
    {
        var callerPath = _fixture.Path_(McpToolsLiveFixture.CrlfBomFile);
        var before = File.ReadAllBytes(callerPath);

        Assert.That(HasBom(before), Is.True, "the fixture copy should start with a UTF-8 BOM");
        Assert.That(IsCrlf(before), Is.True, "the fixture copy should use CRLF endings");

        var preview = await TimedAsync(
            "renameSymbol(preview: true)",
            () => EditTools.RenameSymbolAsync(
                Context,
                "Calculator.Compute",
                "Calculate",
                preview: true,
                cancellationToken: Cancellation));

        Assert.That(preview.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(preview.Applied, Is.False);
        Assert.That(preview.Diff, Is.Not.Null);
        Assert.That(preview.Diff!, Contains.Substring("Calculate").Using(StringComparison.Ordinal));

        // A preview writes nothing at all, which is the property the whole preview/apply pair rests
        // on: the diff describes the operation that is about to happen, not a different one.
        Assert.That(File.ReadAllBytes(callerPath), Is.EqualTo(before));

        var applied = await TimedAsync(
            "renameSymbol(preview: false)",
            () => EditTools.RenameSymbolAsync(
                Context,
                "Calculator.Compute",
                "Calculate",
                cancellationToken: Cancellation));

        Assert.That(applied.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(applied.Applied, Is.True);
        Assert.That(applied.Files, Is.Not.Null);

        var changed = applied.Files!.Select(file => file.Path).ToArray();

        _output.WriteLine("  files                         " + string.Join(", ", changed));

        Assert.That(changed, Does.Contain("Hello.Core/Calculator.cs"));
        Assert.That(changed, Does.Contain("Hello.Core/Caller.cs"));
        Assert.That(changed, Does.Contain("Hello.App/Program.cs"));
        Assert.That(changed.Length >= 3, Is.True, $"the rename touched {changed.Length} file(s)");

        // D61: the codec reports how a file was encoded and writes it back that way. Without it a
        // one-word refactoring produces a diff touching every line, invisibly, because the code
        // still compiles.
        var after = File.ReadAllBytes(callerPath);

        Assert.That(HasBom(after), Is.True, "the rename dropped the byte-order mark");
        Assert.That(IsCrlf(after), Is.True, "the rename rewrote CRLF endings as LF");
        Assert.That(File.ReadAllText(callerPath), Contains.Substring("Calculate()").Using(StringComparison.Ordinal));

        // And the solution still compiles exactly as badly as it did before: one CS0029, no more.
        var diagnostics = await TimedAsync(
            "getDiagnostics after the rename",
            () => DiagnosticTools.GetDiagnosticsAsync(
                Context,
                scope: "solution",
                minSeverity: "error",
                cancellationToken: Cancellation));

        Assert.That(diagnostics.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That((diagnostics.Diagnostics ?? []).Select(entry => entry.Id), Has.All.EqualTo("CS0029"));
    }

    /// <summary>Roslyn's own fix-all across the solution (D65, C20).</summary>
    private async Task FixAllPhaseAsync()
    {
        var calculator = _fixture.Path_("Hello.Core/Calculator.cs");

        Assert.That(File.ReadAllText(calculator), Contains.Substring("using System.Text;").Using(StringComparison.Ordinal));

        // A document pull always sees analyzer diagnostics; a workspace one sees them only once the
        // analyzer scope is raised (C14) *and* the full-solution analyzer pass has finished, which on
        // a machine running the rest of this suite it may not have (C57). So the reliable assertion
        // is the file one, and it is also what a model does: it has just been shown the site.
        var analyzers = await TimedAsync(
            "getDiagnostics(scope: file, ids: [IDE0005])",
            () => DiagnosticTools.GetDiagnosticsAsync(
                Context,
                scope: "file",
                path: "Hello.Core/Calculator.cs",
                minSeverity: "hint",
                includeAnalyzers: true,
                ids: ["IDE0005"],
                cancellationToken: Cancellation));

        _output.WriteLine("  IDE0005                       "
                          + string.Join(", ", (analyzers.Diagnostics ?? []).Select(entry => $"{entry.Path}:{entry.Line}")));

        Assert.That(analyzers.Diagnostics ?? [], Has.Some.Matches<DiagnosticEntry>(entry => entry!.Path == "Hello.Core/Calculator.cs"));

        // C58: with nothing changed since the last solution pull, Roslyn holds the next one. It has
        // to come back anyway, with the answer it gave before, rather than hanging the tool call for
        // ever — which is what an unbounded pull did.
        var repeated = await TimedAsync(
            "getDiagnostics(scope: solution) with nothing changed",
            () => DiagnosticTools.GetDiagnosticsAsync(
                Context,
                scope: "solution",
                minSeverity: "error",
                cancellationToken: Cancellation));

        Assert.That(repeated.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(repeated.Diagnostics ?? [], Has.Some.Matches<DiagnosticEntry>(entry => entry!.Id == "CS0029"));

        // The fix reaches the whole solution; the *site* is looked for in the file the caller named,
        // which is the cheap and reliable half of the same question.
        var fixAll = await TimedAsync(
            "fixDiagnostics(IDE0005, scope: solution)",
            () => CodeActionTools.FixDiagnosticsAsync(
                Context,
                "IDE0005",
                scope: "solution",
                path: "Hello.Core/Calculator.cs",
                cancellationToken: Cancellation));

        Assert.That(fixAll.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(fixAll.Applied, Is.True);

        var changed = fixAll.Files!.Select(file => file.Path).ToArray();

        _output.WriteLine("  files                         " + string.Join(", ", changed));

        // The one unnecessary using in the solution is Calculator.cs's; a fix-all that rewrote
        // anything else would be removing a using something needs.
        Assert.That(changed, Is.EqualTo(["Hello.Core/Calculator.cs"]));
        Assert.That(File.ReadAllText(calculator), !Contains.Substring("using System.Text;").Using(StringComparison.Ordinal));
    }

    /// <summary>The refactoring that creates a file before it edits into it (C21).</summary>
    private async Task MoveTypePhaseAsync()
    {
        var rectangle = _fixture.Path_("Hello.Core/Rectangle.cs");

        Assert.That(File.Exists(rectangle), Is.False, "Rectangle.cs should not exist yet");

        var (line, column) = Position("Hello.Core/Square.cs", "public sealed class Rectangle", "Rectangle");

        var moved = await TimedAsync(
            "applyCodeAction(Move type to Rectangle.cs)",
            () => CodeActionTools.ApplyCodeActionAsync(
                Context,
                "Hello.Core/Square.cs",
                line,
                column,
                title: "Move type to Rectangle.cs",
                cancellationToken: Cancellation));

        Assert.That(moved.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(moved.Applied, Is.True);
        Assert.That(File.Exists(rectangle), Is.True, "the create operation did not produce Rectangle.cs");
        Assert.That(File.ReadAllText(rectangle), Contains.Substring("class Rectangle").Using(StringComparison.Ordinal));

        _output.WriteLine("  files                         "
                          + string.Join(", ", moved.Files!.Select(file => file.Path)));

        // C33 in the engine: a created .cs file is invisible to Roslyn until the owning project is
        // reported changed, so the new type has to become resolvable without a restart.
        await WaitForAsync(
            async () =>
            {
                var resolved = await SymbolTools.ResolveSymbolAsync(
                    Context, "Rectangle", cancellationToken: Cancellation);

                return resolved.Matches?.Any(match => match.Path == "Hello.Core/Rectangle.cs") == true;
            },
            TimeSpan.FromSeconds(20),
            "Rectangle was never found in its new file; the C33 project nudge did not reach Roslyn");

        _output.WriteLine("  Rectangle resolves from its new file");
    }

    /// <summary>Formatting, and the idempotence that makes it safe to run after anything.</summary>
    /// <remarks>
    /// Against a file written badly on purpose. Every file in the fixture is already formatted, so
    /// running this on one of them would assert that a second run changes nothing after a first run
    /// that also changed nothing — which passes whatever <c>textDocument/formatting</c> does, up to
    /// and including answering with an empty array every time.
    /// </remarks>
    private async Task FormatPhaseAsync()
    {
        const string Path = "Hello.Core/Messy.cs";

        File.WriteAllText(
            _fixture.Path_(Path),
            "namespace Hello.Core;\n\npublic sealed class Messy\n{\n        public int Value  =>   41   +  1 ;\n}\n");

        await Context.Engine.NotifyFilesChangedAsync(
            [new FileChange(Uri(Path), FileChangeType.Created)],
            Cancellation);

        var first = await TimedAsync(
            "formatCode(organizeUsings: true)",
            () => EditTools.FormatCodeAsync(
                Context,
                [Path],
                organizeUsings: true,
                cancellationToken: Cancellation));

        Assert.That(first.Status, Is.EqualTo(ToolStatus.Ok));
        _output.WriteLine($"  first run                     {first.FilesChanged} file(s), {first.Edits} edit(s)");

        Assert.That(first.Applied, Is.True, "formatting a badly formatted file changed nothing");
        Assert.That(first.FilesChanged, Is.EqualTo(1));

        var second = await TimedAsync(
            "formatCode(again)",
            () => EditTools.FormatCodeAsync(
                Context,
                [Path],
                organizeUsings: true,
                cancellationToken: Cancellation));

        Assert.That(second.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(second.FilesChanged, Is.EqualTo(0));
        Assert.That(second.Edits, Is.EqualTo(0));
    }

    /// <summary>C38 re-measured, this time for the MCP half's own child.</summary>
    private void ReportWorkingSet()
    {
        var state = _fixture.Ready;

        if (state?.ProcessId is not { } pid)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Refresh();

            _output.WriteLine(
                "roslyn peak working set         "
                + (process.PeakWorkingSet64 / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            _output.WriteLine("roslyn peak working set         (the process has gone)");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private async Task<T> TimedAsync<T>(string what, Func<Task<T>> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await operation().ConfigureAwait(false);
        stopwatch.Stop();

        _output.WriteLine($"{what,-46} {stopwatch.Elapsed.TotalMilliseconds:F0} ms");

        return result;
    }

    /// <summary>Polls until a condition holds, because a watcher round trip is not observable.</summary>
    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan budget, string failure)
    {
        var deadline = DateTime.UtcNow + budget;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(500, Cancellation).ConfigureAwait(false);
        }

        Assert.Fail(failure);
    }

    /// <summary>The <c>file:</c> URI of a workspace-relative path.</summary>
    private static string Uri(string relativePath) => new Uri(_fixture.Path_(relativePath)).AbsoluteUri;

    /// <summary>
    /// The 1-based line a piece of text sits on, found rather than hard-coded.
    /// </summary>
    /// <remarks>
    /// A doc-comment edit in the fixture would otherwise move every position in this file, and the
    /// failure would look like a Roslyn regression rather than a fixture change.
    /// </remarks>
    private static int Line(string relativePath, string text) => Position(relativePath, text, text).Line;

    /// <summary>
    /// The 1-based line and column of a token on the line that holds some text.
    /// </summary>
    /// <remarks>
    /// Doc-comment lines are skipped, and that is not fussiness: the fixture's <c>Program.cs</c>
    /// explains its deliberate <c>int x = "s";</c> in a <c>&lt;remarks&gt;</c> block eleven lines
    /// above the statement itself, so a plain search finds the sentence about the bug rather than the
    /// bug.
    /// </remarks>
    /// <param name="relativePath">The file.</param>
    /// <param name="lineText">Text that identifies the line.</param>
    /// <param name="token">The token on it whose column is wanted.</param>
    private static (int Line, int Column) Position(string relativePath, string lineText, string token)
    {
        var lines = File.ReadAllLines(_fixture.Path_(relativePath));

        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].TrimStart().StartsWith("///", StringComparison.Ordinal))
            {
                continue;
            }

            var column = lines[index].IndexOf(lineText, StringComparison.Ordinal);

            if (column < 0)
            {
                continue;
            }

            var tokenColumn = lines[index].IndexOf(token, column, StringComparison.Ordinal);

            return (index + 1, tokenColumn + 1);
        }

        Assert.Fail($"'{lineText}' is not in {relativePath}");
        return default;
    }

    private static bool HasBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static bool IsCrlf(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var newlines = 0;

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n')
            {
                continue;
            }

            newlines++;

            if (index == 0 || text[index - 1] != '\r')
            {
                return false;
            }
        }

        return newlines > 0;
    }
}
