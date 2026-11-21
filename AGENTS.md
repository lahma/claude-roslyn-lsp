# AGENTS.md

Guidance for humans and AI agents working in this repository. `.claude/CLAUDE.md` imports this file, so keep
it the single source of truth.

`claude-roslyn-lsp` is one .NET 10 binary, published as a Native AOT executable per RID, that puts
Microsoft's `roslyn-language-server` in front of coding agents in two ways at once: as an **LSP
server** that mediates the protocol so Claude Code's language-server tool actually works, and as an
**MCP server** that exposes the mutating operations the LSP tool has no slot for. It contains no
compiler. Roslyn runs in a child process this binary downloads, launches and supervises — treat that
as a hard constraint, not a preference, because it is what keeps the dependency tree small enough for
one person to audit and the binary small enough to ship five of them per release.

## How these instructions are laid out

This file is the entry point and is kept small: it holds what every agent needs before its first edit,
whatever that edit turns out to be. Everything else lives in an `AGENTS.md` beside the code it governs,
with a one-line `CLAUDE.md` (`@AGENTS.md`) next to each, so that Claude Code — which reads `CLAUDE.md`
and never `AGENTS.md` — loads it when it reads a file there. **Read the file on the row matching what
you are about to do, before you touch that code**: most agents never open a nested file unless this
table sends them to it.

| If you are about to… | Read first | What is in it, and what skipping it costs |
|---|---|---|
| touch a verb, `doctor`, an environment variable, the framing, a JSON context or the csproj — or anything under `src/ClaudeRoslynLsp/` with no file of its own (`Cli/`, `Configuration/`, `Protocol/`, `Testing/`) | [`src/ClaudeRoslynLsp/AGENTS.md`](src/ClaudeRoslynLsp/AGENTS.md) | The source layout, the csproj and JSON-context rules, the configuration surface with the plugin-option rule, the console and framing rules, `doctor`, the scripted backend. Cost: a reflection fallback that fails only after an AOT publish, or a blank plugin option shadowing the user's environment. |
| change how a message crosses between the client and Roslyn — a handshake, the readiness gate, `workspace/configuration`, document sync, the id rewrite — or a bridge (diagnostics, file watching, crash recovery, call hierarchy), or the idle recycle | [`src/ClaudeRoslynLsp/Adapter/AGENTS.md`](src/ClaudeRoslynLsp/Adapter/AGENTS.md) | Why the adapter terminates both sessions, what it authors on each side, why the gate blocks rather than bounces, the bridges' delivery policy, crash absorption, and when an idle Roslyn is stopped. Cost: empty answers that look like correct ones. |
| touch sharing one Roslyn between the two verbs — the session file, its lock and sweep, the pipe host, multiplexing, attaching | [`src/ClaudeRoslynLsp/Adapter/Sharing/AGENTS.md`](src/ClaudeRoslynLsp/Adapter/Sharing/AGENTS.md) | The rendezvous, the multiplexing rules, document ref-counting, the fallback to a private Roslyn. Cost: a second Roslyn beside the first, or an attached client answered by a workspace that has not loaded. |
| change which Roslyn is used or how it starts — runtime identifiers, the pin and its hashes, download and cache, the `dotnet` host, the command line, the transport, solution discovery, the GC settings, the compactor | [`src/ClaudeRoslynLsp/Roslyn/AGENTS.md`](src/ClaudeRoslynLsp/Roslyn/AGENTS.md) | The acquisition order, per-version flags, the stage-and-rename install, the launch, solution scoring, the child's memory settings, bumping the pin. Cost: a child that never starts and says why on a stderr nobody reads. |
| add or change an MCP tool, a description or an argument, a result shape, the server instructions or the agent skill | [`src/ClaudeRoslynLsp/Mcp/AGENTS.md`](src/ClaudeRoslynLsp/Mcp/AGENTS.md) | The frozen tool table, the seven places a new tool touches, symbol addressing, statuses, argument-name refusal, and the budget of attention between instructions, descriptions and `SKILL.md`. |
| touch the engine behind the tools — `IRoslynEngine`, `OwnedRoslynEngine`, workspace status, the workspace diagnostic pull, a configuration round trip | [`src/ClaudeRoslynLsp/Mcp/Engine/AGENTS.md`](src/ClaudeRoslynLsp/Mcp/Engine/AGENTS.md) | The seam, why the MCP half composes the mediation rather than sharing its session, the eager start, the bounded workspace pull and its fallback. |
| change how a source file is read, edited or written — the codec, the path guard, the applier, preview and its cache, `.editorconfig` | [`src/ClaudeRoslynLsp/Edits/AGENTS.md`](src/ClaudeRoslynLsp/Edits/AGENTS.md) | The byte-faithful round trip, the workspace boundary, preview-then-apply, the two layout rules `formatCode` applies. Cost: a one-word rename that rewrites every line. |
| write or change a test or a fixture, run the live tests, or read a failure from a rule-enforcing test | [`tests/ClaudeRoslynLsp.Tests/AGENTS.md`](tests/ClaudeRoslynLsp.Tests/AGENTS.md) | NUnit on MTP, the tests that enforce the rules and what each holds, the live tests and their numbers, the fixture rules. Cost: a deleted guard that hid nothing until it shipped. |
| change the build, a package or the SDK, a workflow, `.claude-plugin/`, `.mcp/server.json` or `CHANGELOG.md`, or cut a release | [`build/AGENTS.md`](build/AGENTS.md) | The targets, what `SmokeTest` proves, the package-budget history, the release and NuGet legs, plugin validation. Cost: a pointer package naming packages that do not exist. |
| record a wire fact, or change a client configuration pack or the README snippet that quotes one | [`docs/AGENTS.md`](docs/AGENTS.md) | How the C-numbered facts are kept and cited, and the client configuration packs. |

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
   from, in a comment or in `docs/roslyn-protocol-facts.md`, so the next person can re-verify it
   without re-deriving it.
3. **No new NuGet packages without a recorded decision.** The *Package budget* below is
   complete. If a package looks necessary, add an entry to
   [*Package budget changes*](build/AGENTS.md#package-budget-changes) with the date, the package,
   and why nothing already present can do the job — before referencing it.
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

## The shape of the product

D1–D4 were confirmed with the user on 2026-09-10 and are the shape of the product; every other
decision serves them.

**D1 — Proxy, don't reimplement.** A pure-BCL Native AOT adapter in front of Microsoft's
`roslyn-language-server`, plus an MCP server for the mutations. Reimplementing C# analysis is not a
thing one person maintains, and Microsoft now publishes the real engine as a public MIT dotnet tool
— so the only defensible scope is the *mediation*, which is exactly the part nobody else has done.
Zero Roslyn packages in this repository (hard rule 1).

**D2 — Acquire the pinned Roslyn server at runtime, and pin it hard.** The adapter fetches
`roslyn-language-server.<rid>` from nuget.org into a per-user cache on first run, hash-verified,
with explicit overrides. Bundling it would multiply the release archive by an order of magnitude per
RID; requiring the user to install it would reproduce the exact failure this project exists to fix,
which is a plugin that launches a binary nobody has. The pin is one constant, because the server is
prerelease-only with an unstable CLI: a floating version is a release that breaks on somebody else's
schedule.

**D3 — One binary, one package, verbs — and no default verb.** `lsp`, `mcp`, `doctor`, and a hidden
`fake-roslyn`; `--version` and `--help`. Two servers sharing one stdout means a client that omits
the verb would be connected to the wrong protocol and simply hang, so a bare invocation exits 2 with
the usage text on stderr. This is the one place the sibling repositories' shape is deliberately
*not* copied — there, no arguments means "serve".

**D4 — Reach in v1 is every agent that speaks either protocol.** Claude Code gets a plugin carrying
both servers and the skill; Copilot CLI and OpenCode get LSP configuration; Codex, Gemini CLI,
Cursor and VS Code get MCP configuration. The same binary answers all of them, so breadth costs
documentation rather than code.

## Decisions, and where each one lives

Every design decision is numbered and recorded once, with its argument, in the instruction file of the
area it governs; code comments and tests cite decisions by number, and this table is how a number is
found. D5–D13 are the scaffold's own choices, D14–D107 were settled by the work packages that built each
area, D108–D113 by the 0.9.0 upgrade and D114 just before 1.0.0. A new decision takes the next free
number, goes in the file of its area as a paragraph that opens `**D<n> — <the decision>.**`, and gets a
place in this table.

| Decisions | Recorded in |
|---|---|
| D1–D4 | [this file](#the-shape-of-the-product) |
| D6–D8, D10–D12, D43, D49, D94, D107 | [`src/ClaudeRoslynLsp/AGENTS.md`](src/ClaudeRoslynLsp/AGENTS.md) |
| D13, D14, D16–D19, D44–D48, D52–D58, D74, D95–D100, D106, D111 | [`src/ClaudeRoslynLsp/Adapter/AGENTS.md`](src/ClaudeRoslynLsp/Adapter/AGENTS.md) |
| D23, D85–D93, D102, D103, D105 | [`src/ClaudeRoslynLsp/Adapter/Sharing/AGENTS.md`](src/ClaudeRoslynLsp/Adapter/Sharing/AGENTS.md) |
| D15, D24–D42, D50, D51, D79, D101, D104 | [`src/ClaudeRoslynLsp/Roslyn/AGENTS.md`](src/ClaudeRoslynLsp/Roslyn/AGENTS.md) |
| D5, D20, D63–D72, D80, D109, D112 | [`src/ClaudeRoslynLsp/Mcp/AGENTS.md`](src/ClaudeRoslynLsp/Mcp/AGENTS.md) |
| D60, D75–D78, D82–D84 | [`src/ClaudeRoslynLsp/Mcp/Engine/AGENTS.md`](src/ClaudeRoslynLsp/Mcp/Engine/AGENTS.md) |
| D21, D61, D62, D113 | [`src/ClaudeRoslynLsp/Edits/AGENTS.md`](src/ClaudeRoslynLsp/Edits/AGENTS.md) |
| D9, D59, D81 | [`tests/ClaudeRoslynLsp.Tests/AGENTS.md`](tests/ClaudeRoslynLsp.Tests/AGENTS.md) |
| D22, D108, D110, D114 | [`build/AGENTS.md`](build/AGENTS.md) |
| D73 | [`docs/AGENTS.md`](docs/AGENTS.md) |

The C-numbered wire facts are in [`docs/roslyn-protocol-facts.md`](docs/roslyn-protocol-facts.md), and
the R-numbered release properties in [`build/AGENTS.md`](build/AGENTS.md#release-engineering).

## Traps that cross every area

Each of these is broken before anybody knows which file to open, and each fails silently.

- **Plugin options are written under `CLAUDE_PLUGIN_OPTION_CLAUDE_ROSLYN_LSP_*`, never under the plain
  names.** Claude Code substitutes an option the user never filled in with `""`, so a plain name would
  shadow what the user's environment says, and the symptom is an adapter that opens the wrong solution
  and answers confidently about it. `PluginManifestTests` fails on the "simplification"; the rule and
  its one exception are in
  [`src/ClaudeRoslynLsp/AGENTS.md`](src/ClaudeRoslynLsp/AGENTS.md#the-plugin-option-rule).
- **Roslyn answers a request made before its workspace has loaded with an empty, successful result,
  never an error** (C27). A path that skips the readiness gate therefore passes every test that does
  not wait, and teaches the agent the server is useless.
- **`CHANGELOG.md` is the version authority**, parsed and never mutated by the build; its first line
  must parse as a version header (`# 1.0.0`), so a `# Changelog` title aborts the build.
- **A new MCP tool touches seven places**, and the tests fail on each one missed:
  [`src/ClaudeRoslynLsp/Mcp/AGENTS.md`](src/ClaudeRoslynLsp/Mcp/AGENTS.md#adding-a-tool).
- **If a solution ever stops finishing its load, read C52 and then C48** in
  [`docs/roslyn-protocol-facts.md`](docs/roslyn-protocol-facts.md): two independent causes of one
  silence — every project "successfully loaded", then no `projectInitializationComplete` until the
  readiness budget runs out.

## Package budget

Complete, as of the scaffold. Versions are centrally pinned in `Directory.Packages.props`, with
transitive pinning on.

- `src/ClaudeRoslynLsp`: `ModelContextProtocol` (pinned **exactly** `[2.4.0]`, because the AOT and
  serializer contract this server depends on — resolver chaining, schema generation, annotation
  defaults — is verified against that one version and a floating range would move it under a
  release), `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Logging.Console`.
  Nothing else, ever, for the reasons in hard rule 1.
- `tests/`: `NUnit` and `NUnit3TestAdapter`, whose Microsoft.Testing.Platform runner brings the
  platform packages in transitively (D9). No mocking or assertion libraries, and no test reporter.
- `build/`: `Fallout.Common` + `Fallout.Components` 10.5.0, and nothing pinned beside them (see
  [*Package budget changes*](build/AGENTS.md#package-budget-changes)). The build project opts out of
  central package management.

SourceLink needs no package reference — it is in-SDK on .NET 8 and later.

The acquisition work package (D24–D43) added **zero** packages, which was a constraint rather than a
coincidence: `HttpClient`, `ZipArchive`, `IncrementalHash`, `NamedPipeServerStream`, `Process`,
`JsonDocument`/`Utf8JsonWriter` and one `[LibraryImport]` into `kernel32` cover downloading, hashing,
extracting, launching, supervising and reporting. The only project-file change it needed is
`AllowUnsafeBlocks` in the server csproj, which the `[LibraryImport]` generator requires (D35).

## Build and test

```powershell
dotnet tool restore                                  # once per checkout
.\build.ps1 Test                                     # restore, compile, run the tests
.\build.ps1 SmokeTest                                # AOT publish + the real stdio exchanges against it
.\build.ps1 Pack                                     # the NuGet tool packages (D22) into artifacts/packages
dotnet test --project tests\ClaudeRoslynLsp.Tests    # the tests, without a publish

$env:CLAUDE_ROSLYN_LSP_LIVE_TESTS='1'
.\build.ps1 Test LiveTest                            # + the opt-in tests and the real-Roslyn leg (D59)
```

The orchestrator is [Fallout](https://fallout.build) 10.5.0 in `build/`. A failing test exits 2, and a
run that finds no test exits 8 — which a filtered run can hit by accident.

## Layout

- `src/ClaudeRoslynLsp/` — the one production project (`AssemblyName` `claude-roslyn-lsp`); its tree is
  in its `AGENTS.md`.
- `tests/ClaudeRoslynLsp.Tests/` — the single test project. `tests/fixtures/` — `HelloSolution` (D59)
  and `MANIFEST.md`, which records what each part of the fixture is for and what must not be tidied.
- `build/` — the Fallout orchestrator. `build/` is a resolver convention, which is why hard rule 6 exists.
- `docs/` — `roslyn-protocol-facts.md`, and one configuration file per client under `docs/clients/`.
- `.claude-plugin/` — `marketplace.json` and `plugin.json`: the repository is its own Claude Code plugin
  marketplace, `source: "./"`.
- `.claude/` — `CLAUDE.md` imports this file from here, because the root is the plugin's root, where a
  `CLAUDE.md` is not loaded and fails `--strict`; `agents/builder.md` is the delegation contract for
  work packages; `skills/claude-roslyn-lsp/SKILL.md` is the shipped skill, in one canonical copy.
- `.mcp/server.json` — the MCP server manifest, packed into the NuGet package; its version must match
  `CHANGELOG.md`.
- `.github/lsp.json` — this checkout's own Copilot CLI configuration, pinned to the published package.

A new top-level directory needs a line here and a decision naming why nothing existing could hold it.

## The size budget

Keep this file under **24 KiB** and every co-located `AGENTS.md` under **32 KiB**. 32 KiB is OpenAI
Codex's `project_doc_max_bytes` default, spent across the whole chain from the repository root to the
working directory and truncated silently past it, so the root takes the smaller share; and an agent
host refused this file outright at 167 k characters against a 150 k limit, which is what split it.
`AgentInstructionFileTests` makes both numbers fail rather than merely be stated, and fails as well on
a co-located file the index above does not name, one without its one-line `CLAUDE.md`, a link or an
anchor that does not resolve, and a decision table that disagrees with the files it points at. When a
file has to shrink, move material to the file of the area it governs, with a pointer back, rather than
cutting an argument; keep here only what an agent breaks before it knows which file to open.
