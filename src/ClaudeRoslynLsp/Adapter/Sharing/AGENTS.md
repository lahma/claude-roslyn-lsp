# Agent instructions: one Roslyn per solution

> **Read this when:** you touch the session file, its lock or the sweep, the pipe host, the
> multiplexing rules, attaching, or what a host tells the processes attached to it.
>
> One of the co-located instruction files indexed from the repository-root
> [`AGENTS.md`](../../../../AGENTS.md); read that first. The host is a component of the two owners in
> [`../AGENTS.md`](../AGENTS.md) (which also has the idle recycle this directory reports on) and
> [`../../Mcp/Engine/AGENTS.md`](../../Mcp/Engine/AGENTS.md); `doctor`'s report of the session
> directory (D94, D107) is in [`../../AGENTS.md`](../../AGENTS.md).

## The rendezvous

**D23 — One Roslyn per solution, shared by whichever verb started first.** Settled by WP9 and
written out as **D85-D94**. Claude Code starts the plugin's `mcp` server at session start and its
`lsp` server lazily, so a session that uses both used to load one solution twice and pay for it
twice in memory — and Roslyn's own `--daemon` shares a process but not a workspace (C29), so the
sharing had to live here. Whichever process gets there first publishes itself in
`<home>/sessions/<key>.json`, serves a named pipe and multiplexes the other onto its backend; the
other attaches and starts nothing. The seam turned out to be exactly the one WP5b predicted: both
halves already take an `IRoslynConnectionFactory` and neither asks what is on the other end of it,
so attaching is one new implementation of a two-member interface rather than a second lifecycle.
`getWorkspaceStatus` answers `engine: "attached"` with the host's pid (D84), measured on the fixture
at **0.0-0.1 s to attach** against 2.4-4.6 s to load, and one 296 MB process instead of two.

**D85 — The rendezvous is a file per solution under the adapter's home, and nothing else.** Two
processes have to find each other with no coordinator and no protocol between them, and the only
things they are known to agree on are the solution path and the home directory (D27). So
`<home>/sessions/<sha256(normalised solution)[..16]>.json` carries the host's pid, this binary's
version, the transport, the pipe name, the solution and a timestamp — readable by a human, needing
no daemon, and surviving a reboot for exactly as long as it takes the next reader's pid check to
notice. Sixteen hex characters because it is a rendezvous on one machine rather than a security
boundary, and a key somebody can copy out of an error message is worth more than the other
forty-eight. The version is checked on every read: a 0.1.0 host and a 0.2.0 attacher would speak the
same LSP right up to the moment one of them changed what it fans out, and that failure would be a
wrong answer rather than a refused connection.

**D86 — A lock file, the pipe up before the file is written, and the launch outside the lock.**
D29's argument again — a named mutex is per-session on Windows and absent across containers sharing
a mounted home — for the race D29 was already about: the plugin starts both servers at the same
instant, so "read the file, and write it if it is missing" is a race both processes win. What is new
is the *order*: the winner starts accepting on its pipe, writes the file, and releases the lock in
the time one file write takes; acquiring and launching Roslyn — a 70 MB download on a first run —
happens afterwards. Holding the lock across the launch would make the loser wait out the whole cold
start before it could even find out where to connect, and the loser instead attaches immediately and
waits behind the host's readiness gate (D46), which is where it would have been waiting anyway.

**D92 — Every failure falls back to a private Roslyn, which is what every version before this one
did.** An unwritable home, a platform that refuses named pipes, a host that published itself and
died between the read and the connect, a session file naming a reused pid: each of them ends in the
inner factory. Sharing is an optimisation over a product that already works, and an optimisation
that can fail a session is not worth having. The pid check is deliberately a filter rather than a
proof — a connect that fails deletes the file and hosts instead, so a reused pid costs one attempt.
`CLAUDE_ROSLYN_LSP_SHARE=off` is the deliberate opt-out, for a machine where a pipe between two
processes is not acceptable.

**D102 — The session file says what the host's backend is doing, is rewritten atomically while live,
and is readable by builds on either side of the change.** `SharedSession` gains `roslynProcessId`,
`state` (`starting` | `running` | `dormant`), `lastActivityAt`, `baselineMegabytes`, `recycles`,
`lastRecycleAt` and `attachedClients` — all optional, so an older reader ignores them and a newer
one tolerates their absence. The factory rewrites the file on every status change the recycler
raises, on every attach and detach, and at most once a minute while there was activity since the
last write. Rewriting a live file made two old shortcuts dangerous: `File.WriteAllBytes` can be read
half done, and `TryRead` deletes what it cannot parse — so a torn read would make an attacher delete
a live host's record and launch a second Roslyn beside it. Writes therefore go to a temporary file
in the same directory and are renamed over the real name (`File.Move(…, overwrite: true)`, retried
twice for a Windows reader holding the file without delete sharing); readers open with
`FileShare.ReadWrite | Delete`; and an unreadable file younger than ten minutes is treated as absent
rather than deleted. Every write happens under the factory's publish lock, which `StopHostingAsync`
also takes, so a heartbeat cannot land after the delete and resurrect the file. **And only while the
file is still this host's (the 0.2.0 review).** `Update` and `DeleteIfOwned` act only when the file
names this process *and* this pipe, checked under the decision lock every publisher holds while it
writes (D86), and a host that finds its file taken over stops publishing for good while it goes on
serving the clients already on its pipe — because a failed attach or an upgrade's reader of another
version can hand the file to a newcomer, and two hosts rewriting it in turn would send each reader
to whichever pipe it named last, or on to a third Roslyn.

**D103 — The session directory is swept by whoever becomes a host, and by `doctor --fix`.** A stale
`.json` was only ever removed by a process that looked up that same solution, and nothing ever
removed a `.lock` — which is how one machine reached 287 files for 17 live hosts.
`SessionRegistry.Sweep` deletes a `.json` whose process is gone — not one merely written by another
version, because a live host of another version is still the rendezvous its own version's other half
attaches through, and during a plugin upgrade both run side by side, so deleting it would launch a
second Roslyn for that solution — or that is unreadable and older than ten minutes; a `.lock` with
no `.json` beside it that can be opened `FileShare.None`, deleted through that very handle with
`FileOptions.DeleteOnClose` so a lock somebody holds is never removed (it cannot be opened in the
first place); and a temporary file older than ten minutes. Plain `doctor` counts what a sweep would
remove and deletes nothing (D94). **Three edges closed by the 0.2.0 review.** The liveness check
takes only "no such process" and "has exited" as gone — a pid it may not open, which on Windows is a
`Win32Exception` from `HasExited` when a service reused it, is alive — and the sweep a new host runs
is wrapped, as is `doctor`'s, because a check that cannot judge must never be what deletes, and that
exception used to fail a host whose file was already published. Lock files are swept on Windows
only, because a Unix delete-on-close unlinks before it unlocks, so a process that opened the old
inode could lock it while a third created and locked a new one, and both would host; an empty lock
file left behind costs a directory entry. And a session file records the host's pid namespace on
Linux (`pidNamespace`, `/proc/self/ns/pid`'s target), which the sweep compares with its own and
leaves alone when they differ, because containers sharing a home (D86) number their processes
differently and a live host in one is "gone" by the pid check in the other; `TryRead` is unchanged,
since a foreign host's pipe is no more reachable than a dead one's and the attach-or-replace path
already handles it.

## The host and its clients

**D87 — The host is a component of its owner, not a session of its own.**
`Adapter/Sharing/ISharedEngineHost` is six members wide — the readiness gate, the document mirror,
the backend's `initialize` result, forward-one-request, notify-the-backend, set-an-option, plus the
stream of everything Roslyn says — and both `AdapterSession` and `OwnedRoslynEngine` implement it
without being restructured. That is the seam D75 declined to cut in WP5b and named as the one worth
cutting here, and it held: the multiplexer owns the pipe, the frames, the per-client id tables and
the document ledger; the owner keeps the one connection to Roslyn, the gate and the configuration.
Nothing about "serving a peer" leaked into the owner, and nothing about "which half am I" leaked
into the multiplexer.

**D88 — The multiplexing rules, and every one of them is a decision.** (1) An attached client's
`initialize` is answered *here*, out of the result Roslyn gave the host, with `serverInfo` replaced
by one naming the host process — never forwarded, because Roslyn has one client and a second
handshake on that connection is a protocol error. Everything else crosses verbatim,
`_roslyn_processId` included (C45), so both halves report the same backend. (2) Requests are
forwarded under the host's own `IdMap` and answered under the id the client used: D47 applied a
second time, because two peers that have never met both count from one. (3) Every forwarded request
passes the host's readiness gate first — a request that arrives over a pipe before the workspace has
loaded is answered empty and successfully by Roslyn (C27), which is no less wrong for having arrived
from another process. (4) Server-to-client *requests* stay host-owned: there is one correct answer
to a registration, a configuration pull or a progress-create, and the host already gives it. (5)
`solution/open` and `project/open` from an attached client are swallowed and answered with
readiness, because the workspace is already open. (6) `shutdown` and `exit` detach that client and
nothing else; the host's own shutdown closes every pipe, which is the backend-gone state each
attached supervisor already knows what to do with (D57).

**D89 — Four notifications are fanned out, and the readiness one is replayed to a late attacher.**
`window/logMessage` and `window/showMessage`, because a session that went quiet is diagnosed from
them; `workspace/projectInitializationComplete`, because it is what opens an attached client's own
gate; and `workspace/diagnostic/refresh`, converted from a request into a notification so the
attached client consumes it without owing anybody an answer under an id its connection never issued.
The replay is not a nicety: a client that attaches after the load would otherwise hold every request
until its own budget ran out on a workspace that finished loading minutes ago. The owner raises
*everything* and decides nothing, so the list lives in one place.

**D90 — An attached engine changes a Roslyn setting through the host, and waits for the answer.**
Roslyn asks the *host* for configuration, so an override set in an attached process's own
`ConfigurationResponder` is a value nobody ever reads — and `getDiagnostics scope: "solution"`
without a `fullSolution` compiler scope answers emptily and successfully (C14), which is the failure
this product exists to remove. So `claude-roslyn-lsp/setOption {section, value}` goes to the host,
which sets the override in the responder Roslyn actually asks and runs D77's round trip. It is a
**request** rather than the notification the plan sketched, because the caller has to know when the
change is in effect: a fire-and-forget would race the very call it was made for.
`Adapter/ConfigurationRoundTrip` is the shared half, extracted from `OwnedRoslynEngine` where it
already existed.

**D91 — Documents are ref-counted per URI, and everything is mirrored.** Roslyn keeps one buffer per
document, so a second `didOpen` is redundant and — much worse — a `didClose` from one client would
silently revert the other's live buffer to what is on disk. So `didOpen` reaches Roslyn on the first
open from anyone, `didClose` on the last, and `didChange` always (the file on disk is shared and the
last writer wins). The ledger records which opens *it* forwarded, rather than asking the mirror,
because the mirror is written for both halves: every attached client's open and edit goes into the
host's `DocumentMirror` so a Roslyn that dies is replayed with all of them (D57), and a check
against the mirror would then read its own entries back as the host's. A client that goes away hands
back what only it was holding. Two attached clients are two independent streams and nothing orders
one against the other, so a `didClose` that overtakes another client's `didOpen` for the same file
produces a close and an immediate reopen — Roslyn reverts to disk for an instant and is then given
the other client's text. That is correct for the messages the host actually saw, and preventing it
would mean a barrier between two peers that have no reason to wait for each other.

**D93 — A host that is already hosting relaunches its child rather than attaching to itself.** When
Roslyn dies the supervisor (D57) asks the factory for another connection — and the session file
still names this process, because the pipe is still accepting with the attached clients' requests
held by the gate. So the factory remembers its host and, when it has one, goes straight to the
launcher. That is also what makes a crash invisible to an attached client: it sees a workspace that
went briefly quiet, not a backend that went away. A request that *was* in flight when the backend
died is re-held and asked again once the gate reopens, which is D74 extended over the pipe for D74's
own reason — everything an attached client forwards is a read. The connection that stops the host is
generation-numbered, so a superseded attempt being disposed after a relaunch cannot withdraw a
session file that names a pipe carrying two clients.

**D105 — The host tells attached clients when its backend changes, and stops claiming a workspace is
loaded while it reloads.** `claude-roslyn-lsp/backendChanged` carries the state, Roslyn's process
id, the recycle count, the baseline and the last recycle time; it is built from the recycler's
status, sent whenever the state, the process or the count moves, sent to a late attacher straight
after its handshake, and listed in D89's fan-out set so that set stays the one place that says what
an attached client sees. An attached `OwnedRoslynEngine` reports the host's process id and recycle
counters from it in `getWorkspaceStatus` (it used to name the dead process after any host relaunch,
because C45's id was only read at its own handshake), and an attached `AdapterSession` resets its
diagnostics bridge on it, because its result ids were issued by the process that went (D57).
`SharedRoslynHost._projectsLoaded` is cleared whenever the backend is anything but running: it used
to be set once and never reset, so a client attaching during a reload was told the workspace was
loaded and had its requests answered empty (C27). Two gaps closed by the 0.2.0 review: an attached
engine forgets its cached workspace report whenever the host's backend is not running or is a
different process or recycle count, because the C58 fallback means "nothing has changed since Roslyn
last answered", which is untrue of a Roslyn that never answered this engine; and an attached
`AdapterSession` pulls its open documents again when the host reports a *new* Roslyn running,
because its own gate never closes during the host's reload and its published diagnostics would
otherwise stay the recycled process's until the next edit.
