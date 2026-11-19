using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Edits;
using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Mcp.Models;
using ClaudeRoslynLsp.Mcp.Tools;
using ClaudeRoslynLsp.Tests.Mcp;

using ModelContextProtocol;

using NUnit.Framework;

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
    private static CancellationToken Cancellation => TestContext.CurrentContext.CancellationToken;

    [Test]
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

        Assert.That(result.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(result.Applied, Is.True);
        Assert.That(result.FilesChanged, Is.EqualTo(2));
        Assert.That(result.Edits, Is.EqualTo(2));
        Assert.That(result.Note, Is.EqualTo(EditResult.AppliedNote));

        Assert.That(File.ReadAllText(first), Is.EqualTo("int Calculate() => 1;\n"));
        Assert.That(File.ReadAllText(second), Is.EqualTo("new Calculator().Calculate();\n"));

        Assert.That(
            result.Files!.Select(file => file.Path).Order(StringComparer.Ordinal),
            Is.EqualTo(["App/Program.cs", "Core/Calculator.cs"]));

        Assert.That(result.Diff, Contains.Substring("Calculate").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// The rule that keeps every later answer from being confidently stale: Roslyn is told what was
    /// written, immediately.
    /// </summary>
    [Test]
    public async Task AnAppliedEditTellsRoslynWhatChanged()
    {
        using var workspace = TempWorkspace.Create("tool-notify");
        var file = workspace.File_("a.cs", "class A;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);
        Declare(engine, "A", 5, file, line: 0, character: 6, endCharacter: 7);
        engine.RenameEdit = new WorkspaceEdit { DocumentChanges = [Change(file, 0, 6, 0, 7, "B")] };

        await EditTools.RenameSymbolAsync(context, "A", "B", cancellationToken: Cancellation);

        Assert.That(engine.NotifiedChanges, Has.Exactly(1).Items);
        var change = engine.NotifiedChanges.Single();
        Assert.That(change.Type, Is.EqualTo(FileChangeType.Changed));
        Assert.That(change.Uri, Is.EqualTo(WorkspacePathGuard.ToUri(file)));
    }

    /// <summary>
    /// A preview writes nothing, and the apply that follows reuses the edit it was shown rather than
    /// resolving a second one against a file that may have moved on (D62).
    /// </summary>
    [Test]
    public async Task PreviewWritesNothingAndTheApplyThatFollowsReusesIt()
    {
        using var workspace = TempWorkspace.Create("tool-preview");
        var file = workspace.File_("a.cs", "class A;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);
        Declare(engine, "A", 5, file, line: 0, character: 6, endCharacter: 7);
        engine.RenameEdit = new WorkspaceEdit { DocumentChanges = [Change(file, 0, 6, 0, 7, "B")] };

        var preview = await EditTools.RenameSymbolAsync(context, "A", "B", preview: true, cancellationToken: Cancellation);

        Assert.That(preview.Applied, Is.False);
        Assert.That(preview.Note, Is.EqualTo(EditResult.PreviewNote));
        Assert.That(preview.FilesChanged, Is.EqualTo(1));
        Assert.That(preview.Diff, Contains.Substring("+class B;").Using(StringComparison.Ordinal));
        Assert.That(File.ReadAllText(file), Is.EqualTo("class A;\n"));
        Assert.That(engine.NotifiedChanges, Is.Empty);

        var applied = await EditTools.RenameSymbolAsync(context, "A", "B", cancellationToken: Cancellation);

        Assert.That(applied.Applied, Is.True);
        Assert.That(File.ReadAllText(file), Is.EqualTo("class B;\n"));

        // One rename request for two calls: the second one came out of the cache.
        Assert.That(engine.Calls, Has.Exactly(1).Matches<string>(call => call == nameof(FakeRoslynEngine.RenameAsync)));
    }

    [Test]
    public async Task ANotRenamableSymbolIsRefusedWithAReason()
    {
        using var workspace = TempWorkspace.Create("tool-notrenamable");
        var file = workspace.File_("a.cs", "class A;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);
        Declare(engine, "A", 5, file, line: 0, character: 6, endCharacter: 7);
        engine.PrepareRename = null;

        var exception = await Assert.ThrowsAsync<McpException>(
            () => EditTools.RenameSymbolAsync(context, "A", "B", cancellationToken: Cancellation));

        Assert.That(exception!.Message, Contains.Substring("cannot rename").Using(StringComparison.Ordinal));
        Assert.That(engine.Calls, Does.Not.Contain(nameof(FakeRoslynEngine.RenameAsync)));
    }

    /// <summary>
    /// The fix-all path: the plain action's title and data with a case-sensitive scope, which is what
    /// C20 and S4b showed the server accepting.
    /// </summary>
    [Test]
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

        Assert.That(result.Applied, Is.True);
        Assert.That(engine.RequestedFixAllScope, Is.EqualTo(FixAllScope.Solution));
        Assert.That(engine.RequestedFixAllTitle, Is.EqualTo("Remove unnecessary usings"));
        Assert.That(File.ReadAllText(file), Is.EqualTo("class C;\n"));
    }

    /// <summary>Without a scope it is a plain resolve, and the fix-all method is never called.</summary>
    [Test]
    public async Task ApplyCodeActionWithoutAScopeResolvesTheActionItself()
    {
        using var workspace = TempWorkspace.Create("tool-apply");
        var file = workspace.File_("Core/Calculator.cs", "using System;\nusing System.Text;\n\nclass C;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);

        engine.CodeActions.Add(RoslynPayloads.Action(RoslynPayloads.RemoveUnnecessaryUsings));

        engine.ResolvedEdits["Remove unnecessary usings"] =
            new WorkspaceEdit { DocumentChanges = [Change(file, 0, 0, 3, 0, string.Empty)] };

        var listed = await CodeActionTools.GetCodeActionsAsync(context, "Core/Calculator.cs", 2, 1, cancellationToken: Cancellation);
        Assert.That(listed.Actions!, Has.Exactly(1).Items);
        var id = listed.Actions!.Single().Id;

        var result = await CodeActionTools.ApplyCodeActionAsync(context, "Core/Calculator.cs", 2, 1, id, cancellationToken: Cancellation);

        Assert.That(result.Applied, Is.True);
        Assert.That(engine.RequestedFixAllScope, Is.Null);
        Assert.That(File.ReadAllText(file), Is.EqualTo("class C;\n"));
    }

    /// <summary>
    /// The error a model can act on: what it asked for is not there, and here is what is.
    /// </summary>
    [Test]
    public async Task ApplyCodeActionListsTheCurrentOptionsWhenNothingMatches()
    {
        using var workspace = TempWorkspace.Create("tool-nomatch");
        workspace.File_("Core/Calculator.cs", "class C;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);
        engine.CodeActions.Add(RoslynPayloads.Action(RoslynPayloads.MakeStatic));

        var exception = await Assert.ThrowsAsync<McpException>(
            () => CodeActionTools.ApplyCodeActionAsync(context, "Core/Calculator.cs", 1, 1, title: "Nope", cancellationToken: Cancellation));

        Assert.That(exception!.Message, Contains.Substring("Make static").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// C13: a file that is not open answers with nothing, so a file-scoped pull opens it with the
    /// bytes on disk — and closes it again, because it was not open before.
    /// </summary>
    [Test]
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

        Assert.That(result.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(result.Diagnostics!, Has.Exactly(1).Items);
        Assert.That(result.Diagnostics!.Single().Id, Is.EqualTo("CS0029"));
        Assert.That(result.Diagnostics![0].Line, Is.EqualTo(10));
        Assert.That(result.Diagnostics[0].Column, Is.EqualTo(17));

        Assert.That(engine.Calls, Does.Contain(nameof(FakeRoslynEngine.OpenDocumentAsync)));
        Assert.That(engine.Calls, Does.Contain(nameof(FakeRoslynEngine.CloseDocumentAsync)));
        Assert.That(engine.OpenDocuments, Is.Empty);

        Assert.That(result.Note, Contains.Substring("not a build").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// A document the LSP half has open has unsaved changes in Roslyn's mirror, and closing it would
    /// silently revert Roslyn's view to the bytes on disk.
    /// </summary>
    [Test]
    public async Task AnAlreadyOpenDocumentIsLeftOpen()
    {
        using var workspace = TempWorkspace.Create("tool-open-document");
        var file = workspace.File_("a.cs", "class A;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);
        engine.OpenDocuments.Add(WorkspacePathGuard.ToUri(file));

        await DiagnosticTools.GetDiagnosticsAsync(context, "file", "a.cs", cancellationToken: Cancellation);

        Assert.That(engine.Calls, Does.Not.Contain(nameof(FakeRoslynEngine.OpenDocumentAsync)));
        Assert.That(engine.Calls, Does.Not.Contain(nameof(FakeRoslynEngine.CloseDocumentAsync)));
        Assert.That(engine.OpenDocuments, Has.Exactly(1).Items);
    }

    /// <summary>
    /// Analyzer diagnostics are opt-in, and the severity floor is a warning, so the default answer is
    /// "what stops this compiling" rather than "everything an IDE would underline".
    /// </summary>
    [Test]
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
        Assert.That(byDefault.Diagnostics!.Select(item => item.Id), Is.EqualTo(["CS0029", "CS0168"]));

        var withAnalyzers = await DiagnosticTools.GetDiagnosticsAsync(
            context, "file", "a.cs", minSeverity: "hint", includeAnalyzers: true, cancellationToken: Cancellation);

        // Sorted by severity first, then by file and position: the two informational ones are on
        // lines 4 and 5 of the same file, so CS8019 comes before CA1822.
        Assert.That(
            withAnalyzers.Diagnostics!.Select(item => item.Id),
            Is.EqualTo(["CS0029", "CS0168", "CS8019", "CA1822", "IDE0005"]));

        // An explicit id list is the caller being specific, and overrides the analyzer switch.
        var byId = await DiagnosticTools.GetDiagnosticsAsync(
            context, "file", "a.cs", minSeverity: "hint", ids: ["IDE0005"], cancellationToken: Cancellation);

        Assert.That(byId.Diagnostics!, Has.Exactly(1).Items);
        Assert.That(byId.Diagnostics!.Single().Id, Is.EqualTo("IDE0005"));
    }

    /// <summary>C16: the VS-private tags never reach a model, and the LSP ones do.</summary>
    [Test]
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

        Assert.That(result.Diagnostics!, Has.Exactly(1).Items);
        Assert.That(result.Diagnostics!.Single().Tags, Is.EqualTo(["unnecessary"]));
    }

    /// <summary>
    /// C14 and C15 together: the scope is raised for the call, and the answer's generated files,
    /// project files and per-target-framework duplicates are removed.
    /// </summary>
    [Test]
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

        Assert.That(engine.CompilerScope, Is.EqualTo(CompilerDiagnosticsScope.FullSolution));
        Assert.That(result.Diagnostics!, Has.Exactly(1).Items);
        Assert.That(result.Diagnostics!.Single().Path, Is.EqualTo("Core/Caller.cs"));
        Assert.That(result.TotalCount, Is.EqualTo(1));
        Assert.That(result.Note, Contains.Substring("scope: \"file\"").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// C24: a multi-targeted project answers once per target framework, and a reference list that
    /// reported each hit twice would make a model believe there is twice as much to change.
    /// </summary>
    [Test]
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

        Assert.That(result.TotalCount, Is.EqualTo(2));
        var references = result.References!;
        Assert.That(references.Select(item => item.Path), Is.EqualTo(["App/Program.cs", "Core/Calculator.cs"]));
        Assert.That(references[0].LineText, Is.EqualTo("new C().Compute();"));
        Assert.That(references[1].LineText, Is.EqualTo("int Compute() => 1;"));
        Assert.That(result.ByFile!.Count, Is.EqualTo(2));
    }

    /// <summary>
    /// A name that matches several symbols is reported as a list rather than resolved to whichever
    /// one came first — picking one would rename the wrong member with total confidence.
    /// </summary>
    [Test]
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

        Assert.That(exception!.Message, Contains.Substring("Core/A.cs").Using(StringComparison.Ordinal));
        Assert.That(exception.Message, Contains.Substring("Core/B.cs").Using(StringComparison.Ordinal));

        // resolveSymbol itself reports them instead of failing: listing the candidates is its job.
        var resolved = await SymbolTools.ResolveSymbolAsync(context, "Run", cancellationToken: Cancellation);
        Assert.That(resolved.TotalCount, Is.EqualTo(2));
    }

    /// <summary>A longer name narrows it down, which is the whole point of segment-suffix matching.</summary>
    [Test]
    public async Task AQualifiedNamePicksOneOfTheCandidates()
    {
        using var workspace = TempWorkspace.Create("tool-qualified");
        var first = workspace.File_("Core/A.cs", "class A { void Run() { } }\n");
        var second = workspace.File_("Core/B.cs", "class B { void Run() { } }\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);
        engine.Symbols.Add(Symbol("Run", 6, first, 0, 15, 18, "in A (project Core (net10.0))"));
        engine.Symbols.Add(Symbol("Run", 6, second, 0, 15, 18, "in B (project Core (net10.0))"));

        var resolved = await SymbolTools.ResolveSymbolAsync(context, "B.Run", cancellationToken: Cancellation);

        Assert.That(resolved.Matches!, Has.Exactly(1).Items);
        Assert.That(resolved.Matches!.Single().Path, Is.EqualTo("Core/B.cs"));
        Assert.That(resolved.Matches![0].FullName, Is.EqualTo("B.Run"));
        Assert.That(resolved.Matches[0].Kind, Is.EqualTo("method"));
    }

    /// <summary>
    /// The gate (D66). Nothing is asked of Roslyn, the answer says what is happening, and the call
    /// returns rather than hanging for two minutes.
    /// </summary>
    [Test]
    public async Task EveryToolAnswersWithALoadingStatusRatherThanHanging()
    {
        using var workspace = TempWorkspace.Create("tool-loading");
        workspace.File_("a.cs", "class A;\n");

        var (context, engine) = ToolTestHost.CreateContext(workspace);
        engine.State = new WorkspaceState(WorkspaceLoadStatus.Loading, ProjectsLoaded: 1, ProjectsTotal: 4);

        Assert.That(
            (await WorkspaceTools.GetWorkspaceStatusAsync(context, cancellationToken: Cancellation)).Status,
            Is.EqualTo(ToolStatus.Loading));
        Assert.That(
            (await SymbolTools.ResolveSymbolAsync(context, "A", cancellationToken: Cancellation)).Status,
            Is.EqualTo(ToolStatus.Loading));
        Assert.That(
            (await SymbolTools.GetTypeMembersAsync(context, "A", cancellationToken: Cancellation)).Status,
            Is.EqualTo(ToolStatus.Loading));
        Assert.That(
            (await SymbolTools.FindReferencesAsync(context, "A", cancellationToken: Cancellation)).Status,
            Is.EqualTo(ToolStatus.Loading));
        Assert.That(
            (await DiagnosticTools.GetDiagnosticsAsync(context, "file", "a.cs", cancellationToken: Cancellation)).Status,
            Is.EqualTo(ToolStatus.Loading));
        Assert.That(
            (await CodeActionTools.GetCodeActionsAsync(context, "a.cs", 1, 1, cancellationToken: Cancellation)).Status,
            Is.EqualTo(ToolStatus.Loading));
        Assert.That(
            (await CodeActionTools.ApplyCodeActionAsync(context, "a.cs", 1, 1, "x", cancellationToken: Cancellation)).Status,
            Is.EqualTo(ToolStatus.Loading));
        Assert.That(
            (await CodeActionTools.FixDiagnosticsAsync(context, "IDE0005", cancellationToken: Cancellation)).Status,
            Is.EqualTo(ToolStatus.Loading));
        Assert.That(
            (await EditTools.RenameSymbolAsync(context, "A", "B", cancellationToken: Cancellation)).Status,
            Is.EqualTo(ToolStatus.Loading));
        Assert.That(
            (await EditTools.FormatCodeAsync(context, ["a.cs"], cancellationToken: Cancellation)).Status,
            Is.EqualTo(ToolStatus.Loading));

        // The gate is the only thing that was called: nothing was asked of a workspace that cannot
        // answer, and nothing was written.
        Assert.That(engine.Calls, Has.All.EqualTo(nameof(FakeRoslynEngine.EnsureReadyAsync)));

        var status = await WorkspaceTools.GetWorkspaceStatusAsync(context, cancellationToken: Cancellation);
        Assert.That(status.ProjectsLoaded, Is.EqualTo(1));
        Assert.That(status.ProjectsTotal, Is.EqualTo(4));
        Assert.That(status.Note, Contains.Substring("still loading").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// The not-wired default (D69): the handshake and <c>tools/list</c> work, and every call says
    /// exactly what is missing rather than failing generically.
    /// </summary>
    [Test]
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
        Assert.That(status.Status, Is.EqualTo(ToolStatus.Failed));
        Assert.That(status.Note, Contains.Substring("not wired").Using(StringComparison.Ordinal));

        var resolved = await SymbolTools.ResolveSymbolAsync(context, "A", cancellationToken: Cancellation);
        Assert.That(resolved.Status, Is.EqualTo(ToolStatus.Failed));
        Assert.That(resolved.Note, Contains.Substring("doctor").Using(StringComparison.Ordinal));

        var exception = await Assert.ThrowsAsync<McpException>(
            () => new NotWiredRoslynEngine().WorkspaceSymbolAsync("A", CancellationToken.None));

        Assert.That(exception!.Message, Is.EqualTo(NotWiredRoslynEngine.Message));
    }

    /// <summary>
    /// Formatting is a configuration pull, not a request parameter: the setting is pushed before the
    /// request, because Roslyn reads it when it formats and never takes it as an argument.
    /// </summary>
    [Test]
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

        Assert.That(result.Applied, Is.True);
        Assert.That(engine.OrganizeImportsOnFormat, Is.True);
        Assert.That(File.ReadAllText(file), Is.EqualTo("class A;\n"));
    }

    /// <summary>
    /// Formatting a file that is already formatted must report "nothing to do" rather than claiming
    /// an empty change, because a caller reads <c>applied</c> to decide whether to re-read the file.
    /// </summary>
    [Test]
    public async Task FormattingAnAlreadyFormattedFileChangesNothing()
    {
        using var workspace = TempWorkspace.Create("tool-format-clean");
        workspace.File_("a.cs", "class A;\n");

        var (context, _) = ToolTestHost.CreateContext(workspace);

        var result = await EditTools.FormatCodeAsync(context, ["a.cs"], cancellationToken: Cancellation);

        Assert.That(result.Applied, Is.False);
        Assert.That(result.Note, Is.EqualTo(EditResult.NothingToDoNote));
    }

    /// <summary>
    /// <c>fixDiagnostics</c> finds a site of the diagnostic itself, so a caller does not have to pull
    /// diagnostics and pick one before asking for the fix.
    /// </summary>
    [Test]
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

        Assert.That(result.Applied, Is.True);
        Assert.That(engine.RequestedFixAllScope, Is.EqualTo(FixAllScope.Document));
        Assert.That(File.ReadAllText(file), Is.EqualTo("class C;\n"));
    }

    [Test]
    public async Task FixDiagnosticsSaysSoWhenTheDiagnosticIsNotThere()
    {
        using var workspace = TempWorkspace.Create("tool-fixdiagnostics-none");
        workspace.File_("a.cs", "class A;\n");

        var (context, _) = ToolTestHost.CreateContext(workspace);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => CodeActionTools.FixDiagnosticsAsync(context, "IDE0005", "file", "a.cs", cancellationToken: Cancellation));

        Assert.That(exception!.Message, Contains.Substring("IDE0005").Using(StringComparison.Ordinal));
    }

    /// <summary>The workspace status carries what a report about a slow machine actually needs.</summary>
    [Test]
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
            WorkingSetBytes: 253L * 1024 * 1024,
            PrivateBytes: 611L * 1024 * 1024,
            Recycles: 2,
            BaselineBytes: 400L * 1024 * 1024,
            LastRecycleAt: new DateTimeOffset(2026, 10, 19, 12, 30, 0, TimeSpan.Zero));

        var result = await WorkspaceTools.GetWorkspaceStatusAsync(context, cancellationToken: Cancellation);

        Assert.That(result.Status, Is.EqualTo(ToolStatus.Ok));
        Assert.That(result.Solution, Is.EqualTo("Fixture.slnx"));
        Assert.That(result.Projects!, Has.Exactly(1).Items);
        Assert.That(result.Projects!.Single().Path, Is.EqualTo("Core/Core.csproj"));
        Assert.That(result.Projects![0].TargetFrameworks, Is.EqualTo(["net10.0", "netstandard2.0"]));
        Assert.That(result.RoslynVersion, Is.EqualTo("5.12.0-1.26426.8"));
        Assert.That(result.ProcessId, Is.EqualTo(4242));
        Assert.That(result.MemoryMegabytes, Is.EqualTo(253));
        Assert.That(result.PrivateMegabytes, Is.EqualTo(611));
        Assert.That(result.Recycles, Is.EqualTo(2));
        Assert.That(result.BaselineMegabytes, Is.EqualTo(400));
        Assert.That(result.LastRecycleAt, Is.EqualTo("2026-10-19T12:30:00.0000000+00:00"));
    }

    /// <summary>
    /// The path guard from the tool side: a model asking about a file outside the workspace gets a
    /// refusal that names the root, not a stack trace.
    /// </summary>
    [Test]
    public async Task APathOutsideTheWorkspaceIsRefused()
    {
        using var workspace = TempWorkspace.Create("tool-escape");
        var (context, _) = ToolTestHost.CreateContext(workspace);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => DiagnosticTools.GetDiagnosticsAsync(context, "file", "../../elsewhere.cs", cancellationToken: Cancellation));

        Assert.That(exception!.Message, Contains.Substring("outside the workspace root").Using(StringComparison.Ordinal));
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
