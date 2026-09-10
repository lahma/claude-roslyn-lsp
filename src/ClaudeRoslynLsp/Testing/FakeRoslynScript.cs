using System.Buffers;
using System.Text.Json;

using ClaudeRoslynLsp.Protocol;

namespace ClaudeRoslynLsp.Testing;

/// <summary>One scripted emission: a message the fake sends after receiving <paramref name="Trigger"/>.</summary>
/// <param name="Trigger">The method whose arrival starts this step, or the empty string for startup.</param>
/// <param name="Delay">How long after the trigger the message is sent.</param>
/// <param name="Body">The raw UTF-8 message.</param>
internal sealed record FakeRoslynStep(string Trigger, TimeSpan Delay, byte[] Body);

/// <summary>Produces the answer to one request.</summary>
/// <param name="server">The server answering, so an answer can depend on how far the load has got.</param>
/// <param name="body">The raw request, so an answer can depend on what was asked.</param>
/// <returns>The <c>result</c> member as raw UTF-8 JSON.</returns>
internal delegate ReadOnlyMemory<byte> FakeRoslynResponder(FakeRoslynServer server, byte[] body);

/// <summary>
/// What a <see cref="FakeRoslynServer"/> does: what it answers, and what it volunteers.
/// </summary>
/// <remarks>
/// <para>
/// The default script is a reproduction of a real <c>roslyn-language-server</c> 5.12 startup,
/// transcribed from the WP0 spike logs rather than invented: the same capability document (including
/// the four providers the adapter must <em>not</em> forward, C26), the ten diagnostic-source
/// registrations with their real identifiers and their deliberately shuffled order (C11), the two
/// configuration requests covering all eighty sections, watcher registrations that include the
/// duplicate-glob and NuGet-cache-rooted shapes that make collapsing non-trivial (C32), the
/// work-done progress stream, the log lines, and finally
/// <c>workspace/projectInitializationComplete</c> (C31).
/// </para>
/// <para>
/// It reproduces those things because the adapter's behaviour is a response to <em>them</em>. A fake
/// that answered <c>initialize</c> and went quiet would let every one of the mediation's decisions —
/// answering registrations, answering configuration, consuming progress, not forwarding Roslyn's
/// capability document — pass a test without being exercised.
/// </para>
/// </remarks>
internal sealed class FakeRoslynScript
{
    /// <summary>The name the real server reports, with no version (C26).</summary>
    internal const string RoslynServerName = "CSharpVisualBasicLanguageServerFactory";

    /// <summary>The progress token the real server used: a bare GUID (C31).</summary>
    internal const string ProgressToken = "213a2dbe-ed05-4fff-813e-794239a0e847";

    /// <summary>
    /// The eighty configuration sections the real server asks about, in the two batches it asks in.
    /// </summary>
    /// <remarks>
    /// Transcribed from the wire. They are here rather than in a test fixture because the fake is
    /// what sends them and the tests are what assert the answers — one copy, and a mismatch between
    /// the two is impossible rather than merely unlikely.
    /// </remarks>
    internal static readonly string[] ConfigurationSectionsBatchOne =
    [
        "csharp|symbol_search.dotnet_search_reference_assemblies",
        "visual_basic|symbol_search.dotnet_search_reference_assemblies",
        "csharp|type_members.dotnet_member_insertion_location",
        "visual_basic|type_members.dotnet_member_insertion_location",
        "csharp|type_members.dotnet_property_generation_behavior",
        "visual_basic|type_members.dotnet_property_generation_behavior",
        "csharp|completion.dotnet_show_name_completion_suggestions",
        "visual_basic|completion.dotnet_show_name_completion_suggestions",
        "csharp|completion.dotnet_provide_regex_completions",
        "visual_basic|completion.dotnet_provide_regex_completions",
        "csharp|completion.dotnet_show_completion_items_from_unimported_namespaces",
        "visual_basic|completion.dotnet_show_completion_items_from_unimported_namespaces",
        "csharp|completion.dotnet_completion_items_from_unimported_namespaces_commit_behavior",
        "visual_basic|completion.dotnet_completion_items_from_unimported_namespaces_commit_behavior",
        "csharp|completion.dotnet_trigger_completion_in_argument_lists",
        "visual_basic|completion.dotnet_trigger_completion_in_argument_lists",
        "csharp|quick_info.dotnet_show_remarks_in_quick_info",
        "visual_basic|quick_info.dotnet_show_remarks_in_quick_info",
        "navigation.dotnet_navigate_to_decompiled_sources",
        "csharp|highlighting.dotnet_highlight_related_json_components",
        "visual_basic|highlighting.dotnet_highlight_related_json_components",
        "csharp|highlighting.dotnet_highlight_related_regex_components",
        "visual_basic|highlighting.dotnet_highlight_related_regex_components",
        "csharp|inlay_hints.dotnet_enable_inlay_hints_for_parameters",
        "visual_basic|inlay_hints.dotnet_enable_inlay_hints_for_parameters",
        "csharp|inlay_hints.dotnet_enable_inlay_hints_for_literal_parameters",
        "visual_basic|inlay_hints.dotnet_enable_inlay_hints_for_literal_parameters",
        "csharp|inlay_hints.dotnet_enable_inlay_hints_for_indexer_parameters",
        "visual_basic|inlay_hints.dotnet_enable_inlay_hints_for_indexer_parameters",
        "csharp|inlay_hints.dotnet_enable_inlay_hints_for_object_creation_parameters",
        "visual_basic|inlay_hints.dotnet_enable_inlay_hints_for_object_creation_parameters",
        "csharp|inlay_hints.dotnet_enable_inlay_hints_for_other_parameters",
        "visual_basic|inlay_hints.dotnet_enable_inlay_hints_for_other_parameters",
        "csharp|inlay_hints.dotnet_suppress_inlay_hints_for_parameters_that_differ_only_by_suffix",
        "visual_basic|inlay_hints.dotnet_suppress_inlay_hints_for_parameters_that_differ_only_by_suffix",
        "csharp|inlay_hints.dotnet_suppress_inlay_hints_for_parameters_that_match_method_intent",
        "visual_basic|inlay_hints.dotnet_suppress_inlay_hints_for_parameters_that_match_method_intent",
        "csharp|inlay_hints.dotnet_suppress_inlay_hints_for_parameters_that_match_argument_name",
        "visual_basic|inlay_hints.dotnet_suppress_inlay_hints_for_parameters_that_match_argument_name",
        "csharp|inlay_hints.csharp_enable_inlay_hints_for_types",
        "visual_basic|inlay_hints.csharp_enable_inlay_hints_for_types",
        "csharp|inlay_hints.csharp_enable_inlay_hints_for_implicit_variable_types",
        "visual_basic|inlay_hints.csharp_enable_inlay_hints_for_implicit_variable_types",
        "csharp|inlay_hints.csharp_enable_inlay_hints_for_lambda_parameter_types",
        "visual_basic|inlay_hints.csharp_enable_inlay_hints_for_lambda_parameter_types",
        "csharp|inlay_hints.csharp_enable_inlay_hints_for_implicit_object_creation",
        "visual_basic|inlay_hints.csharp_enable_inlay_hints_for_implicit_object_creation",
        "csharp|inlay_hints.csharp_enable_inlay_hints_for_collection_expressions",
        "visual_basic|inlay_hints.csharp_enable_inlay_hints_for_collection_expressions",
        "csharp|code_style.formatting.indentation_and_spacing.tab_width",
        "visual_basic|code_style.formatting.indentation_and_spacing.tab_width",
        "csharp|code_style.formatting.indentation_and_spacing.indent_size",
        "visual_basic|code_style.formatting.indentation_and_spacing.indent_size",
        "csharp|code_style.formatting.indentation_and_spacing.indent_style",
        "visual_basic|code_style.formatting.indentation_and_spacing.indent_style",
        "csharp|code_style.formatting.new_line.end_of_line",
        "visual_basic|code_style.formatting.new_line.end_of_line",
        "code_style.formatting.new_line.insert_final_newline",
        "csharp|background_analysis.dotnet_analyzer_diagnostics_scope",
        "visual_basic|background_analysis.dotnet_analyzer_diagnostics_scope",
        "csharp|background_analysis.dotnet_compiler_diagnostics_scope",
        "visual_basic|background_analysis.dotnet_compiler_diagnostics_scope",
        "csharp|code_lens.dotnet_enable_references_code_lens",
        "visual_basic|code_lens.dotnet_enable_references_code_lens",
        "csharp|code_lens.dotnet_enable_tests_code_lens",
        "visual_basic|code_lens.dotnet_enable_tests_code_lens",
        "csharp|auto_insert.dotnet_enable_auto_insert",
        "visual_basic|auto_insert.dotnet_enable_auto_insert",
        "projects.dotnet_binary_log_path",
        "projects.dotnet_enable_automatic_restore",
        "projects.dotnet_enable_file_based_programs",
        "projects.dotnet_enable_file_based_programs_when_ambiguous",
        "navigation.dotnet_navigate_to_source_link_and_embedded_sources",
        "csharp|formatting.dotnet_organize_imports_on_format",
        "visual_basic|formatting.dotnet_organize_imports_on_format",
    ];

    /// <summary>The second batch: the Razor and HTML sections, asked for separately (C6).</summary>
    internal static readonly string[] ConfigurationSectionsBatchTwo =
    [
        "razor.format.code_block_brace_on_next_line",
        "razor.format.attribute_indent_style",
        "razor.completion.commit_elements_with_space",
        "html.auto_closing_tags",
        "razor.advanced.show_all_c_sharp_code_actions",
    ];

    /// <summary>Every section, in one list, for tests that assert over the whole set.</summary>
    internal static IReadOnlyList<string> AllConfigurationSections { get; } =
        [.. ConfigurationSectionsBatchOne, .. ConfigurationSectionsBatchTwo];

    /// <summary>The document the fake answers <c>initialize</c> with.</summary>
    /// <remarks>
    /// Roslyn's real one, including <c>semanticTokensProvider</c>, <c>codeLensProvider</c>,
    /// <c>inlayHintProvider</c> and <c>_vs_onAutoInsertProvider</c>. Those four are here precisely so
    /// that a test can prove the adapter does <b>not</b> pass them on (C26) — a fake that omitted
    /// them would make that assertion vacuous.
    /// </remarks>
    internal ReadOnlyMemory<byte> InitializeResult { get; init; } = DefaultInitializeResult;

    /// <summary>The canned answers, by method.</summary>
    internal Dictionary<string, FakeRoslynResponder> Responders { get; init; } = [];

    /// <summary>What the fake volunteers, and when.</summary>
    internal List<FakeRoslynStep> Steps { get; init; } = [];

    /// <summary>
    /// How long after <c>solution/open</c> the workspace finishes loading, or null to wait for
    /// <see cref="FakeRoslynServer.CompleteProjectInitialization"/>.
    /// </summary>
    /// <remarks>
    /// Both modes exist for a reason. A delay is what the smoke test wants, because it is proving a
    /// real timeline through a real process. A manual release is what a unit test wants, because a
    /// test that asserts "held, then answered" must control the moment in between rather than race
    /// it.
    /// </remarks>
    internal TimeSpan? ProjectLoadDelay { get; init; }

    /// <summary>The default Roslyn 5.12 capability document, verbatim from the spike logs.</summary>
    private static ReadOnlyMemory<byte> DefaultInitializeResult { get; } = """
        {"_roslyn_processId":4242,"capabilities":{"_vs_onAutoInsertProvider":{"_vs_triggerCharacters":["'","/","\n","\""]},"textDocumentSync":{"openClose":true,"change":2,"save":{}},"completionProvider":{"triggerCharacters":["\"","(",":","[","\\","{","#",".",">"," ","~","<"],"resolveProvider":true},"hoverProvider":true,"signatureHelpProvider":{"triggerCharacters":["(",",","[","<","{"],"retriggerCharacters":[")","]",">","}"]},"definitionProvider":true,"typeDefinitionProvider":true,"implementationProvider":true,"referencesProvider":{"workDoneProgress":true},"documentHighlightProvider":true,"documentSymbolProvider":true,"codeActionProvider":{"codeActionKinds":["quickfix","refactor"],"resolveProvider":true},"codeLensProvider":{"resolveProvider":true},"documentFormattingProvider":true,"documentRangeFormattingProvider":true,"documentOnTypeFormattingProvider":{"firstTriggerCharacter":"}","moreTriggerCharacter":[";","\n"]},"renameProvider":{"prepareProvider":true},"foldingRangeProvider":true,"executeCommandProvider":{"commands":[]},"selectionRangeProvider":true,"callHierarchyProvider":true,"semanticTokensProvider":{"legend":{"tokenTypes":["namespace","type","class"],"tokenModifiers":["static"]},"range":true,"full":true},"typeHierarchyProvider":true,"inlayHintProvider":{"resolveProvider":true},"workspaceSymbolProvider":true,"workspace":{}},"serverInfo":{"name":"CSharpVisualBasicLanguageServerFactory"}}
        """u8.ToArray();

    /// <summary>
    /// The reproduction of a real 5.12 startup, plus canned navigation answers.
    /// </summary>
    /// <param name="solutionName">
    /// What the progress stream calls the solution. It is what the readiness gate's holding notice
    /// ends up quoting, so a test can assert on it.
    /// </param>
    /// <param name="projectLoadDelay">
    /// How long the load takes, or null to make <c>projectInitializationComplete</c> manual.
    /// </param>
    internal static FakeRoslynScript Roslyn512Startup(
        string solutionName = "Fixture.slnx",
        TimeSpan? projectLoadDelay = null)
    {
        ArgumentNullException.ThrowIfNull(solutionName);

        var steps = new List<FakeRoslynStep>();

        void OnInitialized(string message) =>
            steps.Add(new FakeRoslynStep("initialized", TimeSpan.Zero, System.Text.Encoding.UTF8.GetBytes(message)));

        // The ten diagnostic sources. Their order is shuffled relative to any sensible one on
        // purpose: the real server's order changes every run, so anything that indexes rather than
        // keying on `identifier` has to fail here (C11). The Razor one, which has no identifier at
        // all, arrives in the later batch.
        OnInitialized(Request(101, "client/registerCapability", """
            {"registrations":[{"id":"1801602d-f027-4a7d-a3b8-35210fae6f25","method":"textDocument/diagnostic","registerOptions":{"identifier":"DocumentAnalyzerSemantic","interFileDependencies":true,"workspaceDiagnostics":false}},{"id":"90536aa3-525f-4894-bd65-72904d60082c","method":"textDocument/diagnostic","registerOptions":{"identifier":"XamlDiagnostics","interFileDependencies":true,"workspaceDiagnostics":false}},{"id":"0d585a18-c87a-48b3-bb48-359fa4a7c5a1","method":"textDocument/diagnostic","registerOptions":{"workDoneProgress":true,"identifier":"HotReloadDiagnostics","interFileDependencies":true,"workspaceDiagnostics":true}},{"id":"b199ec81-3430-4472-9bf7-458d3c97dcfd","method":"textDocument/diagnostic","registerOptions":{"workDoneProgress":true,"identifier":"enc","interFileDependencies":true,"workspaceDiagnostics":true}},{"id":"274595ad-782a-457d-a4ab-db45f9fe8f4d","method":"textDocument/diagnostic","registerOptions":{"identifier":"DocumentCompilerSemantic","interFileDependencies":true,"workspaceDiagnostics":false}},{"id":"60f97264-f0ef-4e31-81ec-1c7098fb1dbe","method":"textDocument/diagnostic","registerOptions":{"identifier":"syntax","interFileDependencies":true,"workspaceDiagnostics":false}},{"id":"9e2f9da3-765c-4425-ba23-9b7ae0e62874","method":"textDocument/diagnostic","registerOptions":{"identifier":"NonLocal","interFileDependencies":true,"workspaceDiagnostics":false}},{"id":"9dd92a71-b4a9-4ceb-9167-7a78e0650e12","method":"textDocument/diagnostic","registerOptions":{"identifier":"DocumentAnalyzerSyntax","interFileDependencies":true,"workspaceDiagnostics":false}},{"id":"376b1956-ee04-45f3-8665-5b57a633e6a0","method":"textDocument/diagnostic","registerOptions":{"workDoneProgress":true,"identifier":"WorkspaceDocumentsAndProject","interFileDependencies":true,"workspaceDiagnostics":true}}]}
            """));

        OnInitialized(Request(102, "workspace/configuration", ConfigurationRequest(ConfigurationSectionsBatchOne)));

        OnInitialized(Request(103, "client/registerCapability", """
            {"registrations":[{"id":"b4e01ddd-dc11-4249-8774-1d51c85188de","method":"workspace/didChangeConfiguration"}]}
            """));

        OnInitialized(Notification("window/logMessage", """
            {"type":5,"message":"[Services.VSCodeRemoteServicesInitializer] Initializing remote services."}
            """));

        OnInitialized(Notification("window/logMessage", """
            {"type":3,"message":"[Razor.LanguageClient.Cohost.RazorCohostDynamicRegistrationService] Requesting 2 Razor cohost registrations."}
            """));

        // The Razor batch: a diagnostic registration with no identifier at all, next to a completely
        // unrelated method in the same request.
        OnInitialized(Request(104, "client/registerCapability", """
            {"registrations":[{"id":"6fc4ddc2-2700-4efa-b92d-9442bf45adc2","method":"textDocument/diagnostic","registerOptions":{"documentSelector":[{"language":"aspnetcorerazor","pattern":"**/*.{razor,cshtml}"}],"interFileDependencies":false,"workspaceDiagnostics":false}},{"id":"ee6971aa-3f25-4db0-a874-fff9571f86fa","method":"textDocument/documentColor","registerOptions":{"documentSelector":[{"language":"aspnetcorerazor","pattern":"**/*.{razor,cshtml}"}]}}]}
            """));

        OnInitialized(Request(105, "workspace/configuration", ConfigurationRequest(ConfigurationSectionsBatchTwo)));

        // Watchers. Three shapes that matter: two globs over the same base that differ only in the
        // order of the extensions inside the brace group, a bare project-file watcher on that same
        // base, and one rooted in the NuGet cache — which is where 113 of the real 135 live (C32).
        OnInitialized(Request(106, "client/registerCapability", """
            {"registrations":[{"id":"ef9128c1-5040-45f8-8658-b9823ea5e4b0","method":"workspace/didChangeWatchedFiles","registerOptions":{"watchers":[{"globPattern":{"baseUri":"file:///w/Fixture/Fixture.Core","pattern":"**/*{.cs,.razor,.cshtml}"}}]}},{"id":"aae65205-30f2-47f0-ab30-1391d72e240f","method":"workspace/didChangeWatchedFiles","registerOptions":{"watchers":[{"globPattern":{"baseUri":"file:///w/Fixture/Fixture.Core","pattern":"**/*{.cs,.cshtml,.razor}"}}]}},{"id":"2d77ed59-80d2-44f6-864e-9b3f39eb9209","method":"workspace/didChangeWatchedFiles","registerOptions":{"watchers":[{"globPattern":{"baseUri":"file:///w/Fixture/Fixture.Core","pattern":"Fixture.Core.csproj"}}]}},{"id":"7b1c4c4c-0000-4000-8000-00000000f00d","method":"workspace/didChangeWatchedFiles","registerOptions":{"watchers":[{"globPattern":{"baseUri":"file:///home/u/.nuget/packages/system.text.json/9.0.0","pattern":"**/*.dll"}}]}}]}
            """));

        OnInitialized(Request(107, "window/workDoneProgress/create", $$"""
            {"token":"{{ProgressToken}}"}
            """));

        OnInitialized(Notification("$/progress", $$$"""
            {"token":"{{{ProgressToken}}}","value":{"kind":"begin","title":"Loading {{{solutionName}}}...","cancellable":false,"message":"Loading {{{solutionName}}}...","percentage":0}}
            """));

        OnInitialized(Notification("$/progress", $$$"""
            {"token":"{{{ProgressToken}}}","value":{"kind":"report","message":"Loading 2 project(s)...","percentage":0}}
            """));

        OnInitialized(Notification("window/logMessage", """
            {"type":3,"message":"[initialized] [Razor]  Razor extension startup finished."}
            """));

        // A method nothing in the adapter's table knows, sent as a request. Roslyn's own surface
        // grows between versions (C39), and the rule under test is that an unknown request gets
        // -32601 rather than silence — a Roslyn left waiting on the client stalls the handler that
        // asked, and several of those are on the load path.
        OnInitialized(Request(108, "window/_roslyn_unknownFutureRequest", "{}"));

        foreach (var trigger in new[] { "solution/open", "project/open" })
        {
            steps.Add(Step(trigger, TimeSpan.Zero, Notification("window/logMessage", $$"""
                {"type":3,"message":"[{{trigger}}] [LanguageServerProjectSystem] Loading {{solutionName}}..."}
                """)));

            var loadDelay = projectLoadDelay ?? TimeSpan.Zero;

            if (projectLoadDelay is null)
            {
                continue;
            }

            steps.Add(Step(trigger, loadDelay, Notification("$/progress", $$$"""
                {"token":"{{{ProgressToken}}}","value":{"kind":"report","percentage":99}}
                """)));

            steps.Add(Step(trigger, loadDelay, Notification("window/logMessage", $$"""
                {"type":3,"message":"[{{trigger}}] [LanguageServerProjectSystem] Completed (re)load of all projects in 00:00:01.7511823"}
                """)));

            steps.Add(Step(trigger, loadDelay, Notification("workspace/projectInitializationComplete", null)));

            steps.Add(Step(trigger, loadDelay, Notification("$/progress", $$$"""
                {"token":"{{{ProgressToken}}}","value":{"kind":"end","message":"Loaded {{{solutionName}}}"}}
                """)));
        }

        return new FakeRoslynScript
        {
            Steps = steps,
            ProjectLoadDelay = projectLoadDelay,
            Responders = NavigationAnswers(),
        };
    }

    /// <summary>
    /// Canned answers shaped like the real ones, for the requests the adapter forwards.
    /// </summary>
    /// <remarks>
    /// The shapes matter more than the contents, and <em>when</em> each shape is given matters most
    /// of all: every one of these answers empty until the workspace reports itself loaded, which is
    /// exactly what the real server does (C27).
    /// </remarks>
    private static Dictionary<string, FakeRoslynResponder> NavigationAnswers()
    {
        // C27, reproduced: before workspace/projectInitializationComplete the real server answers
        // navigation with an EMPTY SUCCESSFUL RESULT, never an error. That is the fact the readiness
        // gate exists for, and a fake that answered correctly from the first millisecond would let a
        // broken gate pass every test in the suite.
        static FakeRoslynResponder Gated(ReadOnlyMemory<byte> loaded, ReadOnlyMemory<byte> loading) =>
            (server, _) => server.ProjectsLoaded ? loaded : loading;

        static FakeRoslynResponder List(ReadOnlyMemory<byte> loaded) => Gated(loaded, "[]"u8.ToArray());

        static FakeRoslynResponder Value(ReadOnlyMemory<byte> loaded) => Gated(loaded, "null"u8.ToArray());

        return new Dictionary<string, FakeRoslynResponder>(StringComparer.Ordinal)
        {
            ["textDocument/definition"] = List("""
                [{"uri":"file:///w/Fixture/Fixture.Core/IShape.cs","range":{"start":{"line":2,"character":17},"end":{"line":2,"character":23}}}]
                """u8.ToArray()),

            ["textDocument/typeDefinition"] = List("""
                [{"uri":"file:///w/Fixture/Fixture.Core/IShape.cs","range":{"start":{"line":2,"character":17},"end":{"line":2,"character":23}}}]
                """u8.ToArray()),

            ["textDocument/implementation"] = List("""
                [{"uri":"file:///w/Fixture/Fixture.Core/Square.cs","range":{"start":{"line":2,"character":20},"end":{"line":2,"character":20}}},{"uri":"file:///w/Fixture/Fixture.Core/Circle.cs","range":{"start":{"line":4,"character":20},"end":{"line":4,"character":20}}}]
                """u8.ToArray()),

            ["textDocument/references"] = List("""
                [{"uri":"file:///w/Fixture/Fixture.Core/Calculator.cs","range":{"start":{"line":7,"character":15},"end":{"line":7,"character":22}}},{"uri":"file:///w/Fixture/Fixture.Core/Caller.cs","range":{"start":{"line":7,"character":26},"end":{"line":7,"character":33}}},{"uri":"file:///w/Fixture/Fixture.App/Program.cs","range":{"start":{"line":11,"character":37},"end":{"line":11,"character":44}}}]
                """u8.ToArray()),

            ["textDocument/hover"] = Value("""
                {"contents":{"kind":"markdown","value":"```csharp\r\nint Calculator.Compute()\r\n```\r\n  \r\n"},"range":{"start":{"line":7,"character":15},"end":{"line":7,"character":22}}}
                """u8.ToArray()),

            ["textDocument/documentSymbol"] = List("""
                [{"name":"Fixture.Core","kind":3,"range":{"start":{"line":0,"character":0},"end":{"line":12,"character":1}},"selectionRange":{"start":{"line":0,"character":10},"end":{"line":0,"character":22}},"children":[{"name":"Calculator","kind":5,"range":{"start":{"line":2,"character":0},"end":{"line":11,"character":1}},"selectionRange":{"start":{"line":2,"character":13},"end":{"line":2,"character":23}},"children":[{"name":"Compute()","detail":"int Compute()","kind":6,"range":{"start":{"line":7,"character":4},"end":{"line":10,"character":5}},"selectionRange":{"start":{"line":7,"character":15},"end":{"line":7,"character":22}},"children":[]}]}]}]
                """u8.ToArray()),

            ["workspace/symbol"] = List("""
                [{"name":"Circle","kind":5,"location":{"uri":"file:///w/Fixture/Fixture.Core/Circle.cs","range":{"start":{"line":4,"character":20},"end":{"line":4,"character":26}}},"containerName":"project Fixture.Core (net10.0, netstandard2.0)"}]
                """u8.ToArray()),

            // The opaque data block Roslyn attaches to a call-hierarchy item (C24) is here because it
            // has to round-trip through the adapter verbatim, and a fixture that answered [] would
            // never test that.
            ["textDocument/prepareCallHierarchy"] = List("""
                [{"name":"Calculator.Compute()","kind":6,"detail":"Fixture.Core.Calculator","uri":"file:///w/Fixture/Fixture.Core/Calculator.cs","range":{"start":{"line":7,"character":4},"end":{"line":10,"character":5}},"selectionRange":{"start":{"line":7,"character":15},"end":{"line":7,"character":22}},"data":{"SymbolKeyData":"7 \"C#\" (M \"Compute\" (D (N \"Core\" 0 (N \"Fixture\" 0 (N \"\" 1 (U (S \"Fixture.Core\" 6) 5) 4) 3) 2) \"Calculator\" 0 ! ! 0 0 0 (% 0) 1) 0 0 (% 0) ! (% 0) 0)","ProjectGuid":"049edba0-02c1-43f8-b7fa-c21f164029a7","TextDocument":{"uri":"file:///w/Fixture/Fixture.Core/Calculator.cs"}}}]
                """u8.ToArray()),

            ["callHierarchy/incomingCalls"] = List("""
                [{"from":{"name":"Caller.CallOnce()","kind":6,"detail":"Fixture.Core.Caller","uri":"file:///w/Fixture/Fixture.Core/Caller.cs","range":{"start":{"line":4,"character":4},"end":{"line":8,"character":5}},"selectionRange":{"start":{"line":4,"character":15},"end":{"line":4,"character":23}},"data":{"SymbolKeyData":"7 \"C#\" (M \"CallOnce\" (D (N \"Core\" 0 (N \"Fixture\" 0 (N \"\" 1 (U (S \"Fixture.Core\" 6) 5) 4) 3) 2) \"Caller\" 0 ! ! 0 0 0 (% 0) 1) 0 0 (% 0) ! (% 0) 0)","ProjectGuid":"049edba0-02c1-43f8-b7fa-c21f164029a7","TextDocument":{"uri":"file:///w/Fixture/Fixture.Core/Caller.cs"}}},"fromRanges":[{"start":{"line":7,"character":26},"end":{"line":7,"character":33}}]}]
                """u8.ToArray()),
        };
    }

    /// <summary>Wraps a step so the trigger and delay read in one line at the call site.</summary>
    private static FakeRoslynStep Step(string trigger, TimeSpan delay, string message) =>
        new(trigger, delay, System.Text.Encoding.UTF8.GetBytes(message));

    /// <summary>Renders a server-to-client request.</summary>
    private static string Request(int id, string method, string parameters) =>
        $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters.Trim()}}}""";

    /// <summary>Renders a server-to-client notification, with or without parameters.</summary>
    private static string Notification(string method, string? parameters) =>
        parameters is null
            ? $$"""{"jsonrpc":"2.0","method":"{{method}}"}"""
            : $$"""{"jsonrpc":"2.0","method":"{{method}}","params":{{parameters.Trim()}}}""";

    /// <summary>Renders the parameters of a <c>workspace/configuration</c> request.</summary>
    private static string ConfigurationRequest(string[] sections)
    {
        var buffer = new ArrayBufferWriter<byte>(64 * sections.Length);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("items"u8);

            foreach (var section in sections)
            {
                writer.WriteStartObject();
                writer.WriteString("section"u8, section);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Renders a response the fake sends for a request it was asked.</summary>
    internal static byte[] Response(ReadOnlySpan<byte> idToken, ReadOnlySpan<byte> result) =>
        JsonRpcErrors.RawResult(idToken, result);
}
