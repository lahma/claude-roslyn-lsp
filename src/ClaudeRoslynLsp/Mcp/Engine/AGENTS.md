# Agent instructions: the engine behind the tools

> **Read this when:** you touch `IRoslynEngine` or an implementation of it — `OwnedRoslynEngine`,
> `NotWiredRoslynEngine`, the attached engine — the workspace status, the workspace diagnostic pull,
> or a configuration round trip.
>
> One of the co-located instruction files indexed from the repository-root
> [`AGENTS.md`](../../../../AGENTS.md); read that first. The tools above this seam are in
> [`../AGENTS.md`](../AGENTS.md); the components below it are the mediation's, in
> [`../../Adapter/AGENTS.md`](../../Adapter/AGENTS.md), and sharing one Roslyn between processes is in
> [`../../Adapter/Sharing/AGENTS.md`](../../Adapter/Sharing/AGENTS.md).

## The seam

**D60 — The tool layer talks to an interface, and the fake lives in the test project.**
`Mcp/Engine/IRoslynEngine` is the whole of what the MCP half needs from a running Roslyn, expressed
at the LSP level: seventeen members, zero-based positions, `data` blobs passed through as
`JsonElement`. Almost all of this product's judgement — symbol addressing, per-TFM de-duplication,
code-action ids, diagnostic filtering, the edit applier, the preview cache — lives *above* that
line, and none of it needs a quarter-gigabyte child process to be exercised; `FakeRoslynEngine`
answers with payloads transcribed from the WP0 spikes and the whole tool suite runs in two seconds.
The seam is at the LSP level rather than at a domain level on purpose: a `FindReferences(symbol)`
method would put the interesting decisions on the far side of it, where no test can reach them. Its
shapes are a **third** source-generated context, `Mcp/Engine/RoslynEngineJsonContext`, chained with
neither of the other two — D7's rule is about chaining, not about counting, and merging this into
`Protocol/LspJsonContext` would give the mediation and the MCP half one file to fight over.

**D75 — The MCP half composes the mediation's components; it does not share its session.**
`Mcp/Engine/OwnedRoslynEngine` reuses every class that carries a decision — `ServerEndpoint` and the
authored `initialize` in it (D14), `ReadinessGate` (D46), `ConfigurationResponder` (D48),
`RegistrationTracker`, `ProgressTracker`, `ServerRequestHandler`, `WorkspaceOpener`,
`RoslynSupervisor` (D57), `FileWatchBridge` (D55-D56), `DocumentMirror`, `IdMap` (D47),
`LaunchedRoslynFactory` (D50), `SolutionDiscovery` (D39-D42) — and writes only the lifecycle glue
that joins them. The alternative on offer was extracting a `BackendSession` shared with
`AdapterSession`, and it was declined on the evidence: that class's glue exists to *serve a peer* —
two id maps, byte forwarding with an id rewrite, a queue of somebody else's requests, call-hierarchy
de-duplication on the way back — and none of it exists where the caller is a method on the object. A
shared session would mean inventing an abstraction for "the peer" whose only second implementation
is "there isn't one", and putting that refactor underneath the mediation WP4 spent a live session
getting right. WP9's shared engine — one host, one attach client — is where the two lifecycles
genuinely merge, and that is the seam worth cutting along. The three small helpers that *were*
duplicated are shared instead: `Adapter/RoslynResponses` completes a response, reads an error code,
and builds a `$/cancelRequest`.

**D76 — The `mcp` verb starts Roslyn from `notifications/initialized`, not from the first tool
call.** Every MCP client starts its servers when the session starts and may not call a tool for
minutes, so launching at the handshake spends the 3-45 s load (C31, C53) during time the user is
already spending; a first tool call then answers immediately instead of being the thing that pays. A
session that never asks a C# question pays for a child process it did not need, and that is the
trade taken deliberately: the alternative makes the *first* question the slow one, and the first
question is the one that decides whether the model uses these tools again. Tools still gate on
`EnsureReadyAsync(CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS)` and answer `loading` rather than hanging
(D66).

## Settings, status and the workspace pull

**D77 — Changing a Roslyn setting is a round trip, and the answer outranks every standing one.**
`ConfigurationResponder` gains runtime overrides that sit *above* the client's own settings, the
environment and the defaults, because an override is set for the duration of one call that cannot
work without it — a solution-wide pull needs `fullSolution` (C14), `formatCode organizeUsings: true`
needs the format option — where every other layer is a standing preference. Losing to a static
setting would make those calls answer emptily and successfully. The change is a
`workspace/didChangeConfiguration` notification, and the engine then *waits for the
`workspace/configuration` request Roslyn issues in response*, because that pull is the only
observable moment at which the value is in effect. Two seconds, and a build that stopped re-pulling
gets a logged warning rather than a failed call.

**D78 — The project list is read off the solution file, not asked for.** Roslyn's custom methods
(C39) have no "describe the workspace" request, and the only thing that names a project is a log
line the default child level does not emit (D37). `Mcp/Engine/WorkspaceProjects` therefore reads the
`.slnx`/`.sln` for its project entries and each project file for a literal `TargetFramework(s)`;
`Mcp/Engine/WorkspaceLoadTracker` layers on what Roslyn *does* report — the progress stream's
`Loading N project(s)` and, when the level is raised, the per-project outcomes. The target
frameworks are a text scan and a best effort, exactly as D41's project count is: a project that gets
its TFM from a `Directory.Build.props` reports none, because an empty list reads as "not known"
where a wrong list would read as a fact. Evaluating a project file properly means MSBuild, which
hard rule 1 and the package budget both refuse.

**D84 — `getWorkspaceStatus` reports how this server got its Roslyn.** `engine: "owned"` means this
process launched one of its own; `engine: "attached"`, with `hostProcessId` beside it, means it is
using the one another `claude-roslyn-lsp` process launched for the same solution (D23). Reporting
the distinction is the point: whether a session running both servers has one Roslyn or two is the
difference between 0.3 GB and 0.6 GB on a small solution and rather more on a large one, and this is
where that stops being a paragraph in the README and becomes something an answer says. `processId`
still names Roslyn itself in both cases, so the two numbers answer two different questions.

**D82 — `workspace/diagnostic` needs the analyzer scope raised as well as the compiler one, and a
fix-all looks for its site in the cheapest place that can answer.** C14's second sentence, which
cost a live run: with only `dotnet_compiler_diagnostics_scope` at `fullSolution`, a closed file's
compiler errors are reported and its IDE and CA diagnostics are not — so `fixDiagnostics IDE0005
scope: "solution"` answered "no occurrence of IDE0005" on a solution that has one. `IRoslynEngine`
therefore has a second scope member, and `DiagnosticQuery` raises it only when `includeAnalyzers` or
an explicit `ids` list says the caller wants analyzers, because every analyzer over every file is
the expensive half and D48's standing answer stays `openFiles`. Raising it is still not *sufficient*
on a busy machine (C57), so `fixDiagnostics` no longer depends on it: given a `path` it finds the
site with a **document** pull, which always carries analyzer diagnostics, whatever scope the fix
itself is asked to reach. The site and the reach are two different questions and were being answered
by one call.

**D83 — A workspace diagnostic pull is bounded, falls back to the last report, and is confirmed
after a scope change.** Three behaviours, one method, all of them C57 and C58. Bounded, because the
pull is a long poll and an unbounded one makes a second `getDiagnostics scope: "solution"` a call
that never returns. Falling back to the previous report rather than to nothing, because Roslyn
holding the request *means* "nothing has changed since I last told you". And sampled again for a
short budget after a scope change — waiting for Roslyn's own `workspace/diagnostic/refresh` where it
sends one, and stopping as soon as the report is no longer the one the first pull returned — because
the change does not reach the pull that follows it and a pull already waiting is not woken by it.
The budget is eight seconds and deliberately short of what a contended machine needs: sampling for
thirty did not rescue that case either, and it made every call where the first pull was already
right thirty seconds slower for nothing. The one case that is not a fallback is the **first** pull
of a session: with nothing cached, a timeout throws a sentence naming `scope: "project"` and `scope:
"file"` rather than answering with an empty list, because an empty list would say "nothing was
found" about a solution nothing had looked at — which is the failure this product exists to remove.
That path is not hypothetical: OrchardCore's 239 projects do not finish a first solution-wide pass
inside 120 s (C61).
