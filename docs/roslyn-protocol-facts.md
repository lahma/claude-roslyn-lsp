# roslyn-language-server 5.12.0-1.26426.8: observed protocol facts

Every entry below was observed on the wire against the pinned server (Windows 11, .NET SDK 10.0.401,
2026-09-10) with a two-project fixture, not read from source. They are numbered C1..C54 and cited
from AGENTS.md, the code and the tests. Re-verify the ones marked (re-measure) on a real solution.

C48-C54 were observed by WP4 against `tests/fixtures/HelloSolution` (three projects, one
multi-targeted), against **Quartz.NET** (`Quartz.slnx`, 30 projects) and against **OrchardCore**
(`OrchardCore.slnx`, 239 projects, 7,711 `.cs` files) — the last two through a real Claude Code
2.1.267 session driving the published Native AOT binary as a `--plugin-dir` LSP server.

## Acquisition and launch

- **C1** The tool-store path is ~290 characters and `Process.Start` fails with "file not found"
  even though the file exists. Keep the cache layout flat (`<cache>/roslyn/<version>/<rid>/...`).
- **C2** `dotnet tool install` rejects `--prerelease` together with `--version`; an exact
  prerelease `--version` alone installs fine.
- **C3** nuget.org's registration index has no `packageHash`; the catalog leaf reached through
  `catalogEntry.@id` has `packageHash` (base64 SHA-512), `packageHashAlgorithm` and
  `packageSize`. All eight RID hashes matched a local SHA-512 of the nupkg.
- **C4** The `.nupkg.sha512` file inside a tool store is NOT the SHA-512 of the nupkg next to it.
- **C5** There are eight RID packages, not seven: `DotnetToolSettings.xml` also lists
  `linux-musl-arm64`.
- **C6** `--extensionLogDirectory` produced no files at `--logLevel Information`; all logging
  arrives as `window/logMessage` (21-22 lines during startup).
- **C7** In `--stdio` mode stdout carries only LSP frames and stderr is empty. In `--pipe` mode a
  646-byte startup banner is written to the child's stdout, so a pipe launcher must drain stdout
  and never treat it as protocol.
- **C8** `--pipe` accepts `name` and `\\.\pipe\name`, with or without `CurrentUserOnly` on the
  server stream. Roslyn is the pipe client; the adapter creates
  `NamedPipeServerStream(name, InOut, 1, Byte, Asynchronous | CurrentUserOnly)` and waits.
  Connect plus `initialize` took 0.6-0.7 s.
- **C29** `roslyn-language-server.exe` is a thin client that spawns its own
  `Microsoft.CodeAnalysis.LanguageServer` daemon per instance; two clients gave four processes
  and no shared workspace, even with a shared `ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME`. Launch
  `Microsoft.CodeAnalysis.LanguageServer.dll` directly.
- **C30** Roslyn restores the solution itself server-side when `obj/` is missing (+3.3 s) and
  never asks the client; no `_roslyn_*` message was ever sent and `_roslyn_projectNeedsRestore`
  does not exist in this build.
- **C31** Readiness: `initialize` answers in under 1 s; `workspace/projectInitializationComplete`
  (a notification with no `params`) at 2.5-3.3 s for two projects, 6.3 s with a cold restore,
  9.3 s on a contended machine. Exactly one `$/progress` stream (token = bare GUID, title
  `Loading <solution>...`), preceded by `window/workDoneProgress/create`.
- **C38** (re-measured 2026-09-10, WP4) 253 MB working set with the shipped `System.GC.Server:
  true`, 247 MB with `DOTNET_gcServer=0`; the env var overrides the runtimeconfig. Peak working set
  scales with the solution, not with the server: **356 MB** for the three-project `HelloSolution`
  through a full live session (load, pulls, a watcher reload, a relaunch), **576 MB** for
  Quartz.NET's 30 projects during a load plus one diagnostic pull, and **948 MB** for the same
  solution when the session also ran `findReferences` across 135 files. Plan for roughly
  300 MB + 10 MB per project, and expect a solution-wide reference search to double it.
- **C39** Custom methods present: `solution/open`, `project/open`,
  `workspace/projectInitializationComplete`, `codeAction/resolveFixAll`,
  `workspace/_roslyn_restore`, `workspace/_roslyn_restorableProjects`,
  `workspace/_roslyn_refreshSourceGenerators`, `window/_roslyn_showToast`,
  `roslyn/updateLogLevel`, `roslyn/resolveContext@2`, and a `textDocument/_vs_*` family.
- **C43** (2026-09-10, WP3) A bare handshake — `initialize` with an **empty** `capabilities` object,
  no `initialized`, then `shutdown`/`exit` — answers in 0.33-0.63 s, sends **zero**
  `window/logMessage` notifications, and the process exits 0 within 18-27 ms of `exit`. Peak working
  set at that point is 84 MB; C38's 253 MB is a *loaded solution*, not a started server. It still
  advertises 26 capabilities (`textDocumentSync`, `semanticTokensProvider`, `codeLensProvider`,
  `inlayHintProvider`, `_vs_onAutoInsertProvider` and the rest), which is C26 restated from the
  other direction: the client declaring nothing changes nothing. Pipe connect was 0.18-0.62 s;
  stdio "connect" is 5 ms because there is nothing to connect.
- **C44** (2026-09-10, WP3) The flat container lower-cases both id and version in the path, and a
  request for a mixed-case version is a 404 rather than a redirect. `tools/net10.0/<rid>/` extracts
  to 163 files at the top level plus `BuildHost-net472/`, `BuildHost-netcore/`, `Targets/` and 13
  culture folders; `Microsoft.CodeAnalysis.LanguageServer.runtimeconfig.json` asks for `net10.0`
  with `"rollForward": "Major"` and `System.GC.Server: true`.
- CLI of this build: `--debug --brokeredServicePipeName --logLevel --telemetryLevel --sessionId
  --extension --devKitDependencyPath --csharpDesignTimePath --extensionLogDirectory --pipe
  --stdio --autoLoadProjects [max] --sourceGeneratorExecutionPreference <Automatic|Balanced>
  --clientProcessId <pid> --daemon --daemonKeepAlive <s>`. `--stdio` and `--pipe` are mutually
  exclusive and one is required.
- **C45** The `initialize` result carries a non-standard `_roslyn_processId` naming the process
  that actually holds the workspace. With the thin client in the picture (C29) that is a different
  pid from the one launched, so it is the only number worth reporting for memory or for a kill.
- **C47** `window/logMessage` is sent with `"type": 5`, which LSP 3.17 does not define (it is
  3.18's `Debug`). A client that switches over 1-4 drops or mishandles roughly a third of Roslyn's
  startup output.

## Configuration

- Roslyn issues two `workspace/configuration` requests asking for 80 sections; answering `null`
  everywhere yields defaults. Sections are `csharp|<group>.<name>` / `visual_basic|...` except
  `navigation.*`, `projects.*`, `code_style.formatting.new_line.insert_final_newline` and the
  Razor/HTML ones. Notable: `csharp|background_analysis.dotnet_compiler_diagnostics_scope`,
  `csharp|background_analysis.dotnet_analyzer_diagnostics_scope`,
  `projects.dotnet_enable_automatic_restore`, `navigation.dotnet_navigate_to_decompiled_sources`,
  `csharp|formatting.dotnet_organize_imports_on_format`,
  `csharp|symbol_search.dotnet_search_reference_assemblies`.
- **C46** The two `workspace/configuration` requests are 75 sections and then 5, and the second
  arrives from the Razor subsystem after its cohost registrations — a client that answers only the
  first leaves a request outstanding in a subsystem that is on the startup path.

## Diagnostics

- **C9** `textDocument/diagnostic` with no `identifier` returns the union of every source.
- **C10** Only `DocumentCompilerSemantic` yields compiler errors and only
  `DocumentAnalyzerSemantic` yields IDE/CA diagnostics; sources with nothing to say return
  `{"kind":"full","items":[]}` without a `resultId`.
- **C11** The ten `textDocument/diagnostic` registrations arrive in a different order every run;
  key on `registerOptions.identifier` (the Razor one has none).
- **C12** `previousResultId` yields `{"kind":"unchanged"}` but `resultId` changes on every full
  report; store the latest per (uri, source).
- **C13** `textDocument/diagnostic` on a file that is not open always returns 0 items regardless
  of scope. Non-open files are only reachable via `workspace/diagnostic`.
- **C14** `workspace/diagnostic` returns closed-file diagnostics only when
  `csharp|background_analysis.dotnet_compiler_diagnostics_scope` is `fullSolution`, and only for
  `identifier` omitted or `WorkspaceDocumentsAndProject`; analyzer diagnostics for closed files
  additionally need `dotnet_analyzer_diagnostics_scope = fullSolution`.
- **C15** `workspace/diagnostic` skips open documents, includes `obj/**/*.cs` and `.csproj`
  entries, and lists a multi-targeted project once per TFM.
- **C16** IDE0005 arrives at severity 4 (Hint) at position 0:0 for the whole using block; CA1822
  at severity 3 (Information); `tags` mixes the LSP `Unnecessary` tag (1) with VS-private tags
  2147483640-2147483645 that must not be forwarded.
- **C17** About 90 ms from a full-text `didChange` to a clean pull; the first pull of a session
  costs 1.3-1.7 s. Full-text `didChange` is accepted although the server advertises incremental.
- **C27** Requests issued before `projectInitializationComplete` get empty successful results,
  never `ContentModified`.
- **C28** A `didOpen`ed file that belongs to no loaded project is served in misc-files mode with
  wrong diagnostics (IDE0005 on usings the project needs). Gate diagnostics on readiness too.

## Code actions, fix-all, rename

- **C18** `codeAction` `data` is `CodeActionResolveData` with PascalCase members
  `UniqueIdentifier, CustomTags, Range, TextDocument, CodeActionPath` plus `NestedCodeActions`
  or `FixAllFlavors`. Fix-all entries are separate items titled `Fix All: <title>` with
  `FixAllFlavors: ["Document","Project","Solution"]` and
  `command.command = "roslyn.client.fixAllCodeAction"`.
- **C19** `executeCommandProvider.commands` is `[]`; the `roslyn.client.*` commands are
  client-side markers.
- **C20** `codeAction/resolveFixAll` takes `{ "title", "data", "scope" }`; `scope` is
  case-sensitive (`Document|Project|Solution`) and mandatory; the plain action's `data` is
  accepted. `codeAction/resolve` on a `Fix All:` entry returns no `edit`.
- **C21** Resolved edits always use `documentChanges`; every `TextDocumentEdit` has
  `"version": null`. With `resourceOperations` advertised, `Move type to <X>.cs` resolves to a
  `{"kind":"create"}` operation first, then edits into the not-yet-existing file.
- **C22** A single `newText` can mix `\r\n` and `\n`; normalise incoming text, not only the file.
- **C23** Rename returns minimal character diffs (`Compute` to `Calculate` replaces `ompu` with
  `alcula`), crosses projects, one `TextDocumentEdit` per file; `prepareRename` returns a bare
  `Range`.
- Sites observed: IDE0005 gives `Remove unnecessary usings` (+ Fix All, + `Suppress or configure
  issues` nested); CA1822 gives `Make static` (+ Fix All), `Use expression body`, `Extract base
  class...`; an interface declaration gives only `Extract interface...`; a second type in a file
  gives `Move type to <X>.cs`, `Convert to positional record`, `Extract interface/base class`.

## Navigation

- **C24** Call hierarchy duplicates results once per TFM of a multi-targeted project;
  `workspace/symbol` and `rename` de-duplicate. De-duplicate by (uri, selectionRange).
- **C25** Definition into a BCL symbol returns a real `file:` URI under
  `%TEMP%\MetadataAsSource\<hash>\DecompilationMetadataAsSourceFileProvider\<hash>\<Type>.cs`.
- **C26** Roslyn advertises `semanticTokensProvider`, `codeLensProvider`, `inlayHintProvider`
  and `_vs_onAutoInsertProvider` statically even when the client declared none; an adapter that
  authors its own capability document must filter what it forwards.
- Call-hierarchy items carry opaque `data` (`SymbolKeyData`, `ProjectGuid`, `TextDocument`)
  that must be round-tripped verbatim. `serverInfo.name` is
  `CSharpVisualBasicLanguageServerFactory` with no version.

## File watching

- **C32** Roslyn registers 135-149 `workspace/didChangeWatchedFiles` watchers for a two-project
  solution, one `client/registerCapability` each; 113 are one-per-reference-assembly under the
  NuGet cache. Collapse by `baseUri`, ignore watchers rooted outside the workspace, compare glob
  sets not strings (`**/*{.cs,.razor,.cshtml}` and `**/*{.cs,.cshtml,.razor}` both occur).
  `client/unregisterCapability` uses the field name `unregisterations`.
- **C33** A `Created` event for a new `.cs` file does nothing; a `Changed` event for the owning
  `.csproj` (even with the file untouched on disk) triggers a re-evaluation and the symbol appears
  1.6-2.1 s later. Map "a `.cs` file appeared or disappeared" onto a synthetic `Changed` for the
  containing project file.
- **C34** Without the `didChangeWatchedFiles` client capability Roslyn registers nothing and has
  no in-process watcher fallback.

## dnx (SDK 10.0.401)

- **C35** `dnx` keeps stdout byte-clean cold and warm (1.8 s / 0.6 s); no prompt when stdout is
  redirected.
- **C36** `--yes` is consumed by `dnx` although `dnx --help` does not list it; position does not
  matter.
- **C37** `dnx` steals `--version`, `-v`, `--verbosity`, `--prerelease`, `--configfile`,
  `--source`, `--add-source`, `--allow-roll-forward`, `--disable-parallel`,
  `--ignore-failed-sources`, `--no-http-cache`, `--interactive`, `-?`, `-h`, `--help` from the
  tool and prints its own usage to stdout on a parse error. Documentation must never suggest
  `dnx claude-roslyn-lsp@x --version`; use `doctor`.

## Loading a real solution (WP4, 2026-09-10)

- **C48** **Without watched-file events, a solution that needs a restore never finishes loading at
  all.** Roslyn restores server-side when `obj/` is missing (C30) and logs `Restore complete`, and
  then stops: all three projects say "Successfully completed load", and
  `workspace/projectInitializationComplete` never arrives — observed for the full 120 s budget.
  (Note: C52 is a *second*, independent way to produce the same symptom — check it first, because
  its fix is one environment variable.) What unblocks the watcher case is the restore's own
  `project.assets.json` write coming back as a
  `workspace/didChangeWatchedFiles` event, after which Roslyn logs
  `[workspace/didChangeWatchedFiles] ... Completed (re)load of all projects` and reports
  initialization complete. So the `obj/` exception in the watcher's exclusion list is not a nicety
  for restore results: without it, a freshly cloned repository is a language server that answers
  nothing and says nothing about why. With the watcher working, the same solution loads in **3.9 s
  including the cold restore**.
- **C49** A 30-project solution (Quartz.NET) registers **433** `workspace/didChangeWatchedFiles`
  watchers: 342 rooted outside the workspace (the NuGet cache — C32's 113 for two projects scales
  with the reference graph) and **91 distinct directories inside it**. One `FileSystemWatcher` per
  directory is therefore not viable at that size; a single recursive watcher on the workspace root
  covers all 91 for one handle, and every registered pattern has to be re-based (`Quartz.csproj`
  becomes `**/Quartz.csproj`) to match from there.
- **C50** Claude Code sends **`textDocument/didOpen` for a file it edits, and no `didChange` or
  `didSave` afterwards** — one `didOpen` per session per file, carrying the text as it stands after
  the Edit tool wrote it. So the trigger that actually produces the diagnostics an agent sees is
  `didOpen`, not `didChange`: the debounce on `didChange` is for editors, and a `didOpen`-only
  client would get nothing at all from a bridge that only pulled on change.
- **C52** **MSBuild node reuse stalls the server-side restore, and therefore the whole load.**
  Roslyn restores by running `dotnet restore` and waiting for that child to finish. With node reuse
  on (the default), the restore leaves a persistent MSBuild node behind that inherits the child's
  output handle, so the pipe never reaches end of stream: every project logs `Restored ... (in
  90 ms)`, `Restore complete` never appears, no reload happens and
  `projectInitializationComplete` never arrives. Setting **`MSBUILDDISABLENODEREUSE=1` in the Roslyn
  child's environment** fixes it outright — the same solution then loads in 4.3 s. The failure is
  intermittent in the worst way, because it depends on whether a reusable node happens to be alive
  on the machine: the identical fixture loaded in 3.9 s an hour earlier and then failed six runs in
  a row. Anything that reproduces C48's symptom should check this first.
- **C51** Roslyn keeps registering capabilities for a long time after the workspace is loaded —
  516 live registrations were recorded on Quartz.NET, still arriving as `shutdown` was sent, and a
  session that ends early leaves a `$/progress` stream ending in `Cancelled`. A registration count
  is therefore not a readiness signal, and anything keyed off "registrations have settled" has to be
  debounced rather than waited for.

### OrchardCore: the first large-solution measurements (2026-09-10, WP4)

- **C53** **A 239-project solution loads in 20-46 s and registers 719 distinct watch directories.**
  `OrchardCore.slnx` on a warm cache: `workspace/projectInitializationComplete` at **20.4 s**
  (a colder run in the same series took 45.5 s), against a 120 s readiness default — so the default
  holds with roughly 2.5x headroom, and the recommendation is to leave it where it is. Roslyn
  registers **2,154-2,226** capabilities over the whole session and never stops (C51); the
  watched-file registrations are 719 distinct directories inside the workspace plus 279-394 rooted
  in the NuGet cache. Because the registrations arrive over the entire 45-second load, a watcher
  bridge that rebuilds per debounce rebuilds ~45 times; comparing the computed watch set against the
  applied one and generalising a project's own file name to `**/*.csproj` brings that to **4
  rebuilds and 27 skips**, which matters because every rebuild is a window in which an event is
  missed (C48).
- **C54** **Workstation GC cuts Roslyn's memory by about 3.4x on a large solution and costs load
  time.** OrchardCore with the shipped `System.GC.Server: true`: **1,931-2,089 MB** peak working set,
  **1,763-2,012 MB** peak private bytes. The same solution with `DOTNET_gcServer=0` in the child's
  environment: **577 MB** peak working set, **409 MB** peak private bytes, with the load at 28.2 s
  instead of 20.4 s. The variable reaches the child because the adapter passes its own environment
  through, so a Claude Code plugin can set it in its `env` block with no adapter change — which is
  exactly how this was measured. It is deliberately **not** the default: the trade is real in both
  directions, and 2 GB is only a problem on a machine where it is a problem.

| Measurement | Quartz.NET (30 projects) | OrchardCore (239 projects) |
|---|---|---|
| `doctor`, cold | — | 0.7 s, exit 0 |
| Solution chosen, score | `Quartz.slnx`, 40 | `OrchardCore.slnx`, 349 |
| `initialize` (answered by the adapter) | 22-30 ms | 24-63 ms |
| `projectInitializationComplete` | 5.9-6.2 s | 20.4 s warm, 45.5 s colder |
| `definition` (held by the gate) | 6.6 s | 20.9 s |
| `definition` (workspace loaded) | 1.3 s cold, 2 ms warm | 4 ms warm |
| `references` | 11.2 s (278 refs, 135 files) | 44.9 s (272 refs, 156 files) |
| `incomingCalls` | 3.6 s (225 calls) | 26.7 s (36 calls) |
| `shutdown` | 15-26 ms | 18 ms |
| Live capability registrations | 516 | 2,154-2,226 |
| Watch directories inside the root | 91 | 719 |
| Watch registrations outside the root | 342 | 279-394 |
| `FileSystemWatcher`s actually opened | 1 (collapsed) | 1 (collapsed) |
| Roslyn peak working set | 576-948 MB | 1,931-2,089 MB |
| Same, with `DOTNET_gcServer=0` | not measured | 577 MB (409 MB private) |

### Timings on Quartz.NET through Claude Code 2.1.267

Measured through the real client, with the published Native AOT binary as a `--plugin-dir` LSP
server. The client's `initialize` is answered by the adapter, not by Roslyn, which is why it is
three orders of magnitude faster than everything else.

| Exchange | Elapsed |
|---|---|
| `initialize` (adapter's own document, D45) | 22-30 ms |
| `projectInitializationComplete` (30 projects, warm cache) | 5.9-6.2 s |
| `textDocument/definition` (issued during the load, held by the gate) | 6.6 s |
| `textDocument/definition` (workspace loaded) | 1.3 s cold, 2 ms warm |
| `textDocument/prepareCallHierarchy` (held) | 6.8 s |
| `callHierarchy/incomingCalls` (225 calls) | 3.6 s |
| `textDocument/references` (278 references over 135 files) | 11.2 s |
| `shutdown` | 15-26 ms |

## Claude Code 2.1.267 client (spike S3, 2026-09-10)

- **C40** A plugin-provided `lspServers` entry for `.cs` loaded via `claude --plugin-dir <dir>`
  is launched, the `LSP` tool is exposed, and a server-side `-32601` error is relayed verbatim
  to the model ("LSP request 'textDocument/documentSymbol' failed for server
  'plugin:roslyn-smoke:roslyn': ..."). No binary patch (tweakcc) is needed; the user settings
  had `ENABLE_LSP_TOOL=1`, so whether that variable is still required was not isolated.
- **C41** With the official `csharp-lsp@claude-plugins-official` plugin enabled, it won the
  `.cs` extension over the `--plugin-dir` plugin ("Command 'csharp-ls' not found"): marketplace
  plugins register first. For a single run it can be sidelined without touching user settings:
  `claude --settings '{"enabledPlugins":{"csharp-lsp@claude-plugins-official":false}}'`.
- **C42** The plugin manifest is JSON: a Windows `command` path must use forward slashes or
  escaped backslashes; with `D:\Work\...` unescaped the manifest failed to load silently.
