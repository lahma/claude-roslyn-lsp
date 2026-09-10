using System.Diagnostics;
using System.Globalization;
using System.Text;

using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Mcp.Models;
using ClaudeRoslynLsp.Mcp.Tools;

using Xunit;

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
/// phase asks about, the rename changes the symbol the navigation phases resolve. xunit does not
/// order tests within a class, so a sequence that must be a sequence is written as one.
/// </para>
/// <para>
/// Every phase prints its timing, and the run ends with the Roslyn process's working set. Those
/// numbers are the answer to "what does this cost", and they are the reason the phases report even
/// when they pass.
/// </para>
/// </remarks>
[Collection(McpToolsLiveCollection.Name)]
public class McpToolsLiveTests : IDisposable
{
    private readonly McpToolsLiveFixture _fixture;
    private readonly ITestOutputHelper _output;

    public McpToolsLiveTests(McpToolsLiveFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;

        if (fixture.Enabled)
        {
            fixture.Relay.PointAt(new TestOutputLogger(output, Microsoft.Extensions.Logging.LogLevel.Information));
        }
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private RoslynToolContext Context => _fixture.Context;

    /// <inheritdoc />
    public void Dispose()
    {
        _fixture.Relay.Detach();
        GC.SuppressFinalize(this);
    }

    /// <summary>Every tool, in the one order that lets them all run against one workspace.</summary>
    [Fact]
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

        Assert.Equal(ToolStatus.Ok, status.Status);
        Assert.Equal(OwnedRoslynEngine.Engine, status.Engine);
        Assert.Equal("HelloSolution.slnx", status.Solution);

        // Three projects in the solution file, and Roslyn's own progress stream says how many it is
        // loading — the two numbers agree or the load did not cover the solution (D78).
        Assert.Equal(3, status.ProjectsTotal);
        Assert.Equal(status.ProjectsTotal, status.ProjectsLoaded);
        Assert.NotNull(status.Projects);
        Assert.Contains(status.Projects!, project => project.Name == "Hello.Core");

        // Hello.Core is multi-targeted on purpose; that is what produces C15's and C24's duplicates.
        var core = status.Projects!.Single(project => project.Name == "Hello.Core");
        Assert.Equal(["net10.0", "netstandard2.0"], core.TargetFrameworks);

        Assert.NotNull(status.ProcessId);
        Assert.NotNull(status.RoslynVersion);

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

        Assert.Equal(ToolStatus.Ok, resolved.Status);
        Assert.NotNull(resolved.Matches);

        var compute = Assert.Single(resolved.Matches!, match => match.Path == "Hello.Core/Calculator.cs");

        Assert.Equal("Compute", compute.Name);
        Assert.Equal("method", compute.Kind);
        Assert.Equal(Line("Hello.Core/Calculator.cs", "public int Compute()"), compute.Line);
        Assert.Contains("Compute", compute.Signature ?? string.Empty, StringComparison.Ordinal);

        _output.WriteLine($"  {compute.Path}:{compute.Line}:{compute.Column}   {compute.Signature}");

        var references = await TimedAsync(
            "findReferences(Calculator.Compute)",
            () => SymbolTools.FindReferencesAsync(Context, "Calculator.Compute", cancellationToken: Cancellation));

        Assert.Equal(ToolStatus.Ok, references.Status);
        Assert.NotNull(references.ByFile);

        var files = references.ByFile!.Select(file => file.Path).ToArray();

        _output.WriteLine("  byFile                        " + string.Join(", ", files));

        // The three call sites live in three different projects, which is how "loaded the solution"
        // is told apart from "loaded the two projects the app references".
        Assert.Contains("Hello.Core/Caller.cs", files, StringComparer.Ordinal);
        Assert.Contains("Hello.App/Program.cs", files, StringComparer.Ordinal);
        Assert.Contains("Hello.Tests/ShapeTests.cs", files, StringComparer.Ordinal);
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

        Assert.Equal(ToolStatus.Ok, solution.Status);
        Assert.NotNull(solution.Diagnostics);

        var cs0029 = Assert.Single(solution.Diagnostics!, entry => entry.Id == "CS0029");

        Assert.Equal("Hello.App/Program.cs", cs0029.Path);
        Assert.Equal("error", cs0029.Severity);
        Assert.Equal(Line("Hello.App/Program.cs", "int x = \"s\";"), cs0029.Line);

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

        Assert.Equal(ToolStatus.Ok, file.Status);
        Assert.Contains(file.Diagnostics ?? [], entry => entry.Id == "CS0029");

        // Opened for the call and put back, so the workspace pull above keeps working (C15 skips
        // open documents).
        Assert.False(Context.Engine.IsDocumentOpen(Uri("Hello.App/Program.cs")));
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

        Assert.Equal(ToolStatus.Ok, actions.Status);
        Assert.NotNull(actions.Actions);

        foreach (var action in actions.Actions!)
        {
            _output.WriteLine(
                $"  {action.Id}  \"{action.Title}\""
                + (action.FixAllScopes is { Count: > 0 } scopes ? "  fixAll: " + string.Join(",", scopes) : string.Empty));
        }

        var remove = Assert.Single(
            actions.Actions!,
            action => action.Title.Contains("Remove unnecessary usings", StringComparison.Ordinal));

        // C18's Fix All entry is folded onto the plain action rather than offered as a sibling (D64).
        Assert.NotNull(remove.FixAllScopes);
        Assert.Equal(["document", "project", "solution"], remove.FixAllScopes!);
    }

    /// <summary>A solution-wide semantic rename, previewed and then applied (D62).</summary>
    private async Task RenamePhaseAsync()
    {
        var callerPath = _fixture.Path_(McpToolsLiveFixture.CrlfBomFile);
        var before = File.ReadAllBytes(callerPath);

        Assert.True(HasBom(before), "the fixture copy should start with a UTF-8 BOM");
        Assert.True(IsCrlf(before), "the fixture copy should use CRLF endings");

        var preview = await TimedAsync(
            "renameSymbol(preview: true)",
            () => EditTools.RenameSymbolAsync(
                Context,
                "Calculator.Compute",
                "Calculate",
                preview: true,
                cancellationToken: Cancellation));

        Assert.Equal(ToolStatus.Ok, preview.Status);
        Assert.False(preview.Applied);
        Assert.NotNull(preview.Diff);
        Assert.Contains("Calculate", preview.Diff!, StringComparison.Ordinal);

        // A preview writes nothing at all, which is the property the whole preview/apply pair rests
        // on: the diff describes the operation that is about to happen, not a different one.
        Assert.Equal(before, File.ReadAllBytes(callerPath));

        var applied = await TimedAsync(
            "renameSymbol(preview: false)",
            () => EditTools.RenameSymbolAsync(
                Context,
                "Calculator.Compute",
                "Calculate",
                cancellationToken: Cancellation));

        Assert.Equal(ToolStatus.Ok, applied.Status);
        Assert.True(applied.Applied);
        Assert.NotNull(applied.Files);

        var changed = applied.Files!.Select(file => file.Path).ToArray();

        _output.WriteLine("  files                         " + string.Join(", ", changed));

        Assert.Contains("Hello.Core/Calculator.cs", changed, StringComparer.Ordinal);
        Assert.Contains("Hello.Core/Caller.cs", changed, StringComparer.Ordinal);
        Assert.Contains("Hello.App/Program.cs", changed, StringComparer.Ordinal);
        Assert.True(changed.Length >= 3, $"the rename touched {changed.Length} file(s)");

        // D61: the codec reports how a file was encoded and writes it back that way. Without it a
        // one-word refactoring produces a diff touching every line, invisibly, because the code
        // still compiles.
        var after = File.ReadAllBytes(callerPath);

        Assert.True(HasBom(after), "the rename dropped the byte-order mark");
        Assert.True(IsCrlf(after), "the rename rewrote CRLF endings as LF");
        Assert.Contains("Calculate()", File.ReadAllText(callerPath), StringComparison.Ordinal);

        // And the solution still compiles exactly as badly as it did before: one CS0029, no more.
        var diagnostics = await TimedAsync(
            "getDiagnostics after the rename",
            () => DiagnosticTools.GetDiagnosticsAsync(
                Context,
                scope: "solution",
                minSeverity: "error",
                cancellationToken: Cancellation));

        Assert.Equal(ToolStatus.Ok, diagnostics.Status);
        Assert.All(diagnostics.Diagnostics ?? [], entry => Assert.Equal("CS0029", entry.Id));
    }

    /// <summary>Roslyn's own fix-all across the solution (D65, C20).</summary>
    private async Task FixAllPhaseAsync()
    {
        var calculator = _fixture.Path_("Hello.Core/Calculator.cs");

        Assert.Contains("using System.Text;", File.ReadAllText(calculator), StringComparison.Ordinal);

        // D82: a closed file's analyzer diagnostics need the analyzer scope raised as well as the
        // compiler one (C14's second half). Asserted here rather than only inside fixDiagnostics,
        // because "found no occurrence of IDE0005" is a symptom several things could cause.
        var analyzers = await TimedAsync(
            "getDiagnostics(scope: solution, ids: [IDE0005])",
            () => DiagnosticTools.GetDiagnosticsAsync(
                Context,
                scope: "solution",
                minSeverity: "hint",
                includeAnalyzers: true,
                ids: ["IDE0005"],
                cancellationToken: Cancellation));

        _output.WriteLine("  IDE0005                       "
                          + string.Join(", ", (analyzers.Diagnostics ?? []).Select(entry => $"{entry.Path}:{entry.Line}")));

        // The two halves of D82/D83 together: the analyzer scope raised as well as the compiler one
        // (C14), and the second pull that a scope change needs before the new set appears (C57).
        Assert.Contains(analyzers.Diagnostics ?? [], entry => entry.Path == "Hello.Core/Calculator.cs");

        // C58: with nothing changed since that pull, Roslyn holds the next one. It has to come back
        // anyway, with the same answer, rather than hanging the tool call for ever.
        var repeated = await TimedAsync(
            "getDiagnostics(scope: solution) with nothing changed",
            () => DiagnosticTools.GetDiagnosticsAsync(
                Context,
                scope: "solution",
                minSeverity: "hint",
                includeAnalyzers: true,
                ids: ["IDE0005"],
                cancellationToken: Cancellation));

        Assert.Equal(ToolStatus.Ok, repeated.Status);
        Assert.Contains(repeated.Diagnostics ?? [], entry => entry.Path == "Hello.Core/Calculator.cs");

        var fixAll = await TimedAsync(
            "fixDiagnostics(IDE0005, scope: solution)",
            () => CodeActionTools.FixDiagnosticsAsync(
                Context,
                "IDE0005",
                scope: "solution",
                cancellationToken: Cancellation));

        Assert.Equal(ToolStatus.Ok, fixAll.Status);
        Assert.True(fixAll.Applied);

        var changed = fixAll.Files!.Select(file => file.Path).ToArray();

        _output.WriteLine("  files                         " + string.Join(", ", changed));

        // The one unnecessary using in the solution is Calculator.cs's; a fix-all that rewrote
        // anything else would be removing a using something needs.
        Assert.Equal(["Hello.Core/Calculator.cs"], changed);
        Assert.DoesNotContain("using System.Text;", File.ReadAllText(calculator), StringComparison.Ordinal);
    }

    /// <summary>The refactoring that creates a file before it edits into it (C21).</summary>
    private async Task MoveTypePhaseAsync()
    {
        var rectangle = _fixture.Path_("Hello.Core/Rectangle.cs");

        Assert.False(File.Exists(rectangle), "Rectangle.cs should not exist yet");

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

        Assert.Equal(ToolStatus.Ok, moved.Status);
        Assert.True(moved.Applied);
        Assert.True(File.Exists(rectangle), "the create operation did not produce Rectangle.cs");
        Assert.Contains("class Rectangle", File.ReadAllText(rectangle), StringComparison.Ordinal);

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

        Assert.Equal(ToolStatus.Ok, first.Status);
        _output.WriteLine($"  first run                     {first.FilesChanged} file(s), {first.Edits} edit(s)");

        Assert.True(first.Applied, "formatting a badly formatted file changed nothing");
        Assert.Equal(1, first.FilesChanged);

        var second = await TimedAsync(
            "formatCode(again)",
            () => EditTools.FormatCodeAsync(
                Context,
                [Path],
                organizeUsings: true,
                cancellationToken: Cancellation));

        Assert.Equal(ToolStatus.Ok, second.Status);
        Assert.Equal(0, second.FilesChanged);
        Assert.Equal(0, second.Edits);
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
    private string Uri(string relativePath) => new Uri(_fixture.Path_(relativePath)).AbsoluteUri;

    /// <summary>
    /// The 1-based line a piece of text sits on, found rather than hard-coded.
    /// </summary>
    /// <remarks>
    /// A doc-comment edit in the fixture would otherwise move every position in this file, and the
    /// failure would look like a Roslyn regression rather than a fixture change.
    /// </remarks>
    private int Line(string relativePath, string text) => Position(relativePath, text, text).Line;

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
    private (int Line, int Column) Position(string relativePath, string lineText, string token)
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

/// <summary>
/// The collection that owns the one MCP engine.
/// </summary>
/// <remarks>
/// Its own collection, separate from <c>adapter-live</c>: both start a real Roslyn over a real
/// solution, and running them in parallel would put half a gigabyte and two solution loads on the
/// machine at once for no benefit. The fixture is shared so the load is paid once.
/// </remarks>
[CollectionDefinition(Name)]
#pragma warning disable CA1711 // xunit's own convention: a collection definition is named for what it is.
public sealed class McpToolsLiveCollection : ICollectionFixture<McpToolsLiveFixture>
#pragma warning restore CA1711
{
    /// <summary>The collection's name.</summary>
    public const string Name = "mcp-tools-live";
}
