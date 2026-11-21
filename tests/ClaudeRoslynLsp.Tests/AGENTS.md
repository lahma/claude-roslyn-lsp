# Agent instructions: the tests

> **Read this when:** you write or change a test or a fixture, run the live tests, or read a failure
> from one of the tests that enforce this repository's rules.
>
> One of the co-located instruction files indexed from the repository-root
> [`AGENTS.md`](../../AGENTS.md); read that first. What `SmokeTest`, `LiveTest` and the release legs
> drive against a published binary is in [`../../build/AGENTS.md`](../../build/AGENTS.md).

## Running them

```powershell
.\build.ps1 Test                                            # the whole suite, via the orchestrator
dotnet test --project tests\ClaudeRoslynLsp.Tests           # the same tests, without a publish
dotnet test --project tests\ClaudeRoslynLsp.Tests --filter "FullyQualifiedName~Framing"
$env:CLAUDE_ROSLYN_LSP_LIVE_TESTS='1'; dotnet test --project tests\ClaudeRoslynLsp.Tests --filter "TestCategory=Live"
```

NUnit 5 on Microsoft.Testing.Platform (D9). A failing test exits 2 and a run that finds no test
exits 8 — which a filtered run can hit by accident. NUnit's `Does.Contain` and `Does.StartWith`
compare by culture and this test project does not set `InvariantGlobalization`, so string checks say
`.Using(StringComparison.Ordinal)`; keep it that way, because a culture-sensitive match on a URI or
a diagnostic id is a test that can pass for the wrong reason. No mocking or assertion libraries, and no
`Microsoft.Extensions.TimeProvider.Testing` either — the framing tests drive a hand-rolled stream
that returns one byte at a time, the adapter is driven over `System.IO.Pipelines` pipe pairs against
the scripted backend (D29), and the readiness gate's timeout and its ten-second notice run on a
hand-rolled `TimeProvider` the test moves by hand, because a test that waits two minutes for a
timeout is a test nobody runs. Because `JsonSerializerIsReflectionEnabledByDefault=false` is set in
the test csproj as well (D6), a type missing from a `JsonSerializerContext` fails here rather than
only after an AOT publish.

**D9 — NUnit 5 on Microsoft.Testing.Platform, run by a hand-written `Test` target.** `global.json`
sets `test.runner`, the test project sets `EnableNUnitRunner`, and `build/Build.cs`'s `Test` runs
`dotnet test --project` after `Compile` with the platform's telemetry off: Fallout's stock `ITest`
drives VSTest only (Fallout-build/Fallout#674), and MTP mode rejects both its positional project
argument and its `--logger trx`. `[assembly: FixtureLifeCycle(InstancePerTestCase)]` keeps xunit's
fresh instance per test, and fixtures run one at a time, NUnit's default. A failing test exits 2 and
a run that finds no test exits 8. No mocking or assertion libraries: NUnit's own constraint model,
and fakes hand-rolled, which keeps the test project's dependency tree as auditable as the product's.
This replaced xunit.v3 3.x on the VSTest bridge, held there only because `ITest` needed VSTest — the
reason went with `ITest`.

## The tests that enforce the rules

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
  tool-shaped identifier against the reflected tool inventory in both directions. The "names a tool
  that exists" direction is sharp, because the verb set it scans with is the union of the live
  inventory and the designed table's verbs. The "every tool is named" direction is now sharp too:
  the scaffold's escape, which let it pass while the skill was a placeholder that named nothing, was
  removed with WP6, so a playbook that quietly dropped half the surface — or all of it — fails here.
  It also requires the `compatibility` line, because a skill that teaches tools only present when a
  server is attached has to say so in the file itself.
- **`ConfigSnippetTests`** parses every checked-in client configuration under `docs/clients/`, plus
  this repository's own `.github/lsp.json`, in the format its extension claims — with JSON comments
  allowed only in the two files whose clients document them, and trailing commas allowed nowhere.
  It then asserts that each one names only variables `ClaudeRoslynLspOptions` actually reads, that
  each launch ends in a verb (this binary has no default one, so a snippet that forgot it is a
  process that exits 2 before the handshake), that every `.cs` extension mapping says `csharp`, and
  that every `dnx` invocation pins the `CHANGELOG.md` version. The variable set is recovered from
  `FromEnvironment`'s own source by `EnvironmentSurface`, so no document here can agree with a stale
  copy of the surface — `.mcp/server.json` is held to the same set, in full, by
  `McpServerManifestTests`.
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
- **`AgentInstructionFileTests`** holds the instruction files to the size budget the root `AGENTS.md`
  states — 24 KiB for it, 32 KiB for each co-located one — and keeps them reachable: the root index has
  to name every co-located file, each one needs the one-line `CLAUDE.md` beside it that Claude Code
  loads, and every link and anchor between them has to resolve. It also checks that every decision is
  recorded exactly once, in the file the root's decision table names, and that every decision a source
  file cites exists — because a number that leads nowhere is how a relocated argument gets lost.

## The live tests

`tests/ClaudeRoslynLsp.Tests/Live/` holds the tests that acquire and launch the **real** pinned
Roslyn. They are opt-in through `CLAUDE_ROSLYN_LSP_LIVE_TESTS=1` (and are reported as skipped
otherwise), because they download about 70 MB and start a quarter-gigabyte child; with
`CLAUDE_ROSLYN_LSP_HOME` set they reuse a warm cache, and with it unset they download into a temporary
home so that the acquisition path is itself under test. `RoslynLaunchLiveTests` covers the launch:
that the layout inside the real package, the real command line and both transports are what this
repository believes they are. `AdapterLiveTests` covers the *mediation* — one session over in-memory
pipes with a really launched Roslyn, run in phases (D59), asserting definition, references,
implementations, workspaceSymbol, call hierarchy and hover; a real CS0029 pushed after `didOpen` and
gone after the `didChange` that fixes it; a file written with `File.WriteAllText` found by
`workspace/symbol`; Roslyn killed mid-session and the next question still answered; and a shutdown
inside its budget. Every phase prints its timing, and the run ends with the child's peak working set,
which is C38 re-measured.

`SharedEngineLiveTests` covers the **shared engine** (D23, D85-D94) as one lifecycle, over its own
copy of the fixture and its own adapter home: an `lsp` session hosts and an `mcp` engine attaches, both
answer, a solution-wide `getDiagnostics` from the attached side works (which is D90's option routing
proving itself, because it answers emptily and successfully if the override never reaches the host),
Roslyn is killed and both halves answer again without the attached one having launched anything, the
host is stopped and the attached engine takes ownership, and then the whole thing runs again the other
way round — which is the order the plugin actually produces. The assertion that carries it is the
negative one: the attaching half's own launcher has no process id. Counting
`Microsoft.CodeAnalysis.LanguageServer` processes on the machine would be the obvious check and the
wrong one, because a developer box has several that belong to somebody else.

`McpToolsLiveTests` covers the **MCP half** the same way, through `OwnedRoslynEngine` and its own
copy of the fixture (D81): `getWorkspaceStatus` reaching `ok` with 3/3 projects and the multi-target
frameworks it expects, `resolveSymbol` and a three-project `findReferences`, both halves of
`getDiagnostics` (a `fullSolution` workspace pull for the closed `Program.cs`, and a file pull that
opens and closes it — C13, C14), a `getCodeActions` list carrying the folded fix-all scopes, a
`renameSymbol` previewed and then applied across four files with a CRLF+BOM file coming back
CRLF+BOM, a repeated solution pull that comes back at all rather than being held for ever (C58),
`fixDiagnostics IDE0005 scope: "solution"` touching exactly one file, an
`applyCodeAction "Move type to Rectangle.cs"` whose new file becomes resolvable, and a `formatCode`
that is idempotent on a file written badly on purpose.

**D59 — The fixture does not compile, the live tests are one method, and the LiveTest target is CI's
only real Roslyn.** `tests/fixtures/HelloSolution` is shaped like somebody else's repository — three
projects, one multi-targeted, a real xunit test project — with its own empty `Directory.Build.props`
and a `Directory.Packages.props` that turns CPM off, so this repository's `TargetFramework`,
`TreatWarningsAsErrors` and package budget cannot reach it. `Program.cs` contains `int x = "s";` on
purpose: without a real error the diagnostics bridge could be a no-op and every test would still
pass. The live tests run **one** session in phases rather than several tests over a shared fixture,
because two of the phases (killing Roslyn, shutting down) destroy what the others need, and tests
that depend on running in one order are tests the framework is free to run in another. The
`LiveTest` build target is the same argument at the release boundary: every smoke leg speaks to a
scripted backend and proves nothing about whether Microsoft's server still answers what this adapter
believes it answers. It earned its place immediately — the WP4 run that added it found C48, which
every scripted test had passed.

**D81 — The MCP live tests are one method in phases, with their own copy of the fixture.** D59's
argument, restated where it applies again: the phases destroy each other's preconditions — the
fix-all deletes the using the code-action phase asks about, the rename changes the symbol the
navigation phases resolve — and tests that depend on running in one order are tests the framework is
free to run in another. The copy is private to this collection because `AdapterLiveFixture`'s is
edited differently and collections run in parallel. One file in the copy is rewritten as **CRLF with
a BOM** on the way in, because `.gitattributes` forces `*.cs` to LF on checkout and the property
worth asserting is precisely that a rename writes a file back the way it found it (D61). The
formatting phase formats a file written badly on purpose, because "the second run changes nothing"
is satisfied by a first run that also changed nothing.

## Local numbers

Local numbers on this machine (three-project fixture, warm cache). The LSP session: load 3.9 s
**including** the cold server-side restore, CS0029 published 0.2 s after `didOpen` and cleared 0.56 s
after the fix, `Triangle.cs` found 1.4 s after being written, definition answered 2.9 s after a
`kill -9`, shutdown 0.11 s, Roslyn peak working set 356 MB, whole session 24 s. The MCP session:
load 4.0-4.4 s, `getWorkspaceStatus` 10 ms, `resolveSymbol` 1.1 s cold, `findReferences` 0.6 s,
a solution-wide diagnostic pull 2.1 s, `renameSymbol` 0.2 s to preview and 20 ms to apply,
`applyCodeAction` 0.17 s, `formatCode` 29 ms, Roslyn peak working set 373 MB, whole run 28 s — of
which 20 s is two deliberately-held workspace pulls proving C58 comes back at all.

The shared session, on the same fixture: the host loads in 4.2 s and the second half is **ready in
0.0-0.1 s**, because attaching costs a pipe connect and a handshake the host answers out of its own.
A `kill -9` of the shared Roslyn is answered again 3.2 s later on the LSP side and 3.9 s later on the
attached MCP side, neither of which launched a server of its own; stopping the host makes the attached
engine the owner 2.7 s later. One Roslyn, 296 MB peak — where two of them are two of that.

The same claim with two *published* binaries, which is what the plugin produces: `mcp` and `lsp`
started as separate processes against **Quartz.NET** (30 projects) with one `CLAUDE_ROSLYN_LSP_HOME`
and both answering give **one** language server at **369-374 MB**, five runs out of five and
including a cold payload cache; with `CLAUDE_ROSLYN_LSP_SHARE=off`, **two** at **720-724 MB**
(C63b). That check is a script rather than a test, because counting language-server processes is
machine-wide and a developer box has several that belong to somebody else.

## The idle recycle, on a hand-moved clock

The idle recycle (D95-D107) is proven on the hand-moved clock, a minute at a time with the policy's
background work awaited after each step: `BackendRecyclerTests` for the policy alone (baseline
timing, both arms of the threshold, the window and its reset, the idle shutdown, the compactor's one
call per idle period, "off"); `AdapterIdleRecycleTests` and `OwnedRoslynEngineRecycleTests` for the
two owners over the restartable fake (`shutdown` and `exit` rather than a closed stream, the mirror
replayed with the client's *unsaved* text, the crash budget intact afterwards, registrations not
multiplied, nothing recycled while a request is held or in flight); `SharedRecycleTests` for a host
with attached clients over the real pipe and session directory; and `SessionRegistryTests` and
`SharedEngineReportTests` for the atomic rewrite, the sweep and `doctor`'s section.
`DiagnosticsIpcCompactorTests` checks the compactor's bytes against hand-computed ones from the IPC
specification, its exchange against a scripted endpoint, and the real mechanism against the one .NET
process a test can always reach — itself: the gen2 collection count has to go up while the session is
enabled (C66). None of them launches a real Roslyn, so none of them can say whether a recycled child
actually comes back near its load size — that is a measurement, and it belongs with the live runs.

## Fixtures

Every fixture gets a row in [`tests/fixtures/MANIFEST.md`](../fixtures/MANIFEST.md) recording what
it is, when it arrived, and whether the bytes came off the wire or were written by hand. JSON cannot
carry comments, and a fixture whose provenance nobody recorded is a fixture nobody dares to
re-capture; that file also records which parts of `HelloSolution` must **not** be tidied up, because
several of them are deliberate diagnostics and code-action sites. The acquisition tests deliberately
need no fixture: they build a `.nupkg` in memory, because every property under test is about the
*shape* of a package — a prefix to strip, an entry to refuse — and a captured one would make each of
those cases a 70 MB file.
