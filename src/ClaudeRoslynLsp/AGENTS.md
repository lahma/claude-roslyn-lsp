# Agent instructions: the production project

> **Read this when:** you touch a verb or `doctor`, an environment variable, the framing, a JSON
> context or the csproj — or a file under `src/ClaudeRoslynLsp/` whose directory has no `AGENTS.md` of
> its own: `Program.cs`, `ServerVersion.cs`, `Cli/`, `Configuration/`, `Protocol/`, `Testing/`.
>
> One of the co-located instruction files indexed from the repository-root [`AGENTS.md`](../../AGENTS.md).
> Read that first: it carries the hard rules, the commands, the package budget and the decision index.
> `Adapter/`, `Adapter/Sharing/`, `Roslyn/`, `Mcp/`, `Mcp/Engine/` and `Edits/` each have their own.

## Source layout

```
src/ClaudeRoslynLsp/        One production project; AssemblyName claude-roslyn-lsp
  Program.cs                Entry point — hands argv straight to the CLI dispatcher
  ServerVersion.cs          Product name + version, read from the assembly (the build stamps it)
  Cli/                      The verb dispatch, and the ONLY place the console is touched (D11).
                            CliDispatcher, CliRuntime, CliVerbs, DoctorCommand, FakeRoslynCommand,
                            and SharedEngineReport (doctor's "Shared engines" section, D107)
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
                            RoslynSupervisor D57, CallHierarchyDeduplicator D58. BackendRecycler is
                            the idle policy both owners run (D95-D101, D106), with its
                            RecyclePolicy, IRecycleTarget (the owner's half), IBackendCompactor and
                            BackendMemory
  Adapter/Sharing/          One Roslyn per solution, shared between the two verbs (D23, D85-D94):
                            SharedSession + SessionRegistry (the rendezvous file, its lock, its
                            atomic rewrite and the sweep, D102-D103), ISharedEngineHost (the seam
                            both owners implement), SharedRoslynHost + SharedClient +
                            SharedForwarding (the pipe server and the multiplexing rules),
                            BackendChange (the host's own backendChanged notification, D105),
                            AttachedRoslynFactory (the other end) and SharingRoslynFactory
                            (attach, else host, else keep it private)
  Testing/                  The scripted fake Roslyn server (D49), shared by the tests, `lsp --smoke`
                            and the hidden `fake-roslyn` verb
  Roslyn/                   Everything about the child process (D24–D43): RuntimeIdentifier,
                            RoslynServerManifest (the generated pin), AdapterPaths,
                            NuGetPayloadDownloader, RoslynServerLocator, DotnetHostLocator,
                            RoslynProcessLauncher + DuplexStream + ChildProcessGuard +
                            RoslynStderrPump, RoslynHandshakeProbe, SolutionDiscovery, and
                            LaunchedRoslynFactory (D50), which is the one class the mediation and
                            the acquisition chain meet in, and DiagnosticsIpcCompactor (D101), which
                            asks the launched child's runtime for a compacting collection
  Mcp/                      McpServerSetup (D5), McpBackendSelection (D80), ServerInstructions,
                            SymbolAddress (D63) and CodeActionCatalog (D64)
  Mcp/Engine/               IRoslynEngine — the LSP-level seam the tools run against (D60) — its
                            wire shapes, the outbound request shapes, RoslynEngineJsonContext, and
                            NotWiredRoslynEngine (D69). OwnedRoslynEngine (D75) is the real one:
                            it launches a Roslyn of its own and composes the mediation's components
                            around it, with WorkspaceLoadTracker and WorkspaceProjects (D78)
                            answering getWorkspaceStatus
  Mcp/Tools/                The five tool classes behind the ten tools (D20), the RoslynToolContext
                            they all take, ToolLookup, DiagnosticQuery, DocumentSession, ToolErrors,
                            and ToolArgumentNames (the call filter that refuses unknown names, D109)
  Mcp/Models/               Result records and RoslynToolJsonContext (camelCase, D7)
  Edits/                    The only place in the product that writes a source file (D21):
                            WorkspacePathGuard, TextFileCodec, TextOffsets, WorkspaceEditApplier +
                            WorkspaceEditPlan, UnifiedDiff, EditCache, and EditorConfig (the
                            end_of_line and insert_final_newline rules formatCode applies, D113)
```

## The project file and the JSON contexts

**D8 — No `RuntimeIdentifiers` in the csproj, and `PublishAot` unconditional.** Listing RIDs would
pull an ILCompiler pack per RID on every restore; the RID list lives in the release matrix and in
the csproj's `ToolPackageRuntimeIdentifiers`, which only shapes the tool packages (D22) and restores
nothing per RID — `ToolPackageTests` keeps the two equal — and a RID reaches publish and pack only
through `-r`. `PublishAot` is set without a condition: with it, a RID-less `dotnet pack` builds the
pointer package alone, where with it false the same command packs every RID package
framework-dependent under the native names; and it keeps the AOT, trim and single-file analyzers on
for every `dotnet build` and `dotnet test`, so an AOT-hostile line (IL2026, IL3050) is a build error
without stating the analyzers separately (re-checked when the explicit block went). Publish output
carries neither symbols nor the XML docs (`CopyOutputSymbolsToPublishDirectory`,
`PublishDocumentationFile`): Native AOT's symbols alone are ~100 MB on Windows, and a stale one once
landed in a sibling's package. No `PublishSingleFile` (ignored under AOT), no `SelfContained`
(implied).

**D6 — `JsonSerializerIsReflectionEnabledByDefault=false`, in the product *and* the test project.**
A type missing from a `JsonSerializerContext` then fails under `dotnet test`, where it costs a
minute, instead of only after a Native AOT publish, where it costs a release. Every serializer call
passes an explicit `JsonTypeInfo<T>` so nothing can fall back to reflection by accident.

**D7 — Two source-generated JSON contexts, never chained together.** `Protocol/LspJsonContext`
carries LSP wire shapes, spelled with an explicit `[JsonPropertyName]` on every property because
they are somebody else's specification; `Mcp/Models/RoslynToolJsonContext` carries the tool
vocabulary in camelCase and goes **first** in the MCP server's `TypeInfoResolverChain`, with the
SDK's resolver second. A shared resolver would let one vocabulary leak into the other, and the leak
would look like a working feature.

## Configuration

**D10 — Environment variables only, and `FromEnvironment` never throws.** One
`ClaudeRoslynLspOptions` record, read once per process. A malformed value falls back to the
documented default and is reported through the stderr logger rather than by failing startup: a
server that refuses to start leaves its client with a dead process and no channel to complain on,
because stdout is the protocol. `CLAUDE_PLUGIN_OPTION_<NAME>` is read **first**, with blank treated
as absent, then the plain `<NAME>` — see *The plugin-option rule*. There is no configuration file
and no `Microsoft.Extensions.Configuration`.

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

## The console and the framing

**D11 — `Cli/CliRuntime.cs` owns the console, and the protocol streams are raw `Stream`s.** stdin
and stdout are opened once, as bytes. Never `Console.In`/`Console.Out`: a `TextWriter` applies an
encoding and, on Windows, rewrites `\n` as `\r\n`, which would put a `Content-Length` header out of
step with the body it measured — a corruption that produces no error, only a client that stops
answering.

**D12 — Framing is ours, bounded, and strict.** `Protocol/LspFrameReader` accepts only
`Content-Length` and `Content-Type`, requires CRLF, caps the header section at 64 KiB and the body
at 32 MiB, and treats anything else as an unrecoverable protocol error. Strictness here is not
pedantry: this reader sits *between* two peers that both claim to speak LSP, so accepting something
Roslyn would reject only moves the failure somewhere harder to see. `LspFrameWriter` writes each
frame in a single `WriteAsync` behind a gate, because a header separated from its body can be
interleaved with another frame's.

## `doctor`

**D43 — `doctor` never downloads; `install` is the same command with the download allowed.** A
diagnostic that quietly spends 70 MB of somebody's tethered connection is a diagnostic they will not
run again, so the two verbs are one implementation and one flag (`doctor --fix` ≡ `install`).
`doctor`'s exit code means exactly one thing: 0 iff Roslyn is runnable *right now*, which is
established by launching it and completing a real `initialize`/`shutdown`/`exit` — every static
check it prints can pass on a machine where the server still does not start. `--json` renders the
same gathered record, so the two forms cannot disagree.

**D94 — `doctor` reports the session directory and `--fix` tidies it.** Every file, whether or not
it is any good: the key, the verdict (live, stale because the process is gone, stale because the
version moved, unreadable), the pid, the version, the age and the solution. Listing never deletes —
D43's rule that a diagnostic which spends or destroys things is one people stop running — and
`doctor --fix`, which is already the verb that changes things, removes the stale ones. A process
starting now removes them by itself, so the tidy-up is a convenience rather than a repair.

**D107 — `doctor` lists every live host on the machine with what its Roslyn holds, measured by
`doctor` itself.** Per live session: the adapter pid, the Roslyn pid with its working set and
private bytes read by `Process.GetProcessById` at report time — the operating system's numbers, not
a figure a host wrote down minutes ago — the state, the last activity's age, the recycles, the
baseline, the attached clients and the solution; then one total line of live hosts and their
combined Roslyn memory. That is the report that answers "who is using my memory" without matching a
dozen `dotnet` processes to their sessions by hand. It says in so many words that a server running
with `CLAUDE_ROSLYN_LSP_SHARE=off` keeps a private Roslyn, does not publish itself, and is therefore
not in the list. `Cli/SharedEngineReport` gathers and renders it, with the process sampler a
parameter so the section is tested without a real Roslyn.

## The scripted backend

**D49 — The scripted fake Roslyn backend ships inside the product binary, behind a hidden verb.**
`Testing/FakeRoslynServer` reproduces a real 5.12 startup from the WP0 wire logs — the registration
batches with their real diagnostic identifiers and their shuffled order (C11), both configuration
batches (C41), the watcher shapes that make collapsing non-trivial (C32), the progress stream, the
log lines, `projectInitializationComplete` — and, crucially, answers navigation **empty until the
workspace loads**, exactly as the real server does (C27). Keeping it in the product rather than in
the test project is what lets `SmokeTest` prove the mediation against a published Native AOT binary
on every release RID with no download: `lsp --smoke` runs it in-process, and
`CLAUDE_ROSLYN_LSP_FAKE_BACKEND=child` spawns `<self> fake-roslyn` and speaks to it over stdio,
which is the only leg that proves the process plumbing (C7 is a real banner on a real stdout). The
cost is a few kilobytes of fixtures in the shipped binary; the alternative is release RIDs whose
mediation nobody has ever run.
