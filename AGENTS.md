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
| D15 | **How the adapter talks to Roslyn — named pipe or stdio.** — **TBD in WP3.** Roslyn *connects* to a client-created pipe for `--pipe`; the fallback and the Windows ACL are what WP3 settles. |
| D16 | **The diagnostics bridge: pull from Roslyn, push to the client.** — **TBD in WP4.** Debounce, the per-uri in-flight rule, `previousResultId` handling, the severity floor and the per-file cap all land there. |
| D17 | **Workspace-wide diagnostics for files that are not open are opt-in.** — **TBD in WP4.** They need a full-solution compiler scope, which is the expensive setting, and most of the output would be cut by the client's own delivery cap. |
| D18 | **File watching is on by default, with an opt-out.** — **TBD in WP4.** Without it a file created by Bash or by git never joins its project and every later answer is silently stale, which is the single worst failure mode available. |
| D19 | **A Roslyn crash is absorbed, not forwarded.** — **TBD in WP4.** In-flight requests answered, the process relaunched with a rate limit, the document mirror replayed, the solution re-opened — so the client's own restart budget is preserved for failures that are actually ours. |
| D20 | **The MCP tool table and its annotations.** — **TBD in WP5.** camelCase verb-noun names, 1-based positions, workspace-relative paths, structured content, and a `preview` flag on every mutating tool. |
| D21 | **The MCP server applies its own edits.** — **TBD in WP5.** It is the LSP *client* in that direction, so it writes files itself, preserving BOM and line endings, and then tells Roslyn what changed. |
| D22 | **NuGet is the plugin's launch channel; the AOT archives are everything else's.** The plugin runs `dnx claude-roslyn-lsp@{version}` for both servers — no download step, and an SDK is required for C# work anyway — while file-based clients point at a Native AOT binary from GitHub Releases. The package is pushed by **trusted publishing**: the workflow exchanges its GitHub OIDC token for an API key that lives minutes, so no NuGet API key exists in this repository or in its secrets. The exchange is C# inside the build (`build/Build.Publish.cs`), not a marketplace action. |
| D23 | **One Roslyn per solution, shared by whichever verb started first.** — **TBD in WP9.** Until it lands, running both servers against one solution loads the solution twice; `README.md` says so plainly rather than letting it be discovered. |

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

Empty at the scaffold stage. This is where the **C-numbered** findings go: the things that cost a
probe to discover and that fail *silently*, each with the date it was verified and the file or
capture it came from. WP0's spike results and anything WP2–WP7 learns from a live Roslyn belong here,
not in a commit message.

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
  Mcp/                      McpServerSetup (D5), ServerInstructions, and later Tools/
  Mcp/Models/               Result records and RoslynToolJsonContext (camelCase, D7)
tests/ClaudeRoslynLsp.Tests/  The single test project; internals visible via InternalsVisibleTo
build/                      The Fallout orchestrator (Build.cs, Build.Publish.cs,
                            Build.CI.GitHubActions.cs, ReleaseNotesParser.cs, SemVersion.cs) —
                            `build/` is a resolver convention, and `.gitignore` must never
                            untrack it
```

Directories the plan reserves and the scaffold has not created, so that nobody invents a second home
for them: `src/ClaudeRoslynLsp/Roslyn/` (release pin, acquisition, launch, solution discovery, WP3),
`src/ClaudeRoslynLsp/Edits/` (the workspace-edit applier and its guards, WP5),
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

`SmokeTest` is the end-to-end check the unit tests cannot be: it publishes the Native AOT binary,
spawns it, and drives **two** real exchanges — one `Content-Length`-framed LSP handshake
(`initialize`, `initialized`, `shutdown`, `exit`, with the exit code asserted) and one MCP handshake
(`initialize`, `initialized`, `tools/list`). The LSP leg reads stdout as *frames*, so any byte that is
not part of one fails the test: that is what proves the "nothing else writes to stdout" rule on a real
binary rather than in a source scan. CI runs `Test` and `SmokeTest` on every push and pull request.

When captured data arrives — real Roslyn responses, or the fixture solution of WP7 — every capture
gets a row in a `Fixtures/MANIFEST.md` recording what was requested, when, and whether the bytes came
off the wire or were written by hand. JSON cannot carry comments, and a fixture whose provenance
nobody recorded is a fixture nobody dares to re-capture.

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
