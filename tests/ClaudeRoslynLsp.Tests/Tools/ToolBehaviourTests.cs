using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Edits;
using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Mcp.Models;
using ClaudeRoslynLsp.Mcp.Tools;
using ClaudeRoslynLsp.Tests.Mcp;

using ModelContextProtocol;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Tools;

/// <summary>
/// The tools themselves, against a scriptable engine (D60).
/// </summary>
/// <remarks>
/// These are the assertions that would otherwise need a loaded solution: that a rename reaches disk,
/// that a preview does not, that a fix-all asks for the scope it was told to, that the diagnostic
/// filters remove what C15 says arrives, and that a workspace nobody has loaded produces a status
/// rather than a hang.
/// </remarks>
public class ToolBehaviourTests
{
    /// <summary>The test's own cancellation, so a hung tool call is cancelled rather than waited on.</summary>
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RenameWritesEveryFileAndSaysSoInTheSameWordsEveryTime()
    {
        using var workspace = TempWorkspace.Create("tool-rename");
        var first = workspace.File_("Core/Calculator.cs", "int Compute() => 1;\n");
        var second = workspace.File_("App/Program.cs", "new Calculator().Compute();\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);

        Declare(engine, "Compute", 6, first, line: 0, character: 4, endCharacter: 11);

        engine.RenameEdit = new WorkspaceEdit
        {
            DocumentChanges =
            [
                Change(first, 0, 4, 0, 11, "Calculate"),
                Change(second, 0, 17, 0, 24, "Calculate"),
            ],
        };

        var result = await EditTools.RenameSymbolAsync(context, "Compute", "Calculate", cancellationToken: Cancellation);

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.True(result.Applied);
        Assert.Equal(2, result.FilesChanged);
        Assert.Equal(2, result.Edits);
        Assert.Equal(EditResult.AppliedNote, result.Note);

        Assert.Equal("int Calculate() => 1;\n", File.ReadAllText(first));
        Assert.Equal("new Calculator().Calculate();\n", File.ReadAllText(second));

        Assert.Equal(
            ["App/Program.cs", "Core/Calculator.cs"],
            result.Files!.Select(file => file.Path).Order(StringComparer.Ordinal));

        Assert.Contains("Calculate", result.Diff, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule that keeps every later answer from being confidently stale: Roslyn is told what was
    /// written, immediately.
    /// </summary>
    [Fact]
    public async Task AnAppliedEditTellsRoslynWhatChanged()
    {
        using var workspace = TempWorkspace.Create("tool-notify");
        var file = workspace.File_("a.cs", "class A;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);
        Declare(engine, "A", 5, file, line: 0, character: 6, endCharacter: 7);
        engine.RenameEdit = new WorkspaceEdit { DocumentChanges = [Change(file, 0, 6, 0, 7, "B")] };

        await EditTools.RenameSymbolAsync(context, "A", "B", cancellationToken: Cancellation);

        var change = Assert.Single(engine.NotifiedChanges);
        Assert.Equal(FileChangeType.Changed, change.Type);
        Assert.Equal(WorkspacePathGuard.ToUri(file), change.Uri);
    }

    /// <summary>
    /// A preview writes nothing, and the apply that follows reuses the edit it was shown rather than
    /// resolving a second one against a file that may have moved on (D62).
    /// </summary>
    [Fact]
    public async Task PreviewWritesNothingAndTheApplyThatFollowsReusesIt()
    {
        using var workspace = TempWorkspace.Create("tool-preview");
        var file = workspace.File_("a.cs", "class A;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);
        Declare(engine, "A", 5, file, line: 0, character: 6, endCharacter: 7);
        engine.RenameEdit = new WorkspaceEdit { DocumentChanges = [Change(file, 0, 6, 0, 7, "B")] };

        var preview = await EditTools.RenameSymbolAsync(context, "A", "B", preview: true, cancellationToken: Cancellation);

        Assert.False(preview.Applied);
        Assert.Equal(EditResult.PreviewNote, preview.Note);
        Assert.Equal(1, preview.FilesChanged);
        Assert.Contains("+class B;", preview.Diff, StringComparison.Ordinal);
        Assert.Equal("class A;\n", File.ReadAllText(file));
        Assert.Empty(engine.NotifiedChanges);

        var applied = await EditTools.RenameSymbolAsync(context, "A", "B", cancellationToken: Cancellation);

        Assert.True(applied.Applied);
        Assert.Equal("class B;\n", File.ReadAllText(file));

        // One rename request for two calls: the second one came out of the cache.
        Assert.Single(engine.Calls, call => call == nameof(FakeRoslynEngine.RenameAsync));
    }

    [Fact]
    public async Task ANotRenamableSymbolIsRefusedWithAReason()
    {
        using var workspace = TempWorkspace.Create("tool-notrenamable");
        var file = workspace.File_("a.cs", "class A;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);
        Declare(engine, "A", 5, file, line: 0, character: 6, endCharacter: 7);
        engine.PrepareRename = null;

        var exception = await Assert.ThrowsAsync<McpException>(
            () => EditTools.RenameSymbolAsync(context, "A", "B", cancellationToken: Cancellation));

        Assert.Contains("cannot rename", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(FakeRoslynEngine.RenameAsync), engine.Calls);
    }

    /// <summary>
    /// The fix-all path: the plain action's title and data with a case-sensitive scope, which is what
    /// C20 and S4b showed the server accepting.
    /// </summary>
    [Fact]
    public async Task ApplyCodeActionResolvesAFixAllWithTheRequestedScope()
    {
        using var workspace = TempWorkspace.Create("tool-fixall");
        var file = workspace.File_("Core/Calculator.cs", "using System;\nusing System.Text;\n\nclass C;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);

        engine.CodeActions.Add(RoslynPayloads.Action(RoslynPayloads.RemoveUnnecessaryUsings));
        engine.CodeActions.Add(RoslynPayloads.Action(RoslynPayloads.FixAllRemoveUnnecessaryUsings));

        engine.FixAllEdit = new WorkspaceEdit { DocumentChanges = [Change(file, 0, 0, 3, 0, string.Empty)] };

        var result = await CodeActionTools.ApplyCodeActionAsync(
            context,
            "Core/Calculator.cs",
            line: 2,
            col: 1,
            title: "Remove unnecessary usings",
            fixAllScope: "solution", cancellationToken: Cancellation);

        Assert.True(result.Applied);
        Assert.Equal(FixAllScope.Solution, engine.RequestedFixAllScope);
        Assert.Equal("Remove unnecessary usings", engine.RequestedFixAllTitle);
        Assert.Equal("class C;\n", File.ReadAllText(file));
    }

    /// <summary>Without a scope it is a plain resolve, and the fix-all method is never called.</summary>
    [Fact]
    public async Task ApplyCodeActionWithoutAScopeResolvesTheActionItself()
    {
        using var workspace = TempWorkspace.Create("tool-apply");
        var file = workspace.File_("Core/Calculator.cs", "using System;\nusing System.Text;\n\nclass C;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);

        engine.CodeActions.Add(RoslynPayloads.Action(RoslynPayloads.RemoveUnnecessaryUsings));

        engine.ResolvedEdits["Remove unnecessary usings"] =
            new WorkspaceEdit { DocumentChanges = [Change(file, 0, 0, 3, 0, string.Empty)] };

        var listed = await CodeActionTools.GetCodeActionsAsync(context, "Core/Calculator.cs", 2, 1, cancellationToken: Cancellation);
        var id = Assert.Single(listed.Actions!).Id;

        var result = await CodeActionTools.ApplyCodeActionAsync(context, "Core/Calculator.cs", 2, 1, id, cancellationToken: Cancellation);

        Assert.True(result.Applied);
        Assert.Null(engine.RequestedFixAllScope);
        Assert.Equal("class C;\n", File.ReadAllText(file));
    }

    /// <summary>
    /// The error a model can act on: what it asked for is not there, and here is what is.
    /// </summary>
    [Fact]
    public async Task ApplyCodeActionListsTheCurrentOptionsWhenNothingMatches()
    {
        using var workspace = TempWorkspace.Create("tool-nomatch");
        workspace.File_("Core/Calculator.cs", "class C;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);
        engine.CodeActions.Add(RoslynPayloads.Action(RoslynPayloads.MakeStatic));

        var exception = await Assert.ThrowsAsync<McpException>(
            () => CodeActionTools.ApplyCodeActionAsync(context, "Core/Calculator.cs", 1, 1, title: "Nope", cancellationToken: Cancellation));

        Assert.Contains("Make static", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// C13: a file that is not open answers with nothing, so a file-scoped pull opens it with the
    /// bytes on disk — and closes it again, because it was not open before.
    /// </summary>
    [Fact]
    public async Task FileScopedDiagnosticsOpenTheDocumentAndPutItBack()
    {
        using var workspace = TempWorkspace.Create("tool-file-diagnostics");
        var file = workspace.File_("a.cs", "class A;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);

        engine.DocumentDiagnostics[WorkspacePathGuard.ToUri(file)] = new DocumentDiagnosticReport
        {
            Items = [Diagnostic("CS0029", 1, "Cannot implicitly convert type", 9, 16)],
        };

        var result = await DiagnosticTools.GetDiagnosticsAsync(context, "file", "a.cs", cancellationToken: Cancellation);

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Equal("CS0029", Assert.Single(result.Diagnostics!).Id);
        Assert.Equal(10, result.Diagnostics![0].Line);
        Assert.Equal(17, result.Diagnostics[0].Column);

        Assert.Contains(nameof(FakeRoslynEngine.OpenDocumentAsync), engine.Calls);
        Assert.Contains(nameof(FakeRoslynEngine.CloseDocumentAsync), engine.Calls);
        Assert.Empty(engine.OpenDocuments);

        Assert.Contains("not a build", result.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// A document the LSP half has open has unsaved changes in Roslyn's mirror, and closing it would
    /// silently revert Roslyn's view to the bytes on disk.
    /// </summary>
    [Fact]
    public async Task AnAlreadyOpenDocumentIsLeftOpen()
    {
        using var workspace = TempWorkspace.Create("tool-open-document");
        var file = workspace.File_("a.cs", "class A;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);
        engine.OpenDocuments.Add(WorkspacePathGuard.ToUri(file));

        await DiagnosticTools.GetDiagnosticsAsync(context, "file", "a.cs", cancellationToken: Cancellation);

        Assert.DoesNotContain(nameof(FakeRoslynEngine.OpenDocumentAsync), engine.Calls);
        Assert.DoesNotContain(nameof(FakeRoslynEngine.CloseDocumentAsync), engine.Calls);
        Assert.Single(engine.OpenDocuments);
    }

    /// <summary>
    /// Analyzer diagnostics are opt-in, and the severity floor is a warning, so the default answer is
    /// "what stops this compiling" rather than "everything an IDE would underline".
    /// </summary>
    [Fact]
    public async Task AnalyzerDiagnosticsAndSubWarningSeveritiesAreFilteredOutByDefault()
    {
        using var workspace = TempWorkspace.Create("tool-filter");
        var file = workspace.File_("a.cs", "class A;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);

        engine.DocumentDiagnostics[WorkspacePathGuard.ToUri(file)] = new DocumentDiagnosticReport
        {
            Items =
            [
                Diagnostic("CS0029", 1, "compiler error", 1, 1),
                Diagnostic("CS0168", 2, "compiler warning", 2, 1),
                Diagnostic("CS8019", 3, "compiler information", 3, 1),
                Diagnostic("CA1822", 3, "analyzer information", 4, 1),
                Diagnostic("IDE0005", 4, "analyzer hint", 5, 1),
            ],
        };

        var byDefault = await DiagnosticTools.GetDiagnosticsAsync(context, "file", "a.cs", cancellationToken: Cancellation);
        Assert.Equal(["CS0029", "CS0168"], byDefault.Diagnostics!.Select(item => item.Id));

        var withAnalyzers = await DiagnosticTools.GetDiagnosticsAsync(
            context, "file", "a.cs", minSeverity: "hint", includeAnalyzers: true, cancellationToken: Cancellation);

        // Sorted by severity first, then by file and position: the two informational ones are on
        // lines 4 and 5 of the same file, so CS8019 comes before CA1822.
        Assert.Equal(
            ["CS0029", "CS0168", "CS8019", "CA1822", "IDE0005"],
            withAnalyzers.Diagnostics!.Select(item => item.Id));

        // An explicit id list is the caller being specific, and overrides the analyzer switch.
        var byId = await DiagnosticTools.GetDiagnosticsAsync(
            context, "file", "a.cs", minSeverity: "hint", ids: ["IDE0005"], cancellationToken: Cancellation);

        Assert.Equal("IDE0005", Assert.Single(byId.Diagnostics!).Id);
    }

    /// <summary>C16: the VS-private tags never reach a model, and the LSP ones do.</summary>
    [Fact]
    public async Task OnlySpecificationTagsAreForwarded()
    {
        using var workspace = TempWorkspace.Create("tool-tags");
        var file = workspace.File_("a.cs", "class A;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);

        engine.DocumentDiagnostics[WorkspacePathGuard.ToUri(file)] = new DocumentDiagnosticReport
        {
            Items =
            [
                Diagnostic("CS0168", 2, "unused", 1, 1) with
                {
                    Tags = [2147483641, 2147483643, 2147483640, 2147483645, 1],
                },
            ],
        };

        var result = await DiagnosticTools.GetDiagnosticsAsync(context, "file", "a.cs", cancellationToken: Cancellation);

        Assert.Equal(["unnecessary"], Assert.Single(result.Diagnostics!).Tags);
    }

    /// <summary>
    /// C14 and C15 together: the scope is raised for the call, and the answer's generated files,
    /// project files and per-target-framework duplicates are removed.
    /// </summary>
    [Fact]
    public async Task SolutionScopeRaisesTheCompilerScopeAndCleansUpWhatComesBack()
    {
        using var workspace = TempWorkspace.Create("tool-solution-diagnostics");

        var (context, engine) = ToolTestHost.CreateContext(workspace);

        var real = WorkspacePathGuard.ToUri(workspace.Path_("Core", "Caller.cs"));
        var generated = WorkspacePathGuard.ToUri(workspace.Path_("Core", "obj", "Debug", "net10.0", "AssemblyInfo.cs"));
        var project = WorkspacePathGuard.ToUri(workspace.Path_("Core", "Core.csproj"));

        engine.WorkspaceDiagnostics = new WorkspaceDiagnosticReport
        {
            Items =
            [
                // The same file twice, once per target framework, exactly as C15 describes.
                new WorkspaceDocumentDiagnosticReport { Uri = real, Items = [Diagnostic("CS0168", 2, "unused", 4, 15)] },
                new WorkspaceDocumentDiagnosticReport { Uri = real, Items = [Diagnostic("CS0168", 2, "unused", 4, 15)] },
                new WorkspaceDocumentDiagnosticReport { Uri = generated, Items = [Diagnostic("CS0169", 2, "generated", 1, 1)] },
                new WorkspaceDocumentDiagnosticReport { Uri = project, Items = [Diagnostic("NU1000", 2, "project", 1, 1)] },
            ],
        };

        var result = await DiagnosticTools.GetDiagnosticsAsync(context, "solution", cancellationToken: Cancellation);

        Assert.Equal(CompilerDiagnosticsScope.FullSolution, engine.CompilerScope);
        Assert.Equal("Core/Caller.cs", Assert.Single(result.Diagnostics!).Path);
        Assert.Equal(1, result.TotalCount);
        Assert.Contains("scope: \"file\"", result.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// C24: a multi-targeted project answers once per target framework, and a reference list that
    /// reported each hit twice would make a model believe there is twice as much to change.
    /// </summary>
    [Fact]
    public async Task ReferencesAreDeDuplicatedAcrossTargetFrameworksAndCarryTheirSourceLine()
    {
        using var workspace = TempWorkspace.Create("tool-references");
        var declaration = workspace.File_("Core/Calculator.cs", "class C\n{\n    int Compute() => 1;\n}\n");
        var caller = workspace.File_("App/Program.cs", "new C().Compute();\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);
        Declare(engine, "Compute", 6, declaration, line: 2, character: 8, endCharacter: 15);

        engine.References.Add(Location(declaration, 2, 8, 15));
        engine.References.Add(Location(declaration, 2, 8, 15));
        engine.References.Add(Location(caller, 0, 8, 15));

        var result = await SymbolTools.FindReferencesAsync(context, "Compute", cancellationToken: Cancellation);

        Assert.Equal(2, result.TotalCount);
        var references = result.References!;
        Assert.Equal(["App/Program.cs", "Core/Calculator.cs"], references.Select(item => item.Path));
        Assert.Equal("new C().Compute();", references[0].LineText);
        Assert.Equal("int Compute() => 1;", references[1].LineText);
        Assert.Equal(2, result.ByFile!.Count);
    }

    /// <summary>
    /// A name that matches several symbols is reported as a list rather than resolved to whichever
    /// one came first — picking one would rename the wrong member with total confidence.
    /// </summary>
    [Fact]
    public async Task AnAmbiguousNameIsRefusedWithItsCandidates()
    {
        using var workspace = TempWorkspace.Create("tool-ambiguous");
        var first = workspace.File_("Core/A.cs", "class A { void Run() { } }\n");
        var second = workspace.File_("Core/B.cs", "class B { void Run() { } }\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);
        engine.Symbols.Add(Symbol("Run", 6, first, 0, 15, 18, "in A (project Core (net10.0))"));
        engine.Symbols.Add(Symbol("Run", 6, second, 0, 15, 18, "in B (project Core (net10.0))"));

        var exception = await Assert.ThrowsAsync<McpException>(
            () => EditTools.RenameSymbolAsync(context, "Run", "Execute", cancellationToken: Cancellation));

        Assert.Contains("Core/A.cs", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Core/B.cs", exception.Message, StringComparison.Ordinal);

        // resolveSymbol itself reports them instead of failing: listing the candidates is its job.
        var resolved = await SymbolTools.ResolveSymbolAsync(context, "Run", cancellationToken: Cancellation);
        Assert.Equal(2, resolved.TotalCount);
    }

    /// <summary>A longer name narrows it down, which is the whole point of segment-suffix matching.</summary>
    [Fact]
    public async Task AQualifiedNamePicksOneOfTheCandidates()
    {
        using var workspace = TempWorkspace.Create("tool-qualified");
        var first = workspace.File_("Core/A.cs", "class A { void Run() { } }\n");
        var second = workspace.File_("Core/B.cs", "class B { void Run() { } }\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);
        engine.Symbols.Add(Symbol("Run", 6, first, 0, 15, 18, "in A (project Core (net10.0))"));
        engine.Symbols.Add(Symbol("Run", 6, second, 0, 15, 18, "in B (project Core (net10.0))"));

        var resolved = await SymbolTools.ResolveSymbolAsync(context, "B.Run", cancellationToken: Cancellation);

        Assert.Equal("Core/B.cs", Assert.Single(resolved.Matches!).Path);
        Assert.Equal("B.Run", resolved.Matches![0].FullName);
        Assert.Equal("method", resolved.Matches[0].Kind);
    }

    /// <summary>
    /// The gate (D66). Nothing is asked of Roslyn, the answer says what is happening, and the call
    /// returns rather than hanging for two minutes.
    /// </summary>
    [Fact]
    public async Task EveryToolAnswersWithALoadingStatusRatherThanHanging()
    {
        using var workspace = TempWorkspace.Create("tool-loading");
        workspace.File_("a.cs", "class A;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);
        engine.State = new WorkspaceState(WorkspaceLoadStatus.Loading, ProjectsLoaded: 1, ProjectsTotal: 4);

        Assert.Equal(ToolStatus.Loading, (await WorkspaceTools.GetWorkspaceStatusAsync(context, cancellationToken: Cancellation)).Status);
        Assert.Equal(ToolStatus.Loading, (await SymbolTools.ResolveSymbolAsync(context, "A", cancellationToken: Cancellation)).Status);
        Assert.Equal(ToolStatus.Loading, (await SymbolTools.GetTypeMembersAsync(context, "A", cancellationToken: Cancellation)).Status);
        Assert.Equal(ToolStatus.Loading, (await SymbolTools.FindReferencesAsync(context, "A", cancellationToken: Cancellation)).Status);
        Assert.Equal(ToolStatus.Loading, (await DiagnosticTools.GetDiagnosticsAsync(context, "file", "a.cs", cancellationToken: Cancellation)).Status);
        Assert.Equal(ToolStatus.Loading, (await CodeActionTools.GetCodeActionsAsync(context, "a.cs", 1, 1, cancellationToken: Cancellation)).Status);
        Assert.Equal(ToolStatus.Loading, (await CodeActionTools.ApplyCodeActionAsync(context, "a.cs", 1, 1, "x", cancellationToken: Cancellation)).Status);
        Assert.Equal(ToolStatus.Loading, (await CodeActionTools.FixDiagnosticsAsync(context, "IDE0005", cancellationToken: Cancellation)).Status);
        Assert.Equal(ToolStatus.Loading, (await EditTools.RenameSymbolAsync(context, "A", "B", cancellationToken: Cancellation)).Status);
        Assert.Equal(ToolStatus.Loading, (await EditTools.FormatCodeAsync(context, ["a.cs"], cancellationToken: Cancellation)).Status);

        // The gate is the only thing that was called: nothing was asked of a workspace that cannot
        // answer, and nothing was written.
        Assert.All(engine.Calls, call => Assert.Equal(nameof(FakeRoslynEngine.EnsureReadyAsync), call));

        var status = await WorkspaceTools.GetWorkspaceStatusAsync(context, cancellationToken: Cancellation);
        Assert.Equal(1, status.ProjectsLoaded);
        Assert.Equal(4, status.ProjectsTotal);
        Assert.Contains("still loading", status.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// The not-wired default (D69): the handshake and <c>tools/list</c> work, and every call says
    /// exactly what is missing rather than failing generically.
    /// </summary>
    [Fact]
    public async Task TheNotWiredEngineFailsWithOneClearSentence()
    {
        using var workspace = TempWorkspace.Create("tool-not-wired");
        var guard = new WorkspacePathGuard(workspace.Root);

        var context = new RoslynToolContext(
            new NotWiredRoslynEngine(),
            ClaudeRoslynLspOptions.FromEnvironment(static _ => null),
            guard,
            new WorkspaceEditApplier(guard),
            new EditCache());

        var status = await WorkspaceTools.GetWorkspaceStatusAsync(context, cancellationToken: Cancellation);
        Assert.Equal(ToolStatus.Failed, status.Status);
        Assert.Contains("not wired", status.Note, StringComparison.Ordinal);

        var resolved = await SymbolTools.ResolveSymbolAsync(context, "A", cancellationToken: Cancellation);
        Assert.Equal(ToolStatus.Failed, resolved.Status);
        Assert.Contains("doctor", resolved.Note, StringComparison.Ordinal);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => new NotWiredRoslynEngine().WorkspaceSymbolAsync("A", CancellationToken.None));

        Assert.Equal(NotWiredRoslynEngine.Message, exception.Message);
    }

    /// <summary>
    /// Formatting is a configuration pull, not a request parameter: the setting is pushed before the
    /// request, because Roslyn reads it when it formats and never takes it as an argument.
    /// </summary>
    [Fact]
    public async Task FormatCodeSetsTheOrganizeImportsSettingAndWritesTheResult()
    {
        using var workspace = TempWorkspace.Create("tool-format");
        var file = workspace.File_("a.cs", "class  A ;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);

        engine.FormattingEdits[WorkspacePathGuard.ToUri(file)] =
        [
            new LspTextEdit
            {
                Range = new LspRange
                {
                    Start = new LspPosition { Line = 0, Character = 0 },
                    End = new LspPosition { Line = 0, Character = 10 },
                },
                NewText = "class A;",
            },
        ];

        var result = await EditTools.FormatCodeAsync(context, ["a.cs"], organizeUsings: true, cancellationToken: Cancellation);

        Assert.True(result.Applied);
        Assert.True(engine.OrganizeImportsOnFormat);
        Assert.Equal("class A;\n", File.ReadAllText(file));
    }

    /// <summary>
    /// Formatting a file that is already formatted must report "nothing to do" rather than claiming
    /// an empty change, because a caller reads <c>applied</c> to decide whether to re-read the file.
    /// </summary>
    [Fact]
    public async Task FormattingAnAlreadyFormattedFileChangesNothing()
    {
        using var workspace = TempWorkspace.Create("tool-format-clean");
        workspace.File_("a.cs", "class A;\n");

        var (context, _) = ToolTestHost.CreateContext(workspace);

        var result = await EditTools.FormatCodeAsync(context, ["a.cs"], cancellationToken: Cancellation);

        Assert.False(result.Applied);
        Assert.Equal(EditResult.NothingToDoNote, result.Note);
    }

    /// <summary>
    /// <c>fixDiagnostics</c> finds a site of the diagnostic itself, so a caller does not have to pull
    /// diagnostics and pick one before asking for the fix.
    /// </summary>
    [Fact]
    public async Task FixDiagnosticsFindsASiteAndAppliesTheFixAllAtTheRequestedScope()
    {
        using var workspace = TempWorkspace.Create("tool-fixdiagnostics");
        var file = workspace.File_("Core/Calculator.cs", "using System;\nusing System.Text;\n\nclass C;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);

        engine.DocumentDiagnostics[WorkspacePathGuard.ToUri(file)] = new DocumentDiagnosticReport
        {
            Items = [Diagnostic("IDE0005", 4, "Using directive is unnecessary.", 0, 0)],
        };

        engine.CodeActions.Add(RoslynPayloads.Action(RoslynPayloads.RemoveUnnecessaryUsings));
        engine.CodeActions.Add(RoslynPayloads.Action(RoslynPayloads.FixAllRemoveUnnecessaryUsings));
        engine.FixAllEdit = new WorkspaceEdit { DocumentChanges = [Change(file, 0, 0, 3, 0, string.Empty)] };

        var result = await CodeActionTools.FixDiagnosticsAsync(context, "IDE0005", "file", "Core/Calculator.cs", cancellationToken: Cancellation);

        Assert.True(result.Applied);
        Assert.Equal(FixAllScope.Document, engine.RequestedFixAllScope);
        Assert.Equal("class C;\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task FixDiagnosticsSaysSoWhenTheDiagnosticIsNotThere()
    {
        using var workspace = TempWorkspace.Create("tool-fixdiagnostics-none");
        workspace.File_("a.cs", "class A;\n");

        var (context, _) = ToolTestHost.CreateContext(workspace);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => CodeActionTools.FixDiagnosticsAsync(context, "IDE0005", "file", "a.cs", cancellationToken: Cancellation));

        Assert.Contains("IDE0005", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>The workspace status carries what a report about a slow machine actually needs.</summary>
    [Fact]
    public async Task WorkspaceStatusReportsTheSolutionTheVersionAndTheMemory()
    {
        using var workspace = TempWorkspace.Create("tool-status");
        var solution = workspace.File_("Fixture.slnx", "<Solution />");
        var project = workspace.File_("Core/Core.csproj", "<Project />");

        var (context, engine) = ToolTestHost.CreateContext(workspace);

        engine.State = new WorkspaceState(
            WorkspaceLoadStatus.Ready,
            solution,
            ProjectsLoaded: 1,
            ProjectsTotal: 1,
            Projects: [new WorkspaceProject("Core", project, ["net10.0", "netstandard2.0"])],
            RoslynVersion: "5.12.0-1.26426.8",
            ProcessId: 4242,
            WorkingSetBytes: 253L * 1024 * 1024);

        var result = await WorkspaceTools.GetWorkspaceStatusAsync(context, cancellationToken: Cancellation);

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Equal("Fixture.slnx", result.Solution);
        Assert.Equal("Core/Core.csproj", Assert.Single(result.Projects!).Path);
        Assert.Equal(["net10.0", "netstandard2.0"], result.Projects![0].TargetFrameworks);
        Assert.Equal("5.12.0-1.26426.8", result.RoslynVersion);
        Assert.Equal(4242, result.ProcessId);
        Assert.Equal(253, result.MemoryMegabytes);
    }

    /// <summary>
    /// The path guard from the tool side: a model asking about a file outside the workspace gets a
    /// refusal that names the root, not a stack trace.
    /// </summary>
    [Fact]
    public async Task APathOutsideTheWorkspaceIsRefused()
    {
        using var workspace = TempWorkspace.Create("tool-escape");
        var (context, _) = ToolTestHost.CreateContext(workspace);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => DiagnosticTools.GetDiagnosticsAsync(context, "file", "../../elsewhere.cs", cancellationToken: Cancellation));

        Assert.Contains("outside the workspace root", exception.Message, StringComparison.Ordinal);
    }

    private static void Declare(
        FakeRoslynEngine engine,
        string name,
        int kind,
        string path,
        int line,
        int character,
        int endCharacter) =>
        engine.Symbols.Add(Symbol(name, kind, path, line, character, endCharacter, "in C (project Core (net10.0))"));

    private static SymbolInformation Symbol(
        string name,
        int kind,
        string path,
        int line,
        int character,
        int endCharacter,
        string container) =>
        new()
        {
            Name = name,
            Kind = kind,
            ContainerName = container,
            Location = Location(path, line, character, endCharacter),
        };

    private static LspLocation Location(string path, int line, int character, int endCharacter) => new()
    {
        Uri = WorkspacePathGuard.ToUri(path),
        Range = new LspRange
        {
            Start = new LspPosition { Line = line, Character = character },
            End = new LspPosition { Line = line, Character = endCharacter },
        },
    };

    private static RawDiagnostic Diagnostic(string id, int severity, string message, int line, int character) => new()
    {
        Range = new LspRange
        {
            Start = new LspPosition { Line = line, Character = character },
            End = new LspPosition { Line = line, Character = character + 1 },
        },
        Severity = severity,
        Code = System.Text.Json.JsonDocument.Parse("\"" + id + "\"").RootElement.Clone(),
        Message = message,
    };

    private static DocumentChange Change(
        string path,
        int line,
        int character,
        int endLine,
        int endCharacter,
        string newText) =>
        new()
        {
            TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = WorkspacePathGuard.ToUri(path) },
            Edits =
            [
                new LspTextEdit
                {
                    Range = new LspRange
                    {
                        Start = new LspPosition { Line = line, Character = character },
                        End = new LspPosition { Line = endLine, Character = endCharacter },
                    },
                    NewText = newText,
                },
            ],
        };
}
