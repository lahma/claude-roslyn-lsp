using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

using Fallout.Common;
using Fallout.Common.CI;
using Fallout.Common.CI.GitHubActions;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using Fallout.Common.Tools.DotNet;
using Fallout.Common.Utilities.Collections;
using Fallout.Components;
using Fallout.Solutions;

using Serilog;

using static Fallout.Common.Tools.DotNet.DotNetTasks;

using Project = Fallout.Solutions.Project;

/// <summary>
/// The Fallout orchestrator for claude-roslyn-lsp.
/// </summary>
/// <remarks>
/// Restore / Compile / Test come from the Fallout.Components interfaces; only the two things that
/// are specific to shipping a Native AOT binary that speaks two protocols are hand-written:
/// <c>PublishAot</c> (publish + archive per RID) and <c>SmokeTest</c>, which drives a real
/// <c>Content-Length</c>-framed LSP handshake <em>and</em> a real MCP handshake against the published
/// binary.
/// </remarks>
[ShutdownDotNetAfterServerBuild]
partial class Build : FalloutBuild,
    IHasSolution,
    IHasConfiguration,
    IHasArtifacts,
    IHasChangelog,
    IHasGitRepository,
    IRestore,
    ICompile,
    ITest,
    ICreateGitHubRelease
{
    public static int Main() => Execute<Build>(x => ((ITest)x).Test);

    /// <summary>
    /// The binary's product name. It is also the MCP <c>serverInfo.name</c> and the LSP
    /// <c>serverInfo.name</c>, both asserted by SmokeTest - one constant, checked over two protocols.
    /// </summary>
    const string ProductName = "claude-roslyn-lsp";

    /// <summary>
    /// The tool names <c>tools/list</c> must return, verbatim and complete.
    /// </summary>
    /// <remarks>
    /// The complete set, compared verbatim on a published Native AOT binary. This is the one place
    /// the inventory is asserted outside the test assembly, which is what makes it a check on the
    /// *shipped* server rather than on a reflected type list: an AOT publish that dropped a tool
    /// class, or a serializer context that could not describe a result type, fails here and nowhere
    /// else. Adding a tool means editing this array, AGENTS.md's tool table, the README table,
    /// ToolInventoryTests, ToolSchemaTests and SKILL.md.
    /// </remarks>
    static readonly string[] ExpectedToolNames =
    [
        "applyCodeAction",
        "findReferences",
        "fixDiagnostics",
        "formatCode",
        "getCodeActions",
        "getDiagnostics",
        "getTypeMembers",
        "getWorkspaceStatus",
        "renameSymbol",
        "resolveSymbol",
    ];

    /// <summary>How long SmokeTest waits for the MCP responses before giving up.</summary>
    static readonly TimeSpan SmokeTestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the LSP leg waits for the server to exit after <c>exit</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately short. The whole exchange is four small frames against a server with no backend,
    /// so anything slower than this is a server that did not understand <c>exit</c> - which is a bug
    /// a user experiences as an editor that hangs on shutdown, and is worth failing fast on.
    /// </remarks>
    static readonly TimeSpan LspExitTimeout = TimeSpan.FromSeconds(5);

    [Parameter("Runtime identifier to publish for - defaults to the host RID")]
    readonly string Runtime = RuntimeInformation.RuntimeIdentifier;

    [Solution] readonly Solution Solution;
    Solution IHasSolution.Solution => Solution;

    public AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestsDirectory => RootDirectory / "tests";
    // Spelled out rather than derived from ProductName: on Linux the path is case-sensitive, and the
    // project directory is PascalCase where the product is kebab-case.
    AbsolutePath ServerProject => SourceDirectory / "ClaudeRoslynLsp" / "ClaudeRoslynLsp.csproj";
    AbsolutePath ChangelogPath => RootDirectory / "CHANGELOG.md";
    AbsolutePath PublishDirectory => ArtifactsDirectory / "publish" / Runtime;
    AbsolutePath StagingDirectory => ArtifactsDirectory / "staging" / Runtime;
    AbsolutePath ArchivesDirectory => ArtifactsDirectory / "archives";
    AbsolutePath ReleaseNotesFile => ArtifactsDirectory / "release-notes.md";

    bool IsWindowsRuntime => Runtime.StartsWith("win", StringComparison.OrdinalIgnoreCase);
    string ExecutableName => IsWindowsRuntime ? ProductName + ".exe" : ProductName;
    string ArchiveExtension => IsWindowsRuntime ? ".zip" : ".tar.gz";
    AbsolutePath PublishedExecutable => PublishDirectory / ExecutableName;
    AbsolutePath ArchiveFile => ArchivesDirectory / $"{ProductName}-{Version}-{Runtime}{ArchiveExtension}";

    /// <summary>The version parsed out of CHANGELOG.md - the single version authority.</summary>
    string Version { get; set; }

    ReleaseNotes LatestReleaseNotes { get; set; }

    /// <summary>True when this build is running for a <c>v*</c> tag - i.e. it is a release build.</summary>
    /// <remarks>
    /// Fallout's own repositories derive this from <c>GitRepository.Tags</c> because there the tag
    /// <em>is</em> the version. Here CHANGELOG.md is the version authority and the tag only has to
    /// agree with it, so on GitHub Actions the ref the run was triggered for is the authoritative
    /// answer: <c>GITHUB_REF_NAME</c> is the tag name on a tag push, and it is what
    /// <see cref="AssertReleaseTagMatchesChangelogVersion"/> compares against anyway. Off CI it falls
    /// back to a v* tag pointing at HEAD, so the gate can be exercised locally.
    /// </remarks>
    bool IsTaggedBuild => VersionTag != null;

    /// <summary>The <c>v*</c> tag this build is running for, or <c>null</c> if it is not a tag build.</summary>
    string VersionTag
    {
        get
        {
            if (GitHubActions.Instance == null)
            {
                return ((IHasGitRepository)this).GitRepository?.Tags.FirstOrDefault(IsVersionTag);
            }

            // GITHUB_REF_TYPE distinguishes a tag push from a branch push, but it is only consulted
            // when it is actually set, so the gate stays exercisable with GITHUB_REF_NAME alone.
            var refType = Environment.GetEnvironmentVariable("GITHUB_REF_TYPE");
            var refName = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");

            return refType is null or "tag" && IsVersionTag(refName) ? refName : null;
        }
    }

    /// <summary>A version tag is <c>v</c> followed by a digit - so a <c>vnext</c> branch is not one.</summary>
    static bool IsVersionTag(string value) =>
        value?.StartsWith('v') == true && value.Length > 1 && char.IsAsciiDigit(value[1]);

    protected override void OnBuildInitialized()
    {
        base.OnBuildInitialized();

        // CHANGELOG.md is the version authority (never mutated by the build). Its first line must
        // parse as a version header - a "# Changelog" title would abort here.
        var changelog = new ReleaseNotesParser().Parse(File.ReadAllText(ChangelogPath));
        LatestReleaseNotes = changelog.FirstOrDefault()
            .NotNull($"{ChangelogPath} contains no parsable release section");

        Version = LatestReleaseNotes.SemVersion.ToString();
        Log.Information("Version from {Changelog}: {Version}", ChangelogPath, Version);
    }

    Target Clean => _ => _
        .Description("Deletes all build output and the artifacts directory")
        .Before<IRestore>()
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").DeleteDirectories();
            TestsDirectory.GlobDirectories("**/bin", "**/obj").DeleteDirectories();
            ArtifactsDirectory.CreateOrCleanDirectory();
        });

    IEnumerable<Project> ITest.TestProjects => Solution.GetAllProjects("*.Tests");

    Configure<DotNetBuildSettings> ICompile.CompileSettings => _ => _
        .SetProperty("Version", Version);

    Configure<DotNetTestSettings> ITest.TestSettings => _ => _
        .SetProperty("Version", Version);

    Target PublishAot => _ => _
        .Description("Publishes a Native AOT binary for --runtime and archives it into artifacts/archives")
        .Produces(ArchivesDirectory / "*.zip")
        .Produces(ArchivesDirectory / "*.tar.gz")
        .Executes(() =>
        {
            // Deliberately independent of Compile: the AOT publish is a self-contained, per-RID
            // Release publish (D8 - the RID only ever reaches the SDK through -r).
            PublishDirectory.CreateOrCleanDirectory();

            DotNetPublish(_ => _
                .SetProject(ServerProject)
                .SetConfiguration(Configuration.Release)
                .SetRuntime(Runtime)
                .SetSelfContained(true)
                .SetOutput(PublishDirectory)
                .SetProperty("Version", Version));

            Assert.True(PublishedExecutable.FileExists(),
                $"Native AOT publish did not produce '{PublishedExecutable}'");

            // Archive exactly the three files a user needs, not the whole publish directory.
            StagingDirectory.CreateOrCleanDirectory();
            PublishedExecutable.CopyToDirectory(StagingDirectory, ExistsPolicy.FileOverwrite);
            (RootDirectory / "LICENSE").CopyToDirectory(StagingDirectory, ExistsPolicy.FileOverwrite);
            (RootDirectory / "README.md").CopyToDirectory(StagingDirectory, ExistsPolicy.FileOverwrite);

            ArchivesDirectory.CreateDirectory();
            ArchiveFile.DeleteFile();

            if (IsWindowsRuntime)
            {
                StagingDirectory.ZipTo(ArchiveFile, fileMode: FileMode.Create);
            }
            else
            {
                // R2: NOT CompressionExtensions.TarGZipTo. It goes through SharpZipLib's
                // TarEntry.CreateEntryFromFile, which hard-codes every entry's mode to 0700 instead of
                // reading it off disk - the binary would come out of the archive unreadable by anyone
                // but the extracting user, and LICENSE/README.md would come out executable. The tar CLI
                // copies the real mode (0755 for a published AOT binary), and both the GitHub runners
                // and Git Bash on Windows ship it. Files are named explicitly so the archive is flat.
                ProcessTasks
                    .StartProcess("tar",
                        $"-czf {ArchiveFile} -C {StagingDirectory} {ExecutableName} LICENSE README.md")
                    .AssertWaitForExit()
                    .AssertZeroExitCode();
            }

            Log.Information("Created {Archive}", ArchiveFile);
            ReportSummary(_ => _
                .AddPair("Runtime", Runtime)
                .AddPair("Archive", ArchiveFile.Name));
        });

    Target SmokeTest => _ => _
        .Description("Drives three real protocol exchanges against the published AOT binary")
        .DependsOn(PublishAot)
        .Executes(() =>
        {
            // Written so it resolves. Solution discovery treats an explicit setting that names
            // nothing as a hard failure rather than a fall-through (D39), and a failed selection
            // opens the readiness gate at once - which would let the definition request through
            // before the backend reported the workspace loaded and quietly turn the gate assertion
            // below into a test of nothing.
            File.WriteAllText(SmokeSolutionPath, "<Solution />");

            // Leg 1: the LSP verb against the scripted backend running INSIDE the server process.
            // This is the leg that proves the mediation itself survived Native AOT compilation on
            // this architecture - framing, the readiness gate, the id map, shutdown ordering.
            RunLspHandshake("in-process fake backend", new Dictionary<string, string>
            {
                ["CLAUDE_ROSLYN_LSP_SOLUTION"] = SmokeSolutionPath,
            }, arguments: "lsp --smoke");

            // Leg 2: the same exchange with the scripted backend in a CHILD PROCESS, which is the
            // only leg that proves the plumbing: that this RID can spawn a child at all, that its
            // three redirected handles are wired the right way round, and that nothing the child
            // writes at startup lands on what is now a protocol channel. The real server does
            // exactly that in one of its transports (C7), so it is not a hypothetical.
            RunLspHandshake("child-process fake backend", new Dictionary<string, string>
            {
                ["CLAUDE_ROSLYN_LSP_SOLUTION"] = SmokeSolutionPath,
                ["CLAUDE_ROSLYN_LSP_FAKE_BACKEND"] = "child",
            }, arguments: "lsp");

            // Leg 3: the MCP verb with NO backend wired in at all (D69). The handshake and
            // tools/list must not depend on one - that is what lets a client render the tool surface
            // in the seconds before anything has been launched, and what keeps this leg free of a
            // 70 MB download on five release runners.
            var toolNames = Handshake(
                new Dictionary<string, string> { ["CLAUDE_ROSLYN_LSP_FAKE_BACKEND"] = "none" },
                ExpectedToolNames,
                arguments: "mcp");

            // Leg 4: the MCP verb with the scripted backend and the eager start (D76) actually
            // running. It proves the one thing leg 3 cannot: that launching a backend from the
            // initialized notification does not put a byte on stdout, which is the protocol channel.
            Handshake(environment: null, ExpectedToolNames, arguments: "mcp --smoke");

            ReportSummary(_ => _
                .AddPair("Runtime", Runtime)
                .AddPair("LSP legs", "2")
                .AddPair("MCP legs", "2")
                .AddPair("Tools", toolNames.Length.ToString()));
        });

    /// <summary>
    /// Drives the published binary against the fixture solution and a <b>real</b> Roslyn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gap SmokeTest cannot close. Every smoke leg speaks to a scripted backend, which proves
    /// the mediation and proves nothing at all about whether Microsoft's server still answers what
    /// this adapter believes it answers. This target acquires the pinned server for real, opens a
    /// three-project solution that has never been restored, and asserts on the one thing the product
    /// exists for: a compile error arriving as a <c>publishDiagnostics</c> at a client that never
    /// asked for one.
    /// </para>
    /// <para>
    /// Opt-in through <c>CLAUDE_ROSLYN_LSP_LIVE_TESTS=1</c>, and <c>OnlyWhenStatic</c> rather than a
    /// runtime check so that a run without it says "skipped" instead of quietly passing. The
    /// download lands in <c>artifacts/roslyn-home</c>, which is inside the directory CI already
    /// caches and <c>Clean</c> already empties.
    /// </para>
    /// </remarks>
    Target LiveTest => _ => _
        .Description("Drives the published binary against the fixture solution and a real Roslyn")
        .DependsOn(PublishAot)
        .OnlyWhenStatic(() => Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_LSP_LIVE_TESTS") == "1")
        .Executes(() =>
        {
            var fixture = RootDirectory / "tests" / "fixtures" / "HelloSolution";
            var solution = fixture / "HelloSolution.slnx";
            var program = fixture / "Hello.App" / "Program.cs";

            Assert.True(solution.FileExists(), $"The fixture solution is missing: {solution}");
            Assert.True(program.FileExists(), $"The fixture's Program.cs is missing: {program}");

            RunLspHandshake(
                "real Roslyn against the fixture solution",
                new Dictionary<string, string>
                {
                    ["CLAUDE_ROSLYN_LSP_SOLUTION"] = solution.ToString(),
                    ["CLAUDE_ROSLYN_LSP_HOME"] = (ArtifactsDirectory / "roslyn-home").ToString(),

                    // Not inherited: the smoke legs set it, and a leftover value would put the
                    // scripted backend in front of the test that exists to avoid one.
                    ["CLAUDE_ROSLYN_LSP_FAKE_BACKEND"] = string.Empty,
                },
                arguments: "lsp",
                rootUri: UriOf(fixture),
                document: program,
                expectedDiagnosticCode: "CS0029",
                answerTimeout: LiveAnswerTimeout);

            ReportSummary(_ => _
                .AddPair("Runtime", Runtime)
                .AddPair("Solution", solution.Name));
        });

    // ---------------------------------------------------------------------------------------
    // The LSP leg
    // ---------------------------------------------------------------------------------------

    const int LspInitializeId = 1;
    const int LspDefinitionId = 2;
    const int LspShutdownId = 3;

    /// <summary>
    /// How long the live leg allows for the whole exchange.
    /// </summary>
    /// <remarks>
    /// Four minutes, and almost all of it is budget for the first run on a cold machine: about
    /// 70 MB of Roslyn to download, 140 MB to extract, a solution to restore server-side and three
    /// projects to load. A warm run finishes in seconds - the local measurement is 24 s including
    /// a kill and a relaunch - so this is a deadlock detector, not a target.
    /// </remarks>
    static readonly TimeSpan LiveAnswerTimeout = TimeSpan.FromMinutes(4);

    /// <summary>How long the LSP leg waits for a request that the readiness gate is holding.</summary>
    /// <remarks>
    /// The scripted backend reports the workspace loaded a fraction of a second after
    /// <c>solution/open</c>, so this is generous by two orders of magnitude. It is a deadlock
    /// detector, not a budget.
    /// </remarks>
    static readonly TimeSpan LspAnswerTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A solution path handed to the smoke legs so the adapter sends <c>solution/open</c> and the
    /// scripted backend starts its load timeline.
    /// </summary>
    /// <remarks>
    /// It is an empty <c>.slnx</c> written into the publish directory, and it has to exist: nothing
    /// here opens a real solution - the fake answers <c>solution/open</c> from a script - but
    /// discovery refuses to fall through when an explicit setting names something that is not there
    /// (D39), and a refused selection opens the readiness gate immediately. What the path is for is
    /// to take the adapter down the configured branch rather than the misc-files one (C28), so that
    /// the gate is actually exercised.
    /// </remarks>
    string SmokeSolutionPath => (PublishDirectory / "SmokeTest.slnx").ToString();

    /// <summary>
    /// Spawns the published binary as an LSP server and drives a whole session over stdio:
    /// <c>initialize</c>, <c>initialized</c>, a <c>didOpen</c> and a <c>textDocument/definition</c>;
    /// then, once the definition has been answered, <c>shutdown</c> and <c>exit</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// stdout is read as <b>frames</b>, and the whole stream has to parse as a sequence of them with
    /// no bytes left over. That is the point of this leg: it is the only check that nothing in the
    /// process - a logger, a startup banner, a stray Console.WriteLine somebody added in
    /// <c>Cli/</c> where the source scan permits it - writes anything to stdout that is not a
    /// message. A source scan cannot prove that; a running binary can.
    /// </para>
    /// <para>
    /// The definition request is what proves the <em>mediation</em> rather than the transport. It is
    /// sent immediately after <c>initialized</c>, while the scripted backend is still loading, and
    /// that backend answers navigation with an empty successful result until it reports the workspace
    /// loaded - exactly as the real server does (C27). So a non-empty answer here can only mean the
    /// readiness gate held the request and released it afterwards; a gate that had been "simplified"
    /// away would produce a well-formed, successful, empty answer and fail this assertion.
    /// </para>
    /// <para>
    /// <c>shutdown</c> is deliberately <b>not</b> sent until that answer has arrived. Writing the
    /// whole exchange up front would let a correct server shut down with the request still held, and
    /// the leg would then be asserting nothing about the gate at all.
    /// </para>
    /// <para>
    /// stderr is captured and folded into every failure message, because a server that dies during
    /// startup says why there and nowhere else.
    /// </para>
    /// </remarks>
    /// <param name="legName">What this leg is called in the log and in failure messages.</param>
    /// <param name="environment">Extra environment variables that select the backend.</param>
    /// <param name="arguments">The verb and its flags.</param>
    /// <param name="rootUri">The workspace root the client declares; the fake legs use a made-up one.</param>
    /// <param name="document">
    /// A real file to open and ask about, for the live leg. Null uses the one-line document the fake
    /// legs need, which exists only to give the definition request something to name.
    /// </param>
    /// <param name="expectedDiagnosticCode">
    /// A diagnostic the leg waits for on <c>publishDiagnostics</c> before shutting down. Only the
    /// live leg sets it: the scripted backend has no semantic model and publishes nothing.
    /// </param>
    /// <param name="answerTimeout">How long a held request may take. Null uses the smoke budget.</param>
    void RunLspHandshake(
        string legName,
        IReadOnlyDictionary<string, string> environment,
        string arguments,
        string rootUri = "file:///smoke",
        AbsolutePath document = null,
        string expectedDiagnosticCode = null,
        TimeSpan? answerTimeout = null)
    {
        var budget = answerTimeout ?? LspAnswerTimeout;
        var documentUri = document == null ? "file:///smoke/Program.cs" : UriOf(document);
        var documentText = document == null ? "class Program { }" : File.ReadAllText(document);

        // The fake legs ask about the only identifier in their one-line document; the live leg asks
        // about a real call site, found by text so that editing the fixture's comments cannot
        // silently move it.
        var position = document == null
            ? (Line: 0, Character: 6)
            : FindPosition(documentText, "calculator.Compute()", "Compute");

        // Written out whole rather than assembled, so each one reads exactly as it goes on the wire.
        // The ids must stay in step with the Lsp*Id constants.
        //
        // The $$$ and the spaces before the trailing braces are not style: in a raw interpolated
        // string a run of N braces is the interpolation delimiter, and JSON ends in runs of them.
        // Three dollars moves the delimiter to {{{ }}}, and one space between the last closing
        // braces keeps any literal run below it. JSON does not care about the space.
        var session = new[]
        {
            $$$"""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":null,"rootUri":"{{{rootUri}}}","capabilities":{} } }""",
            """{"jsonrpc":"2.0","method":"initialized","params":{}}""",
            $$$"""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"{{{documentUri}}}","languageId":"csharp","version":1,"text":{{{JsonEncode(documentText)}}} } } }""",
            $$$"""{"jsonrpc":"2.0","id":2,"method":"textDocument/definition","params":{"textDocument":{"uri":"{{{documentUri}}}"},"position":{"line":{{{position.Line}}},"character":{{{position.Character}}} } } }""",
        };

        var goodbye = new[]
        {
            """{"jsonrpc":"2.0","id":3,"method":"shutdown"}""",
            """{"jsonrpc":"2.0","method":"exit"}""",
        };

        var diagnostics = new List<string>();

        using var process = new Process { StartInfo = StartInfoFor(arguments, environment) };
        process.ErrorDataReceived += (_, e) => Collect(diagnostics, e.Data);

        Log.Information("Starting {Executable} {Arguments} ({Leg})", PublishedExecutable, arguments, legName);
        process.Start();
        process.BeginErrorReadLine();

        // Drained on its own thread so a server that answers before we finish writing cannot fill the
        // pipe buffer and deadlock us both. It keeps every byte for the byte-exact frame check at the
        // end and, as it goes, records which ids have been answered so the exchange can wait for one.
        var collector = new LspFrameCollector(process.StandardOutput.BaseStream);

        foreach (var request in session)
        {
            WriteLspFrame(process.StandardInput.BaseStream, request);
        }

        var answered = collector.WaitForId(LspDefinitionId, budget);

        if (!answered)
        {
            process.Kill(entireProcessTree: true);
            collector.Join();

            Assert.Fail(
                $"[{legName}] textDocument/definition (id {LspDefinitionId}) was never answered within " +
                $"{budget.TotalSeconds:0} s. A request the readiness gate holds must still be " +
                $"answered once the workspace loads.{FormatDiagnostics(diagnostics)}");
        }

        if (expectedDiagnosticCode != null)
        {
            // The whole point of the live leg. Roslyn reports diagnostics by pull and Claude Code
            // consumes only push, so a published set arriving here - unasked for, at a client that
            // declared nothing - is the one thing no scripted backend can prove.
            var published = collector.WaitForDiagnostic(documentUri, expectedDiagnosticCode, budget);

            if (!published)
            {
                process.Kill(entireProcessTree: true);
                collector.Join();

                Assert.Fail(
                    $"[{legName}] no publishDiagnostics carrying {expectedDiagnosticCode} arrived for " +
                    $"{documentUri} within {budget.TotalSeconds:0} s. The diagnostics bridge is what turns " +
                    $"Roslyn's pull into the push this client only understands.{FormatDiagnostics(diagnostics)}");
            }

            Log.Information("[{Leg}] {Code} was published for {Document}", legName, expectedDiagnosticCode, documentUri);
        }

        foreach (var request in goodbye)
        {
            WriteLspFrame(process.StandardInput.BaseStream, request);
        }

        var exited = process.WaitForExit((int) LspExitTimeout.TotalMilliseconds);

        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            collector.Join();

            Assert.Fail(
                $"[{legName}] The LSP server did not exit within {LspExitTimeout.TotalSeconds:0} s of " +
                $"being sent shutdown and exit.{FormatDiagnostics(diagnostics)}");
        }

        collector.Join();

        Assert.True(collector.Failure == null,
            $"[{legName}] Reading the LSP server's stdout failed: {collector.Failure?.Message}" +
            FormatDiagnostics(diagnostics));

        // The specification's rule, and the reason the exchange sends shutdown before exit: exit
        // after shutdown is 0, exit without one is 1.
        Assert.True(process.ExitCode == 0,
            $"[{legName}] The LSP server exited with code {process.ExitCode} after a shutdown/exit " +
            $"sequence, expected 0.{FormatDiagnostics(diagnostics)}");

        var responses = ParseLspFrames(collector.Bytes, diagnostics);

        Assert.True(responses.ContainsKey(LspInitializeId),
            $"[{legName}] The LSP server never answered initialize (id {LspInitializeId})." +
            FormatDiagnostics(diagnostics));
        Assert.True(responses.ContainsKey(LspShutdownId),
            $"[{legName}] The LSP server never answered shutdown (id {LspShutdownId})." +
            FormatDiagnostics(diagnostics));

        var result = responses[LspInitializeId].GetProperty("result");
        var serverName = result.GetProperty("serverInfo").GetProperty("name").GetString();

        Assert.True(serverName == ProductName,
            $"[{legName}] initialize returned serverInfo.name '{serverName}', expected '{ProductName}'.");

        // C26: the capability document is this repository's own, not the backend's. The scripted
        // backend answers with Roslyn's real one, which advertises all three of these.
        var capabilities = result.GetProperty("capabilities");

        foreach (var provider in new[] { "semanticTokensProvider", "codeLensProvider", "inlayHintProvider" })
        {
            Assert.True(!capabilities.TryGetProperty(provider, out _),
                $"[{legName}] The adapter advertised '{provider}', which it bridges nothing for.");
        }

        Assert.True(capabilities.TryGetProperty("definitionProvider", out _),
            $"[{legName}] The adapter did not advertise definitionProvider.");

        AssertDefinitionWasHeldUntilReady(legName, responses, diagnostics);

        var shutdown = responses[LspShutdownId];

        Assert.True(shutdown.TryGetProperty("result", out var shutdownResult)
                    && shutdownResult.ValueKind == JsonValueKind.Null,
            $"[{legName}] shutdown must answer with a result member that is present and null; JSON-RPC " +
            "identifies a response by the presence of result or error.");

        Log.Information("LSP handshake OK ({Leg}): {Server} answered {Count} request(s) and exited 0",
            legName, serverName, responses.Count);
    }

    /// <summary>
    /// Asserts that the definition answer is the one the backend only gives once the workspace is
    /// loaded - which is the whole of the readiness gate, proved on a real binary.
    /// </summary>
    /// <param name="legName">Which leg is being checked.</param>
    /// <param name="responses">Everything the server wrote, keyed by id.</param>
    /// <param name="diagnostics">The server's stderr, folded into any failure.</param>
    static void AssertDefinitionWasHeldUntilReady(
        string legName,
        IReadOnlyDictionary<int, JsonElement> responses,
        List<string> diagnostics)
    {
        var definition = responses[LspDefinitionId];

        Assert.True(!definition.TryGetProperty("error", out var error),
            $"[{legName}] textDocument/definition failed: {error}.{FormatDiagnostics(diagnostics)}");

        var locations = definition.GetProperty("result");

        Assert.True(locations.ValueKind == JsonValueKind.Array,
            $"[{legName}] textDocument/definition returned a '{locations.ValueKind}', expected an array.");

        Assert.True(locations.GetArrayLength() > 0,
            $"[{legName}] textDocument/definition returned an EMPTY array. The scripted backend answers " +
            "navigation empty until it reports the workspace loaded (C27), so an empty answer here means " +
            "the request was forwarded before the readiness gate should have released it." +
            FormatDiagnostics(diagnostics));
    }

    /// <summary>
    /// Reads a server's stdout on its own thread, keeping every byte and noting which JSON-RPC ids
    /// have been answered so far.
    /// </summary>
    /// <remarks>
    /// Two jobs in one class because they have to share the read: the byte-exact frame check at the
    /// end needs the whole stream, and the exchange needs to know when a particular answer has
    /// arrived so it can send the next message. A second reader on the same pipe would race the first.
    /// </remarks>
    sealed class LspFrameCollector
    {
        readonly MemoryStream _buffer = new();
        readonly HashSet<int> _answered = [];
        readonly List<string> _notifications = [];
        readonly Thread _thread;
        readonly object _gate = new();

        internal LspFrameCollector(Stream stdout)
        {
            _thread = new Thread(() => Read(stdout)) { IsBackground = true };
            _thread.Start();
        }

        /// <summary>What went wrong while reading, if anything.</summary>
        internal Exception Failure { get; private set; }

        /// <summary>Every byte the server wrote.</summary>
        internal byte[] Bytes
        {
            get
            {
                lock (_gate)
                {
                    return _buffer.ToArray();
                }
            }
        }

        /// <summary>Blocks until a response with <paramref name="id"/> has been seen, or gives up.</summary>
        /// <param name="id">The JSON-RPC id being waited for.</param>
        /// <param name="timeout">How long to wait.</param>
        internal bool WaitForId(int id, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (_answered.Contains(id))
                    {
                        return true;
                    }

                    if (Failure != null || !_thread.IsAlive)
                    {
                        return false;
                    }
                }

                Thread.Sleep(20);
            }

            return false;
        }

        /// <summary>
        /// Blocks until a <c>publishDiagnostics</c> for one document carries a diagnostic code.
        /// </summary>
        /// <remarks>
        /// Matched as substrings of the raw message rather than by parsing. The body is one
        /// notification whose URI and codes are both distinctive strings, and a JSON walk here would
        /// only be a second implementation of what <c>ParseLspFrames</c> already does at the end.
        /// </remarks>
        /// <param name="uri">The document URI.</param>
        /// <param name="code">The diagnostic id, e.g. <c>CS0029</c>.</param>
        /// <param name="timeout">How long to wait.</param>
        internal bool WaitForDiagnostic(string uri, string code, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    foreach (var notification in _notifications)
                    {
                        if (notification.Contains(uri, StringComparison.Ordinal)
                            && notification.Contains(code, StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }

                    if (Failure != null || !_thread.IsAlive)
                    {
                        return false;
                    }
                }

                Thread.Sleep(20);
            }

            return false;
        }

        /// <summary>Waits for the reader to finish, which happens when the server closes stdout.</summary>
        internal void Join() => _thread.Join(TimeSpan.FromSeconds(5));

        void Read(Stream stdout)
        {
            var pending = new List<byte>();
            var chunk = new byte[4096];

            try
            {
                int read;

                while ((read = stdout.Read(chunk, 0, chunk.Length)) > 0)
                {
                    lock (_gate)
                    {
                        _buffer.Write(chunk, 0, read);
                    }

                    for (var index = 0; index < read; index++)
                    {
                        pending.Add(chunk[index]);
                    }

                    NoteCompleteFrames(pending);
                }
            }
            catch (Exception exception)
            {
                Failure = exception;
            }
        }

        /// <summary>Pulls whole frames off the front of the buffer and records their ids.</summary>
        void NoteCompleteFrames(List<byte> pending)
        {
            while (true)
            {
                var wire = pending.ToArray();
                var separator = wire.AsSpan().IndexOf("\r\n\r\n"u8);

                if (separator < 0)
                {
                    return;
                }

                var header = Encoding.ASCII.GetString(wire, 0, separator);
                var length = -1;

                foreach (var line in header.Split("\r\n"))
                {
                    var colon = line.IndexOf(':');

                    if (colon > 0
                        && line.Substring(0, colon).Trim()
                            .Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(line.Substring(colon + 1).Trim(), out var parsed))
                    {
                        length = parsed;
                    }
                }

                var start = separator + 4;

                if (length < 0 || wire.Length < start + length)
                {
                    return;
                }

                try
                {
                    using var document = JsonDocument.Parse(wire.AsMemory(start, length));

                    if (document.RootElement.TryGetProperty("method", out var method))
                    {
                        if (method.GetString() == "textDocument/publishDiagnostics")
                        {
                            var text = Encoding.UTF8.GetString(wire, start, length);

                            lock (_gate)
                            {
                                _notifications.Add(text);
                            }
                        }
                    }
                    else if (document.RootElement.TryGetProperty("id", out var id) && id.TryGetInt32(out var value))
                    {
                        lock (_gate)
                        {
                            _answered.Add(value);
                        }
                    }
                }
                catch (JsonException)
                {
                    // The final ParseLspFrames pass is what reports a malformed body; this pass only
                    // has to know which ids have come back.
                }

                pending.RemoveRange(0, start + length);
            }
        }
    }

    /// <summary>The <c>file:</c> URI of a path, which is what an LSP client sends.</summary>
    static string UriOf(AbsolutePath path) => new Uri(path.ToString()).AbsoluteUri;

    /// <summary>Encodes a string as a JSON string literal, quotes included.</summary>
    /// <remarks>
    /// A whole source file goes through this, so the escaping has to be real: the fixture carries
    /// backslashes in its doc comments and every line ends in one of two ways depending on how git
    /// checked it out.
    /// </remarks>
    static string JsonEncode(string value)
    {
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(value);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Finds a zero-based LSP position by searching the text, rather than hard-coding a line number.
    /// </summary>
    /// <param name="text">The whole document.</param>
    /// <param name="lineContains">A substring identifying the line.</param>
    /// <param name="token">The token in it whose first character is the position.</param>
    static (int Line, int Character) FindPosition(string text, string lineContains, string token)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].Contains(lineContains, StringComparison.Ordinal))
            {
                continue;
            }

            var character = lines[index].IndexOf(token, StringComparison.Ordinal);

            if (character >= 0)
            {
                return (index, character);
            }
        }

        Assert.Fail($"'{lineContains}' is not in the document; the fixture has moved underneath this build.");
        return default;
    }

    /// <summary>Writes one <c>Content-Length</c>-framed message.</summary>
    static void WriteLspFrame(Stream stream, string message)
    {
        var body = Encoding.UTF8.GetBytes(message);

        // The header is ASCII and the length is the encoded BYTE count. Written together, because a
        // header that reaches the peer without its body is a frame the peer waits forever for.
        var frame = new MemoryStream();
        var header = Encoding.ASCII.GetBytes(
            "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");

        frame.Write(header, 0, header.Length);
        frame.Write(body, 0, body.Length);

        var bytes = frame.ToArray();
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    /// <summary>
    /// Parses the whole of stdout as a sequence of frames, keyed by JSON-RPC id, and fails on any
    /// byte that is not part of one.
    /// </summary>
    static IReadOnlyDictionary<int, JsonElement> ParseLspFrames(byte[] wire, List<string> diagnostics)
    {
        var responses = new Dictionary<int, JsonElement>();
        var offset = 0;

        while (offset < wire.Length)
        {
            var relative = wire.AsSpan(offset).IndexOf("\r\n\r\n"u8);

            Assert.True(relative >= 0,
                $"stdout carried {wire.Length - offset} trailing byte(s) that are not a framed message: " +
                $"'{Preview(wire, offset)}'.{FormatDiagnostics(diagnostics)}");

            var headerText = Encoding.ASCII.GetString(wire, offset, relative);
            int? contentLength = null;

            foreach (var line in headerText.Split("\r\n"))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var colon = line.IndexOf(':');

                Assert.True(colon > 0,
                    $"stdout carried the header line '{line}', which has no field separator." +
                    FormatDiagnostics(diagnostics));

                var name = line.Substring(0, colon).Trim();
                var headerValue = line.Substring(colon + 1).Trim();

                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.True(
                        int.TryParse(headerValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed),
                        $"Content-Length was '{headerValue}', which is not a non-negative integer." +
                        FormatDiagnostics(diagnostics));

                    contentLength = parsed;
                }
                else
                {
                    Assert.True(name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase),
                        $"stdout carried the header '{name}', which the LSP base protocol does not define." +
                        FormatDiagnostics(diagnostics));
                }
            }

            Assert.True(contentLength.HasValue,
                "A message on stdout carried no Content-Length header." + FormatDiagnostics(diagnostics));

            var start = offset + relative + 4;

            Assert.True(start + contentLength.Value <= wire.Length,
                $"A message declared {contentLength.Value} body bytes but only {wire.Length - start} " +
                $"followed.{FormatDiagnostics(diagnostics)}");

            using var document = JsonDocument.Parse(wire.AsMemory(start, contentLength.Value));

            if (document.RootElement.TryGetProperty("id", out var id) && id.TryGetInt32(out var value))
            {
                responses[value] = document.RootElement.Clone();
            }

            offset = start + contentLength.Value;
        }

        return responses;
    }

    /// <summary>A short, printable excerpt of whatever was on stdout that should not have been.</summary>
    static string Preview(byte[] wire, int offset)
    {
        var length = Math.Min(120, wire.Length - offset);
        return Encoding.UTF8.GetString(wire, offset, length).Replace("\r", "\\r").Replace("\n", "\\n");
    }

    // ---------------------------------------------------------------------------------------
    // The MCP leg
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Drives one MCP handshake and asserts the inventory it answers with, returning the names for
    /// any further assertion the caller wants to make.
    /// </summary>
    /// <param name="environment">Extra environment variables for the server process, or null.</param>
    /// <param name="expectedToolNames">The complete set <c>tools/list</c> must return.</param>
    /// <param name="arguments">The verb. This binary has no default one, so it is never empty.</param>
    string[] Handshake(IReadOnlyDictionary<string, string> environment, string[] expectedToolNames, string arguments)
    {
        var responses = RunHandshake(arguments, environment);

        var serverName = responses[InitializeId]
            .GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString();
        Assert.True(serverName == ProductName,
            $"initialize returned serverInfo.name '{serverName}', expected '{ProductName}'");

        var toolsElement = responses[ToolsListId].GetProperty("result").GetProperty("tools");
        Assert.True(toolsElement.ValueKind == JsonValueKind.Array,
            $"tools/list returned a '{toolsElement.ValueKind}' for 'tools', expected an array");

        var toolNames = toolsElement.EnumerateArray()
            .Select(x => x.GetProperty("name").GetString())
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var expected = expectedToolNames.OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.True(toolNames.SequenceEqual(expected, StringComparer.Ordinal),
            $"tools/list returned [{string.Join(", ", toolNames)}], expected [{string.Join(", ", expected)}]");

        Log.Information("MCP handshake OK: {Server} exposed {Count} tool(s){Mode}",
            serverName,
            toolNames.Length,
            environment == null ? string.Empty : " with " + string.Join(", ", environment.Select(x => $"{x.Key}={x.Value}")));

        return toolNames;
    }

    const int InitializeId = 1;
    const int ToolsListId = 2;

    /// <summary>
    /// Spawns the published binary, drives one initialize / initialized / tools/list exchange and
    /// returns the responses keyed by JSON-RPC id.
    /// </summary>
    /// <remarks>
    /// stdin is deliberately held open until both responses have been read: the stdio transport tears
    /// down as soon as stdin hits EOF and drops whatever is still in flight, so closing stdin first
    /// loses responses. Responses are matched by id because the order is not guaranteed.
    /// </remarks>
    /// <param name="arguments">The verb to launch with.</param>
    /// <param name="environment">
    /// Extra environment variables for the server process, layered on top of this process's own. Null
    /// inherits the environment unchanged.
    /// </param>
    IReadOnlyDictionary<int, JsonElement> RunHandshake(
        string arguments,
        IReadOnlyDictionary<string, string> environment = null)
    {
        // The literal ids must stay in sync with InitializeId / ToolsListId; the JSON is written out
        // verbatim rather than interpolated so it reads exactly as it goes on the wire.
        var requests = new[]
        {
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"fallout-smoketest","version":"1.0.0"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""",
        };

        var responses = new Dictionary<int, JsonElement>();
        var diagnostics = new List<string>();
        Exception readerFailure = null;

        using var process = new Process { StartInfo = StartInfoFor(arguments, environment) };
        process.ErrorDataReceived += (_, e) => Collect(diagnostics, e.Data);

        Log.Information("Starting {Executable} {Arguments}", PublishedExecutable, arguments);
        process.Start();
        process.BeginErrorReadLine();

        var reader = new Thread(() =>
        {
            try
            {
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.TryGetProperty("id", out var id) && id.TryGetInt32(out var value))
                    {
                        lock (responses)
                        {
                            responses[value] = document.RootElement.Clone();
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                readerFailure = exception;
            }
        }) { IsBackground = true };
        reader.Start();

        try
        {
            foreach (var request in requests)
            {
                process.StandardInput.WriteLine(request);
                process.StandardInput.Flush();
            }

            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                lock (responses)
                {
                    if (responses.ContainsKey(InitializeId) && responses.ContainsKey(ToolsListId))
                    {
                        break;
                    }
                }

                if (readerFailure != null)
                {
                    throw new InvalidOperationException(
                        $"Reading the server's stdout failed.{FormatDiagnostics(diagnostics)}", readerFailure);
                }

                if (process.HasExited && stopwatch.Elapsed > TimeSpan.FromSeconds(1))
                {
                    throw new InvalidOperationException(
                        $"The server exited with code {process.ExitCode} before answering." +
                        FormatDiagnostics(diagnostics));
                }

                Assert.True(stopwatch.Elapsed < SmokeTestTimeout,
                    $"Timed out after {SmokeTestTimeout.TotalSeconds:0} s waiting for the initialize and " +
                    $"tools/list responses.{FormatDiagnostics(diagnostics)}");

                Thread.Sleep(millisecondsTimeout: 25);
            }

            lock (responses)
            {
                return new Dictionary<int, JsonElement>(responses);
            }
        }
        finally
        {
            // Only now: EOF on stdin is the server's shutdown signal.
            TryCloseInput(process);

            if (!process.WaitForExit(milliseconds: 5000))
            {
                Log.Warning("Server did not exit after stdin was closed; killing it");
                process.Kill(entireProcessTree: true);
            }

            reader.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>The start info both legs use: redirected streams, UTF-8, and the verb.</summary>
    /// <param name="arguments">The verb to launch with.</param>
    /// <param name="environment">Extra variables layered on this process's own block, or null.</param>
    ProcessStartInfo StartInfoFor(string arguments, IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo
                        {
                            FileName = PublishedExecutable,
                            Arguments = arguments,
                            WorkingDirectory = PublishDirectory,
                            UseShellExecute = false,
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        };

        // ProcessStartInfo.Environment starts as a copy of this process's block, so assigning here
        // overrides one variable and leaves PATH and the rest intact.
        foreach (var variable in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        return startInfo;
    }

    static void Collect(List<string> diagnostics, string line)
    {
        if (line == null)
        {
            return;
        }

        lock (diagnostics)
        {
            diagnostics.Add(line);
        }
    }

    static void TryCloseInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The server may already have gone away.
        }
    }

    static string FormatDiagnostics(List<string> diagnostics)
    {
        lock (diagnostics)
        {
            return diagnostics.Count == 0
                ? string.Empty
                : Environment.NewLine + "Server stderr:" + Environment.NewLine + string.Join(Environment.NewLine, diagnostics);
        }
    }

    string ICreateGitHubRelease.Name => $"v{Version}";

    /// <summary>A version with a pre-release suffix (0.1.0-rc.1, 0.0.1-test) never gets marked "Latest".</summary>
    bool ICreateGitHubRelease.Prerelease => Version.Contains('-');

    IEnumerable<AbsolutePath> ICreateGitHubRelease.AssetFiles => ArchivesDirectory.GlobFiles("*.zip", "*.tar.gz");

    /// <summary>
    /// The release body is read from here rather than from CHANGELOG.md directly.
    /// </summary>
    /// <remarks>
    /// <c>ICreateGitHubRelease</c> builds the body with <c>ChangelogTasks.ExtractChangelogSectionNotes</c>,
    /// which only recognises <c>## </c> headings and stops a section at the first line that is not a
    /// bullet. Our changelog uses <c>#</c> headings (the format <c>ReleaseNotesParser</c> - the version
    /// authority - expects) and wraps its bullets over several lines, so pointed at CHANGELOG.md that
    /// helper finds nothing and the release ships with an empty body. <see cref="WriteReleaseNotes"/>
    /// rewrites the top section into the shape it does understand.
    /// </remarks>
    string IHasChangelog.ChangelogFile => ReleaseNotesFile;

    // Both actions run before the inherited release logic: actions are appended in call order.
    Target ICreateGitHubRelease.CreateGitHubRelease => _ => _
        .Executes(AssertReleaseTagMatchesChangelogVersion)
        .Executes(WriteReleaseNotes)
        .Inherit<ICreateGitHubRelease>();

    /// <summary>
    /// Rewrites the newest CHANGELOG.md section into <see cref="ReleaseNotesFile"/> as a
    /// <c>## version</c> heading followed by one single-line bullet per entry, which is the only shape
    /// <c>ExtractChangelogSectionNotes</c> reads back in full.
    /// </summary>
    void WriteReleaseNotes()
    {
        var bullets = new List<string>();

        foreach (var line in LatestReleaseNotes.Notes)
        {
            // A "- " line opens a new entry; every other line is the continuation of a wrapped one.
            // The leading prose paragraph has no bullet to continue, so it becomes an entry itself.
            if (line.StartsWith("- ", StringComparison.Ordinal) || bullets.Count == 0)
            {
                bullets.Add(line.StartsWith("- ", StringComparison.Ordinal) ? line[2..] : line);
            }
            else
            {
                bullets[^1] += " " + line;
            }
        }

        var lines = new List<string> { $"## {Version}" };
        lines.AddRange(bullets.Select(x => $"- {x}"));

        ReleaseNotesFile.Parent.CreateDirectory();
        ReleaseNotesFile.WriteAllLines(lines.ToArray());

        Log.Information("Wrote {Count} release note(s) to {File}", bullets.Count, ReleaseNotesFile);
    }

    /// <summary>
    /// In CI the git tag is what people see; CHANGELOG.md is what the build believes. If the two
    /// disagree the release would be named after one and contain the other, so fail loudly.
    /// </summary>
    void AssertReleaseTagMatchesChangelogVersion()
    {
        if (GitHubActions.Instance == null)
        {
            Log.Warning("Not running in GitHub Actions - skipping the release tag check");
            return;
        }

        var expected = $"v{Version}";
        var actual = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");

        Assert.True(actual == expected,
            $"Refusing to publish: the workflow ran for ref '{actual}' but CHANGELOG.md says the version " +
            $"is {Version} (tag '{expected}'). Tag the commit that carries the matching CHANGELOG entry.");
    }
}
