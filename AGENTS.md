# AGENTS.md

Guidance for humans and AI agents working in this repository. `CLAUDE.md` imports this file, so keep
it the single source of truth.

`claude-roslyn-lsp` is one .NET 10 binary, published as a Native AOT executable per RID, that puts
Microsoft's `roslyn-language-server` in front of coding agents in two ways at once: as an **LSP
server** that mediates the protocol so Claude Code's language-server tool actually works, and as an
**MCP server** that exposes the mutating operations the LSP tool has no slot for. It contains no
compiler. Roslyn runs in a child process this binary downloads, launches and supervises — treat that
as a hard constraint, not a preference, because it is what keeps the dependency tree small enough for
one person to audit and the binary small enough to ship five of them per release.

## Hard rules

1. **Never reference `Microsoft.CodeAnalysis.*`, `StreamJsonRpc` or an OmniSharp package.** The whole
   design (D1) is that the compiler is somebody else's process. A Roslyn reference here would add
   tens of megabytes and a second, divergent copy of the analysis stack to a binary whose job is to
   *talk to* the real one. Framing, process launch, download, unzip, hashing and named pipes are all
   pure BCL, and they stay that way.
2. **Never transcribe code from the prior art.** `ClaudeCodeRoslynLspProxy`,
   `erinloy/claude-roslyn-lsp`, `SamHurne/roslyn-lsp-adapter` and `csharp-ls` are all worth reading
   and all credited in `README.md`; their licences would even permit copying. Read them for *facts* —
   which flag `roslyn-language-server` wants, which request Roslyn answers, where a client trips — and
   write the expression here. Protocol facts come from the **LSP 3.17 specification** and from the
   **`dotnet/roslyn` LanguageServer sources** (MIT); cite the file or the section you took a fact
   from, in a comment or in *Protocol gotchas* below, so the next person can re-verify it without
   re-deriving it.
3. **No new NuGet packages without a decision recorded in this file.** The *Package budget* below is
   complete. If a package looks necessary, add a row to *Package budget changes* with the date, the
   package, and why nothing already present can do the job — before referencing it.
4. **Never hand-edit `.github/workflows/build.yml` or `.github/workflows/publish.yml`.** Both are
   generated from the two `[GitHubActions]` attributes in `build/Build.CI.GitHubActions.cs`, and
   regenerating overwrites edits. Change the attribute and re-run the build (any run regenerates;
   `dotnet fallout --generate-configuration GitHubActions_publish --host GitHubActions` does just the
   one). A run whose generated output differs *aborts* with `Configuration files for GitHubActions
   (…) have changed` — run it again and commit the regenerated YAML.
   (`.github/workflows/release.yml` is hand-written by design — the generator has no matrix support —
   and *is* edited directly.)
5. **No `Console.*` outside `src/ClaudeRoslynLsp/Cli/`, and inside it only `Cli/CliRuntime.cs` opens a
   standard stream.** In both server modes stdout *is* the protocol channel — `Content-Length`-framed
   JSON-RPC for `lsp`, newline-delimited JSON-RPC for `mcp` — and one stray write corrupts it with no
   error anywhere. Logging goes to stderr
   (`AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)`). `NoStdoutWritesTest` enforces
   the directory; the single-file rule is enforced by review, and is what makes the directory rule
   reviewable at all.
6. **`.gitignore` must never contain a bare `build` rule.** The Fallout build project lives in
   `build/`; a stock Visual Studio .gitignore silently untracks the entire orchestrator.
7. LF line endings everywhere (`.gitattributes` enforces it). `TreatWarningsAsErrors` is on — the
   compiler and the analyzers are the lint step. Never suppress a warning to get green, and never
   weaken or delete a rule-enforcing test to make a change pass.

## Design decisions

D1–D4 were confirmed with the user on 2026-09-10 and are the shape of the product. D5–D13 are the
scaffold's own choices, most of them inherited from the sibling repositories `bitbucket-mcp` and
`sonarqube-mcp` and restated here with the argument that applies *here*. D14 and beyond are named so
that later work packages have a number to fill in rather than a decision to invent; a row that says
**TBD** is a decision that has not been made, not one that has been forgotten.

| # | Decision |
|---|---|
| D1 | **Proxy, don't reimplement.** A pure-BCL Native AOT adapter in front of Microsoft's `roslyn-language-server`, plus an MCP server for the mutations. Reimplementing C# analysis is not a thing one person maintains, and Microsoft now publishes the real engine as a public MIT dotnet tool — so the only defensible scope is the *mediation*, which is exactly the part nobody else has done. Zero Roslyn packages in this repository (hard rule 1). |
| D2 | **Acquire the pinned Roslyn server at runtime, and pin it hard.** The adapter fetches `roslyn-language-server.<rid>` from nuget.org into a per-user cache on first run, hash-verified, with explicit overrides. Bundling it would multiply the release archive by an order of magnitude per RID; requiring the user to install it would reproduce the exact failure this project exists to fix, which is a plugin that launches a binary nobody has. The pin is one constant, because the server is prerelease-only with an unstable CLI: a floating version is a release that breaks on somebody else's schedule. |
| D3 | **One binary, one package, verbs — and no default verb.** `lsp`, `mcp`, `doctor`, and a hidden `fake-roslyn`; `--version` and `--help`. Two servers sharing one stdout means a client that omits the verb would be connected to the wrong protocol and simply hang, so a bare invocation exits 2 with the usage text on stderr. This is the one place the sibling repositories' shape is deliberately *not* copied — there, no arguments means "serve". |
| D4 | **Reach in v1 is every agent that speaks either protocol.** Claude Code gets a plugin carrying both servers and the skill; Copilot CLI and OpenCode get LSP configuration; Codex, Gemini CLI, Cursor and VS Code get MCP configuration. The same binary answers all of them, so breadth costs documentation rather than code. |
| D5 | **A bare `ServiceCollection` for the MCP server**, not `Host.CreateApplicationBuilder`. The SDK registers `McpServer` as a singleton and running it directly is the SDK's own AOT test-app shape; the generic host would add configuration providers, metrics and lifetime machinery to a cold start that a client waits on. `PosixSignalRegistration` handles SIGINT/SIGTERM instead. |
| D6 | **`JsonSerializerIsReflectionEnabledByDefault=false`, in the product *and* the test project.** A type missing from a `JsonSerializerContext` then fails under `dotnet test`, where it costs a minute, instead of only after a Native AOT publish, where it costs a release. Every serializer call passes an explicit `JsonTypeInfo<T>` so nothing can fall back to reflection by accident. |
| D7 | **Two source-generated JSON contexts, never chained together.** `Protocol/LspJsonContext` carries LSP wire shapes, spelled with an explicit `[JsonPropertyName]` on every property because they are somebody else's specification; `Mcp/Models/RoslynToolJsonContext` carries the tool vocabulary in camelCase and goes **first** in the MCP server's `TypeInfoResolverChain`, with the SDK's resolver second. A shared resolver would let one vocabulary leak into the other, and the leak would look like a working feature. |
| D8 | **No `RuntimeIdentifiers` in the csproj, and `PublishAot` keyed off `RuntimeIdentifier`.** Listing RIDs would pull an ILCompiler pack per RID on every restore; the RID list lives in `Build.cs` and the release matrix and reaches the SDK only through `-r`. `dotnet pack` of a tool package runs its own publish, which has no RID, so an unconditional `PublishAot=true` would fail it — keying the property off `RuntimeIdentifier` gives the release path AOT and the pack path a portable IL tool from one project file. No `PublishSingleFile` (ignored under AOT), no `SelfContained` (implied). |
| D9 | **xunit.v3 3.x with the `xunit.runner.visualstudio` VSTest bridge**, plus `Microsoft.NET.Test.Sdk`, so the stock Fallout `ITest` component works unmodified. No mocking or assertion libraries: fakes are hand-rolled, which keeps the test project's dependency tree as auditable as the product's. Held at 3.x deliberately — xunit.v3 4.0.0 drops VSTest-mode support, and VSTest mode is how Fallout reports failures with their text rather than as a bare pass/fail. |
| D10 | **Environment variables only, and `FromEnvironment` never throws.** One `ClaudeRoslynLspOptions` record, read once per process. A malformed value falls back to the documented default and is reported through the stderr logger rather than by failing startup: a server that refuses to start leaves its client with a dead process and no channel to complain on, because stdout is the protocol. `CLAUDE_PLUGIN_OPTION_<NAME>` is read **first**, with blank treated as absent, then the plain `<NAME>` — see *The plugin-option rule*. There is no configuration file and no `Microsoft.Extensions.Configuration`. |
| D11 | **`Cli/CliRuntime.cs` owns the console, and the protocol streams are raw `Stream`s.** stdin and stdout are opened once, as bytes. Never `Console.In`/`Console.Out`: a `TextWriter` applies an encoding and, on Windows, rewrites `\n` as `\r\n`, which would put a `Content-Length` header out of step with the body it measured — a corruption that produces no error, only a client that stops answering. |
| D12 | **Framing is ours, bounded, and strict.** `Protocol/LspFrameReader` accepts only `Content-Length` and `Content-Type`, requires CRLF, caps the header section at 64 KiB and the body at 32 MiB, and treats anything else as an unrecoverable protocol error. Strictness here is not pedantry: this reader sits *between* two peers that both claim to speak LSP, so accepting something Roslyn would reject only moves the failure somewhere harder to see. `LspFrameWriter` writes each frame in a single `WriteAsync` behind a gate, because a header separated from its body can be interleaved with another frame's. |
| D13 | **Full document synchronisation (`change: 1`), not incremental.** Roslyn accepts full-text `didChange`, and the adapter has to keep a document mirror anyway so it can replay state after a Roslyn crash. A mirror rebuilt from a range-edit history that was interrupted halfway is wrong in a way that shows up as wrong *answers*, not as an error; the bytes incremental sync saves are not worth that. |
| D14 | **The `initialize` document the adapter sends Roslyn is authored, not derived from the client's.** — **TBD in WP4.** The shape is sketched in the plan (configuration, workspaceFolders, dynamic watched files, dynamic diagnostics with refresh, work-done progress, the navigation and edit capabilities, `workspaceEdit` with `documentChanges` and `resourceOperations`, UTF-16) and deliberately omits semantic tokens, inlay hints and code lens so Roslyn never registers or refreshes them. |
| D15 | **How the adapter talks to Roslyn — a named pipe by default, stdio as the fallback.** Roslyn *connects* to a pipe the adapter created (C8), so the `NamedPipeServerStream` (`InOut`, one instance, byte mode, `Asynchronous \| CurrentUserOnly`) has to exist before the process is started; the name is `claude-roslyn-lsp-<pid>-<8 hex>` and the connect budget is 30 s. The pipe is preferred because on it the protocol is a channel nothing else holds a handle to — the pinned build writes a 646-byte banner to its stdout in pipe mode (C7), which under `--stdio` would have been protocol corruption. `CLAUDE_ROSLYN_LSP_TRANSPORT=stdio` exists for environments with no usable named pipe. Details in D37 and D38. |
| D16 | **The diagnostics bridge: pull from Roslyn, push to the client.** — **TBD in WP4.** Debounce, the per-uri in-flight rule, `previousResultId` handling, the severity floor and the per-file cap all land there. |
| D17 | **Workspace-wide diagnostics for files that are not open are opt-in.** — **TBD in WP4.** They need a full-solution compiler scope, which is the expensive setting, and most of the output would be cut by the client's own delivery cap. |
| D18 | **File watching is on by default, with an opt-out.** — **TBD in WP4.** Without it a file created by Bash or by git never joins its project and every later answer is silently stale, which is the single worst failure mode available. |
| D19 | **A Roslyn crash is absorbed, not forwarded.** — **TBD in WP4.** In-flight requests answered, the process relaunched with a rate limit, the document mirror replayed, the solution re-opened — so the client's own restart budget is preserved for failures that are actually ours. |
| D20 | **The MCP tool table and its annotations.** — **TBD in WP5.** camelCase verb-noun names, 1-based positions, workspace-relative paths, structured content, and a `preview` flag on every mutating tool. |
| D21 | **The MCP server applies its own edits.** — **TBD in WP5.** It is the LSP *client* in that direction, so it writes files itself, preserving BOM and line endings, and then tells Roslyn what changed. |
| D22 | **NuGet is the plugin's launch channel; the AOT archives are everything else's.** The plugin runs `dnx claude-roslyn-lsp@{version}` for both servers — no download step, and an SDK is required for C# work anyway — while file-based clients point at a Native AOT binary from GitHub Releases. The package is pushed by **trusted publishing**: the workflow exchanges its GitHub OIDC token for an API key that lives minutes, so no NuGet API key exists in this repository or in its secrets. The exchange is C# inside the build (`build/Build.Publish.cs`), not a marketplace action. |
| D23 | **One Roslyn per solution, shared by whichever verb started first.** — **TBD in WP9.** Until it lands, running both servers against one solution loads the solution twice; `README.md` says so plainly rather than letting it be discovered. |
| D24 | **Eight runtime identifiers, and a host outside them is named rather than corrected.** `roslyn-language-server` is a 33 KB shim; the payload is `roslyn-language-server.<rid>`, and Microsoft publishes eight of them — `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, `osx-arm64` (C5, from the shim's own `DotnetToolSettings.xml`). That list is deliberately longer than this repository's five-RID release matrix, because the framework-dependent NuGet tool (D22) reaches platforms no archive is built for. musl is detected from `RuntimeInformation.RuntimeIdentifier` **and** from `/lib/ld-musl-*`, because a portable build on Alpine reports the RID it was *built* for. `win-x86` and `linux-arm` resolve to themselves and are reported unsupported: downloading the 64-bit payload for a 32-bit host produces a child that dies about an image format, which is nobody's idea of a diagnosis. |
| D25 | **The hash table is per RID; a version override has no hash at all.** `Roslyn/RoslynServerManifest.cs` carries one base64 SHA-512 per RID for the pinned version, and `dotnet fallout UpdateRoslynPin` is the only thing allowed to write them — a hash typed in by a person is a hash nobody verified. `CLAUDE_ROSLYN_LSP_ROSLYN_VERSION` is honoured (somebody debugging against a newer build should not have to fork the adapter) but that download is verified by TLS alone, and the result carries `Verified = false` all the way to `doctor` rather than being quietly equivalent. |
| D26 | **Command-line feature flags are per Roslyn version.** `--clientProcessId`, `--daemon` and `--daemonKeepAlive` exist in 5.12 and do not exist in the 5.5 builds still sitting in people's tool stores. Roslyn parses its command line with `System.CommandLine`, which *exits* on an unknown option — so a flag passed to the wrong version is not a degraded feature, it is a child that never starts, explaining itself on a stderr nobody is reading yet. The manifest therefore answers per version: the pin's flags for the pin, a conservative set (stdio only) for anything else. `UpdateRoslynPin` derives the pin's by running the downloaded server's own `--help`. |
| D27 | **One home directory, and the platform's cache location is the last resort — not a dotfile.** `CLAUDE_ROSLYN_LSP_HOME` → `CLAUDE_PLUGIN_DATA` → `%LOCALAPPDATA%` / `~/Library/Caches` / `$XDG_CACHE_HOME` / `~/.cache`. The scaffold's placeholder documentation said `~/.claude-roslyn-lsp`; that is wrong for 70 MB compressed and ~140 MB extracted **per version per RID**, which is cache by every definition an operating system has. On Windows it also keeps the payload out of a roaming profile, where a corporate policy would try to synchronise a Roslyn build over the network at every logon. `CLAUDE_ROSLYN_LSP_CACHE_DIR` moves the payloads only; logs and staged downloads stay under the home. |
| D28 | **Hash while streaming, extract to a staging directory, rename last.** The bytes are hashed on the way to disk rather than by re-reading the file, because re-reading is a second chance for the file to be different from the one that was checked. Extraction goes into a sibling `<rid>.<guid>.tmp` and is promoted with one `Directory.Move`, so the cache directory either does not exist or is complete, and the `.complete` marker — holding the accepted hash — is written last. A half-extracted directory that *looks* finished is how one bad network moment becomes a permanently broken install that only a manual delete fixes. |
| D29 | **A lock file, not a named mutex.** Claude Code starts the `lsp` and `mcp` servers as two processes at the same instant (D23 has not landed), and on a first run both would fetch the same 70 MB. A named mutex is the obvious answer and the wrong one: its name is per-session on Windows and it does not exist at all across containers sharing a mounted cache. `<cache>/<version>/<rid>.lock` opened `FileShare.None` is understood by every platform that can host the cache; the loser polls every 250 ms for up to ten minutes, then re-checks the cache and finds it warm. |
| D30 | **Everything under `tools/net10.0/<rid>/`, and nothing outside it.** 163 top-level files plus `BuildHost-net472/`, `BuildHost-netcore/`, `Targets/` and thirteen satellite-resource folders (C1). An earlier sketch took only the top level; the build hosts are what evaluate project files, so a server without them loads nothing and says nothing about why. Every entry is checked against the destination root before it is opened — a `..` segment, a rooted path, a drive qualifier and a backslash separator are each refused — and a package containing one is rejected whole rather than extracted in part. |
| D31 | **The adapter resolves the `dotnet` host itself and asks it what it has, once.** `DOTNET_ROOT` → every match on `PATH` → the platform's install locations (`C:\Program Files\dotnet`, `/usr/share/dotnet`, `/usr/lib/dotnet`, `/usr/local/share/dotnet`, `~/.dotnet`), then one `--list-runtimes` requiring `Microsoft.NETCore.App` **10.x** exactly. Letting the OS resolve `dotnet` at launch time would turn a missing runtime into a child that exits with a code and a message on a stderr the client never sees; resolving it here makes it a live failure state with a `doctor` hint (D10). The result is cached for the process, and `DOTNET_ROOT` is *set* in the child's environment rather than inherited, because a Native AOT adapter has no host of its own to inherit a correct one from. |
| D32 | **Acquisition order is by trust, and only the last step spends bandwidth.** Explicit path → this adapter's cache → a global tool installation *at exactly the wanted version* → download. The result carries the winner, the path, the version, whether it was hash-verified, and the full chain of what was looked at — because "which one did it pick" is the first question of every report about a machine with more than one. |
| D33 | **A global tool store at a different version is reported, never used.** The tempting shortcut is wrong twice over: the CLI changes between builds (D26), and the point of a pin is that a release is tested against one server. So a 5.5 installation shows up in `doctor`'s chain as *present, not used, wrong version*, which is the most useful line that report can carry for somebody who is certain they installed it. A tool store's `.nupkg.sha512` is also **not** the hash of the payload beside it (C4), so a tool-store hit is never reported as verified. |
| D34 | **`CLAUDE_ROSLYN_LSP_ROSLYN_PATH` that does not resolve is a failure, not a fall-through.** Falling back to a download would be friendlier for about ten seconds and then indistinguishable from the variable having been ignored. The value may name a directory holding `Microsoft.CodeAnalysis.LanguageServer.dll`, that assembly directly, or any other executable — the last case is what lets `SmokeTest` point the launcher at this binary's own `fake-roslyn` verb. |
| D35 | **A Windows Job Object with `KILL_ON_JOB_CLOSE`, and nothing at all elsewhere.** Roslyn already exits when the process id it was given dies, which covers every orderly failure. What it does not cover is an adapter killed hard in the window before `initialize`: on Unix the child is in this process's group and a session teardown reaches it, on Windows the orphan is a 250 MB server holding a solution open with nobody to talk to. `[LibraryImport]`, not `[DllImport]`, because `IL2026`/`IL3050` are errors here — which is also why the server project now sets `AllowUnsafeBlocks` (the generator emits `unsafe` marshalling stubs; no hand-written `unsafe` exists in the product, and adding one would need its own argument). |
| D36 | **Every redirected child stream is drained, including the one that "should be empty".** An unread pipe fills at about 4 KB and blocks the writer inside a `write` it cannot return from — presenting as a language server that answered three requests and then stopped, with no error, no exit and no clue. In pipe mode the child's *stdout* is pumped too (C7's banner); in both modes stdin is redirected even though pipe-mode Roslyn never reads it, because an inherited stdin is **this** process's stdin, which in `lsp` mode is the client's half of the protocol. |
| D37 | **The Roslyn command line is fixed, and everything standing in for Roslyn must accept it.** Arguments are: the program, then `CLAUDE_ROSLYN_LSP_ROSLYN_ARGS` (split on whitespace, no quoting dialect — it exists so the smoke test can add one word), then `--pipe <name>` or `--stdio`, `--logLevel <level>` (default **Warning**: Roslyn's `Information` is 21-22 `window/logMessage` lines during startup alone, C6), `--extensionLogDirectory <home>/logs/roslyn`, `--telemetryLevel off`, and `--clientProcessId <pid>` only where D26 says it exists. WP2's `fake-roslyn` and WP4's fakes have to tolerate that whole set. |
| D38 | **Roslyn is launched as `<dotnet> …/Microsoft.CodeAnalysis.LanguageServer.dll`, never through the apphost beside it.** `roslyn-language-server.exe` is a *thin client* that spawns its own daemon per instance: two of them gave four processes and no shared workspace (C29). The daemon is not a shortcut to D23's shared engine, it is a second copy of the problem. Launching the managed assembly through the host also sidesteps C1, where `Process.Start` refuses a ~290-character executable path with "file not found" for a file that plainly exists. |
| D39 | **An explicit solution setting that does not resolve fails; discovery never runs after one.** Both `CLAUDE_ROSLYN_LSP_SOLUTION` and `.vscode/settings.json`'s `dotnet.defaultSolution` are statements of intent. Falling back to a scan when one points at a moved file produces an adapter that opens a *different* solution and then answers about it with total confidence — the failure this project exists to remove, reintroduced as a convenience. |
| D40 | **`dotnet.defaultSolution` is honoured, including `disable`.** Any repository opened in VS Code with the C# extension has already been asked this question and has checked in the answer; reading it costs one file — parsed as JSONC, because that file has comments and trailing commas in it — and removes the commonest reason to set `CLAUDE_ROSLYN_LSP_SOLUTION` at all. `disable` means no solution, on purpose. A malformed settings file is treated as absent rather than as a startup failure: it is somebody else's file. |
| D41 | **Solutions are scored, not taken in the order the walk found them.** Name equal to the root folder +100, `.slnx` over `.sln` +10, +1 per project (`.sln`: lines beginning `Project("{`; `.slnx`: `<Project Path=` elements — counted by text, because the package budget has no room for `Microsoft.Build` and the number is only ever a tie-break). Ties go to the shallower file, then to ordinal path order. Depth-3 breadth-first walk, skipping `bin obj .git node_modules .vs artifacts TestResults`. |
| D42 | **The project fallback keeps every candidate, tests included, capped at 500.** An earlier sketch trimmed test projects to save load time; that is backwards for this product, because an agent asked to fix a failing test needs the test project loaded, and a symbol that resolves everywhere except in tests is worse than a slower load. The cap is a guard against opening a monorepo by accident, not a curation policy. |
| D43 | **`doctor` never downloads; `install` is the same command with the download allowed.** A diagnostic that quietly spends 70 MB of somebody's tethered connection is a diagnostic they will not run again, so the two verbs are one implementation and one flag (`doctor --fix` ≡ `install`). `doctor`'s exit code means exactly one thing: 0 iff Roslyn is runnable *right now*, which is established by launching it and completing a real `initialize`/`shutdown`/`exit` — every static check it prints can pass on a machine where the server still does not start. `--json` renders the same gathered record, so the two forms cannot disagree. |

### The plugin-option rule

Worth its own paragraph because the failure is invisible and a sibling repository paid for it
(sonarqube-mcp issue #1). A Claude Code plugin manifest substitutes `${user_config.KEY}` with the
option's value, and **an option the user never filled in resolves to the empty string rather than
being omitted**. Mapping one onto `CLAUDE_ROSLYN_LSP_SOLUTION` therefore sets that variable to `""`
in the child process and *shadows* whatever the user's environment already said. So the manifest
writes to `CLAUDE_PLUGIN_OPTION_CLAUDE_ROSLYN_LSP_*`, and `ClaudeRoslynLspOptions` reads those first,
treating blank as absent, falling back to the plain names: a filled-in option wins, a blank one
changes nothing. Do not "simplify" the manifest back to the plain names — `PluginManifestTests` fails
if you do, and the runtime symptom is an adapter that opens the wrong solution and answers
confidently about it.

The one exception is `CLAUDE_ROSLYN_LSP_HOME`, which the manifest sets directly to
`${CLAUDE_PLUGIN_DATA}`. It is not a user option at all: Claude Code substitutes a real directory it
owns, so there is no blank case to guard against and no plain variable underneath to shadow. The test
asserts that it is the *only* exception.

### Naming reconciliations

The plan names three things twice. Resolved once, here, so nobody re-resolves them differently:

- **`CLAUDE_ROSLYN_LSP_ROSLYN_PATH` / `_ROSLYN_VERSION` are canonical**; `_SERVER_PATH` and
  `_SERVER_VERSION` are accepted aliases. The `ROSLYN_` spelling is what the plugin manifest and the
  smoke test use, so it wins; the other spelling is read rather than ignored, because a documented
  variable that silently does nothing is the one configuration bug a user cannot diagnose.
- **`CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS` is the only readiness timeout.** The plan also calls it
  `CLAUDE_ROSLYN_LSP_LOAD_TIMEOUT` where it discusses MCP mode; it is the same budget — how long a
  request waits for the workspace — and one name is enough. 120 s by default, matching the plugin
  manifest's `startupTimeout`, so the client and the server give up at the same moment.
- **`CLAUDE_ROSLYN_SOLUTION`**, in the skill sketch, is a typo for `CLAUDE_ROSLYN_LSP_SOLUTION`. Every
  variable this product reads begins `CLAUDE_ROSLYN_LSP_`.

### Protocol gotchas

The **C-numbered** findings live in [`docs/roslyn-protocol-facts.md`](docs/roslyn-protocol-facts.md):
C1-C39 were observed on the wire against the pinned server on 2026-09-10 (WP0 spikes) and are cited
from code comments and tests by number. Add to that file, never restate a fact here; a finding that
changes on a pin bump gets re-verified there with the new date.

## Package budget

Complete, as of the scaffold. Versions are centrally pinned in `Directory.Packages.props`, with
transitive pinning on.

- `src/ClaudeRoslynLsp`: `ModelContextProtocol` (pinned **exactly** `[2.2.0]`, because the AOT and
  serializer contract this server depends on — resolver chaining, schema generation, annotation
  defaults — is verified against that one version and a floating range would move it under a
  release), `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Logging.Console`.
  Nothing else, ever, for the reasons in hard rule 1.
- `tests/`: `xunit.v3`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`. No mocking or
  assertion libraries.
- `build/`: `Fallout.Common` + `Fallout.Components` 10.4.0, plus `NuGet.Frameworks` 7.9.0 (see
  below). The build project opts out of central package management.

SourceLink needs no package reference — it is in-SDK on .NET 8 and later.

The acquisition work package (D24–D43) added **zero** packages, which was a constraint rather than a
coincidence: `HttpClient`, `ZipArchive`, `IncrementalHash`, `NamedPipeServerStream`, `Process`,
`JsonDocument`/`Utf8JsonWriter` and one `[LibraryImport]` into `kernel32` cover downloading, hashing,
extracting, launching, supervising and reporting. The only project-file change it needed is
`AllowUnsafeBlocks` in the server csproj, which the `[LibraryImport]` generator requires (D35).

### Package budget changes

**2026-09-10 — `NuGet.Frameworks` 7.9.0, `build/_build.csproj` only, inherited from the siblings.**
The .NET SDK binds `NuGet.Frameworks, Version=7.9.0.0` by strong name inside the build process
whenever Fallout's `ITest` evaluates a test project in-process — which it does only when
`GITHUB_ACTIONS` is set, so the failure is invisible locally and appears on the first CI run. Fallout
10.4.0 brings 6.14.3 transitively through `NuGet.Packaging`, and the app-local copy shadows the SDK's,
so every CI `Test` run throws `InvalidProjectFileException`. Pinning forward is safe in both
directions, because the runtime binds a higher assembly version than the one requested. Remove once
Fallout ships a `NuGet.Packaging` new enough to bring 7.9.0 in on its own
(Fallout-build/Fallout#638).

**Fallout stays on 10.4.0.** The `11.0.x` line is the *edge* channel, not the newer one; `10.4.0` is
stable and functionally ahead. `GitHubActionsAttribute.Env`, which the publish workflow needs, does
not exist in 11.0.x, and regenerating the workflows there would move the actions *backwards*.

## Layout

```
.claude-plugin/             marketplace.json + plugin.json — the repository as its own Claude Code
                            plugin marketplace, source "./" (PluginManifestTests)
.claude/agents/builder.md   The delegation contract for implementation work packages
.claude/skills/             The shipped Agent Skill (one canonical copy; AgentSkillTests keeps its
                            tool references in step with the inventory)
.mcp/server.json            MCP server manifest (D22), packed into the NuGet package at
                            /.mcp/server.json; its version must match CHANGELOG.md (test-enforced)
src/ClaudeRoslynLsp/        One production project; AssemblyName claude-roslyn-lsp
  Program.cs                Entry point — hands argv straight to the CLI dispatcher
  ServerVersion.cs          Product name + version, read from the assembly (the build stamps it)
  Cli/                      The verb dispatch, and the ONLY place the console is touched (D11).
                            CliDispatcher, CliRuntime, DoctorCommand, FakeRoslynCommand
  Configuration/            ClaudeRoslynLspOptions.FromEnvironment() — the complete env-var surface,
                            the plugin-option precedence, and it never throws (D10)
  Protocol/                 Content-Length framing (D12), the JSON-RPC message shapes the adapter
                            understands, and LspJsonContext (D7)
  Lsp/                      The LSP server. Today a correct stub; WP2/WP4 grow the mediation here
  Roslyn/                   Everything about the child process (D24–D43): RuntimeIdentifier,
                            RoslynServerManifest (the generated pin), AdapterPaths,
                            NuGetPayloadDownloader, RoslynServerLocator, DotnetHostLocator,
                            RoslynProcessLauncher + DuplexStream + ChildProcessGuard +
                            RoslynStderrPump, RoslynHandshakeProbe, SolutionDiscovery
  Mcp/                      McpServerSetup (D5), ServerInstructions, and later Tools/
  Mcp/Models/               Result records and RoslynToolJsonContext (camelCase, D7)
tests/ClaudeRoslynLsp.Tests/  The single test project; internals visible via InternalsVisibleTo.
                            Http/ holds the hand-rolled StubHttpMessageHandler, Roslyn/ the
                            acquisition and discovery tests, Live/ the opt-in real-Roslyn tests
build/                      The Fallout orchestrator (Build.cs, Build.Publish.cs, Build.Roslyn.cs,
                            Build.CI.GitHubActions.cs, ReleaseNotesParser.cs, SemVersion.cs) —
                            `build/` is a resolver convention, and `.gitignore` must never
                            untrack it
docs/                       roslyn-protocol-facts.md — the C-numbered findings (see above)
```

Directories the plan reserves and the scaffold has not created, so that nobody invents a second home
for them: `src/ClaudeRoslynLsp/Edits/` (the workspace-edit applier and its guards, WP5),
`src/ClaudeRoslynLsp/Testing/` (the scripted fake Roslyn server, shared by the tests and the hidden
verb, WP2), `tests/fixtures/HelloSolution/` (WP7) and `docs/clients/` (WP6).

## Build

The orchestrator is [Fallout](https://fallout.build) 10.4.0 (stable channel), the maintained hard
fork of NUKE. The CLI is pinned in `.config/dotnet-tools.json` as `fallout.globaltool` (command
`fallout`) and resolves `build/_build.csproj` by convention.

```powershell
dotnet tool restore      # once per checkout
.\build.ps1 Test         # restore, compile, run tests
.\build.ps1 SmokeTest    # AOT publish + both real stdio handshakes against the binary
.\build.ps1 Pack         # the NuGet tool package (D22) into artifacts/packages
```

`dotnet fallout UpdateRoslynPin` (optionally `--roslyn-version <v>`, defaulting to the current pin) is
the **only** way `Roslyn/RoslynServerManifest.cs`'s generated block may change (D25). It downloads all
eight RID payloads into `artifacts/roslyn-pin/` — about 560 MB, resumable, existing files are reused —
hashes each one, cross-checks it against nuget.org's catalog leaf (the registration index has no hash;
C3), runs the host RID's server with `--help` to derive the feature flags (D26), and rewrites the
block between the `// <generated pin>` markers with LF endings and no BOM. It then reminds you that
`CHANGELOG.md` names the pinned version — `RoslynServerManifestTests` fails if it does not.

`CHANGELOG.md` is the **version authority**: its top section is parsed in `OnBuildInitialized` and
passed to the build as the version. The file is never mutated by the build, and the first line must
parse as a version header (`# 0.1.0`) — do not add a `# Changelog` title, it would abort the build.

## Testing

```powershell
.\build.ps1 Test                                  # the whole suite, via the orchestrator
dotnet test tests\ClaudeRoslynLsp.Tests           # the same tests, without a publish
dotnet test tests\ClaudeRoslynLsp.Tests --filter "FullyQualifiedName~Framing"
```

xunit.v3 with the VSTest bridge (D9). No mocking or assertion libraries: the framing tests drive a
hand-rolled stream that returns one byte at a time, and the LSP server is driven over in-memory
streams. Because `JsonSerializerIsReflectionEnabledByDefault=false` is set in the test csproj as well
(D6), a type missing from a `JsonSerializerContext` fails here rather than only after an AOT publish.

Some rules are too easy to break silently to be left to review, so tests enforce them by reflection or
by scanning the repository. Do not delete one to make a change pass:

- **`NoStdoutWritesTest`** scans the production sources for console usage and fails on any outside
  `src/ClaudeRoslynLsp/Cli/` (hard rule 5). It strips comments and literals first, so the several
  comments that *discuss* the rule do not trip it, and it self-checks in both directions: it must
  find source files at all, and it must still find real usages inside `Cli/`.
- **`PluginManifestTests`** asserts that the marketplace lists this repository as its one plugin with
  `source: "./"`, that the declared skill path still has a `SKILL.md` behind it, that both server
  entries run the same `dnx` pin with only their verb differing, that their environment blocks are
  identical, that every value is written under the plugin-option prefix except
  `CLAUDE_ROSLYN_LSP_HOME`, that every `userConfig` option is referenced by exactly one entry, and
  that the LSP entry still maps `.cs` to `csharp` — without which the server is configuration nothing
  ever launches.
- **`McpServerManifestTests`** reads `.mcp/server.json`, `CHANGELOG.md` and the server csproj and
  asserts the manifest's version and its NuGet entry's version both equal the changelog's, that its
  `identifier` equals `<PackageId>`, and that it passes `mcp` as a positional argument. Nothing in
  the build reads the manifest back, so without this a released package would keep advertising an old
  version — or tell every client to run the binary with no verb, which exits 2.
- **`AgentSkillTests`** checks the skill's frontmatter against the Agent Skills spec (`name` equals
  the directory name, `description` within 1024 characters) and cross-checks every backticked
  tool-shaped identifier against the reflected tool inventory in both directions. The inventory is
  empty until WP5, so the "every tool is named" direction is vacuous today and becomes real with the
  first tool; the "names a tool that exists" direction is sharp already, because the verb set it
  scans with includes the designed tool table's verbs.
- **`ConfigurationTests`** asserts that every documented variable reaches a property, under its plain
  name *and* under the plugin prefix. A knob that is written down and never read is the one
  configuration bug with no symptom to search for.
- **`RoslynServerManifestTests`** (the plan's `RoslynReleaseTests`) asserts that every published RID
  has a hash, that each one is 88 base64 characters decoding to 64 bytes, that no two are equal, that
  the pin parses as a prerelease version, that the generated markers are still there, and that
  `CHANGELOG.md` names the pin. The numbers themselves are the generator's job — a test that restated
  them would only be a second place to type them wrong — but a release that claims to verify a
  download it has no hash for, or that ships a server version its own changelog never mentions, fails
  here.

`SmokeTest` is the end-to-end check the unit tests cannot be: it publishes the Native AOT binary,
spawns it, and drives **two** real exchanges — one `Content-Length`-framed LSP handshake
(`initialize`, `initialized`, `shutdown`, `exit`, with the exit code asserted) and one MCP handshake
(`initialize`, `initialized`, `tools/list`). The LSP leg reads stdout as *frames*, so any byte that is
not part of one fails the test: that is what proves the "nothing else writes to stdout" rule on a real
binary rather than in a source scan. CI runs `Test` and `SmokeTest` on every push and pull request.

`tests/ClaudeRoslynLsp.Tests/Live/` holds the tests that acquire and launch the **real** pinned
Roslyn. They are opt-in through `CLAUDE_ROSLYN_LSP_LIVE_TESTS=1` (and are reported as skipped
otherwise), because they download about 70 MB and start a quarter-gigabyte child; with
`CLAUDE_ROSLYN_LSP_HOME` set they reuse a warm cache, and with it unset they download into a temporary
home so that the acquisition path is itself under test. What they add over the unit tests is the part
no stub can assert: that the layout inside the real package, the real command line and the real pipe
handshake are what this repository believes they are. Both transports are exercised, because they
fail differently.

When captured data arrives — real Roslyn responses, or the fixture solution of WP7 — every capture
gets a row in a `Fixtures/MANIFEST.md` recording what was requested, when, and whether the bytes came
off the wire or were written by hand. JSON cannot carry comments, and a fixture whose provenance
nobody recorded is a fixture nobody dares to re-capture. The acquisition tests deliberately need no
fixture: they build a `.nupkg` in memory, because every property under test is about the *shape* of a
package — a prefix to strip, an entry to refuse — and a captured one would make each of those cases a
70 MB file.

## Release engineering

Releases are cut by pushing a `v*` tag, which starts **two independent workflows**: `release.yml`
(binaries + the GitHub Release) and `publish.yml` (nuget.org, D22). They do not depend on each other,
deliberately: a nuget.org outage or a policy mismatch must not be able to hold up the GitHub Release,
and neither must the reverse.

`.github/workflows/release.yml` is hand-written (the `[GitHubActions]` generator has no matrix
support) and runs five publish legs — one per RID, each on its own hardware — followed by a `release`
job that assembles the GitHub Release:

| RID | Runner |
|---|---|
| `linux-x64` | `ubuntu-latest` |
| `linux-arm64` | `ubuntu-24.04-arm` |
| `win-x64` | `windows-latest` |
| `win-arm64` | `windows-11-arm` |
| `osx-arm64` | `macos-latest` |

Every leg runs `dotnet fallout SmokeTest --runtime <rid>`, and `SmokeTest` depends on `PublishAot`, so
each leg compiles its own binary, speaks both protocols to it *on the architecture it targets*, and
only then uploads the archive. **Never ship a binary that has not answered a handshake on its own
architecture** — that is why the arm64 legs run on arm64 runners instead of being cross-compiled.
`fail-fast` is off so one broken runtime cannot mask the state of the other four.

`CHANGELOG.md` stays the version authority here too: `CreateGitHubRelease` refuses to publish unless
`GITHUB_REF_NAME` equals `v{version parsed from CHANGELOG.md}`. To release, land the changelog entry
first, then tag that commit. `Prerelease` follows the version string (`Version.Contains('-')`), so a
`0.1.0-rc.1` tag publishes as a prerelease and never displaces the latest stable release.

Two properties of the release path are inherited from the template, where a throwaway tag established
them, and both still apply:

- **R1 — arm64 runners are available and need no extra native-toolchain setup** on a public
  repository; both arm64 legs complete a full AOT publish plus handshake. If that ever regresses, the
  documented fallback is to cross-compile the affected RID from the x64 runner of the same OS with
  its smoke step skipped — and to mark it as unverified in both `release.yml` and here, because an
  unverified binary must not go out silently.
- **R2 — `CompressionExtensions.TarGZipTo` cannot be used.** It archives through SharpZipLib's
  `TarEntry.CreateEntryFromFile`, which hard-codes *every* entry's mode to `0700` instead of reading
  it off disk — shipping `LICENSE` and `README.md` executable and the binary unreadable to anyone but
  the extracting user. `PublishAot` therefore invokes the **`tar` CLI**
  (`ProcessTasks.StartProcess`), which is present on the GitHub runners and in Git Bash. Windows RIDs
  keep using `ZipTo`: a zip carries no Unix mode and the payload is a `.exe`.

The release body is composed from `artifacts/release-notes.md`, which `Build.WriteReleaseNotes`
reflows out of `CHANGELOG.md`'s newest section — one single-line bullet per entry — because
`ChangelogTasks.ExtractChangelogSectionNotes` only recognises `## ` headings and ends a section at the
first non-bullet line, and this changelog uses `#` headings with wrapped bullets. Keep changelog
entries as `- ` bullets with wrapped continuation lines and this keeps working.

### The NuGet leg

`.github/workflows/publish.yml` is **generated** from the second `[GitHubActions]` attribute in
`build/Build.CI.GitHubActions.cs` (hard rule 4 — never hand-edit it). One Ubuntu job runs
`dotnet fallout Compile Test Pack Publish` with `permissions: { contents: read, id-token: write }`,
`environment: nuget` and `env: NUGET_USER: lahma`.

Three shapes are load-bearing and were chosen, not defaulted:

- **Its own file.** A nuget.org trusted publishing policy is scoped by repository + workflow *file
  name* (+ environment) and the nuget.org UI has no branch or tag filter, so whichever file the policy
  names can mint a publishing key on *every* run of that file. `publish.yml` only ever runs on a
  `v*.*.*` tag, and `build.yml` — which runs on every push and pull request — is not the file named in
  the policy and therefore cannot mint anything.
- **No `paths:` filter.** A path filter alongside `tags:` evaluates over the tag commit's diff and can
  silently skip the release run.
- **One job.** More jobs would mean more OIDC exchanges racing the one-key-per-30-s rate limit, and
  the target is Linux-only anyway.

`Publish` is gated on a tag build and refuses in ordered steps otherwise: a non-tag invocation is a
logged *skip* (there is no preview feed — the only push this repository makes is a tagged release),
and once past the gate it asserts, in order, that it is on GitHub Actions, that `NUGET_USER` is set,
and that `GITHUB_REF_NAME` equals `v{version from CHANGELOG.md}`. It then mints the key immediately
before pushing, because the key lives 15–60 minutes and one OIDC token mints exactly one key. The
whole ladder is exercisable locally:

```powershell
dotnet fallout Publish --skip                      # skips, and says why
$env:GITHUB_ACTIONS='true'; $env:NUGET_USER='lahma'; $env:GITHUB_REF_NAME='v0.1.0'
dotnet fallout Publish --skip                      # must fail with "GitHub OIDC is unavailable"
```

That last failure is the success signal: it proves the tagged path routed all the way to the token
exchange. Clear the three variables afterwards.

Three things about the exchange are worth not rediscovering:

- **The POST to `https://www.nuget.org/api/v2/token` must carry a `User-Agent`.** The endpoint sits
  behind Azure Front Door, which answers `400 {"error":"A User-Agent header is required."}` to a
  request without one, *before* looking at the token or the policy — and a bare `HttpClient` sends
  none. So **a 400 means the request never reached the policy**; stop looking at nuget.org settings.
  `Build.Publish.cs` sends `claude-roslyn-lsp-build/1.0`.
- The OIDC audience is `https://www.nuget.org` and the response field is `apiKey`. The minted key is
  `add-mask`ed, and Fallout already marks `DotNetNuGetPushSettings.ApiKey` as secret.
- `NUGET_USER` is the nuget.org **profile name of whoever created the trusted publishing policy**
  (`lahma`) — not an email, not an organisation. It is public information and is deliberately a plain
  workflow-level `env:` value, not a secret: a secret would be masked out of exactly the error message
  that names it. Getting it wrong is the most common 401.

Setting up the policy on nuget.org is manual and must exist **before** the first tag: Trusted
Publishing → policy owner `lahma`, Repository Owner `lahma`, Repository `claude-roslyn-lsp`, Workflow
File `publish.yml` (filename only, **not** `release.yml` and not the `.github/workflows/` path),
Environment `nuget`. A policy covers every package the selected owner owns; there is no per-package
scoping.

### Validating the plugin

Both manifests are checked with the official CLI, which is non-interactive:

```powershell
claude plugin validate . --strict          # the marketplace manifest and the plugin manifest
claude plugin marketplace add .            # resolves it; remove afterwards
```
