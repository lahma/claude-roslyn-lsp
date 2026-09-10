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

> **Status: under construction.** This release is the repository scaffold. The `lsp` verb answers the
> LSP handshake and refuses everything else; the `mcp` verb completes the MCP handshake and registers
> no tools; `doctor` and `fake-roslyn` report that they are not implemented yet. Nothing downloads or
> launches Roslyn. This file is a placeholder so that the NuGet package and the release archives ship
> with the README they reference — the full document is written against the frozen tool surface.

## Installing

_To be written: the Claude Code plugin (marketplace and `/plugin install`), the Native AOT archives
from GitHub Releases, and `dnx claude-roslyn-lsp`._

## Using it

_To be written: the four verbs, what each one is launched by, and what to expect on a first run
against a large solution._

## Configuration

_To be written: the `CLAUDE_ROSLYN_LSP_*` variables, their defaults, and the
`CLAUDE_PLUGIN_OPTION_*` precedence rule._

## What the MCP tools do

_To be written: the tool table, with the read/write and preview conventions._

## Other clients

_To be written: Copilot CLI, OpenCode, Neovim, Helix and Zed over LSP; Codex, Gemini CLI, Cursor and
VS Code over MCP._

## How it works

_To be written: acquisition and the pinned Roslyn version, solution discovery, the readiness gate,
the diagnostics bridge, file watching and crash recovery._

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
