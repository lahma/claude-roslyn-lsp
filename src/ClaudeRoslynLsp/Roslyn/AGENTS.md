# Agent instructions: the child process

> **Read this when:** you touch which Roslyn is used and how it arrives — runtime identifiers, the pin
> and its hashes, the cache, the download — or how it is started and kept: the `dotnet` host, the
> command line, the transport, the job object, the stream pumps, solution discovery, the GC settings,
> the compactor.
>
> One of the co-located instruction files indexed from the repository-root
> [`AGENTS.md`](../../../AGENTS.md); read that first — D2, acquire the server at runtime and pin it
> hard, is there. What the mediation does with the connection this directory produces is in
> [`../Adapter/AGENTS.md`](../Adapter/AGENTS.md).

## Which server, from where

**D24 — Eight runtime identifiers, and a host outside them is named rather than corrected.**
`roslyn-language-server` is a 33 KB shim; the payload is `roslyn-language-server.<rid>`, and
Microsoft publishes eight of them — `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`,
`linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, `osx-arm64` (C5, from the shim's own
`DotnetToolSettings.xml`). That list is deliberately longer than this repository's five-RID release
matrix, because the framework-dependent NuGet tool (D22) reaches platforms no archive is built for.
musl is detected from `RuntimeInformation.RuntimeIdentifier` **and** from `/lib/ld-musl-*`, because
a portable build on Alpine reports the RID it was *built* for. `win-x86` and `linux-arm` resolve to
themselves and are reported unsupported: downloading the 64-bit payload for a 32-bit host produces a
child that dies about an image format, which is nobody's idea of a diagnosis.

**D25 — The hash table is per RID; a version override has no hash at all.**
`Roslyn/RoslynServerManifest.cs` carries one base64 SHA-512 per RID for the pinned version, and
`dotnet fallout UpdateRoslynPin` is the only thing allowed to write them — a hash typed in by a
person is a hash nobody verified. `CLAUDE_ROSLYN_LSP_ROSLYN_VERSION` is honoured (somebody debugging
against a newer build should not have to fork the adapter) but that download is verified by TLS
alone, and the result carries `Verified = false` all the way to `doctor` rather than being quietly
equivalent.

**D26 — Command-line feature flags are per Roslyn version.** `--clientProcessId`, `--daemon` and
`--daemonKeepAlive` exist in 5.12 and do not exist in the 5.5 builds still sitting in people's tool
stores. Roslyn parses its command line with `System.CommandLine`, which *exits* on an unknown option
— so a flag passed to the wrong version is not a degraded feature, it is a child that never starts,
explaining itself on a stderr nobody is reading yet. The manifest therefore answers per version: the
pin's flags for the pin, a conservative set (stdio only) for anything else. `UpdateRoslynPin`
derives the pin's by running the downloaded server's own `--help`.

**D32 — Acquisition order is by trust, and only the last step spends bandwidth.** Explicit path →
this adapter's cache → a global tool installation *at exactly the wanted version* → download. The
result carries the winner, the path, the version, whether it was hash-verified, and the full chain
of what was looked at — because "which one did it pick" is the first question of every report about
a machine with more than one.

**D33 — A global tool store at a different version is reported, never used.** The tempting shortcut
is wrong twice over: the CLI changes between builds (D26), and the point of a pin is that a release
is tested against one server. So a 5.5 installation shows up in `doctor`'s chain as *present, not
used, wrong version*, which is the most useful line that report can carry for somebody who is
certain they installed it. A tool store's `.nupkg.sha512` is also **not** the hash of the payload
beside it (C4), so a tool-store hit is never reported as verified.

**D34 — `CLAUDE_ROSLYN_LSP_ROSLYN_PATH` that does not resolve is a failure, not a fall-through.**
Falling back to a download would be friendlier for about ten seconds and then indistinguishable from
the variable having been ignored. The value may name a directory holding
`Microsoft.CodeAnalysis.LanguageServer.dll`, that assembly directly, or any other executable — the
last case is what lets `SmokeTest` point the launcher at this binary's own `fake-roslyn` verb.

**D27 — One home directory, and the platform's cache location is the last resort — not a dotfile.**
`CLAUDE_ROSLYN_LSP_HOME` → `CLAUDE_PLUGIN_DATA` → `%LOCALAPPDATA%` / `~/Library/Caches` /
`$XDG_CACHE_HOME` / `~/.cache`. The scaffold's placeholder documentation said
`~/.claude-roslyn-lsp`; that is wrong for 70 MB compressed and ~140 MB extracted **per version per
RID**, which is cache by every definition an operating system has. On Windows it also keeps the
payload out of a roaming profile, where a corporate policy would try to synchronise a Roslyn build
over the network at every logon. `CLAUDE_ROSLYN_LSP_CACHE_DIR` moves the payloads only; logs and
staged downloads stay under the home.

**D28 — Hash while streaming, extract to a staging directory, rename last.** The bytes are hashed on
the way to disk rather than by re-reading the file, because re-reading is a second chance for the
file to be different from the one that was checked. Extraction goes into a sibling
`<rid>.<guid>.tmp` and is promoted with one `Directory.Move`, so the cache directory either does not
exist or is complete, and the `.complete` marker — holding the accepted hash — is written last. A
half-extracted directory that *looks* finished is how one bad network moment becomes a permanently
broken install that only a manual delete fixes.

**D29 — A lock file, not a named mutex.** Claude Code starts the `lsp` and `mcp` servers as two
processes at the same instant (D23 has not landed), and on a first run both would fetch the same 70
MB. A named mutex is the obvious answer and the wrong one: its name is per-session on Windows and it
does not exist at all across containers sharing a mounted cache. `<cache>/<version>/<rid>.lock`
opened `FileShare.None` is understood by every platform that can host the cache; the loser polls
every 250 ms for up to ten minutes, then re-checks the cache and finds it warm.

**D30 — Everything under `tools/net10.0/<rid>/`, and nothing outside it.** 163 top-level files plus
`BuildHost-net472/`, `BuildHost-netcore/`, `Targets/` and thirteen satellite-resource folders (C1).
An earlier sketch took only the top level; the build hosts are what evaluate project files, so a
server without them loads nothing and says nothing about why. Every entry is checked against the
destination root before it is opened — a `..` segment, a rooted path, a drive qualifier and a
backslash separator are each refused — and a package containing one is rejected whole rather than
extracted in part.

## Bumping the pin

`dotnet fallout UpdateRoslynPin` (optionally `--roslyn-version <v>`, defaulting to the current pin) is
the **only** way `Roslyn/RoslynServerManifest.cs`'s generated block may change (D25). It downloads all
eight RID payloads into `artifacts/roslyn-pin/` — about 560 MB, resumable, existing files are reused —
hashes each one, cross-checks it against nuget.org's catalog leaf (the registration index has no hash;
C3), runs the host RID's server with `--help` to derive the feature flags (D26), and rewrites the
block between the `// <generated pin>` markers with LF endings and no BOM. It then reminds you that
`CHANGELOG.md` names the pinned version — `RoslynServerManifestTests` fails if it does not.

## The launch

**D31 — The adapter resolves the `dotnet` host itself and asks it what it has, once.** `DOTNET_ROOT`
→ every match on `PATH` → the platform's install locations (`C:\Program Files\dotnet`,
`/usr/share/dotnet`, `/usr/lib/dotnet`, `/usr/local/share/dotnet`, `~/.dotnet`), then one
`--list-runtimes` requiring `Microsoft.NETCore.App` **10.x** exactly. Letting the OS resolve
`dotnet` at launch time would turn a missing runtime into a child that exits with a code and a
message on a stderr the client never sees; resolving it here makes it a live failure state with a
`doctor` hint (D10). The result is cached for the process, and `DOTNET_ROOT` is *set* in the child's
environment rather than inherited, because a Native AOT adapter has no host of its own to inherit a
correct one from.

**D38 — Roslyn is launched as `<dotnet> …/Microsoft.CodeAnalysis.LanguageServer.dll`, never through
the apphost beside it.** `roslyn-language-server.exe` is a *thin client* that spawns its own daemon
per instance: two of them gave four processes and no shared workspace (C29). The daemon is not a
shortcut to D23's shared engine, it is a second copy of the problem. Launching the managed assembly
through the host also sidesteps C1, where `Process.Start` refuses a ~290-character executable path
with "file not found" for a file that plainly exists.

**D37 — The Roslyn command line is fixed, and everything standing in for Roslyn must accept it.**
Arguments are: the program, then `CLAUDE_ROSLYN_LSP_ROSLYN_ARGS` (split on whitespace, no quoting
dialect — it exists so the smoke test can add one word), then `--pipe <name>` or `--stdio`,
`--logLevel <level>` (default **Warning**: Roslyn's `Information` is 21-22 `window/logMessage` lines
during startup alone, C6), `--extensionLogDirectory <home>/logs/roslyn`, `--telemetryLevel off`, and
`--clientProcessId <pid>` only where D26 says it exists. WP2's `fake-roslyn` and WP4's fakes have to
tolerate that whole set.

**D15 — How the adapter talks to Roslyn — a named pipe by default, stdio as the fallback.** Roslyn
*connects* to a pipe the adapter created (C8), so the `NamedPipeServerStream` (`InOut`, one
instance, byte mode, `Asynchronous | CurrentUserOnly`) has to exist before the process is started;
the name is `claude-roslyn-lsp-<pid>-<8 hex>` and the connect budget is 30 s. The pipe is preferred
because on it the protocol is a channel nothing else holds a handle to — the pinned build writes a
646-byte banner to its stdout in pipe mode (C7), which under `--stdio` would have been protocol
corruption. `CLAUDE_ROSLYN_LSP_TRANSPORT=stdio` exists for environments with no usable named pipe.
Details in D37 and D38.

**D35 — A Windows Job Object with `KILL_ON_JOB_CLOSE`, and nothing at all elsewhere.** Roslyn
already exits when the process id it was given dies, which covers every orderly failure. What it
does not cover is an adapter killed hard in the window before `initialize`: on Unix the child is in
this process's group and a session teardown reaches it, on Windows the orphan is a 250 MB server
holding a solution open with nobody to talk to. `[LibraryImport]`, not `[DllImport]`, because
`IL2026`/`IL3050` are errors here — which is also why the server project now sets
`AllowUnsafeBlocks` (the generator emits `unsafe` marshalling stubs; no hand-written `unsafe` exists
in the product, and adding one would need its own argument).

**D36 — Every redirected child stream is drained, including the one that "should be empty".** An
unread pipe fills at about 4 KB and blocks the writer inside a `write` it cannot return from —
presenting as a language server that answered three requests and then stopped, with no error, no
exit and no clue. In pipe mode the child's *stdout* is pumped too (C7's banner); in both modes stdin
is redirected even though pipe-mode Roslyn never reads it, because an inherited stdin is **this**
process's stdin, which in `lsp` mode is the client's half of the protocol.

**D50 — The launcher is the default backend, and every fake is behind an explicit opt-in.**
`Roslyn/LaunchedRoslynFactory` is the one class that joins WP3's acquisition chain to WP2's
connection seam — locator, .NET host, launcher, one framed byte channel — and it is what `lsp` uses
unless `--smoke` or `CLAUDE_ROSLYN_LSP_FAKE_BACKEND=child` says otherwise. Neither half had to
change to be joined, which is what the seam was for. The resolution is cached after the first
success, because a relaunch (D57) changes the process and not where the server lives; the `dotnet`
host is resolved once (D31); and download progress goes to the client as a `window/logMessage` every
8 MB, because a first run spends a minute before anything can answer and a client that is told
nothing shows a server that hung.

**D51 — Acquisition failure is a live state with two messages, never a crash.** Offline, no .NET 10
runtime, a proxy serving HTML where a nupkg should be, a hash that does not match the pin: all
ordinary, and all arriving as a `RoslynAcquisitionException` that the session turns into
`ReadinessState.Failed`. The adapter *stays alive* — the handshake stands and every request is
answered `-32603` with a sentence naming `doctor` — because a process that exits during startup
leaves its client with a dead pipe and no channel to be told why (stdout **is** the channel). The
client gets one `window/showMessage` (Error, once) and a `window/logMessage`: the first is what a
client surfaces, the second is what it files where somebody looking for the reason will find it, and
sending either on every subsequent failure would train the reader to dismiss them.

## Which solution

**D39 — An explicit solution setting that does not resolve fails; discovery never runs after one.**
Both `CLAUDE_ROSLYN_LSP_SOLUTION` and `.vscode/settings.json`'s `dotnet.defaultSolution` are
statements of intent. Falling back to a scan when one points at a moved file produces an adapter
that opens a *different* solution and then answers about it with total confidence — the failure this
project exists to remove, reintroduced as a convenience.

**D40 — `dotnet.defaultSolution` is honoured, including `disable`.** Any repository opened in VS
Code with the C# extension has already been asked this question and has checked in the answer;
reading it costs one file — parsed as JSONC, because that file has comments and trailing commas in
it — and removes the commonest reason to set `CLAUDE_ROSLYN_LSP_SOLUTION` at all. `disable` means no
solution, on purpose. A malformed settings file is treated as absent rather than as a startup
failure: it is somebody else's file.

**D41 — Solutions are scored, not taken in the order the walk found them.** Name equal to the root
folder +100, `.slnx` over `.sln` +10, +1 per project (`.sln`: lines beginning `Project("{`; `.slnx`:
`<Project Path=` elements — counted by text, because the package budget has no room for
`Microsoft.Build` and the number is only ever a tie-break). Ties go to the shallower file, then to
ordinal path order. Depth-3 breadth-first walk, skipping `bin obj .git node_modules .vs artifacts
TestResults`.

**D42 — The project fallback keeps every candidate, tests included, capped at 500.** An earlier
sketch trimmed test projects to save load time; that is backwards for this product, because an agent
asked to fix a failing test needs the test project loaded, and a symbol that resolves everywhere
except in tests is worse than a slower load. The cap is a guard against opening a monorepo by
accident, not a curation policy.

## The child's memory

**D79 — Workstation GC is the default for the child, in both verbs.** Roslyn's payload ships
`System.GC.Server: true` (C44), and C54 measured what that costs on OrchardCore: 1,931-2,089 MB of
working set against **577 MB** for workstation, for a load of 20.4 s instead of 28.2 s. WP5b
re-measured it on Quartz.NET (300-343 MB, C60) and on OrchardCore through the MCP half (439-473 MB,
C61). The eight seconds are paid once by somebody who is waiting for a language server to start and
expects to; the one and a half gigabytes are paid by every other process on the machine for as long
as the session lasts — and this product exists because a Claude session is already running a model's
tool calls on the same box. So `DOTNET_gcServer=0` goes into the child's environment unless
`CLAUDE_ROSLYN_LSP_GC=server` asks otherwise, an explicit `DOTNET_gcServer` in the environment wins
over both and is never overwritten, and `doctor` prints which of the three is in effect.

**D104 — The child runs with `DOTNET_GCConserveMemory=7`, by D79's rule.** What the level does was
measured rather than assumed (C66): it caps how far gen2 may grow past its live data before the next
full collection — `f = (10/N − 1)/2 + 1`, about 1.21× at level 7 and 1.5× at 5 (dotnet/runtime
`src/coreclr/gc/dynamic_tuning.cpp`, `desired_new_allocation`) — and the collector keeps free
regions committed in proportion to that budget. After one full collection on Quartz.NET the free
space it kept committed was about 495 MB at the runtime's default, 332 MB at 5 and 183 MB at 7, and
the workload's private bytes ended at 1,465, 1,355 and 1,236 MB. **7 halves the free space the
collector keeps committed after a full collection relative to 5 and costs nothing measurable** —
Roslyn's CPU over the whole run was 426, 413 and 423 s, less than run-to-run noise — while 9 was not
measured and leaves the collector almost no headroom. It does not by itself make an idle child
collect; the compactor does that (D101). `CLAUDE_ROSLYN_LSP_GC_CONSERVE` (0-9) sets the level, the
default is the one named constant `DefaultGcConserveMemory`, `0` sets nothing at all — different
from setting `0`, which would override a `runtimeconfig.json` knob — and a `DOTNET_GCConserveMemory`
already in the environment is passed through untouched, exactly as `DOTNET_gcServer` is. `doctor`
prints the effective value and which of the three it came from. An inherited
`COMPlus_GCConserveMemory` or `COMPlus_gcServer` is just as explicit (the 0.2.0 review), because the
runtime reads every setting under both prefixes and the `DOTNET_` spelling wins, so writing it
beside the user's older one would silently overrule it; `doctor` names whichever spelling is set.

**D101 — An idle child is asked to compact before anything is recycled, over the runtime's own
diagnostics channel.** `Roslyn/DiagnosticsIpcCompactor` speaks the .NET diagnostics IPC protocol
(dotnet/diagnostics `documentation/design-docs/ipc-protocol.md`): the named pipe
`dotnet-diagnostic-{pid}` on Windows, the socket `$TMPDIR/dotnet-diagnostic-{pid}-{key}-socket`
elsewhere, a 20-byte header, and one `CollectTracing2` that enables only the runtime provider's
`GCHeapCollect` keyword (`0x800000`) with rundown off. Enabling that keyword is
`ETW::GCLog::ForceGCForDiagnostics` — a blocking max-generation collection (dotnet/runtime
`src/coreclr/vm/eventtrace_gcheap.cpp`) — which compacts when gen2 is fragmented
(`src/coreclr/gc/plan_phase.cpp`, `decide_on_compacting`); the `OK` arrives after it, about 0.9 s on
a 1 GB heap, and the session is stopped at once over a second connection while the first is drained.
Measured on Quartz.NET over an agentic workload (C66): one forced collection took private bytes from
1,465 to 1,366 MB at the runtime's default, from 1,355 to 1,179 MB at `DOTNET_GCConserveMemory=5`
and from 1,236 to 1,028 MB at 7 (D104), leaving 532-555 MB live — so compaction is worth 100-200 MB
per idle session with no reload on top of what the conserve level already saved, and it makes the
recycle decision measure what the workspace retains instead of what the collector has not yet given
back. It runs once per idle period, when idle reaches the earlier of five minutes and half the
recycle window, only when the growth recycle is on and a baseline exists, and only for a child this
process launched — both verbs pass it for the real launcher alone, because the scripted backends
report a process id that is not theirs and forcing a collection in whatever process holds it would
be a side effect on somebody else's program; the next sample is taken ten seconds later and the
decision sees it. Pure BCL (hard rule 1): a pipe or a socket and a few little-endian integers,
against `Microsoft.Diagnostics.NETCore.Client`'s whole client. Every failure — no endpoint,
`DOTNET_EnableDiagnostics=0`, an `HRESULT`, a broken pipe — is `false` with a debug line inside one
30-second budget, and the policy then behaves exactly as the plain recycle. Since the 0.2.0 review
it is also checked per connection: the compactor runs only when C45's id equals the process the
connection launched (`RoslynConnection.LaunchedProcessId`, set by the launcher and passed through by
the sharing wrapper), and a mismatch is logged once and never compacted, because the real launcher
can be fronted by a wrapper whose announced id is somebody else's process.
