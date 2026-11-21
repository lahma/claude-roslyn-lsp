# Agent instructions: the LSP mediation

> **Read this when:** you change how a message crosses between the client and Roslyn — either
> handshake, the readiness gate, `workspace/configuration`, document synchronisation, the id rewrite —
> or one of the bridges (diagnostics, file watching, crash recovery, call hierarchy), or the idle
> recycle that both owners of a Roslyn run.
>
> One of the co-located instruction files indexed from the repository-root
> [`AGENTS.md`](../../../AGENTS.md); read that first. The same machinery continues elsewhere: sharing
> one Roslyn between processes in [`Sharing/AGENTS.md`](Sharing/AGENTS.md), the child process and its
> acquisition in [`../Roslyn/AGENTS.md`](../Roslyn/AGENTS.md), and the MCP half that reuses these
> components (D75) in [`../Mcp/Engine/AGENTS.md`](../Mcp/Engine/AGENTS.md).

## Two sessions, one adapter

**D44 — The adapter terminates both sessions; it is not a relay with patches.** It is a *server* to
Claude and a *client* to Roslyn, with two independent handshakes and state of its own. A byte relay
cannot answer Claude's `initialize` before Roslyn has answered its own, cannot answer the ~140
registrations and 80 configuration sections that Claude refuses, and has nowhere to keep a document
mirror or a readiness queue. Two read loops, one `OutboundQueue` per side (a channel, because a
semaphore serialises writes but does not order them), and every shared structure owns its own
`System.Threading.Lock`.

**D45 — The capability document the adapter advertises is authored and static, and `initialize` is
answered immediately.** Claude Code holds `initialize` open indefinitely if it is not answered, and
Roslyn takes the better part of a second to answer its own (C31) — so deriving one from the other
would make the client's startup wait on the backend's. Worse, Roslyn advertises
`semanticTokensProvider`, `codeLensProvider`, `inlayHintProvider` and `_vs_onAutoInsertProvider`
statically whether or not anyone asked (C26), and advertising a capability is a promise to answer
the requests it enables. The list is therefore exactly what the mediation carries: hover,
definition, typeDefinition, implementation, references, documentSymbol, workspaceSymbol,
callHierarchy, rename (prepare), codeAction (resolve; quickfix + refactor), formatting,
rangeFormatting, signatureHelp, completion, and `textDocumentSync` `{openClose, change: 1 (D13),
save.includeText: false}`. `SmokeTest` asserts the three absent providers on a published binary
talking to a backend that advertises all three.

**D14 — The `initialize` document the adapter sends Roslyn is authored, not derived from the
client's.** Claude Code's own document is wrong in both directions: it declares things the adapter
has to do itself (it refuses `client/registerCapability` with `-32601`, and answers
`workspace/configuration` only when a `settings` block happens to be present) and omits everything
the adapter needs Roslyn to do — dynamic diagnostics, watched files, work-done progress. Forwarding
it would switch off exactly the machinery this project exists to supply. So `RoslynInitializeParams`
is written here: configuration, workspaceFolders, dynamic `didChangeConfiguration` and
`didChangeWatchedFiles` with relative patterns (without which Roslyn registers no watchers at all
and has no in-process fallback, C34), symbol, diagnostics refresh, `workspaceEdit` with
`documentChanges` and create/rename/delete (C21), synchronisation with `didSave`, hover in markdown,
definition/typeDefinition/implementation with **`linkSupport: false`** — a `LocationLink` renders as
nothing in a client that only knows `Location`, and navigation crosses verbatim — references,
hierarchical documentSymbol, callHierarchy, rename with prepare, codeAction with `dataSupport` and
`resolveSupport: ["edit"]`, formatting, rangeFormatting, dynamic `diagnostic` with
`relatedDocumentSupport`, `window.workDoneProgress`, and `general.positionEncodings: ["utf-16"]`.
Semantic tokens, inlay hints and code lens are **absent**, so Roslyn never registers or refreshes
them. Two additions to the plan's sketch: `signatureHelp` and `completion`, because the adapter
advertises both providers to its own client (D45) and a provider promised in one direction but never
declared in the other is the exact asymmetry that produces empty answers with no error anywhere.
`workspace.applyEdit` is deliberately **not** declared — the v1 adapter writes no files, D21 puts
that in the MCP half — and Roslyn's `workspace/applyEdit` is handled defensively rather than
invited.

**D46 — The readiness gate blocks; it does not bounce.** A request issued before
`workspace/projectInitializationComplete` is answered by Roslyn with an *empty successful result*,
never an error (C27) — so an ungated client silently reports "no definition found" for the first
several seconds of every session, which is indistinguishable from a correct answer and teaches the
agent that the server is useless. The obvious alternative, `-32801 ContentModified` plus a client
retry, does not survive contact with Claude Code: it retries about three times inside three and a
half seconds, and a load takes 2.5-9 s (C31). So requests queue in arrival order and are released
**synchronously, in that order** — continuation scheduling gives no ordering guarantee — with
`CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS` as a floor rather than a promise: after it the gate passes
everything through, because a partially loaded workspace answering some questions beats a server
that has stopped answering. A `$/cancelRequest` for a held request dequeues it and answers `-32800`;
a failed backend answers `-32603` naming `doctor`; a client that is holding requests is told every
ten seconds through `window/logMessage`, which is the only channel it renders. Notifications flow
from `RoslynInitialized`, not from `ProjectsLoaded`.

**D47 — Pass-through is a three-segment id rewrite of the original bytes, and every outbound request
is renumbered.** `LspMessageScanner` makes one depth-1 `Utf8JsonReader` pass to find the message's
own `id` token — never a nested one, which matters because Roslyn puts `id` members inside `data`,
`item` and `TextDocument` payloads (C18, C24) — and forwarding copies everything before the token,
the new token, and everything after. Reserialising would reorder members, renormalise numbers and
re-escape strings whose exact shape the peer has already been told, and it would put this repository
in the business of modelling every result Roslyn returns. Renumbering is not optional: two peers
that have never met both count from one, so an unrewritten id lets a client request and an adapter
request collide, and one answer is then delivered as the other's — a *wrong answer*, not an error.
One counter serves both kinds of outbound request, which makes the collision structurally impossible
rather than merely unlikely; there is one `IdMap` per direction, because Roslyn asks the client
questions too.

**D48 — The adapter answers `workspace/configuration` itself, with agent-tuned defaults.** The
section names contain a pipe (`csharp|background_analysis...`), which Claude Code's dotted settings
lookup cannot address at all, and it only answers the request when a `settings` block happens to be
present in the plugin configuration. Precedence is client settings → `CLAUDE_ROSLYN_LSP_OPTIONS` →
these defaults → `null`. The defaults assume an agent rather than a human with a screen: both
diagnostic scopes at `openFiles`, because `fullSolution` is what makes a large solution unusable and
WP4's opt-in mode raises it deliberately (C14); automatic restore **on**, because Roslyn restores
server-side and never asks (C30) and a freshly cloned repository would otherwise load with no
references; decompiled-source navigation **on**, so definition into a BCL symbol reaches real source
(C25); and reference-assembly symbol search, every `csharp|inlay_hints.*`, every
`csharp|code_lens.*` and auto-insert **off**, because the adapter advertises none of those (D45) and
the work would be discarded. The two families are matched by prefix rather than enumerated, so a
Roslyn that adds a hint kind cannot quietly switch it back on.

**D13 — Full document synchronisation (`change: 1`), not incremental.** Roslyn accepts full-text
`didChange`, and the adapter has to keep a document mirror anyway so it can replay state after a
Roslyn crash. A mirror rebuilt from a range-edit history that was interrupted halfway is wrong in a
way that shows up as wrong *answers*, not as an error; the bytes incremental sync saves are not
worth that.

**D111 — A client's byte order mark is encoding, not text, and the adapter removes it where the text
enters.** Claude Code sends a UTF-8 file's mark as the document's first character in `didOpen` and
`didChange` (C67). Roslyn takes it for text, so every position on the first line moved by one and
the formatter proposed deleting it; and the MCP half, which applies an edit to the file as
`TextFileCodec` decodes it — without the mark (D61) — applied that deletion to the first real
character, wrote it and answered `ok` (issue #3). Everything else in the product, Roslyn reading a
closed file included, treats the mark as encoding, so `AdapterSession` strips one leading U+FEFF
from a client's document notifications before the mirror, Roslyn or a shared host sees them
(`DocumentMirror.WithoutByteOrderMark`): one place, because it is the only one external client text
enters by, and an attached client is always this binary. A notification that carried a mark is
reserialised; every other one still crosses as the client's bytes (D47). The cost is a client that
keeps the mark in its own buffer *and* applies edits Roslyn computed for line 1 — none of the agents
this product serves applies LSP edits, and an editor normally decodes the mark as encoding before
its buffer ever holds it.

## The bridges

D16–D19 were reserved for the bridges before WP4 settled them as D52–D58.

**D16 — The diagnostics bridge: pull from Roslyn, push to the client.** Settled as **D52** and
**D53**.

**D17 — Workspace-wide diagnostics for files that are not open are opt-in.** Settled as **D54**.

**D18 — File watching is on by default, with an opt-out.** Settled as **D55** and **D56**.

**D19 — A Roslyn crash is absorbed, not forwarded.** Settled as **D57**.

**D52 — The diagnostics bridge pulls with no identifier, keeps one pull in flight per URI, and tags
every publish with the mirror version.** One pull with no `identifier` returns the union of every
source (C9) — one round trip instead of ten for the same set — and `previousResultId` per URI (C12)
makes an idle session silent, because an `unchanged` report publishes nothing. One pull in flight
with a dirty flag, because Roslyn takes 1.3-1.7 s for the first pull and ~90 ms afterwards (C17) and
a queue would spend the whole budget answering about text that no longer exists. The version is the
mirror's at pull start, which is the client's own mechanism for discarding a set an edit has
overtaken. Triggers: `didOpen` immediately (deferred until the workspace loads, C28), `didChange` on
a 400 ms trailing debounce, `didSave` at once, `projectInitializationComplete` for everything open,
`workspace/diagnostic/refresh` after 500 ms, a project file changing on disk after 1 s (C33's
re-evaluation takes 1.6-2.1 s, so pulling sooner reports the world as it was). `-32801`/`-32800`
earns exactly one retry after 300 ms; the world settles or it does not, and a retry loop against a
fast typist never returns.

**D53 — The severity floor, the `Unnecessary` rule and the fifty-per-file cap are a delivery policy,
not taste.** Claude Code renders diagnostics as a text attachment on the next turn and cuts it, so
every entry kept is an entry that pushed another out. Warning floor by default
(`CLAUDE_ROSLYN_LSP_DIAGNOSTIC_MIN_SEVERITY` lowers it): Hint and Information are style, and the
agent did not ask about style. `Unnecessary`-tagged non-errors go regardless, because IDE0005
arrives at severity 4 *at position 0:0 for the whole using block* (C16) — the least useful entry and
the one most likely to read as "something is wrong at the top of this file"; an unnecessary-tagged
*error* stays, being a real one with a fade hint. Tags at or above 2147483640 are stripped, because
those are Visual Studio's private values (C16) and several clients treat an unknown tag as a reason
to drop the whole diagnostic. Then severity, then line, then fifty. Each surviving diagnostic is
copied member-by-member rather than modelled, so an opaque `data` payload round-trips untouched.

**D54 — Whole-solution diagnostics are opt-in, compile-errors-only, and cut to ten files of five.**
`CLAUDE_ROSLYN_LSP_WORKSPACE_DIAGNOSTICS=errors` is the only accepted value and it names the whole
contract. A closed file is unreachable at any lower scope (C13) and `workspace/diagnostic` only
reports one when the compiler scope is `fullSolution` (C14) — the setting that puts every project's
compilation in memory in a child process that is already the largest thing on the machine during a
Claude session. So the scope is raised by that variable and by nothing else, the analyser scope
stays at `openFiles`, and the output is filtered hard: C15 puts `obj/**/*.cs`, `.csproj` entries and
one copy per target framework in the report, and passing that through would fill the attachment with
generated files and duplicates. What survives is errors only, ten files, five each, with the saved
file's own project first. Published URIs are remembered and cleared with `[]`, because a client
keeps the last set for a URI forever and a closed file that was fixed would otherwise stay broken
for the session.

**D55 — File watching is on by default, and switching it off withdraws the capability rather than
going quiet.** Roslyn has no in-process watcher and no fallback (C34), and Claude Code answers every
one of its ~140 registrations with `-32601`. `CLAUDE_ROSLYN_LSP_FILE_WATCHER=off` therefore drops
`didChangeWatchedFiles` from the document the adapter sends Roslyn, so Roslyn registers nothing:
declaring it and then not delivering would be the worst of both, a server that stands up 140
registrations and waits to be told about changes that never come, which looks exactly like a working
watcher until an answer turns out to be stale. On Linux the limits that bite are
`fs.inotify.max_user_instances` (128 by default, and one per `FileSystemWatcher`) and
`fs.inotify.max_user_watches`; the bridge reports each refused directory by name and says which
sysctl to raise, because a stale workspace is otherwise invisible.

**D56 — The C33 rule is the watcher, and past 64 directories it becomes one recursive watcher on the
root.** A `Created` event for a new `.cs` file does *nothing* — what makes Roslyn re-evaluate is a
`Changed` for the owning `.csproj` (C33) — so every appearance or disappearance of a `.cs` file
emits a synthetic one for the nearest project file above it, collapsed with the rest of the 200 ms
batch. Bases outside the workspace root are dropped (342 of Quartz.NET's 433 are the NuGet cache,
C49), `bin obj .git node_modules .vs artifacts TestResults` are excluded, and the one exception is
`project.assets.json` under `obj` — which C48 shows is not a nicety: without it a freshly cloned
repository restores server-side and then never finishes loading at all. Open documents are excluded,
because the client already owns their text and re-reading the saved copy would discard the live
buffer. A watcher error or overflow reports every project file under the root as changed, since once
events are lost there is no knowing which. Past 64 kept directories the per-directory scheme is
abandoned for one recursive watcher on the workspace root with every pattern re-based, because
Quartz.NET's 91 directories under a cap of 64 meant 27 of them silently unwatched.

**D57 — A dead Roslyn gets two goes in ten minutes, and the client never learns it happened.**
Claude Code's own restart budget is three and spending it restarts *this* process, throwing away the
client session, the document mirror, the readiness state and every answer in flight. Roslyn dying is
not that kind of failure: everything needed to rebuild the backend is held here. So the gate closes
(a request arriving in the gap is *held*, not answered empty by a backend that has not loaded — C27
arriving late), the mirror replays one `didOpen` per document at its latest text, the solution
re-opens, and the diagnostics result ids are forgotten because they are tokens in a dead server's
memory. Two restarts per ten minutes: an accident comes back and works, and a Roslyn that dies three
times on the same solution is deterministic, so a fourth attempt would turn one broken session into
an infinite loop that also holds every request in it. Connect attempts are **generation-numbered**,
because a backend that dies during its own handshake produces both a relaunch decision and an
abandoned `initialize` that throws, and without the generation the second races the first and fails
a session the supervisor had just decided to save.

**D74 — The model never sees a crash — including one that happens mid-request.** D57 absorbs a
backend that dies *between* requests; a backend that dies *holding* one used to answer it `-32603`,
which put the crash in front of the model as a failed tool call seconds before the relaunched server
could have answered it correctly. So `IdMap` keeps a forwarded request's original bytes, and a
backend death that the supervisor decides to restart puts every such request **back into the
readiness gate** — closed first, so nothing is replayed into the corpse or into a server that has
not loaded the solution (C27) — and re-forwards it under a fresh id when the gate reopens. The
client is still waiting on its own id and gets a real answer under it. Only adapter-originated
requests fail: a diagnostic pull carries a `resultId` that died with the old process and is
re-issued on the bridge's own schedule anyway. When the supervisor gives up, the held requests are
released as `Failed`, which is `-32603` with a `doctor` hint — the state a user can act on. Safe
because every request this adapter forwards is a *read*: the v1 LSP half writes no files (D21 puts
edits in the MCP half), so replaying one cannot apply anything twice. Found by CI on Linux, where
the kill lands after the forward far more often than on Windows (C56).

**D58 — Call-hierarchy answers are de-duplicated; it is the only result the adapter rewrites.** A
multi-targeted project reports every call once per target framework (C24), which `workspace/symbol`
and `rename` collapse themselves and call hierarchy does not. For an agent that is not cosmetic: a
model asked "who calls this" reports twice the number, and a walk down the tree does every branch
twice — eight times the work for a three-level hierarchy over two frameworks. The key is (uri,
selectionRange), because `selectionRange` is the identifier itself and `data` is precisely what
differs; survivors are copied verbatim so the opaque payload the next request must round-trip is
untouched; an entry whose key cannot be read is kept, because dropping something this code did not
understand would turn a shape change in Roslyn into a silently shorter answer; and an answer with
nothing to remove keeps its original bytes.

## The idle recycle

Both owners of a Roslyn — `AdapterSession` here and `OwnedRoslynEngine` in the MCP half — run
`BackendRecycler`. The rest of the recycle lives beside the code it touches: the compactor (D101) and
the conserve level (D104) in [`../Roslyn/AGENTS.md`](../Roslyn/AGENTS.md), what the session file and
the attached clients are told (D102, D103, D105) in [`Sharing/AGENTS.md`](Sharing/AGENTS.md), and
`doctor`'s view of every host (D107) in [`../AGENTS.md`](../AGENTS.md).

**D95 — One activity clock per Roslyn, owned by the process that owns the child, and it counts only
what a person asked for.** `Adapter/BackendRecycler` stamps it. In `lsp` mode every client message
counts except three: `shutdown` and `exit` (waking a dormant Roslyn to shut it down again would be a
whole load spent on a goodbye) and `$/cancelRequest` (the client giving up is the opposite of using
it). In `mcp` mode the stamp is `EnsureReadyAsync`, which every tool calls first —
`getWorkspaceStatus` included — plus a tool's document opens and closes and its file-change
notifications. A shared host's clock is also fed by every attached client through
`ISharedEngineHost.Recycler`, with the same three exclusions and two more: an attached
`textDocument/diagnostic` and `workspace/diagnostic`, because an attached LSP session's bridge sends
those in reaction to the `workspace/diagnostic/refresh` the host fans out (D89), and Roslyn sends a
refresh whenever background analysis settles or a project file changes — counting them would let
Roslyn keep itself awake through its own clients. What never counts: the bridges' own traffic
(`AskAsync`, `NotifyServer`), `ServerRequestHandler`'s answers, and anything Roslyn originates. A
process whose engine is *attached* owns no child, never recycles, and reaches the host's clock
through the messages it forwards — and an attached MCP engine also sends
`claude-roslyn-lsp/activity` at the start of every tool call, which the host counts and swallows,
because the pull exclusion cannot tell a bridge's refresh-driven pull from a person's
`getDiagnostics scope: "solution"`, and that call sends nothing else.

**D96 — A grown backend whose session has gone idle is stopped, and relaunched lazily by the next
request; the gate has a `Dormant` state for the gap.** The recycle fires when the window
`CLAUDE_ROSLYN_LSP_IDLE_RECYCLE_MINUTES` (20; `0` is off) has passed since the last activity, the
gate is open, nothing is held or in flight, and the newest memory sample exceeds the threshold
(D100). Lazily, because a quiet session may never ask again and an eager reload would pay the load —
and most of the growth back — for nothing. `ReadinessGate.MarkDormant` closes the gate *before*
Roslyn is told anything, so whatever arrives from then on is held rather than forwarded into a
server that is shutting down. Dormant is not `MarkRestarting`: a restarting gate arms the load
budget at once and would open itself `READY_TIMEOUT_SECONDS` later onto no backend, narrating a load
nobody started every ten seconds. A dormant gate holds with no clock, mirrors document notifications
and drops the rest, and **every request it holds raises `HeldWhileDormant`**, which is wired to the
activity clock: the gate is the one place every request passes through — the client's, an attached
client's, a D74 re-hold, a shared forward waiting to be asked again — so it is the one place that
can promise none of them waits for ever. The wake is `MarkWaking` → `Starting` with the timers
armed, so the budget counts from the wake; then the crash path's reconnect (D57) — mirror replayed
at its latest *unsaved* text, solution re-opened, `ReapplyConfiguration` — and one
`window/logMessage` (lsp) or log line (mcp) saying why the next answer is slow. A recycle abandoned
before Roslyn heard anything — a request slipped in, activity arrived since the decision — reopens
through `CancelDormant`, which deliberately does not raise `Opened`: nothing downstream was told the
gate had closed. **Neither the stop nor the wake may strand the session (the 0.2.0 review,
2026-10-20).** A recycle target never throws once its gate is dormant — a failure before Roslyn was
told anything undoes the recycle, one after it ends the process and retires the endpoint by hand —
and if one ever does the policy asks it `IsDormant` instead of assuming the backend still runs,
because a dormant gate under a running policy holds every request for ever and wakes nothing. A wake
whose relaunch fails refuses only what was held for it (`-32603`, "the next request tries again")
and returns the gate (`ReturnToDormant`) and the policy (`OnWakeFailed`) to dormant, failing the
session only after `BackendRecycler.MaxConsecutiveWakeFailures` (3) in a row, because a wake is a
launch the session would never have made had it kept its child and a transient failure in it must
not end a session that was working.

**D97 — A recycle stops Roslyn by protocol, never by disposing the connection, and kills it only
through a seam that touches nothing else.** `ServerEndpoint.StopByProtocolAsync` sends `shutdown` (5
s), `exit`, and waits 10 s for `Connection.Exited`; the read loop then sees the stream end, which is
what tells the owner the backend has gone. Disposal is the wrong primitive because for a shared host
disposing the *current* connection is what withdraws the session file and closes every attached
client's pipe (D93) — a recycle that disposed would turn "the backend went quiet for a reload" into
"the host went away" for every other process using it. A Roslyn still alive after `exit` is ended
through `RoslynConnection.Terminate`, which the launcher sets to a process-tree kill and the sharing
wrapper passes straight through, and which leaves the connection — and the sharing built on it —
exactly where it was. `StopByProtocolAsync` itself never throws — a faulted `Exited` is a backend
that is gone, and a kill that fails is logged (a tree kill throws `AggregateException` when part of
the tree is somebody else's, which `RoslynConnection.Kill` now catches) — because its caller has
already closed the gate (D96).

**D98 — The endpoint of a backend that has gone is kept until a successor exists, and everything
that described it is forgotten when one connects.** After a crash the dead endpoint used to be
dropped with `_server = null` and never disposed, which leaked a process handle per relaunch; with
recycles once per idle period over days that matters. It is now kept as `_retired` and disposed in
`ConnectAsync` right after the successor is adopted — by which time the sharing factory's generation
has moved on, so the wrapper releases the dead process's handles without stopping hosting — or at
session stop, where for a *dormant* host it is the only connection there is and its disposal is what
still withdraws the session file. In the same place the registration table is cleared
(`RegistrationTracker.Clear`): entries are keyed by the id Roslyn minted, a new process mints new
ones, and without the clear a session recycled hourly for a week would stand its watchers up from
168 identical sets. The MCP engine also forgets C45's process id and its cached workspace report
there, on every relaunch rather than only after a wake: a pull the new process holds means "nothing
new since I last told you", and it has told this engine nothing yet (C58).

**D99 — A recycle never spends the crash budget, and a crash after one gets all of it.** The owner
records "recycling generation g" in `_stateLock`, in the same block that captures the server, and
`OnBackendGone` reads it in its own lock block: the backend going away while that flag names the
current generation is the recycle — the supervisor (D57) is not asked, nothing is relaunched, the
gate stays dormant — and a death during the stop is simply the exit that was asked for. The flag is
cleared there, and the generation is bumped as for a crash, so a backend that dies after the wake is
a different generation and therefore a crash with two restarts in ten minutes in front of it. A
client request that passed the gate in the instant before it closed is re-held by the D74 path into
the dormant gate — holding it wakes the backend — so the request that raced the recycle is also the
one that undoes it. The MCP engine has no replay for a tool call already at Roslyn, so it abandons
the recycle whenever anything is in flight once the gate has closed, and replaces `_opened` as the
gate closes so a tool call that stamps activity from then on waits for the relaunched workspace.

**D100 — The baseline is per backend generation, the metric is private bytes on Windows and the
working set elsewhere, and the threshold is `max(baseline × factor, baseline + 512 MB)`.** The
baseline is sampled once, 60 s after the workspace has really loaded — the gate opening into
`ProjectsLoaded`, or the `projectInitializationComplete` that arrives after a `LoadTimedOut`
(`ReadinessGate.LoadedLate`) — because the instant of `projectInitializationComplete` is not the
settled size — the first pulls, the analyzers' first pass over the open documents and the build
hosts winding down land in the seconds after it — and because a sample taken a minute after a
*timeout* is a workspace still loading, whose low threshold would recycle a solution slower than its
budget every idle period (the 0.2.0 review). The process is the child this process launched: C45's
`_roslyn_processId` when it names that child, the child's own id when it does not
(`BackendRecycler.ChooseProcess`), because a wrapper on `CLAUDE_ROSLYN_LSP_ROSLYN_PATH` — or the
`fake-roslyn` verb, which announces 4242 — names a process this adapter never started. On Windows
the working set is the wrong number: the OS trims an idle process's working set as soon as anything
else wants memory, and a grown child was observed at 119 MB of working set over 711 MB of private
bytes — a threshold on the first would keep exactly the child the recycle exists for. On Linux
`PrivateMemorySize64` is not the anonymous resident memory a GC heap is, and the resident set is
what the kernel's own accounting uses. The factor (`CLAUDE_ROSLYN_LSP_RECYCLE_GROWTH_FACTOR`, 2,
invariant-culture decimal, 1.2-20) alone is wrong at both ends — doubling a 300 MB baseline is
growth one find-references produces — so the 512 MB floor decides small solutions and the factor
decides large ones. The policy looks once a minute and decides synchronously in the timer callback,
so the decision is made at the moment the clock says; and it never looks at all while the gate is
not open or while anything is held or in flight (`IdMap.PendingCount`, which includes an attached
client's forwards and the bridges' pulls, all of them bounded).

**D106 — An idle shutdown regardless of size exists and is off.**
`CLAUDE_ROSLYN_LSP_IDLE_SHUTDOWN_MINUTES` recycles after that much idle time whatever the backend
holds — the issue's second proposal, for a machine running a dozen sessions where four children that
have answered nothing for 22 hours are 1.7 GB nobody is using. Off by default because every relaunch
costs a whole solution load and the growth rule already catches the children that cost real memory;
when both rules are due, this one is the reason given. It needs no baseline and skips the compaction
step, which belongs to the growth rule.
