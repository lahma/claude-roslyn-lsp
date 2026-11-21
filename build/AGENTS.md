# Agent instructions: the build, the packages and the release

> **Read this when:** you change the build orchestrator or a target, a package or the SDK, a workflow,
> the plugin manifests under `.claude-plugin/`, the MCP Registry manifest `.mcp/server.json` or
> `CHANGELOG.md` — or you cut a release.
>
> One of the co-located instruction files indexed from the repository-root [`AGENTS.md`](../AGENTS.md);
> read that first — the package budget, and hard rules 3, 4 and 6, are there. What the test project
> holds is in [`../tests/ClaudeRoslynLsp.Tests/AGENTS.md`](../tests/ClaudeRoslynLsp.Tests/AGENTS.md).

## The orchestrator

The orchestrator is [Fallout](https://fallout.build) 10.5.0 (stable channel), the maintained hard
fork of NUKE. The CLI is pinned in `.config/dotnet-tools.json` as `fallout.globaltool` (command
`fallout`) and resolves `build/_build.csproj` by convention.

```powershell
dotnet tool restore      # once per checkout
.\build.ps1 Test         # restore, compile, run tests
.\build.ps1 SmokeTest    # AOT publish + both real stdio handshakes against the binary
.\build.ps1 Pack         # the NuGet tool package (D22) into artifacts/packages

$env:CLAUDE_ROSLYN_LSP_LIVE_TESTS='1'
.\build.ps1 Test LiveTest   # + the opt-in tests and the real-Roslyn leg (D59)
```

`LiveTest` is `OnlyWhenStatic` on `CLAUDE_ROSLYN_LSP_LIVE_TESTS=1`, so a run without it reports
*skipped* rather than passing quietly. It acquires the pinned server into `artifacts/roslyn-home`
(inside the directory CI already caches and `Clean` already empties), opens
`tests/fixtures/HelloSolution` — which has never been restored, so the server-side restore path is
exercised (C30, C48) — and waits for a `publishDiagnostics` carrying the fixture's deliberate CS0029.
A warm local run is about 12 seconds; the four-minute budget is for a cold one, which downloads
70 MB and extracts 140 MB before anything can answer.

`dotnet fallout UpdateRoslynPin` is the only thing allowed to rewrite the Roslyn pin (D25); what it
downloads, checks and writes is in
[`../src/ClaudeRoslynLsp/Roslyn/AGENTS.md`](../src/ClaudeRoslynLsp/Roslyn/AGENTS.md#bumping-the-pin).

`CHANGELOG.md` is the **version authority**: its top section is parsed in `OnBuildInitialized` and
passed to the build as the version. The file is never mutated by the build, and the first line must
parse as a version header (`# 0.1.0`) — do not add a `# Changelog` title, it would abort the build.

## What `SmokeTest` proves

`SmokeTest` is the end-to-end check the unit tests cannot be: it publishes the Native AOT binary,
spawns it, and drives **four** real exchanges. Two of them are LSP sessions —
`initialize`, `initialized`, a `didOpen`, a `textDocument/definition`, then, once that definition has
been answered, `shutdown` and `exit` with the exit code asserted — one against the scripted backend
running *inside* the server process (`lsp --smoke`) and one against the same script in a *child*
process (`CLAUDE_ROSLYN_LSP_FAKE_BACKEND=child`, which spawns `<self> fake-roslyn` — or, under the
`dotnet` host, `dotnet <self>.dll fake-roslyn`). The other two are MCP exchanges (`initialize`,
`initialized`, `tools/list`, and a `tools/call` of `renameSymbol` with a mis-cased `Preview` that
must come back as D109's refusal rather than run), whose `tools/list` answers are compared against
`ExpectedToolNames` verbatim — the only assertion this repository makes about the tool inventory of
the *published* binary rather than of a reflected type list, and therefore the only thing that would
catch an AOT publish which dropped a tool class or a serializer context that could not describe a
result type. The first of them runs with `CLAUDE_ROSLYN_LSP_FAKE_BACKEND=none`, so no Roslyn is
wired in at all (D69, D80), which is exactly the point: the handshake and `tools/list` must not
depend on a backend. The second runs `mcp --smoke`, where a backend *is* started from the handshake
(D76) — the leg that proves the eager start puts no byte on the protocol channel. The same legs
(`AssertServerAnswers`) run against every binary that ships: the archive's (`SmokeTest`), each RID
tool package's (`PackRuntime`) and the `any` fallback's (`Pack`).

Each leg proves something the others cannot. The LSP legs read stdout as *frames*, so any byte that
is not part of one fails the test: that is what proves the "nothing else writes to stdout" rule on a
real binary rather than in a source scan. They also assert that the capability document is this
repository's own and not the backend's (D45) — the scripted backend answers with Roslyn's real one,
which advertises three providers the adapter must not carry (C26). And because that backend answers
navigation with an *empty successful result* until it reports the workspace loaded, exactly as the
real server does (C27), a **non-empty** definition answer is the proof that the readiness gate held
the request and released it (D46); `shutdown` is deliberately not sent until that answer arrives,
because a server that shut down with the request still held would otherwise pass. The child-process
leg is the only one that proves the plumbing: that this RID can spawn a child at all, that its three
redirected handles are wired the right way round, and that nothing the child writes at startup lands
on what is now a protocol channel — which the real server does do in one of its transports (C7). CI
runs `Test`, `SmokeTest`, `LiveTest` **and** `Pack` on every push and pull request, with
`CLAUDE_ROSLYN_LSP_LIVE_TESTS: 1` set for the whole job — so the 70 MB download is paid per run and
`RoslynServerManifest.cs` is in the cache key, because a pin bump has to miss the cache or the job
would test the new pin against the old payload.

## Packages and the toolchain

**D108 — The SDK and the target framework stay on .NET 10 until a measured gain says otherwise.**
`global.json` pins the .NET 10 SDK and `TargetFramework` is `net10.0`, through .NET 11 (STS,
released 2026-11-10). Measured on the sibling `sonarqube-mcp` on 2026-11-18, `net11.0` made the AOT
binary 1.9% larger and no faster; .NET 11 ends support on the same day as .NET 10 (2028-11-14); it
raises the platform floor (macOS 15, x86-64-v2); and ModelContextProtocol has no `net11.0` build.
Here the argument is stronger still: the Roslyn payload is `tools/net10.0/<rid>/` and needs a .NET
10 runtime whatever this binary targets (D31), so `net11.0` would make the framework-dependent `any`
package (D22) need two runtimes where one does today. The SDK stays too: the .NET 11 SDK would bring
back the `NuGet.Frameworks` pin (*Package budget changes*, 2026-11-19). Move when the Roslyn payload
moves, at .NET 12 LTS, or when a .NET 11 feature is actually needed.

### Package budget changes

**2026-09-10 — `NuGet.Frameworks` 7.9.0, `build/_build.csproj` only, inherited from the siblings.**
The .NET SDK binds `NuGet.Frameworks, Version=7.9.0.0` by strong name inside the build process
whenever Fallout's `ITest` evaluates a test project in-process — which it does only when
`GITHUB_ACTIONS` is set, so the failure is invisible locally and appears on the first CI run. Fallout
10.4.0 brings 6.14.3 transitively through `NuGet.Packaging`, and the app-local copy shadows the SDK's,
so every CI `Test` run throws `InvalidProjectFileException`. Pinning forward is safe in both
directions, because the runtime binds a higher assembly version than the one requested. Removed on
2026-11-19, once Fallout 10.5.0 brought 7.9.0 in on its own (below).

**Fallout stays on the 10.x line.** The `11.0.x` line is the *edge* channel, not the newer one; 10.x
is stable and functionally ahead. `GitHubActionsAttribute.Env`, which the publish workflow needs, does
not exist in 11.0.x, and regenerating the workflows there would move the actions *backwards*.

**2026-11-19 — Fallout 10.4.0 to 10.5.0, and the `NuGet.Frameworks` pin removed.** 10.5.0 brings
`NuGet.Frameworks` and `NuGet.Packaging` 7.9.0 itself (Fallout-build/Fallout#677, which closed #638),
the version .NET SDK 10.0.4xx binds, so the build project references the two Fallout packages and
nothing else. The CI path was reproduced locally before the pin went — `dotnet fallout Test` with
`GITHUB_ACTIONS=true` and the `GITHUB_*` variables set for that one process, still on Fallout's
`ITest` — and passed. The bump also clears NU1902/NU1903 on the build project's restore: 10.4.0
brought System.Security.Cryptography.Pkcs and System.Security.Cryptography.Xml 10.0.10, which carry
known advisories, and 10.5.0 brings 10.0.13. `build.ps1` takes 10.5.0's bootstrap change
(`DOTNET_INSTALL_SKIP_TAR=1`, Fallout-build/Fallout#730). The pin comes back, at **8.0.0**, if
`global.json` moves to the .NET 11 SDK while a Fallout component still evaluates projects
in-process: that SDK's `Microsoft.Build.dll` asks for `NuGet.Frameworks` 7.11.0.0, Fallout brings
7.9.0, and whether the evaluation fails depends on which copy loads first. 8.0.0 is the SDK's own
copy and satisfies both SDKs; Fallout-build/Fallout#731 tracks the fix.

**2026-11-19 — the tests move to NUnit 5 on Microsoft.Testing.Platform.** `xunit.v3`,
`xunit.runner.visualstudio` and `Microsoft.NET.Test.Sdk` out; `NUnit` 5.0.0 and `NUnit3TestAdapter`
6.3.1 in, with the adapter's MTP runner enabled (D9). The restored test graph goes from 40 packages to
30, and everything beyond the two references arrives through the adapter (the platform, its VSTest
bridge and its telemetry extension). The move kept every test: 845 run, 839 passed and 6 skipped
before and after, the same 549 method names with the same count each, and a deliberately broken
assertion failed the run with exit code 2; the six live tests passed under NUnit against the real
server. Nothing else was added: no TRX or GitHub reporter, so CI reports the runner's own console
output and exit code. bitbucket-mcp, on the same stack, added `GitHubActionsTestLogger` for
annotations and a job summary; declined here, as in sonarqube-mcp, because the console output already
names a failing test with its assertion, and a CI nicety is not worth a package and its graph in a
project whose dependency tree one person audits. The telemetry extension is switched off by the
`Test` target (`TESTINGPLATFORM_TELEMETRY_OPTOUT=1`).

**2026-11-19 — app version refresh, no ids added or removed.** `ModelContextProtocol` `[2.2.0]` to
`[2.4.0]`, still an exact pin; for net10.0 it adds no package id, only newer
`Microsoft.Extensions.*.Abstractions` versions. Two of its changes touch this server: since 2.3.0
`tools/list` keeps registration order (nothing here depends on the order — `SmokeTest` and
`ToolInventoryTests` compare sets), and since 2.4.0 protocol property names match case-sensitively
(modelcontextprotocol/csharp-sdk#1934). `Microsoft.Extensions.DependencyInjection` and
`Microsoft.Extensions.Logging.Console` 10.0.12 to **10.0.15**, deliberately not 11.0.0: the 11.x
packages target net10.0 too, but they would pull the 11.x abstractions in as packages, while the
10.0 servicing line is the one that matches the target framework (D108).

## Shipping

**D22 — NuGet is the plugin's launch channel, and since 0.9.0 it delivers the Native AOT binary.**
The plugin runs `dnx claude-roslyn-lsp@{version}` for both servers — no download step, and an SDK is
required for C# work anyway — while file-based clients point at the same binaries from GitHub
Releases. `dnx` resolves a **pointer** package that names one RID-specific package per
release-matrix RID, then fetches the one for the user's platform, or the framework-dependent
**`any`** package anywhere else — which covers the three Roslyn RIDs no archive is built for (D24)
at no new cost, because Roslyn needs a .NET 10 runtime there anyway (D31). Measured on win-x64 for
the sibling sonarqube-mcp, `initialize` + `tools/list` through `dnx` took 715 ms framework-dependent
and 357 ms native, and this plugin pays that start twice per session, once per verb. Native AOT
cannot cross-compile across operating systems, so each `release.yml` leg packs its own RID package
(`PackRuntime`), extracts the binary, holds it to the glibc floor (R3) and drives every smoke leg
against it — `dotnet pack -r` publishes again, so the archive's binary is not the package's.
`publish.yml`, the only file the trusted-publishing policy names, collects the five from the same
tag's `release.yml` run and pushes every RID package before the pointer, stopping at the first
failure, so the pointer never names a package that does not exist. The SDK types the RID packages
`DotnetToolRidPackage`, so `PackageType=McpServer` stays on the pointer alone, which nuget.org
requires. The push is **trusted publishing**: the workflow exchanges its GitHub OIDC token for an
API key that lives minutes, so no NuGet API key exists in this repository or in its secrets, and the
exchange is C# inside the build (`build/Build.Publish.cs`), not a marketplace action.

**D110 — Every release is listed in the official MCP Registry, once `dnx` can install it.** The
manifest is `.mcp/server.json` (schema `2025-12-11`), as `io.github.lahma/claude-roslyn-lsp`, which
the registry's GitHub OIDC login proves this repository owns. Its NuGet validator accepts exactly
`registryBaseUrl: https://api.nuget.org/v3/index.json`, a `description` may be 100 characters at
most, and it proves the package is ours by finding `mcp-name: io.github.lahma/claude-roslyn-lsp` in
the README nuget.org serves **for that version** — and a registry version is immutable, so a version
packed without the marker can never be listed. `McpServerManifestTests` holds all four.
`PublishMcpRegistry` runs in the publish job after the NuGet push: it leaves a version the registry
already lists alone, waits until nuget.org serves that version's README (through
`ReadmeUriTemplate/6.13.0`, the resource the registry reads) **and** every package of it (through
`PackageBaseAddress/3.0.0`, the one `dnx` downloads from) — the README arrives minutes before the
native packages, and a listing is an invitation to run `dnx` — then exchanges a GitHub OIDC token
(audience `https://registry.modelcontextprotocol.io`) at `/v0/auth/github-oidc` and posts the
manifest to `/v0/publish`. A registry failure fails the job loudly but cannot hold up or undo the
NuGet push. Validate the manifest at the public `/v0/validate` before tagging.

**D114 — The Claude Code plugin is called `roslyn-adapter`; everything else keeps the product's
name.** Claude Code 2.1.325's `claude plugin validate` refuses a third party's plugin name that
begins `claude-` — "it passes as one of Anthropic's own" — and an install that works today is one a
later Claude Code may refuse. `roslyn-lsp`, the obvious alternative, was already the name of four
public plugins (lewing/agent-plugins, unsafePtr/ClaudeCodeRoslynLspProxy, 0xdhx/dhx-hooks,
Jodre11/claude-code-plugins), and two plugins of one name give colliding tool ids. `roslyn-adapter`
was in no public `.claude-plugin` manifest and not in the official marketplace on 2026-11-19, and it
is what the product calls itself. The rename is the plugin's alone: the marketplace stays
`claude-roslyn-lsp`, because that is what every existing user's `/plugin marketplace add` registered
and the reserved-prefix rule is about plugin names; the binary, the NuGet id, the MCP server's
`serverInfo.name` and the registry name stay `claude-roslyn-lsp` (`ServerVersion.Name`). What it
costs a user of 0.9.0 or earlier, once, and why it was done before 1.0 rather than after: `/plugin
update` cannot follow a rename, so the new plugin is installed and the old one removed by hand;
plugin options and `${CLAUDE_PLUGIN_DATA}` are per plugin, so options are set again and Roslyn is
downloaded again into the new data directory; and the MCP tool ids change from
`mcp__plugin_claude-roslyn-lsp_roslyn__*` to `mcp__plugin_roslyn-adapter_roslyn__*`, which
permission rules name. The local plugin directory follows as `roslyn-adapter-local`, and
`PluginManifestTests` holds both names.

## Release engineering

Releases are cut by pushing a `v*` tag, which starts **two workflows**: `release.yml` (binaries, the
RID tool packages and the GitHub Release) and `publish.yml` (nuget.org and the MCP Registry, D22,
D110). The coupling runs one way: `publish.yml` collects the five native tool packages the same tag's
`release.yml` legs built, so a failed leg stops both, but `release.yml` never waits for `publish.yml`
— a nuget.org outage or a policy mismatch cannot hold up the GitHub Release.

`.github/workflows/release.yml` is hand-written (the `[GitHubActions]` generator has no matrix
support) and runs five publish legs — one per RID, each on its own hardware — followed by a `release`
job that assembles the GitHub Release:

| RID | Runner |
|---|---|
| `linux-x64` | `ubuntu-24.04` (named, for the glibc floor: R3) |
| `linux-arm64` | `ubuntu-24.04-arm` |
| `win-x64` | `windows-latest` |
| `win-arm64` | `windows-11-arm` |
| `osx-arm64` | `macos-latest` |

Every leg runs `dotnet fallout SmokeTest PackRuntime --runtime <rid>`. `SmokeTest` depends on
`PublishAot`, so each leg compiles its own binary, speaks both protocols to it *on the architecture it
targets*, and only then uploads the archive; `PackRuntime` then runs `dotnet pack -r <rid>`, extracts
the binary from the package — a second publish, not the archive's binary — holds it to the glibc
floor on Linux, drives the same legs against it and uploads the package as `tool-package-<rid>`, the
name `CollectRuntimePackages` looks for. **Never ship a binary that has not answered a handshake on
its own architecture** — that is why the arm64 legs run on arm64 runners instead of being
cross-compiled. `fail-fast` is off so one broken runtime cannot mask the state of the other four.

A manual run (`gh workflow run release.yml --ref <branch>`) is the **dry run**: all five legs build,
smoke-test, glibc-check and pack, and the `release` job, which runs only for a tag, releases nothing.
Its packages are what `CollectRuntimePackages` is rehearsed against before a tag exists:
`GITHUB_TOKEN=$(gh auth token) dotnet fallout CollectRuntimePackages --release-run-id <id>`.
`ToolPackageTests` keeps the csproj's `ToolPackageRuntimeIdentifiers` (minus `any`) equal to the
matrix, requires `any`, and checks that every leg uploads `tool-package-<rid>`.

`CHANGELOG.md` stays the version authority here too: `CreateGitHubRelease` refuses to publish unless
`GITHUB_REF_NAME` equals `v{version parsed from CHANGELOG.md}`. To release, land the changelog entry
first, then tag that commit. `Prerelease` follows the version string (`Version.Contains('-')`), so a
`0.1.0-rc.1` tag publishes as a prerelease and never displaces the latest stable release.

Three properties of the release path are worth not rediscovering; the first two are inherited from
the template, where a throwaway tag established them:

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
- **R3 — the Linux binaries need glibc 2.34 or newer, and a publish that needs more fails.** A Native
  AOT binary is linked against the glibc of the image that builds it and will not start on an older
  one. `PublishAot` and `PackRuntime` read the binary's version-needs table back after every Linux
  publish (`build/GlibcRequirements.cs`, pure C# over the ELF, no `objdump`) and fail above `MaxGlibc`
  in `build/Build.cs`, because the floor follows the runner image: `ubuntu-latest` moving to Ubuntu
  26.04 would otherwise raise it without a single change here. The `linux-x64` release leg and the
  CI job therefore name `ubuntu-24.04`, and README.md states the floor. Raise it only on purpose, in
  both places.

The release body is composed from `artifacts/release-notes.md`, which `Build.WriteReleaseNotes`
reflows out of `CHANGELOG.md`'s newest section — one single-line bullet per entry — because
`ChangelogTasks.ExtractChangelogSectionNotes` only recognises `## ` headings and ends a section at the
first non-bullet line, and this changelog uses `#` headings with wrapped bullets. Keep changelog
entries as `- ` bullets with wrapped continuation lines and this keeps working.

### The NuGet leg

`.github/workflows/publish.yml` is **generated** from the second `[GitHubActions]` attribute in
`build/Build.CI.GitHubActions.cs` (hard rule 4 — never hand-edit it). One Ubuntu job runs
`dotnet fallout Compile Test Pack Publish PublishMcpRegistry` with
`permissions: { contents: read, actions: read, id-token: write }`, the job's `GITHUB_TOKEN`,
`environment: nuget` and `env: NUGET_USER: lahma`. `Pack` builds the pointer and the `any` fallback
here, and drives every smoke leg against the fallback as `dotnet claude-roslyn-lsp.dll`, the way the
CLI runs a framework-dependent tool. `Publish` depends on `CollectRuntimePackages`, which finds the
tag's `release.yml` run by `head_sha` and `event=push`, polls until its five `tool-package-<rid>`
artifacts exist — failing at once if the run finished without one — and downloads them (that is
what `actions: read` is for). It then refuses an incomplete set, and pushes every RID package first
and the pointer last, stopping at the first failure. Our policy accepted the six new package ids
without a change.

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

After a push, nuget.org serves each package on its own schedule: on the sibling repositories the
README and `any` came within minutes and the native packages took 14-25 minutes, and `dnx` resolves
through the registration index, which trails the flat container. `PublishMcpRegistry` waits for all
of them (D110). Do not trust the target summary for the push itself — `--skip-duplicate` once masked
a 409 as success; the log has one `PUT … Created` line per package. The proof a release reached
users is a real `dnx claude-roslyn-lsp@<version> --yes doctor` that leaves
`claude-roslyn-lsp.<rid>` in the NuGet package cache.

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
claude plugin validate . --strict          # the marketplace manifest and the plugin manifest; must end "Validation passed"
claude plugin marketplace add .            # resolves it; remove afterwards
```

`--strict` turns every plugin warning into a failure, so it has to stay warning-free. Three things
keep it that way:

- **A plugin name that does not begin `claude-`** (D114): the validator reserves the prefix for
  Anthropic's own plugins and fails on it outright, `--strict` or not.
- **No `CLAUDE.md` at the repository root.** The root is the plugin's root (`source: "./"`, D72),
  where Claude Code does not load one as context, and the validator warns about it. The import that
  gives contributors the root `AGENTS.md` lives in `.claude/CLAUDE.md` (`@../AGENTS.md`, relative to the
  importing file), which Claude Code reads as project instructions just the same. The one-line
  `CLAUDE.md` beside each co-located `AGENTS.md` is not at the root and draws no warning (checked
  with `--strict` on 2026-11-21).
- **One expected line that is not a warning: `ICON_MISSING`.** It is a *plugin directory* check, and
  the validator says itself that those do not change the result. No icon ships yet, on purpose: the
  first icon a plugin is submitted to the directory with becomes its listing icon for good, so it is
  the owner's choice. When one is chosen: a square PNG of 512–2048 px, under 2 MB, at
  `.claude-plugin/icon.png`.
