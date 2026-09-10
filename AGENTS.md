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
**TBD** is a decision that has not been made, not one that has been forgotten. D14 and D24-D29 were
settled by the protocol work package (WP2) against the wire facts in
[`docs/roslyn-protocol-facts.md`](docs/roslyn-protocol-facts.md). D20, D21 and D60-D69 were settled
by the MCP work package (WP5a), which also froze the tool table below; **D74-D84** by WP5b, which
put a real Roslyn behind those tools and, in doing so, found five things about the pinned server
that a scripted backend cannot show (C55-C62). **D85-D94** were settled by WP9, which made the two
verbs share one Roslyn per solution and filled in D23.

The four rows D16-D19 reserved were settled by WP4 and are written out as **D50-D59**, which is
where the argument lives; the reserved rows point there rather than restating it in two places.

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
| D14 | **The `initialize` document the adapter sends Roslyn is authored, not derived from the client's.** Claude Code's own document is wrong in both directions: it declares things the adapter has to do itself (it refuses `client/registerCapability` with `-32601`, and answers `workspace/configuration` only when a `settings` block happens to be present) and omits everything the adapter needs Roslyn to do — dynamic diagnostics, watched files, work-done progress. Forwarding it would switch off exactly the machinery this project exists to supply. So `RoslynInitializeParams` is written here: configuration, workspaceFolders, dynamic `didChangeConfiguration` and `didChangeWatchedFiles` with relative patterns (without which Roslyn registers no watchers at all and has no in-process fallback, C34), symbol, diagnostics refresh, `workspaceEdit` with `documentChanges` and create/rename/delete (C21), synchronisation with `didSave`, hover in markdown, definition/typeDefinition/implementation with **`linkSupport: false`** — a `LocationLink` renders as nothing in a client that only knows `Location`, and navigation crosses verbatim — references, hierarchical documentSymbol, callHierarchy, rename with prepare, codeAction with `dataSupport` and `resolveSupport: ["edit"]`, formatting, rangeFormatting, dynamic `diagnostic` with `relatedDocumentSupport`, `window.workDoneProgress`, and `general.positionEncodings: ["utf-16"]`. Semantic tokens, inlay hints and code lens are **absent**, so Roslyn never registers or refreshes them. Two additions to the plan's sketch: `signatureHelp` and `completion`, because the adapter advertises both providers to its own client (D25) and a provider promised in one direction but never declared in the other is the exact asymmetry that produces empty answers with no error anywhere. `workspace.applyEdit` is deliberately **not** declared — the v1 adapter writes no files, D21 puts that in the MCP half — and Roslyn's `workspace/applyEdit` is handled defensively rather than invited. |
| D15 | **How the adapter talks to Roslyn — a named pipe by default, stdio as the fallback.** Roslyn *connects* to a pipe the adapter created (C8), so the `NamedPipeServerStream` (`InOut`, one instance, byte mode, `Asynchronous \| CurrentUserOnly`) has to exist before the process is started; the name is `claude-roslyn-lsp-<pid>-<8 hex>` and the connect budget is 30 s. The pipe is preferred because on it the protocol is a channel nothing else holds a handle to — the pinned build writes a 646-byte banner to its stdout in pipe mode (C7), which under `--stdio` would have been protocol corruption. `CLAUDE_ROSLYN_LSP_TRANSPORT=stdio` exists for environments with no usable named pipe. Details in D37 and D38. |
| D16 | **The diagnostics bridge: pull from Roslyn, push to the client.** Settled as **D52** and **D53**. |
| D17 | **Workspace-wide diagnostics for files that are not open are opt-in.** Settled as **D54**. |
| D18 | **File watching is on by default, with an opt-out.** Settled as **D55** and **D56**. |
| D19 | **A Roslyn crash is absorbed, not forwarded.** Settled as **D57**. |
| D20 | **The MCP tool table and its annotations.** Settled by WP5a; the table is *The MCP tool surface* below and the arguments are **D60-D69**. camelCase verb-noun names, 1-based positions, workspace-relative paths, structured content, and a `preview` flag on every mutating tool. |
| D21 | **The MCP server applies its own edits.** Settled by WP5a as **D61** and **D62**. It is the LSP *client* in that direction, so it writes files itself, preserving BOM and line endings, and then tells Roslyn what changed — including C33's synthetic project event, which WP5b's engine owns (D75). |
| D22 | **NuGet is the plugin's launch channel; the AOT archives are everything else's.** The plugin runs `dnx claude-roslyn-lsp@{version}` for both servers — no download step, and an SDK is required for C# work anyway — while file-based clients point at a Native AOT binary from GitHub Releases. The package is pushed by **trusted publishing**: the workflow exchanges its GitHub OIDC token for an API key that lives minutes, so no NuGet API key exists in this repository or in its secrets. The exchange is C# inside the build (`build/Build.Publish.cs`), not a marketplace action. |
| D23 | **One Roslyn per solution, shared by whichever verb started first.** Settled by WP9 and written out as **D85-D94**. Claude Code starts the plugin's `mcp` server at session start and its `lsp` server lazily, so a session that uses both used to load one solution twice and pay for it twice in memory — and Roslyn's own `--daemon` shares a process but not a workspace (C29), so the sharing had to live here. Whichever process gets there first publishes itself in `<home>/sessions/<key>.json`, serves a named pipe and multiplexes the other onto its backend; the other attaches and starts nothing. The seam turned out to be exactly the one WP5b predicted: both halves already take an `IRoslynConnectionFactory` and neither asks what is on the other end of it, so attaching is one new implementation of a two-member interface rather than a second lifecycle. `getWorkspaceStatus` answers `engine: "attached"` with the host's pid (D84), measured on the fixture at **0.0-0.1 s to attach** against 2.4-4.6 s to load, and one 296 MB process instead of two. |
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
| D44 | **The adapter terminates both sessions; it is not a relay with patches.** It is a *server* to Claude and a *client* to Roslyn, with two independent handshakes and state of its own. A byte relay cannot answer Claude's `initialize` before Roslyn has answered its own, cannot answer the ~140 registrations and 80 configuration sections that Claude refuses, and has nowhere to keep a document mirror or a readiness queue. Two read loops, one `OutboundQueue` per side (a channel, because a semaphore serialises writes but does not order them), and every shared structure owns its own `System.Threading.Lock`. |
| D45 | **The capability document the adapter advertises is authored and static, and `initialize` is answered immediately.** Claude Code holds `initialize` open indefinitely if it is not answered, and Roslyn takes the better part of a second to answer its own (C31) — so deriving one from the other would make the client's startup wait on the backend's. Worse, Roslyn advertises `semanticTokensProvider`, `codeLensProvider`, `inlayHintProvider` and `_vs_onAutoInsertProvider` statically whether or not anyone asked (C26), and advertising a capability is a promise to answer the requests it enables. The list is therefore exactly what the mediation carries: hover, definition, typeDefinition, implementation, references, documentSymbol, workspaceSymbol, callHierarchy, rename (prepare), codeAction (resolve; quickfix + refactor), formatting, rangeFormatting, signatureHelp, completion, and `textDocumentSync` `{openClose, change: 1 (D13), save.includeText: false}`. `SmokeTest` asserts the three absent providers on a published binary talking to a backend that advertises all three. |
| D46 | **The readiness gate blocks; it does not bounce.** A request issued before `workspace/projectInitializationComplete` is answered by Roslyn with an *empty successful result*, never an error (C27) — so an ungated client silently reports "no definition found" for the first several seconds of every session, which is indistinguishable from a correct answer and teaches the agent that the server is useless. The obvious alternative, `-32801 ContentModified` plus a client retry, does not survive contact with Claude Code: it retries about three times inside three and a half seconds, and a load takes 2.5-9 s (C31). So requests queue in arrival order and are released **synchronously, in that order** — continuation scheduling gives no ordering guarantee — with `CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS` as a floor rather than a promise: after it the gate passes everything through, because a partially loaded workspace answering some questions beats a server that has stopped answering. A `$/cancelRequest` for a held request dequeues it and answers `-32800`; a failed backend answers `-32603` naming `doctor`; a client that is holding requests is told every ten seconds through `window/logMessage`, which is the only channel it renders. Notifications flow from `RoslynInitialized`, not from `ProjectsLoaded`. |
| D47 | **Pass-through is a three-segment id rewrite of the original bytes, and every outbound request is renumbered.** `LspMessageScanner` makes one depth-1 `Utf8JsonReader` pass to find the message's own `id` token — never a nested one, which matters because Roslyn puts `id` members inside `data`, `item` and `TextDocument` payloads (C18, C24) — and forwarding copies everything before the token, the new token, and everything after. Reserialising would reorder members, renormalise numbers and re-escape strings whose exact shape the peer has already been told, and it would put this repository in the business of modelling every result Roslyn returns. Renumbering is not optional: two peers that have never met both count from one, so an unrewritten id lets a client request and an adapter request collide, and one answer is then delivered as the other's — a *wrong answer*, not an error. One counter serves both kinds of outbound request, which makes the collision structurally impossible rather than merely unlikely; there is one `IdMap` per direction, because Roslyn asks the client questions too. |
| D48 | **The adapter answers `workspace/configuration` itself, with agent-tuned defaults.** The section names contain a pipe (`csharp\|background_analysis...`), which Claude Code's dotted settings lookup cannot address at all, and it only answers the request when a `settings` block happens to be present in the plugin configuration. Precedence is client settings → `CLAUDE_ROSLYN_LSP_OPTIONS` → these defaults → `null`. The defaults assume an agent rather than a human with a screen: both diagnostic scopes at `openFiles`, because `fullSolution` is what makes a large solution unusable and WP4's opt-in mode raises it deliberately (C14); automatic restore **on**, because Roslyn restores server-side and never asks (C30) and a freshly cloned repository would otherwise load with no references; decompiled-source navigation **on**, so definition into a BCL symbol reaches real source (C25); and reference-assembly symbol search, every `csharp\|inlay_hints.*`, every `csharp\|code_lens.*` and auto-insert **off**, because the adapter advertises none of those (D45) and the work would be discarded. The two families are matched by prefix rather than enumerated, so a Roslyn that adds a hint kind cannot quietly switch it back on. |
| D49 | **The scripted fake Roslyn backend ships inside the product binary, behind a hidden verb.** `Testing/FakeRoslynServer` reproduces a real 5.12 startup from the WP0 wire logs — the registration batches with their real diagnostic identifiers and their shuffled order (C11), both configuration batches (C41), the watcher shapes that make collapsing non-trivial (C32), the progress stream, the log lines, `projectInitializationComplete` — and, crucially, answers navigation **empty until the workspace loads**, exactly as the real server does (C27). Keeping it in the product rather than in the test project is what lets `SmokeTest` prove the mediation against a published Native AOT binary on every release RID with no download: `lsp --smoke` runs it in-process, and `CLAUDE_ROSLYN_LSP_FAKE_BACKEND=child` spawns `<self> fake-roslyn` and speaks to it over stdio, which is the only leg that proves the process plumbing (C7 is a real banner on a real stdout). The cost is a few kilobytes of fixtures in the shipped binary; the alternative is release RIDs whose mediation nobody has ever run. |
| D50 | **The launcher is the default backend, and every fake is behind an explicit opt-in.** `Roslyn/LaunchedRoslynFactory` is the one class that joins WP3's acquisition chain to WP2's connection seam — locator, .NET host, launcher, one framed byte channel — and it is what `lsp` uses unless `--smoke` or `CLAUDE_ROSLYN_LSP_FAKE_BACKEND=child` says otherwise. Neither half had to change to be joined, which is what the seam was for. The resolution is cached after the first success, because a relaunch (D57) changes the process and not where the server lives; the `dotnet` host is resolved once (D31); and download progress goes to the client as a `window/logMessage` every 8 MB, because a first run spends a minute before anything can answer and a client that is told nothing shows a server that hung. |
| D51 | **Acquisition failure is a live state with two messages, never a crash.** Offline, no .NET 10 runtime, a proxy serving HTML where a nupkg should be, a hash that does not match the pin: all ordinary, and all arriving as a `RoslynAcquisitionException` that the session turns into `ReadinessState.Failed`. The adapter *stays alive* — the handshake stands and every request is answered `-32603` with a sentence naming `doctor` — because a process that exits during startup leaves its client with a dead pipe and no channel to be told why (stdout **is** the channel). The client gets one `window/showMessage` (Error, once) and a `window/logMessage`: the first is what a client surfaces, the second is what it files where somebody looking for the reason will find it, and sending either on every subsequent failure would train the reader to dismiss them. |
| D52 | **The diagnostics bridge pulls with no identifier, keeps one pull in flight per URI, and tags every publish with the mirror version.** One pull with no `identifier` returns the union of every source (C9) — one round trip instead of ten for the same set — and `previousResultId` per URI (C12) makes an idle session silent, because an `unchanged` report publishes nothing. One pull in flight with a dirty flag, because Roslyn takes 1.3-1.7 s for the first pull and ~90 ms afterwards (C17) and a queue would spend the whole budget answering about text that no longer exists. The version is the mirror's at pull start, which is the client's own mechanism for discarding a set an edit has overtaken. Triggers: `didOpen` immediately (deferred until the workspace loads, C28), `didChange` on a 400 ms trailing debounce, `didSave` at once, `projectInitializationComplete` for everything open, `workspace/diagnostic/refresh` after 500 ms, a project file changing on disk after 1 s (C33's re-evaluation takes 1.6-2.1 s, so pulling sooner reports the world as it was). `-32801`/`-32800` earns exactly one retry after 300 ms; the world settles or it does not, and a retry loop against a fast typist never returns. |
| D53 | **The severity floor, the `Unnecessary` rule and the fifty-per-file cap are a delivery policy, not taste.** Claude Code renders diagnostics as a text attachment on the next turn and cuts it, so every entry kept is an entry that pushed another out. Warning floor by default (`CLAUDE_ROSLYN_LSP_DIAGNOSTIC_MIN_SEVERITY` lowers it): Hint and Information are style, and the agent did not ask about style. `Unnecessary`-tagged non-errors go regardless, because IDE0005 arrives at severity 4 *at position 0:0 for the whole using block* (C16) — the least useful entry and the one most likely to read as "something is wrong at the top of this file"; an unnecessary-tagged *error* stays, being a real one with a fade hint. Tags at or above 2147483640 are stripped, because those are Visual Studio's private values (C16) and several clients treat an unknown tag as a reason to drop the whole diagnostic. Then severity, then line, then fifty. Each surviving diagnostic is copied member-by-member rather than modelled, so an opaque `data` payload round-trips untouched. |
| D54 | **Whole-solution diagnostics are opt-in, compile-errors-only, and cut to ten files of five.** `CLAUDE_ROSLYN_LSP_WORKSPACE_DIAGNOSTICS=errors` is the only accepted value and it names the whole contract. A closed file is unreachable at any lower scope (C13) and `workspace/diagnostic` only reports one when the compiler scope is `fullSolution` (C14) — the setting that puts every project's compilation in memory in a child process that is already the largest thing on the machine during a Claude session. So the scope is raised by that variable and by nothing else, the analyser scope stays at `openFiles`, and the output is filtered hard: C15 puts `obj/**/*.cs`, `.csproj` entries and one copy per target framework in the report, and passing that through would fill the attachment with generated files and duplicates. What survives is errors only, ten files, five each, with the saved file's own project first. Published URIs are remembered and cleared with `[]`, because a client keeps the last set for a URI forever and a closed file that was fixed would otherwise stay broken for the session. |
| D55 | **File watching is on by default, and switching it off withdraws the capability rather than going quiet.** Roslyn has no in-process watcher and no fallback (C34), and Claude Code answers every one of its ~140 registrations with `-32601`. `CLAUDE_ROSLYN_LSP_FILE_WATCHER=off` therefore drops `didChangeWatchedFiles` from the document the adapter sends Roslyn, so Roslyn registers nothing: declaring it and then not delivering would be the worst of both, a server that stands up 140 registrations and waits to be told about changes that never come, which looks exactly like a working watcher until an answer turns out to be stale. On Linux the limits that bite are `fs.inotify.max_user_instances` (128 by default, and one per `FileSystemWatcher`) and `fs.inotify.max_user_watches`; the bridge reports each refused directory by name and says which sysctl to raise, because a stale workspace is otherwise invisible. |
| D56 | **The C33 rule is the watcher, and past 64 directories it becomes one recursive watcher on the root.** A `Created` event for a new `.cs` file does *nothing* — what makes Roslyn re-evaluate is a `Changed` for the owning `.csproj` (C33) — so every appearance or disappearance of a `.cs` file emits a synthetic one for the nearest project file above it, collapsed with the rest of the 200 ms batch. Bases outside the workspace root are dropped (342 of Quartz.NET's 433 are the NuGet cache, C49), `bin obj .git node_modules .vs artifacts TestResults` are excluded, and the one exception is `project.assets.json` under `obj` — which C48 shows is not a nicety: without it a freshly cloned repository restores server-side and then never finishes loading at all. Open documents are excluded, because the client already owns their text and re-reading the saved copy would discard the live buffer. A watcher error or overflow reports every project file under the root as changed, since once events are lost there is no knowing which. Past 64 kept directories the per-directory scheme is abandoned for one recursive watcher on the workspace root with every pattern re-based, because Quartz.NET's 91 directories under a cap of 64 meant 27 of them silently unwatched. |
| D57 | **A dead Roslyn gets two goes in ten minutes, and the client never learns it happened.** Claude Code's own restart budget is three and spending it restarts *this* process, throwing away the client session, the document mirror, the readiness state and every answer in flight. Roslyn dying is not that kind of failure: everything needed to rebuild the backend is held here. So the gate closes (a request arriving in the gap is *held*, not answered empty by a backend that has not loaded — C27 arriving late), the mirror replays one `didOpen` per document at its latest text, the solution re-opens, and the diagnostics result ids are forgotten because they are tokens in a dead server's memory. Two restarts per ten minutes: an accident comes back and works, and a Roslyn that dies three times on the same solution is deterministic, so a fourth attempt would turn one broken session into an infinite loop that also holds every request in it. Connect attempts are **generation-numbered**, because a backend that dies during its own handshake produces both a relaunch decision and an abandoned `initialize` that throws, and without the generation the second races the first and fails a session the supervisor had just decided to save. |
| D58 | **Call-hierarchy answers are de-duplicated; it is the only result the adapter rewrites.** A multi-targeted project reports every call once per target framework (C24), which `workspace/symbol` and `rename` collapse themselves and call hierarchy does not. For an agent that is not cosmetic: a model asked "who calls this" reports twice the number, and a walk down the tree does every branch twice — eight times the work for a three-level hierarchy over two frameworks. The key is (uri, selectionRange), because `selectionRange` is the identifier itself and `data` is precisely what differs; survivors are copied verbatim so the opaque payload the next request must round-trip is untouched; an entry whose key cannot be read is kept, because dropping something this code did not understand would turn a shape change in Roslyn into a silently shorter answer; and an answer with nothing to remove keeps its original bytes. |
| D59 | **The fixture does not compile, the live tests are one method, and the LiveTest target is CI's only real Roslyn.** `tests/fixtures/HelloSolution` is shaped like somebody else's repository — three projects, one multi-targeted, a real xunit test project — with its own empty `Directory.Build.props` and a `Directory.Packages.props` that turns CPM off, so this repository's `TargetFramework`, `TreatWarningsAsErrors` and package budget cannot reach it. `Program.cs` contains `int x = "s";` on purpose: without a real error the diagnostics bridge could be a no-op and every test would still pass. The live tests run **one** session in phases rather than several `[Fact]`s over a shared fixture, because two of the phases (killing Roslyn, shutting down) destroy what the others need and xunit does not order tests within a class. The `LiveTest` build target is the same argument at the release boundary: every smoke leg speaks to a scripted backend and proves nothing about whether Microsoft's server still answers what this adapter believes it answers. It earned its place immediately — the WP4 run that added it found C48, which every scripted test had passed. |
| D60 | **The tool layer talks to an interface, and the fake lives in the test project.** `Mcp/Engine/IRoslynEngine` is the whole of what the MCP half needs from a running Roslyn, expressed at the LSP level: seventeen members, zero-based positions, `data` blobs passed through as `JsonElement`. Almost all of this product's judgement — symbol addressing, per-TFM de-duplication, code-action ids, diagnostic filtering, the edit applier, the preview cache — lives *above* that line, and none of it needs a quarter-gigabyte child process to be exercised; `FakeRoslynEngine` answers with payloads transcribed from the WP0 spikes and the whole tool suite runs in two seconds. The seam is at the LSP level rather than at a domain level on purpose: a `FindReferences(symbol)` method would put the interesting decisions on the far side of it, where no test can reach them. Its shapes are a **third** source-generated context, `Mcp/Engine/RoslynEngineJsonContext`, chained with neither of the other two — D7's rule is about chaining, not about counting, and merging this into `Protocol/LspJsonContext` would give the mediation and the MCP half one file to fight over. |
| D61 | **Reading and writing a source file is a component, not two `File` calls.** `Edits/TextFileCodec` decodes bytes to a string and reports how to write them back; `Edits/TextOffsets` turns LSP positions into UTF-16 offsets; `Edits/WorkspacePathGuard` decides what may be written at all. A round trip through `File.ReadAllText`/`WriteAllText` silently drops a UTF-8 mark and re-encodes a UTF-16 file, which turns a one-word refactoring into a diff that touches every line — invisibly, because the code still compiles. The guard is the other half: an edit is an instruction from another process, and Roslyn hands out URIs outside the workspace as a matter of course (C25's `MetadataAsSource` temp files), so the rule is `file:` scheme plus a *resolved* path under the root, compared case-insensitively on Windows and macOS and case-sensitively elsewhere, the way the file systems themselves do. |
| D62 | **`preview` runs the identical planning pass and simply never writes; the apply that follows reuses what was previewed.** Planning and applying are separate methods over one `WorkspaceEditPlan`, so a preview's diff is not a description of a different operation. `Edits/EditCache` then remembers the resolved edit under a key of tool plus answer-changing arguments — `preview` itself deliberately excluded, or the two calls would never meet — stamped with every read file's last write time and length. If any of them moved, the entry is dropped and the apply re-resolves: that turns "silently applied a different edit than the one that was approved" into "slightly slower", which is the right way round. The diff is budgeted (`maxDiffLines`, 200) because a solution-wide fix-all can rewrite two hundred files, and what is cut is **named** — "… 14 more hunks" is information, silence is not. Every applied result carries one fixed sentence, *Files were changed on disk; re-read a file before editing it.*, because Claude Code's own `Edit` tool refuses a file that changed since its last `Read` and the refusal is undiagnosable from its message. |
| D63 | **Symbols are addressed by name or by `path:line:col`, and every number a model sees is 1-based.** The name form is what fixes the behaviour this product exists to fix — a name survives an edit that moves the declaration, and a model that can write `resolveSymbol("IScheduler.Start")` never greps for a line number first. The position form is what makes these tools compose with Claude Code's own `LSP` tool, which reports and consumes positions. `Mcp/SymbolAddress` parses the position form from the **right**, because a Windows drive letter is a colon too. A dotted name matches as a dot-segment suffix, so `IScheduler.Start` matches `Quartz.IScheduler.Start` and `Scheduler.Start` does not; the qualifier is checked against Roslyn's container text (`in IScheduler (project …)`), because `workspace/symbol` searches simple names and reports no namespace. LSP counts from zero and every editor, compiler error and stack trace in the C# world counts from one, so the conversion happens here and in the result mapping, and nowhere else. |
| D64 | **A code action's id is derived, and the `Fix All:` entries are folded away.** An action has no identity on the wire — opaque `data`, a title in a natural language, a list regenerated per request — but `getCodeActions` and `applyCodeAction` have to be two calls about the same thing. So the id is eight hex characters over `UniqueIdentifier` and `CodeActionPath` (C18), the two members that say *which fix* rather than *where*: stable across calls and processes, distinct between siblings that differ only in path, short enough for a model to retype. Roslyn also offers `Fix All: <title>` as a *separate* action carrying `FixAllFlavors`, so three real choices arrive as five rows; since `codeAction/resolveFixAll` accepts the plain action's own `data` (C20, S4b), the fix-all entry becomes `fixAllScopes` on the plain action and disappears. An orphan `Fix All:` entry with no plain twin is kept, so nothing is ever silently dropped. Nested actions are read out of `command.arguments[0].NestedCodeActions`, not out of `data`, and each child gets its own id. |
| D65 | **Fix-all goes through `codeAction/resolveFixAll`, never through the marker command.** `executeCommandProvider.commands` is empty (C19) — `roslyn.client.fixAllCodeAction` is a client-side marker, not something to execute — and `codeAction/resolve` on a `Fix All:` entry returns no `edit` at all (C20). The scope is mandatory and case-sensitive: a lowercase spelling fails with "Sequence contains no elements" and an omitted one with an `InvalidCastException` (S4b), so the enum is `Document\|Project\|Solution` and the tool argument that maps onto it is validated rather than forwarded. |
| D66 | **A workspace that is not ready produces a `status`, never a hang and never an error.** Every result record carries `status` and `note` in the same place, so "is this a real answer" is one property lookup across ten tools. Three behaviours were available: block, which is what the LSP half does because a language client has nowhere else to be (D46); fail, which teaches a model the server is broken; or answer with a status. An MCP call is a turn of a conversation — a model told "still loading, 3 of 8 projects" can do something else and come back, and a model told nothing for two minutes cannot. Every tool gates on `EnsureReadyAsync(CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS)` first and asks Roslyn nothing until it passes, because a question asked early comes back empty rather than wrong-looking (C27). |
| D67 | **`getDiagnostics` is two completely different requests wearing one name, and it corrects what comes back.** File scope goes through `textDocument/diagnostic` with no `identifier` (C9), which needs the document *open* — a closed file answers zero items whatever the scope (C13) — so it opens it with the bytes on disk and closes it again **unless it was already open**, because closing a document the LSP half is mirroring would revert Roslyn's view to disk. Project and solution scope go through `workspace/diagnostic`, which reports closed files only under a `fullSolution` compiler scope (C14), so the scope is raised for the call and the answer is then cleaned of the three things C15 says arrive uninvited: `obj/**` and `bin/**` sources, `.csproj` entries, and one copy per target framework. Analyzer diagnostics are opt-in and the floor is `warning`, so the default answer is "what stops this compiling" rather than everything an IDE would underline; an explicit `ids` list is the caller being specific and overrides that. Every answer carries the design-time caveat, and a workspace-scoped one also says that open files were skipped. |
| D68 | **The workspace root is the process's working directory.** Every MCP client — Claude Code, Codex, Gemini CLI, Cursor — launches a stdio server with the workspace as its current directory, and no protocol field carries a root. That directory bounds *writes*, not analysis: a solution configured elsewhere still loads, and `getWorkspaceStatus` reports its real path so the discrepancy is visible rather than mysterious. |
| D69 | **A build with no backend registers `NotWiredRoslynEngine`, which fails with one sentence.** The MCP handshake and `tools/list` must work with no Roslyn in the picture — that is the leg `SmokeTest` drives against a published Native AOT binary on every release RID, and it is also what a client sees in the seconds before anything is launched. Registering nothing would make the container throw at the first call with the SDK's generic "An error occurred"; this names the problem and the command that diagnoses it. `EnsureReadyAsync` is the one member that does not throw — it answers `Failed`, a state every tool already handles, so the nine gated tools return a status object and `getWorkspaceStatus` answers correctly instead of failing. WP5b replaces the factory, and nothing above the seam changes. |
| D74 | **The model never sees a crash — including one that happens mid-request.** D57 absorbs a backend that dies *between* requests; a backend that dies *holding* one used to answer it `-32603`, which put the crash in front of the model as a failed tool call seconds before the relaunched server could have answered it correctly. So `IdMap` keeps a forwarded request's original bytes, and a backend death that the supervisor decides to restart puts every such request **back into the readiness gate** — closed first, so nothing is replayed into the corpse or into a server that has not loaded the solution (C27) — and re-forwards it under a fresh id when the gate reopens. The client is still waiting on its own id and gets a real answer under it. Only adapter-originated requests fail: a diagnostic pull carries a `resultId` that died with the old process and is re-issued on the bridge's own schedule anyway. When the supervisor gives up, the held requests are released as `Failed`, which is `-32603` with a `doctor` hint — the state a user can act on. Safe because every request this adapter forwards is a *read*: the v1 LSP half writes no files (D21 puts edits in the MCP half), so replaying one cannot apply anything twice. Found by CI on Linux, where the kill lands after the forward far more often than on Windows (C56). |
| D75 | **The MCP half composes the mediation's components; it does not share its session.** `Mcp/Engine/OwnedRoslynEngine` reuses every class that carries a decision — `ServerEndpoint` and the authored `initialize` in it (D14), `ReadinessGate` (D46), `ConfigurationResponder` (D48), `RegistrationTracker`, `ProgressTracker`, `ServerRequestHandler`, `WorkspaceOpener`, `RoslynSupervisor` (D57), `FileWatchBridge` (D55-D56), `DocumentMirror`, `IdMap` (D47), `LaunchedRoslynFactory` (D50), `SolutionDiscovery` (D39-D42) — and writes only the lifecycle glue that joins them. The alternative on offer was extracting a `BackendSession` shared with `AdapterSession`, and it was declined on the evidence: that class's glue exists to *serve a peer* — two id maps, byte forwarding with an id rewrite, a queue of somebody else's requests, call-hierarchy de-duplication on the way back — and none of it exists where the caller is a method on the object. A shared session would mean inventing an abstraction for "the peer" whose only second implementation is "there isn't one", and putting that refactor underneath the mediation WP4 spent a live session getting right. WP9's shared engine — one host, one attach client — is where the two lifecycles genuinely merge, and that is the seam worth cutting along. The three small helpers that *were* duplicated are shared instead: `Adapter/RoslynResponses` completes a response, reads an error code, and builds a `$/cancelRequest`. |
| D76 | **The `mcp` verb starts Roslyn from `notifications/initialized`, not from the first tool call.** Every MCP client starts its servers when the session starts and may not call a tool for minutes, so launching at the handshake spends the 3-45 s load (C31, C53) during time the user is already spending; a first tool call then answers immediately instead of being the thing that pays. A session that never asks a C# question pays for a child process it did not need, and that is the trade taken deliberately: the alternative makes the *first* question the slow one, and the first question is the one that decides whether the model uses these tools again. Tools still gate on `EnsureReadyAsync(CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS)` and answer `loading` rather than hanging (D66). |
| D77 | **Changing a Roslyn setting is a round trip, and the answer outranks every standing one.** `ConfigurationResponder` gains runtime overrides that sit *above* the client's own settings, the environment and the defaults, because an override is set for the duration of one call that cannot work without it — a solution-wide pull needs `fullSolution` (C14), `formatCode organizeUsings: true` needs the format option — where every other layer is a standing preference. Losing to a static setting would make those calls answer emptily and successfully. The change is a `workspace/didChangeConfiguration` notification, and the engine then *waits for the `workspace/configuration` request Roslyn issues in response*, because that pull is the only observable moment at which the value is in effect. Two seconds, and a build that stopped re-pulling gets a logged warning rather than a failed call. |
| D78 | **The project list is read off the solution file, not asked for.** Roslyn's custom methods (C39) have no "describe the workspace" request, and the only thing that names a project is a log line the default child level does not emit (D37). `Mcp/Engine/WorkspaceProjects` therefore reads the `.slnx`/`.sln` for its project entries and each project file for a literal `TargetFramework(s)`; `Mcp/Engine/WorkspaceLoadTracker` layers on what Roslyn *does* report — the progress stream's `Loading N project(s)` and, when the level is raised, the per-project outcomes. The target frameworks are a text scan and a best effort, exactly as D41's project count is: a project that gets its TFM from a `Directory.Build.props` reports none, because an empty list reads as "not known" where a wrong list would read as a fact. Evaluating a project file properly means MSBuild, which hard rule 1 and the package budget both refuse. |
| D79 | **Workstation GC is the default for the child, in both verbs.** Roslyn's payload ships `System.GC.Server: true` (C44), and C54 measured what that costs on OrchardCore: 1,931-2,089 MB of working set against **577 MB** for workstation, for a load of 20.4 s instead of 28.2 s. WP5b re-measured it on Quartz.NET (300-343 MB, C60) and on OrchardCore through the MCP half (439-473 MB, C61). The eight seconds are paid once by somebody who is waiting for a language server to start and expects to; the one and a half gigabytes are paid by every other process on the machine for as long as the session lasts — and this product exists because a Claude session is already running a model's tool calls on the same box. So `DOTNET_gcServer=0` goes into the child's environment unless `CLAUDE_ROSLYN_LSP_GC=server` asks otherwise, an explicit `DOTNET_gcServer` in the environment wins over both and is never overwritten, and `doctor` prints which of the three is in effect. |
| D80 | **The `mcp` verb has the same backend vocabulary as `lsp`, plus one value for "none".** `mcp --smoke` runs the scripted backend in this process, `CLAUDE_ROSLYN_LSP_FAKE_BACKEND=child` runs it as a child, and `=none` registers `NotWiredRoslynEngine`. That last one used to be what happened by default, which made `SmokeTest`'s claim about D69 accidental; with the eager start (D76) it would also have become a 70 MB download on five release runners, so it is spelled out. `SmokeTest` now drives **two** MCP legs: `=none`, which proves the handshake and `tools/list` do not depend on a backend at all, and `--smoke`, which proves that starting one from the handshake puts no byte on stdout — the channel it would corrupt. |
| D81 | **The MCP live tests are one method in phases, with their own copy of the fixture.** D59's argument, restated where it applies again: the phases destroy each other's preconditions — the fix-all deletes the using the code-action phase asks about, the rename changes the symbol the navigation phases resolve — and xunit does not order tests within a class. The copy is private to this collection because `AdapterLiveFixture`'s is edited differently and collections run in parallel. One file in the copy is rewritten as **CRLF with a BOM** on the way in, because `.gitattributes` forces `*.cs` to LF on checkout and the property worth asserting is precisely that a rename writes a file back the way it found it (D61). The formatting phase formats a file written badly on purpose, because "the second run changes nothing" is satisfied by a first run that also changed nothing. |
| D82 | **`workspace/diagnostic` needs the analyzer scope raised as well as the compiler one, and a fix-all looks for its site in the cheapest place that can answer.** C14's second sentence, which cost a live run: with only `dotnet_compiler_diagnostics_scope` at `fullSolution`, a closed file's compiler errors are reported and its IDE and CA diagnostics are not — so `fixDiagnostics IDE0005 scope: "solution"` answered "no occurrence of IDE0005" on a solution that has one. `IRoslynEngine` therefore has a second scope member, and `DiagnosticQuery` raises it only when `includeAnalyzers` or an explicit `ids` list says the caller wants analyzers, because every analyzer over every file is the expensive half and D48's standing answer stays `openFiles`. Raising it is still not *sufficient* on a busy machine (C57), so `fixDiagnostics` no longer depends on it: given a `path` it finds the site with a **document** pull, which always carries analyzer diagnostics, whatever scope the fix itself is asked to reach. The site and the reach are two different questions and were being answered by one call. |
| D83 | **A workspace diagnostic pull is bounded, falls back to the last report, and is confirmed after a scope change.** Three behaviours, one method, all of them C57 and C58. Bounded, because the pull is a long poll and an unbounded one makes a second `getDiagnostics scope: "solution"` a call that never returns. Falling back to the previous report rather than to nothing, because Roslyn holding the request *means* "nothing has changed since I last told you". And sampled again for a short budget after a scope change — waiting for Roslyn's own `workspace/diagnostic/refresh` where it sends one, and stopping as soon as the report is no longer the one the first pull returned — because the change does not reach the pull that follows it and a pull already waiting is not woken by it. The budget is eight seconds and deliberately short of what a contended machine needs: sampling for thirty did not rescue that case either, and it made every call where the first pull was already right thirty seconds slower for nothing. The one case that is not a fallback is the **first** pull of a session: with nothing cached, a timeout throws a sentence naming `scope: "project"` and `scope: "file"` rather than answering with an empty list, because an empty list would say "nothing was found" about a solution nothing had looked at — which is the failure this product exists to remove. That path is not hypothetical: OrchardCore's 239 projects do not finish a first solution-wide pass inside 120 s (C61). |
| D84 | **`getWorkspaceStatus` reports how this server got its Roslyn.** `engine: "owned"` means this process launched one of its own; `engine: "attached"`, with `hostProcessId` beside it, means it is using the one another `claude-roslyn-lsp` process launched for the same solution (D23). Reporting the distinction is the point: whether a session running both servers has one Roslyn or two is the difference between 0.3 GB and 0.6 GB on a small solution and rather more on a large one, and this is where that stops being a paragraph in the README and becomes something an answer says. `processId` still names Roslyn itself in both cases, so the two numbers answer two different questions. |
| D85 | **The rendezvous is a file per solution under the adapter's home, and nothing else.** Two processes have to find each other with no coordinator and no protocol between them, and the only things they are known to agree on are the solution path and the home directory (D27). So `<home>/sessions/<sha256(normalised solution)[..16]>.json` carries the host's pid, this binary's version, the transport, the pipe name, the solution and a timestamp — readable by a human, needing no daemon, and surviving a reboot for exactly as long as it takes the next reader's pid check to notice. Sixteen hex characters because it is a rendezvous on one machine rather than a security boundary, and a key somebody can copy out of an error message is worth more than the other forty-eight. The version is checked on every read: a 0.1.0 host and a 0.2.0 attacher would speak the same LSP right up to the moment one of them changed what it fans out, and that failure would be a wrong answer rather than a refused connection. |
| D86 | **A lock file, the pipe up before the file is written, and the launch outside the lock.** D29's argument again — a named mutex is per-session on Windows and absent across containers sharing a mounted home — for the race D29 was already about: the plugin starts both servers at the same instant, so "read the file, and write it if it is missing" is a race both processes win. What is new is the *order*: the winner starts accepting on its pipe, writes the file, and releases the lock in the time one file write takes; acquiring and launching Roslyn — a 70 MB download on a first run — happens afterwards. Holding the lock across the launch would make the loser wait out the whole cold start before it could even find out where to connect, and the loser instead attaches immediately and waits behind the host's readiness gate (D46), which is where it would have been waiting anyway. |
| D87 | **The host is a component of its owner, not a session of its own.** `Adapter/Sharing/ISharedEngineHost` is six members wide — the readiness gate, the document mirror, the backend's `initialize` result, forward-one-request, notify-the-backend, set-an-option, plus the stream of everything Roslyn says — and both `AdapterSession` and `OwnedRoslynEngine` implement it without being restructured. That is the seam D75 declined to cut in WP5b and named as the one worth cutting here, and it held: the multiplexer owns the pipe, the frames, the per-client id tables and the document ledger; the owner keeps the one connection to Roslyn, the gate and the configuration. Nothing about "serving a peer" leaked into the owner, and nothing about "which half am I" leaked into the multiplexer. |
| D88 | **The multiplexing rules, and every one of them is a decision.** (1) An attached client's `initialize` is answered *here*, out of the result Roslyn gave the host, with `serverInfo` replaced by one naming the host process — never forwarded, because Roslyn has one client and a second handshake on that connection is a protocol error. Everything else crosses verbatim, `_roslyn_processId` included (C45), so both halves report the same backend. (2) Requests are forwarded under the host's own `IdMap` and answered under the id the client used: D47 applied a second time, because two peers that have never met both count from one. (3) Every forwarded request passes the host's readiness gate first — a request that arrives over a pipe before the workspace has loaded is answered empty and successfully by Roslyn (C27), which is no less wrong for having arrived from another process. (4) Server-to-client *requests* stay host-owned: there is one correct answer to a registration, a configuration pull or a progress-create, and the host already gives it. (5) `solution/open` and `project/open` from an attached client are swallowed and answered with readiness, because the workspace is already open. (6) `shutdown` and `exit` detach that client and nothing else; the host's own shutdown closes every pipe, which is the backend-gone state each attached supervisor already knows what to do with (D57). |
| D89 | **Four notifications are fanned out, and the readiness one is replayed to a late attacher.** `window/logMessage` and `window/showMessage`, because a session that went quiet is diagnosed from them; `workspace/projectInitializationComplete`, because it is what opens an attached client's own gate; and `workspace/diagnostic/refresh`, converted from a request into a notification so the attached client consumes it without owing anybody an answer under an id its connection never issued. The replay is not a nicety: a client that attaches after the load would otherwise hold every request until its own budget ran out on a workspace that finished loading minutes ago. The owner raises *everything* and decides nothing, so the list lives in one place. |
| D90 | **An attached engine changes a Roslyn setting through the host, and waits for the answer.** Roslyn asks the *host* for configuration, so an override set in an attached process's own `ConfigurationResponder` is a value nobody ever reads — and `getDiagnostics scope: "solution"` without a `fullSolution` compiler scope answers emptily and successfully (C14), which is the failure this product exists to remove. So `claude-roslyn-lsp/setOption {section, value}` goes to the host, which sets the override in the responder Roslyn actually asks and runs D77's round trip. It is a **request** rather than the notification the plan sketched, because the caller has to know when the change is in effect: a fire-and-forget would race the very call it was made for. `Adapter/ConfigurationRoundTrip` is the shared half, extracted from `OwnedRoslynEngine` where it already existed. |
| D91 | **Documents are ref-counted per URI, and everything is mirrored.** Roslyn keeps one buffer per document, so a second `didOpen` is redundant and — much worse — a `didClose` from one client would silently revert the other's live buffer to what is on disk. So `didOpen` reaches Roslyn on the first open from anyone, `didClose` on the last, and `didChange` always (the file on disk is shared and the last writer wins). The ledger records which opens *it* forwarded, rather than asking the mirror, because the mirror is written for both halves: every attached client's open and edit goes into the host's `DocumentMirror` so a Roslyn that dies is replayed with all of them (D57), and a check against the mirror would then read its own entries back as the host's. A client that goes away hands back what only it was holding. |
| D92 | **Every failure falls back to a private Roslyn, which is what every version before this one did.** An unwritable home, a platform that refuses named pipes, a host that published itself and died between the read and the connect, a session file naming a reused pid: each of them ends in the inner factory. Sharing is an optimisation over a product that already works, and an optimisation that can fail a session is not worth having. The pid check is deliberately a filter rather than a proof — a connect that fails deletes the file and hosts instead, so a reused pid costs one attempt. `CLAUDE_ROSLYN_LSP_SHARE=off` is the deliberate opt-out, for a machine where a pipe between two processes is not acceptable. |
| D93 | **A host that is already hosting relaunches its child rather than attaching to itself.** When Roslyn dies the supervisor (D57) asks the factory for another connection — and the session file still names this process, because the pipe is still accepting with the attached clients' requests held by the gate. So the factory remembers its host and, when it has one, goes straight to the launcher. That is also what makes a crash invisible to an attached client: it sees a workspace that went briefly quiet, not a backend that went away. A request that *was* in flight when the backend died is re-held and asked again once the gate reopens, which is D74 extended over the pipe for D74's own reason — everything an attached client forwards is a read. The connection that stops the host is generation-numbered, so a superseded attempt being disposed after a relaunch cannot withdraw a session file that names a pipe carrying two clients. |
| D94 | **`doctor` reports the session directory and `--fix` tidies it.** Every file, whether or not it is any good: the key, the verdict (live, stale because the process is gone, stale because the version moved, unreadable), the pid, the version, the age and the solution. Listing never deletes — D43's rule that a diagnostic which spends or destroys things is one people stop running — and `doctor --fix`, which is already the verb that changes things, removes the stale ones. A process starting now removes them by itself, so the tidy-up is a convenience rather than a repair. |

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

### The MCP tool surface

Ten tools, frozen by D20. Names are camelCase verb-noun; lines and columns are **1-based** in and
out; paths are workspace-relative with forward slashes; every tool sets `UseStructuredContent` and
every result carries `status` (`ok` / `loading` / `failed`) and an optional `note` in the same place
(D66). Every `symbol`-shaped argument accepts a name or a `path:line:col` address (D63).

| Tool | Annotations | What it does |
|---|---|---|
| `getWorkspaceStatus` | readOnly, idempotent | The solution, how far it has loaded, load errors, projects with their target frameworks, the Roslyn build, and the language server's pid and working set (C45, C38). The tool every `loading` note points at. |
| `resolveSymbol` | readOnly, idempotent | `symbol`, `kind?`, `maxResults?` → declarations with 1-based positions and hover signatures. The replacement for grepping a declaration, and the feeder for Claude Code's own `LSP` tool. Ambiguity returns every candidate. |
| `getTypeMembers` | readOnly, idempotent | `type`, `maxResults?` → a type's members without reading the file. |
| `findReferences` | readOnly, idempotent | `symbol`, `includeDeclaration?`, `maxResults?`, `offset?` → semantic references with the source line beside each, a `byFile` summary, de-duplicated across target frameworks (C24), paged. |
| `getDiagnostics` | readOnly, idempotent | `scope?`, `path?`, `project?`, `minSeverity?`, `includeAnalyzers?`, `ids?`, `maxResults?` → compiler and (opt-in) analyzer diagnostics for a file, a project or the solution, in about a second (D67). Carries the design-time caveat. |
| `getCodeActions` | readOnly, idempotent | `path`, `line`, `col`, `endLine?`, `endCol?`, `kind?` → the lightbulb list, with a stable `id`, `diagnosticIds` and `fixAllScopes` per entry (D64). |
| `applyCodeAction` | **destructive**, not idempotent | `path`, `line`, `col`, `id?`, `title?`, `endLine?`, `endCol?`, `fixAllScope?`, `preview` → resolves and applies one action, optionally across the document, project or solution. |
| `renameSymbol` | mutating, not destructive, idempotent | `symbol`, `newName`, `preview` → a solution-wide semantic rename. Does not rename the file. |
| `fixDiagnostics` | **destructive**, idempotent | `diagnosticId`, `scope?`, `path?`, `project?`, `preview` → finds a site of the diagnostic, then Roslyn's own fix-all across that scope (D65). |
| `formatCode` | mutating, not destructive, idempotent | `paths?`, `project?`, `organizeUsings?`, `preview` → `textDocument/formatting`, honouring `.editorconfig`, never shelling out to `dotnet format`. |

All ten declare `openWorldHint: false`. The four mutating tools all take `preview` (default `false`),
return the same `EditResult` shape, and answer an applied edit with one fixed sentence (D62).

**Adding a tool means editing seven places**, and the tests fail if any of them is missed:

1. the tool method itself, in one of the five classes under `src/ClaudeRoslynLsp/Mcp/Tools/`;
2. its result record in `Mcp/Models/ToolResults.cs`, **and** a `[JsonSerializable]` line for it in
   `Mcp/Models/RoslynToolJsonContext.cs` — without which the schema exporter cannot describe it and
   `dotnet test` fails rather than the AOT publish (D6);
3. `McpServerSetup.RegisterTools` and `McpServerSetup.ToolTypes`, if it is a new class;
4. the table above;
5. `ExpectedToolNames` in `build/Build.cs`, which is the only assertion made against the *published*
   binary;
6. `ToolInventoryTests.ExpectedToolNames` and its annotation rows, plus a row in
   `ToolSchemaTests`'s frozen schema table;
7. `.claude/skills/claude-roslyn-lsp/SKILL.md` — `AgentSkillTests` requires every tool to be named
   there, in backticks, and every tool-shaped name in it to exist (see *Agent skill*).

### Protocol gotchas

The **C-numbered** findings live in [`docs/roslyn-protocol-facts.md`](docs/roslyn-protocol-facts.md):
C1-C47 were observed on the wire against the pinned server on 2026-09-10 (WP0 spikes, WP3),
C63-C65 by WP9 with two halves of this product sharing one server, and
C48-C54 by WP4 against the fixture solution and against real Quartz.NET (30 projects) and
OrchardCore (239 projects) sessions through Claude Code 2.1.267. C55, C56 and C62 are *host* findings rather
than protocol ones — a BCL path function that behaves differently off Windows, and the order two
operating systems report a dead backend in — kept in the same file because they are cited from the
same code and there is nowhere else they would be re-verified from. They are cited from code
comments and tests by number. Add to that file, never
restate a fact here; a finding that changes on a pin bump gets re-verified there with the new date.

**If a solution ever stops finishing its load, read C52 and then C48**, in that order. They are two
independent causes of the same silence — every project reported "successfully loaded", a restore
that visibly restored everything, and then no `projectInitializationComplete` until the readiness
budget runs out. C52 is MSBuild node reuse holding the restore child's output pipe open, and its fix
is one environment variable on the child; C48 is the restore's `project.assets.json` write never
reaching Roslyn because nothing was watching for it. Both were found by the live tests, and neither
would ever have been found by a scripted backend.

## Agent skill

`.claude/skills/claude-roslyn-lsp/SKILL.md` is one file and it is the **primary lever of this
project**, not an add-on. Everything else here is machinery for answering a question correctly; the
skill is the only thing that decides whether the question gets asked at all. The complaint this
repository started from is behavioural — an agent reaching for `grep` and `sed` on C# — and no
amount of correct `textDocument/references` fixes a model that never sends one.

D70-D73 below are recorded here rather than as rows in the decision table, because each of them is
about this file and would be read nowhere else.

### The budget of attention

Guidance can live in three places, and they are paid for at three different rates. Putting a
sentence in the wrong one is either a tax on every session that never touches C# or a rule the
caller never sees.

| Surface | Paid by | What belongs there |
|---|---|---|
| `Mcp/ServerInstructions.cs` | **every session** that attaches the MCP server, whether or not any C# work happens | Only what a caller needs *before* the first call and cannot learn from a schema: what the server is, what it costs to start, and the conventions that span all ten tools. Roughly ten lines, and it has to stay that way. |
| A tool's `[Description]`, and each parameter's | the client, per tool, when it renders or calls one | Everything true of *that* tool: what its arguments mean, what its defaults are, what it returns. |
| `SKILL.md` body | only a session that actually starts C# work — the frontmatter description is the only always-loaded part | The order the calls go in, and what to reach for *instead of* what. |

**D70 — the skill holds order and substitution, because a schema structurally cannot.** Every schema
describes one tool, so no schema can say "resolve the symbol before you search for it", "rename
semantically instead of editing the declaration and chasing the compiler", or "run a design-time
diagnostic pass instead of a build". Those are statements about *pairs* of tools, one of which is
usually a tool this server does not own — `Grep`, `Edit`, `Bash dotnet build`. The skill is the only
surface where a claim about two tools can be made at all.

**D71 — the skill teaches two tool families at once, and the pairing between them is the point.**
Claude Code's built-in `LSP` tool has nine read-only operations and needs a file, a line and a
character for every one of them; this server's tools are name-addressed and include the mutations.
A model that has only the first has to find a position before it can navigate, and the only way it
knows to find one is to search for it — which is the exact habit being replaced. So the skill
teaches the join: `resolveSymbol` reports the `path:line:col` the `LSP` tool consumes, and once that
sentence is in the skill the built-in tool stops being a reason to grep. This is why the skill names
a tool it does not own, and why `AgentSkillTests`' verb scan is written to ignore identifiers that
are not shaped like this server's names.

**D72 — one canonical copy, and a test that keeps it in step.** The file lives under
`.claude/skills/`, which is what Claude Code loads from a checkout, what the plugin manifest points
at, and what `npx skills add` and `gh skill install` read when installing into another agent. There
is no second copy anywhere; every other tool is pointed at this path. `AgentSkillTests`
cross-references it against the *reflected* tool inventory in both directions, which is what makes
the skill one of the seven places a new tool has to be added — nothing loads this file at build
time, so it is exactly the kind of document that would keep advertising a surface that has moved on.
The scaffold's escape hatch, which let the "every tool is named" direction pass while the skill was
an under-construction placeholder, was removed with WP6: naming no tool is no longer a state this
repository has.

### The client configuration packs

**D73 — the configuration for every other client is a checked-in file, not a fenced block.**
`docs/clients/` holds one complete, minimal file per client — Copilot CLI, OpenCode, Neovim, Helix,
Zed, Codex, Gemini CLI, Cursor, VS Code, Claude Desktop and a local Claude Code plugin directory —
and `README.md` quotes them rather than being their source. Three reasons. A file can be copied
verbatim, which is what a reader actually does with it. A file can be *parsed by a test*, and
`ConfigSnippetTests` does: every one has to parse in its own format, name only variables
`ClaudeRoslynLspOptions` actually reads, and launch the binary with a verb — this product has no
default verb, so a snippet that forgot one is a server that exits 2 before the handshake and a
client that reports it as a crash. And `.github/lsp.json` is the same idea turned on the repository
itself: it is this checkout's own Copilot CLI configuration, pinned to the published package, so the
project is configured by the file it ships.

What no test can check is whether the surrounding key names are the ones somebody else's parser
expects. Those are facts about other people's software; they are stated in `README.md` next to each
snippet, with the ones nobody has run — Neovim, Helix, Zed — labelled untested rather than presented
as verified. Zed gets the additional caveat that it cannot attach a language server it does not
already know about, so the snippet replaces the binary behind its C# extension's server instead of
adding one.

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
                            CliDispatcher, CliRuntime, CliVerbs, DoctorCommand, FakeRoslynCommand
  Configuration/            ClaudeRoslynLspOptions.FromEnvironment() — the complete env-var surface,
                            the plugin-option precedence, and it never throws (D10)
  Protocol/                 Content-Length framing (D12), the depth-1 message scanner and id rewrite
                            (D47), JsonRpcId/JsonRpcErrors, the wire shapes the adapter understands,
                            and LspJsonContext (D7)
  Adapter/                  The mediation (D44): AdapterSession, ClientEndpoint, ServerEndpoint,
                            OutboundQueue, IdMap, ReadinessGate, DocumentMirror, RegistrationTracker,
                            ConfigurationResponder, ProgressTracker, ServerRequestHandler,
                            RequestRouter, WorkspaceOpener + WorkspaceSelection,
                            IRoslynConnectionFactory, RoslynResponses (shared with the MCP engine,
                            D75), ConfigurationRoundTrip (D77, shared with the shared host), and
                            LspAdapterServer (the `lsp` verb's stdio entry point). WP4's
                            bridges live here too, all four reaching the session through the one
                            narrow IAdapterChannel seam: DiagnosticsBridge (+ .Workspace,
                            DiagnosticTranslation) D52-D54, FileWatchBridge + GlobMatcher D55-D56,
                            RoslynSupervisor D57, CallHierarchyDeduplicator D58
  Adapter/Sharing/          One Roslyn per solution, shared between the two verbs (D23, D85-D94):
                            SharedSession + SessionRegistry (the rendezvous file and its lock),
                            ISharedEngineHost (the seam both owners implement), SharedRoslynHost +
                            SharedClient + SharedForwarding (the pipe server and the multiplexing
                            rules), AttachedRoslynFactory (the other end) and SharingRoslynFactory
                            (attach, else host, else keep it private)
  Testing/                  The scripted fake Roslyn server (D49), shared by the tests, `lsp --smoke`
                            and the hidden `fake-roslyn` verb
  Roslyn/                   Everything about the child process (D24–D43): RuntimeIdentifier,
                            RoslynServerManifest (the generated pin), AdapterPaths,
                            NuGetPayloadDownloader, RoslynServerLocator, DotnetHostLocator,
                            RoslynProcessLauncher + DuplexStream + ChildProcessGuard +
                            RoslynStderrPump, RoslynHandshakeProbe, SolutionDiscovery, and
                            LaunchedRoslynFactory (D50), which is the one class the mediation and
                            the acquisition chain meet in
  Mcp/                      McpServerSetup (D5), McpBackendSelection (D80), ServerInstructions,
                            SymbolAddress (D63) and CodeActionCatalog (D64)
  Mcp/Engine/               IRoslynEngine — the LSP-level seam the tools run against (D60) — its
                            wire shapes, the outbound request shapes, RoslynEngineJsonContext, and
                            NotWiredRoslynEngine (D69). OwnedRoslynEngine (D75) is the real one:
                            it launches a Roslyn of its own and composes the mediation's components
                            around it, with WorkspaceLoadTracker and WorkspaceProjects (D78)
                            answering getWorkspaceStatus
  Mcp/Tools/                The five tool classes behind the ten tools (D20), the RoslynToolContext
                            they all take, ToolLookup, DiagnosticQuery, DocumentSession, ToolErrors
  Mcp/Models/               Result records and RoslynToolJsonContext (camelCase, D7)
  Edits/                    The only place in the product that writes a source file (D21):
                            WorkspacePathGuard, TextFileCodec, TextOffsets, WorkspaceEditApplier +
                            WorkspaceEditPlan, UnifiedDiff, EditCache
tests/ClaudeRoslynLsp.Tests/  The single test project; internals visible via InternalsVisibleTo.
                            Http/ holds the hand-rolled StubHttpMessageHandler, Roslyn/ the
                            acquisition and discovery tests, Live/ the opt-in real-Roslyn tests,
                            Mcp/ the FakeRoslynEngine and the spike-derived payloads, Edits/ the
                            applier and codec tests, Tools/ the inventory, schema and behaviour tests
tests/fixtures/             HelloSolution (D59) plus the empty Directory.Build.props and the
                            CPM-disabling Directory.Packages.props that keep this repository's
                            conventions out of it, and MANIFEST.md, which records what each part of
                            the fixture is for and which parts must not be tidied up
build/                      The Fallout orchestrator (Build.cs, Build.Publish.cs, Build.Roslyn.cs,
                            Build.CI.GitHubActions.cs, ReleaseNotesParser.cs, SemVersion.cs) —
                            `build/` is a resolver convention, and `.gitignore` must never
                            untrack it
docs/                       roslyn-protocol-facts.md — the C-numbered findings (see above)
docs/clients/               One complete configuration file per client, parsed by ConfigSnippetTests
                            and quoted by README.md rather than the other way round (D73)
.github/lsp.json            This checkout's own Copilot CLI configuration — the project configured
                            by the file it ships
```

Directories the plan reserves and the repository has not created, so that nobody invents a second
Every directory the plan reserved now exists; a new top-level directory needs a row in this
tree and a decision naming why nothing existing could hold it.

## Build

The orchestrator is [Fallout](https://fallout.build) 10.4.0 (stable channel), the maintained hard
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

xunit.v3 with the VSTest bridge (D9). No mocking or assertion libraries, and no
`Microsoft.Extensions.TimeProvider.Testing` either — the framing tests drive a hand-rolled stream
that returns one byte at a time, the adapter is driven over `System.IO.Pipelines` pipe pairs against
the scripted backend (D29), and the readiness gate's timeout and its ten-second notice run on a
hand-rolled `TimeProvider` the test moves by hand, because a test that waits two minutes for a
timeout is a test nobody runs. Because `JsonSerializerIsReflectionEnabledByDefault=false` is set in
the test csproj as well (D6), a type missing from a `JsonSerializerContext` fails here rather than
only after an AOT publish.

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

`SmokeTest` is the end-to-end check the unit tests cannot be: it publishes the Native AOT binary,
spawns it, and drives **four** real exchanges. Two of them are LSP sessions —
`initialize`, `initialized`, a `didOpen`, a `textDocument/definition`, then, once that definition has
been answered, `shutdown` and `exit` with the exit code asserted — one against the scripted backend
running *inside* the server process (`lsp --smoke`) and one against the same script in a *child*
process (`CLAUDE_ROSLYN_LSP_FAKE_BACKEND=child`, which spawns `<self> fake-roslyn`). The other two
are MCP handshakes (`initialize`, `initialized`, `tools/list`), whose answers are compared against
`ExpectedToolNames` verbatim — the only assertion this repository makes about the tool inventory of
the *published* binary rather than of a reflected type list, and therefore the only thing that would
catch an AOT publish which dropped a tool class or a serializer context that could not describe a
result type. The first of them runs with `CLAUDE_ROSLYN_LSP_FAKE_BACKEND=none`, so no Roslyn is
wired in at all (D69, D80), which is exactly the point: the handshake and `tools/list` must not
depend on a backend. The second runs `mcp --smoke`, where a backend *is* started from the handshake
(D76) — the leg that proves the eager start puts no byte on the protocol channel.

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
runs `Test`, `SmokeTest` **and** `LiveTest` on every push and pull request, with
`CLAUDE_ROSLYN_LSP_LIVE_TESTS: 1` set for the whole job — so the 70 MB download is paid per run and
`RoslynServerManifest.cs` is in the cache key, because a pin bump has to miss the cache or the job
would test the new pin against the old payload.

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

Every fixture gets a row in [`tests/fixtures/MANIFEST.md`](tests/fixtures/MANIFEST.md) recording what
it is, when it arrived, and whether the bytes came off the wire or were written by hand. JSON cannot
carry comments, and a fixture whose provenance nobody recorded is a fixture nobody dares to
re-capture; that file also records which parts of `HelloSolution` must **not** be tidied up, because
several of them are deliberate diagnostics and code-action sites. The acquisition tests deliberately
need no fixture: they build a `.nupkg` in memory, because every property under test is about the
*shape* of a package — a prefix to strip, an entry to refuse — and a captured one would make each of
those cases a 70 MB file.

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
