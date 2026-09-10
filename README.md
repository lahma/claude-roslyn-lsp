# claude-roslyn-lsp

Claude Code's C# story is broken in a specific, fixable way. The official `csharp-lsp` plugin
launches `csharp-ls` from `PATH` and installs nothing, so on most machines the language-server tool
never activates at all; and even when a server does start, Claude Code's LSP client refuses
`client/registerCapability`, never sends Roslyn's `solution/open`, never waits for
`workspace/projectInitializationComplete`, and consumes only *push* diagnostics — which Microsoft's
Roslyn server does not send. The result is an agent that reaches for `grep` and `sed` on a language
that has had exact, semantic answers for twenty years.

`claude-roslyn-lsp` is a single .NET 10 Native AOT binary that closes both halves of that gap. As an
LSP server it fronts Microsoft's own `roslyn-language-server` — acquiring it, launching it, and
mediating the protocol so that every operation Claude Code's LSP tool offers works, with edit-time
diagnostics bridged from Roslyn's pull model to the push model the client understands. As an MCP
server it offers the mutating half the LSP tool has no slot for: solution-wide semantic rename, code
actions, fix-all by diagnostic id, formatting and on-demand diagnostics, addressed by symbol name
rather than by a line number the model had to grep for first. One binary also serves GitHub Copilot
CLI and OpenCode over LSP, and Codex, Gemini CLI, Cursor and VS Code over MCP.

> **Status: under construction.** The `lsp` verb is the real adapter — it answers the handshake from
> its own capability document, holds requests until the workspace reports itself loaded, answers
> Roslyn's registrations, configuration and progress on the client's behalf, and forwards everything
> else with only the JSON-RPC id rewritten. Acquisition works end to end: `claude-roslyn-lsp install`
> downloads and hash-verifies the pinned Roslyn server, and `claude-roslyn-lsp doctor` reports the
> whole resolution chain and then starts the real server and completes a handshake with it. The two
> halves are not yet wired together, so the only backend the adapter talks to is the scripted one
> behind `lsp --smoke`; the `mcp` verb completes the MCP handshake and registers no tools. This file
> is a placeholder so that the NuGet package and the release archives ship with the README they
> reference — the full document is written against the frozen tool surface.

## Installing

_To be written: the Claude Code plugin (marketplace and `/plugin install`), the Native AOT archives
from GitHub Releases, and `dnx claude-roslyn-lsp`._

## Using it

_To be written: the verbs (`lsp`, `mcp`, `doctor`, `install`), what each one is launched by, and what
to expect on a first run
against a large solution._

## Configuration

Environment variables only — there is no configuration file. A client launches this binary with an
environment block and nothing else, and reading configuration **never** fails startup: a malformed
value falls back to its documented default and says so on stderr, because stdout is the protocol
channel and a dead process has nowhere to explain itself.

| Variable | Default | What it does |
|---|---|---|
| `CLAUDE_ROSLYN_LSP_SOLUTION` | discovered | The `.slnx`, `.sln` or `.csproj` to open. A path that does not exist **fails loudly** rather than falling back to discovery. |
| `CLAUDE_ROSLYN_LSP_ROSLYN_PATH` | — | A directory holding `Microsoft.CodeAnalysis.LanguageServer.dll`, that assembly, or another executable to run instead. First in the resolution chain; its version is not checked. Alias: `_SERVER_PATH`. |
| `CLAUDE_ROSLYN_LSP_ROSLYN_VERSION` | the pin | Fetch a different `roslyn-language-server` version. There is no hash for it, so `doctor` reports it as unverified. Alias: `_SERVER_VERSION`. |
| `CLAUDE_ROSLYN_LSP_ROSLYN_ARGS` | — | Extra arguments for the Roslyn child, split on whitespace. |
| `CLAUDE_ROSLYN_LSP_HOME` | platform cache dir | Where downloads, logs and lock files live. Falls back to `CLAUDE_PLUGIN_DATA`, then `%LOCALAPPDATA%\claude-roslyn-lsp`, `~/Library/Caches/claude-roslyn-lsp`, `$XDG_CACHE_HOME/claude-roslyn-lsp` or `~/.cache/claude-roslyn-lsp`. |
| `CLAUDE_ROSLYN_LSP_CACHE_DIR` | `<home>/roslyn` | Moves the downloaded servers only; logs stay under the home. |
| `CLAUDE_ROSLYN_LSP_OFFLINE` | `0` | Never download. A missing server becomes an explained failure instead of a 70 MB fetch. |
| `CLAUDE_ROSLYN_LSP_TRANSPORT` | `pipe` | `pipe` or `stdio`. The pipe keeps the protocol on a channel nothing else can write to. |
| `CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS` | `120` | How long a request waits for the workspace to load before passing through anyway. |
| `CLAUDE_ROSLYN_LSP_DIAGNOSTICS` | `1` | The pull-to-push diagnostics bridge. |
| `CLAUDE_ROSLYN_LSP_DIAGNOSTIC_MIN_SEVERITY` | `warning` | The severity floor applied before diagnostics are published. |
| `CLAUDE_ROSLYN_LSP_WORKSPACE_DIAGNOSTICS` | off | Opt-in workspace-wide diagnostics for files that are not open. |
| `CLAUDE_ROSLYN_LSP_FILE_WATCHER` | `1` | The file-watch bridge. Without it, a file created by a shell command never joins its project. |
| `CLAUDE_ROSLYN_LSP_LOG_LEVEL` | `Information` | This adapter's own stderr logger. |
| `CLAUDE_ROSLYN_LSP_ROSLYN_LOG_LEVEL` | `Warning` | The level handed to Roslyn. Its `Information` is about twenty lines of narration per start. |
| `CLAUDE_ROSLYN_LSP_OPTIONS` | — | A JSON object merged into the answers given to Roslyn's `workspace/configuration` requests. |
| `CLAUDE_ROSLYN_LSP_LIVE_TESTS` | — | Development only: runs the tests that download and launch the real server. |

Booleans accept `1/true/yes/on` and `0/false/no/off`, in any case.

**The plugin-option rule.** Every one of these is also read as
`CLAUDE_PLUGIN_OPTION_<NAME>`, **first**, with a blank value treated as absent. A Claude Code plugin
manifest substitutes an unfilled option as the empty string rather than omitting it, so mapping one
straight onto the plain name would set it to `""` in the child process and shadow whatever the user's
environment already said. `CLAUDE_ROSLYN_LSP_HOME` is the single exception: the manifest sets it
directly to `${CLAUDE_PLUGIN_DATA}`, which is a real directory Claude Code owns.

## What the MCP tools do

_To be written: the tool table, with the read/write and preview conventions._

## Other clients

_To be written: Copilot CLI, OpenCode, Neovim, Helix and Zed over LSP; Codex, Gemini CLI, Cursor and
VS Code over MCP._

## How it works

### Acquisition and pinning

This binary contains no compiler. It runs Microsoft's `roslyn-language-server` as a child process,
and it fetches that server itself the first time it is needed — pinned to one exact version,
**5.12.0-1.26426.8**, whose SHA-512 for every published platform is checked into this repository.

Bundling the server would multiply every release archive by an order of magnitude, and asking the user
to install it would reproduce the exact failure this project exists to fix: a plugin that launches a
binary nobody has. So the first run downloads about 70 MB, verifies it against the pin, and extracts
it into a per-user cache. Afterwards there is nothing to download and nothing to check.

The pin is one constant rather than a floating range because the server is prerelease-only with an
unstable command line: `--clientProcessId`, `--daemon` and `--daemonKeepAlive` exist in 5.12 and do
not exist in the builds a year older, and Roslyn *exits* on an option it does not recognise. A
floating version is a release that breaks on somebody else's schedule.

Where the server comes from, in order:

1. **`CLAUDE_ROSLYN_LSP_ROSLYN_PATH`**, if set. Its version is not checked — you asked for it. A value
   that does not resolve is an error, not a fall-through to a download: a variable that silently does
   nothing is the one configuration bug you cannot diagnose.
2. **The cache**, `<home>/roslyn/<version>/<rid>/`, if a `.complete` marker there records the hash the
   pin expects.
3. **A global `dotnet tool` installation**, but only at exactly the pinned version. One at a different
   version is *reported* by `doctor` and not used.
4. **nuget.org**, streamed straight to disk and hashed on the way, extracted into a staging directory
   and renamed into place so that the cache is never half-populated. A lock file serialises the two
   processes Claude Code starts at the same instant, so a first run downloads once, not twice.

`CLAUDE_ROSLYN_LSP_OFFLINE=1` turns step 4 into an explanation instead of a fetch. Eight platforms are
published (`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`,
`osx-x64`, `osx-arm64`); on anything else, `doctor` says so rather than downloading something that
cannot run.

Roslyn needs a **.NET 10 runtime**, which this adapter does not bundle. It resolves the `dotnet` host
itself — `DOTNET_ROOT`, then `PATH`, then the usual install locations — and asks it for its runtime
list before launching anything, so a missing runtime is a sentence in a report rather than a child
process that dies without one.

### Checking, and installing on purpose

```sh
claude-roslyn-lsp doctor      # report everything, then start Roslyn and complete a handshake
claude-roslyn-lsp install     # the same, but download the server if it is missing
claude-roslyn-lsp doctor --json
```

`doctor` prints the adapter's version and platform, the `dotnet` host and its runtimes with the .NET 10
ones marked, the whole Roslyn resolution chain with the winner and whether its bytes were verified, the
cache directory and its free space, which solution would be opened and how it scored against the other
candidates, and whether Claude Code's language-server tool is enabled — plus a warning if the official
`csharp-lsp` plugin is also enabled, because the first plugin registered for `.cs` wins.

Then it launches the server it just described and completes a real `initialize`/`shutdown`/`exit`, and
reports what the server said and how long each step took. **It exits 0 only if that worked.** Every
static check above it can pass on a machine where Roslyn still does not start.

`doctor` never downloads; `install` (equivalently `doctor --fix`) is the same command with the download
allowed. Through `dnx`, use `doctor` rather than `--version`: `dnx` consumes `--version` itself.

### Which solution gets opened

`CLAUDE_ROSLYN_LSP_SOLUTION` decides it outright, and a path that is not there is an error rather than
a licence to guess. Failing that, `.vscode/settings.json`'s `dotnet.defaultSolution` is honoured —
any repository that has been opened in VS Code has already answered this question — including its
`disable` sentinel.

Otherwise the adapter looks: three directories deep, skipping `bin`, `obj`, `.git`, `node_modules`,
`.vs`, `artifacts` and `TestResults`, and scores what it finds. A name matching the repository folder
is worth 100, `.slnx` over `.sln` is worth 10, and each project listed is worth one; ties go to the
shallower file. With no solution at all it opens every `.csproj` it can find, up to 500 — test projects
included, because an agent asked to fix a failing test needs the test project loaded. `doctor` prints
the candidates and their scores, so a wrong choice is visible rather than mysterious.

_To be written: the readiness gate, the diagnostics bridge, file watching and crash recovery._

### Known limitation: two Roslyn processes

Until the shared-engine work lands, running both the LSP server and the MCP server against the same
solution starts **two** Roslyn instances, and each one loads the whole solution. On a large
repository that is double the memory and double the load time. Both halves work; the cost is real and
is stated here rather than discovered. Attaching one to the other is the last work package of this
version, and until it ships the honest advice for a very large solution is to enable one of the two.

## Building

```sh
./build.sh Test          # restore, compile, run the tests
./build.sh SmokeTest     # publish the Native AOT binary and drive both real stdio handshakes
```

On Windows use `.\build.ps1` with the same arguments. The build is
[Fallout](https://fallout.build), pinned in `.config/dotnet-tools.json`; `CHANGELOG.md` is the
version authority. `AGENTS.md` is the design document.

## Prior art

Read for facts, credited here, and not copied from — the code in this repository is its own:

- [SamHurne/roslyn-lsp-adapter](https://github.com/SamHurne/roslyn-lsp-adapter) — an adapter with the
  same premise.
- [erinloy/claude-roslyn-lsp](https://github.com/erinloy/claude-roslyn-lsp) — the name collision is
  not an accident; it is the same idea, arrived at independently.
- `ClaudeCodeRoslynLspProxy` on nuget.org — a relay in front of the same server.
- [oraios/serena](https://github.com/oraios/serena) — semantic tooling for agents over many
  languages, and the clearest statement of why name-addressed beats position-addressed.
- [seblyng/roslyn.nvim](https://github.com/seblyng/roslyn.nvim) — the reference for how
  `roslyn-language-server` actually wants to be launched and talked to.
- [razzmatazz/csharp-language-server](https://github.com/razzmatazz/csharp-language-server)
  (`csharp-ls`) — the honest baseline this project has to beat, and the server the official plugin
  points at.

Microsoft's [`roslyn-language-server`](https://www.nuget.org/packages/roslyn-language-server) is the
engine; it is MIT-licensed and is downloaded, never vendored.

## Licence

MIT — see [LICENSE](LICENSE).
