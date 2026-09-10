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
    /// Empty, and asserted as empty: this scaffold registers no tools, and "the MCP server answers
    /// tools/list with nothing in it" is a true and useful statement about it. The moment WP5 adds a
    /// tool, this array is where its name goes - alongside AGENTS.md's tool table, the inventory tests
    /// and SKILL.md - and until then the empty comparison fails loudly if a tool appears without
    /// being declared here.
    /// </remarks>
    static readonly string[] ExpectedToolNames = [];

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
        .Description("Drives a real LSP handshake and a real MCP handshake against the published AOT binary")
        .DependsOn(PublishAot)
        .Executes(() =>
        {
            // Leg 1: the LSP verb. This is the leg that proves the framing, the id echo and the
            // shutdown/exit contract survived Native AOT compilation on this architecture.
            RunLspHandshake();

            // Leg 2: the MCP verb, same binary, different argument.
            var toolNames = Handshake(environment: null, ExpectedToolNames, arguments: "mcp");

            ReportSummary(_ => _
                .AddPair("Runtime", Runtime)
                .AddPair("Tools", toolNames.Length.ToString()));
        });

    // ---------------------------------------------------------------------------------------
    // The LSP leg
    // ---------------------------------------------------------------------------------------

    const int LspInitializeId = 1;
    const int LspShutdownId = 2;

    /// <summary>
    /// Spawns the published binary as an LSP server and drives
    /// <c>initialize</c> / <c>initialized</c> / <c>shutdown</c> / <c>exit</c> over stdio.
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
    /// stderr is captured and folded into every failure message, because a server that dies during
    /// startup says why there and nowhere else.
    /// </para>
    /// </remarks>
    void RunLspHandshake()
    {
        // Written out verbatim rather than interpolated so each one reads exactly as it goes on the
        // wire. The ids must stay in step with LspInitializeId / LspShutdownId.
        var requests = new[]
        {
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":null,"rootUri":null,"capabilities":{}}}""",
            """{"jsonrpc":"2.0","method":"initialized","params":{}}""",
            """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""",
            """{"jsonrpc":"2.0","method":"exit"}""",
        };

        var diagnostics = new List<string>();

        using var process = new Process { StartInfo = StartInfoFor(arguments: "lsp", environment: null) };
        process.ErrorDataReceived += (_, e) => Collect(diagnostics, e.Data);

        Log.Information("Starting {Executable} lsp", PublishedExecutable);
        process.Start();
        process.BeginErrorReadLine();

        // Drained on a thread so a server that answers before we finish writing cannot fill the pipe
        // buffer and deadlock us both.
        var stdout = new MemoryStream();
        Exception readerFailure = null;

        var reader = new Thread(() =>
        {
            try
            {
                process.StandardOutput.BaseStream.CopyTo(stdout);
            }
            catch (Exception exception)
            {
                readerFailure = exception;
            }
        }) { IsBackground = true };
        reader.Start();

        foreach (var request in requests)
        {
            WriteLspFrame(process.StandardInput.BaseStream, request);
        }

        var exited = process.WaitForExit((int) LspExitTimeout.TotalMilliseconds);

        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            reader.Join(TimeSpan.FromSeconds(5));

            Assert.Fail(
                $"The LSP server did not exit within {LspExitTimeout.TotalSeconds:0} s of being sent " +
                $"shutdown and exit.{FormatDiagnostics(diagnostics)}");
        }

        reader.Join(TimeSpan.FromSeconds(5));

        Assert.True(readerFailure == null,
            $"Reading the LSP server's stdout failed: {readerFailure?.Message}{FormatDiagnostics(diagnostics)}");

        // The specification's rule, and the reason the exchange sends shutdown before exit: exit
        // after shutdown is 0, exit without one is 1.
        Assert.True(process.ExitCode == 0,
            $"The LSP server exited with code {process.ExitCode} after a shutdown/exit sequence, " +
            $"expected 0.{FormatDiagnostics(diagnostics)}");

        var responses = ParseLspFrames(stdout.ToArray(), diagnostics);

        Assert.True(responses.ContainsKey(LspInitializeId),
            $"The LSP server never answered initialize (id {LspInitializeId}).{FormatDiagnostics(diagnostics)}");
        Assert.True(responses.ContainsKey(LspShutdownId),
            $"The LSP server never answered shutdown (id {LspShutdownId}).{FormatDiagnostics(diagnostics)}");

        var result = responses[LspInitializeId].GetProperty("result");
        var serverName = result.GetProperty("serverInfo").GetProperty("name").GetString();

        Assert.True(serverName == ProductName,
            $"initialize returned serverInfo.name '{serverName}', expected '{ProductName}'.");

        var shutdown = responses[LspShutdownId];

        Assert.True(shutdown.TryGetProperty("result", out var shutdownResult)
                    && shutdownResult.ValueKind == JsonValueKind.Null,
            "shutdown must answer with a result member that is present and null; JSON-RPC identifies a " +
            "response by the presence of result or error.");

        Log.Information("LSP handshake OK: {Server} answered {Count} request(s) and exited 0",
            serverName, responses.Count);
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
