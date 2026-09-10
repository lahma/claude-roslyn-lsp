# claude-roslyn-lsp

Microsoft's Roslyn language server, mediated so that a coding agent can actually use it — as an
**LSP server** for the agents that speak LSP, and as an **MCP server** for the refactorings LSP
clients have no button for. It contains no compiler. Roslyn runs in a child process this binary
downloads, launches and supervises, which is the whole design: reimplementing C# analysis is not
something one person maintains, and Microsoft now publishes the real engine as a public MIT dotnet
tool. The only part nobody had done is the mediation.

Claude Code's C# story is broken in a specific, fixable way. The official `csharp-lsp` plugin
launches `csharp-ls` from `PATH` and installs nothing, so on most machines the language-server tool
never activates at all. And even when a server does start, Claude Code's LSP client refuses
`client/registerCapability`, answers `workspace/configuration` only when a settings block happens to
be present, never sends Roslyn's `solution/open`, never waits for
`workspace/projectInitializationComplete` — so the first several seconds of every session answer
"no definition found" rather than an error — and consumes only *push* diagnostics, which Roslyn does
not send. The result is an agent that reaches for `grep` and `sed` on a language that has had exact,
semantic answers for twenty years.

This binary supplies each of those: it acquires and launches Roslyn, answers the registrations,
configuration and progress requests on the client's behalf, opens the solution, holds requests until
the workspace has actually loaded, and bridges Roslyn's pull diagnostics onto the push model the
client understands. On top of that, the `mcp` verb exposes the mutating half no LSP client offers a
model — solution-wide semantic rename, code actions, fix-all by diagnostic id, formatting and
diagnostics on demand — addressed **by symbol name** rather than by a line number the model had to
grep for first.

One binary serves all of it: Claude Code (both protocols, as a plugin), GitHub Copilot CLI and
OpenCode over LSP, and Codex CLI, Gemini CLI, Cursor, VS Code and Claude Desktop over MCP. It is
written in C# on .NET 10 and ships as a Native AOT executable per platform. The runtime dependency
tree is three packages, all from Microsoft or the official MCP organisation; there is not one
`Microsoft.CodeAnalysis.*` reference in it, and there never will be. MIT licensed.

## What it does

```
claude-roslyn-lsp lsp        The LSP server. What an editor or an agent's LSP client launches.
claude-roslyn-lsp mcp        The MCP server: ten tools, over stdio.
claude-roslyn-lsp doctor     Report everything, then start Roslyn and complete one handshake.
claude-roslyn-lsp install    The same, with the download allowed.
```

There is **no default verb**. Both servers speak a different protocol on the same stdout, so a
client that forgot the subcommand would be connected to the wrong one and simply hang; a bare
invocation exits 2 with the usage text on stderr instead.

- **`lsp`** mediates the session in both directions. It answers `initialize` immediately from its own
  authored capability document, opens the discovered solution, holds semantic requests until Roslyn
  reports the workspace loaded, answers the requests Roslyn makes that Claude Code refuses, and
  forwards everything else with only the JSON-RPC id rewritten — no reserialisation, so a result
  crosses byte for byte.
- **`mcp`** is the refactoring surface: `resolveSymbol`, `findReferences`, `getTypeMembers`,
  `getDiagnostics`, `getCodeActions`, `applyCodeAction`, `renameSymbol`, `fixDiagnostics`,
  `formatCode`, `getWorkspaceStatus`. Four of them write files; all four take `preview`. It runs a
  Roslyn of its own — launched, supervised and file-watched exactly as the `lsp` half's is — and it
  starts loading the solution as soon as the client finishes the MCP handshake rather than when the
  first tool is called, because a client starts its servers when a session starts and may not ask a
  C# question for minutes. Until the workspace is loaded every tool answers `status: "loading"`
  instead of hanging, and `getWorkspaceStatus` says how far it has got.
- **`doctor`** is the support report and the exit code that means something: 0 only if Roslyn is
  runnable right now, established by launching it and completing a real handshake.

**What to expect on a first run.** The first `lsp` session for a machine downloads about 70 MB of
Roslyn and extracts about 140 MB, reporting progress to the client as it goes; every later session
starts from the cache. Then the solution loads, and everything asked during that window is held and
answered afterwards rather than answered empty. Measured through Claude Code on this machine:

| | Quartz.NET, 30 projects | OrchardCore, 239 projects |
|---|---|---|
| Handshake (answered by the adapter, not by Roslyn) | 22-30 ms | 24-63 ms |
| Workspace loaded | 5.9 s | 20 s warm, 46 s colder |
| Go to definition, once loaded | 2 ms warm | 4 ms warm |
| Find references | 11 s (278 hits) | 45 s (272 hits) |
| Roslyn memory, workstation GC (the default) | 0.30-0.34 GB | 0.44-0.47 GB |
| Roslyn memory, `CLAUDE_ROSLYN_LSP_GC=server` | 0.6-0.9 GB | 1.9-2.1 GB |

A repository that has never been restored takes longer, because Roslyn restores it itself before it
can load anything.

**The child runs with the workstation garbage collector by default**, although Roslyn's own
configuration asks for the server one. On OrchardCore that is the difference between about 1.9 GB
and **577 MB**, for roughly eight seconds more on the load — and this adapter exists to be run
beside a model's tool calls on the same machine, so the memory is the side worth taking. Set
`CLAUDE_ROSLYN_LSP_GC=server` to buy the seconds back; an explicit `DOTNET_gcServer` in the
environment wins over both and is never overwritten, and `doctor` prints which of the three is in
effect.

**A solution-wide `getDiagnostics` is not a one-second call.** The per-file pass is; the first
solution-wide one compiles every project, which took about a minute on Quartz.NET's 30 and did not
finish inside the 120-second budget on OrchardCore's 239 — where it is refused with a sentence
naming `scope: "project"` rather than answered with an empty list. Use file or project scope after
an edit, and solution scope when you actually want the whole picture.

The 120-second readiness budget (`CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS`) has roughly 2.5x headroom
on a 239-project solution; raise it if yours is larger or is being loaded for the first time.

## Prerequisites

**A .NET 10 runtime, and in practice the .NET 10 SDK.** Roslyn is a .NET 10 application and this
adapter does not bundle a runtime for it: it resolves the `dotnet` host itself — `DOTNET_ROOT`, then
every match on `PATH`, then the platform's install locations — and asks it for its runtime list
*before* launching anything, so a missing runtime is a sentence in a report rather than a child
process that dies about a framework nobody was watching for. That check is what `doctor` prints, and
it wants `Microsoft.NETCore.App` 10.x.

The SDK is what you want in practice, for two reasons that are not this adapter's doing: Roslyn
evaluates project files and restores packages server-side, which is MSBuild's job, and `dnx` — the
launch channel the Claude Code plugin uses — is part of the SDK. Anyone doing C# work has it.

The adapter itself is self-contained. The Native AOT binary has no runtime dependency at all.

## Install

Two channels. **Claude Code gets the plugin**, which carries both servers and the workflow skill and
runs them through `dnx` — no download step, and the SDK is required for C# work anyway. **Every
other client gets the Native AOT binary** from GitHub Releases: a file-based client wants an
absolute path and a start measured in milliseconds.

### Claude Code: the plugin

This repository is its own [plugin marketplace](https://code.claude.com/docs/en/plugin-marketplaces),
so one install delivers the LSP server, the MCP server and the skill:

```
/plugin marketplace add lahma/claude-roslyn-lsp
/plugin install claude-roslyn-lsp@claude-roslyn-lsp
```

or, without starting a session:

```bash
claude plugin marketplace add lahma/claude-roslyn-lsp
claude plugin install claude-roslyn-lsp@claude-roslyn-lsp
```

**Then disable the official C# plugin.** The first plugin registered for an extension wins, and
marketplace plugins register before everything else — so with `csharp-lsp@claude-plugins-official`
enabled, `.cs` goes to it and this adapter is never launched. The symptom is
`Command 'csharp-ls' not found`, or nothing at all.

```bash
claude plugin disable csharp-lsp@claude-plugins-official
```

In a session, `/plugin` → *Manage plugins* → `csharp-lsp` → *Disable* does the same thing.

Enabling asks for an optional solution path, Roslyn version, Roslyn path and log level; every field
is optional and each maps to the environment variable of the same name in *Environment variables*
below. Leave them blank and nothing is set — a blank plugin option is treated as absent rather than
as an empty value, which is not the default behaviour and is the subject of a test.

The **first run downloads about 70 MB** of Roslyn into the plugin's own data directory and verifies
it against a checked-in SHA-512. Expect the first session to take a few seconds longer to answer;
afterwards there is nothing to download and nothing to check.

The plugin is **pinned to the release it shipped with** rather than floating, so the skill and the
binary it describes always move together. `/plugin update` picks up the next release.

### Claude Code: without the marketplace

For a locally built binary, or to try a change without publishing anything, point Claude Code at a
plugin directory:

```bash
claude --plugin-dir /path/to/claude-roslyn-lsp/docs/clients/claude-code-local-plugin
```

[`docs/clients/claude-code-local-plugin/.claude-plugin/plugin.json`](docs/clients/claude-code-local-plugin/.claude-plugin/plugin.json)
is a complete manifest for that: edit the two `command` values to the absolute path of your binary.
**Use forward slashes on Windows** (`D:/tools/claude-roslyn-lsp.exe`) or escape the backslashes — the
manifest is JSON, and an unescaped `D:\Work\...` makes it fail to load *silently*, with no plugin and
no error.

The official plugin still wins the `.cs` extension. To sideline it for one run without touching your
settings:

```bash
claude --settings '{"enabledPlugins":{"csharp-lsp@claude-plugins-official":false}}'
```

A local plugin directory cannot carry the skill, because a plugin may not reference files outside its
own root. Install it separately — see [Agent skill](#agent-skill) — or work inside a checkout of this
repository, where `.claude/skills/` is loaded as a project skill automatically.

### Native AOT binary

Download the archive for your platform from
[GitHub Releases](https://github.com/lahma/claude-roslyn-lsp/releases) and extract it. Each archive
is named `claude-roslyn-lsp-{version}-{rid}` and contains the executable, `LICENSE` and this
`README.md`.

| Platform | RID | Archive |
|---|---|---|
| Windows x64 | `win-x64` | `claude-roslyn-lsp-{version}-win-x64.zip` |
| Windows ARM64 | `win-arm64` | `claude-roslyn-lsp-{version}-win-arm64.zip` |
| Linux x64 | `linux-x64` | `claude-roslyn-lsp-{version}-linux-x64.tar.gz` |
| Linux ARM64 | `linux-arm64` | `claude-roslyn-lsp-{version}-linux-arm64.tar.gz` |
| macOS Apple silicon | `osx-arm64` | `claude-roslyn-lsp-{version}-osx-arm64.tar.gz` |

```bash
tar -xzf claude-roslyn-lsp-0.1.0-linux-x64.tar.gz
chmod +x claude-roslyn-lsp
./claude-roslyn-lsp install
```

Every archive is built *and handshake-tested on its own architecture* before it is uploaded — the
arm64 legs run on arm64 runners rather than being cross-compiled — so no binary ships that has not
answered both protocols on the hardware it targets.

Note the absolute path: that is what every configuration file below needs. The Roslyn server the
binary downloads is a separate matter and covers more platforms than this table does — see
*Acquisition and pinning*.

### NuGet package (dnx)

The same binary is published to nuget.org as
[`claude-roslyn-lsp`](https://www.nuget.org/packages/claude-roslyn-lsp), a .NET tool package carrying
the `McpServer` package type. `dnx` — part of the .NET 10 SDK — downloads and runs it in one step:

```bash
dnx claude-roslyn-lsp@0.1.0 --yes doctor
```

`--yes` accepts the download prompt and is consumed by `dnx` itself; everything after it reaches the
adapter. Pin the version rather than floating: a language server is something an agent runs on your
behalf, and a pinned version is one you decided to run.

**Never `dnx claude-roslyn-lsp@0.1.0 --version`.** `dnx` takes `--version` for itself, along with
`-v`, `--verbosity`, `--prerelease`, `--source`, `--interactive`, `-h` and about eight others, and
prints its own usage to stdout on a parse error. Use `doctor`, which reports the version along with
everything else.

Releases are pushed to nuget.org by a workflow only a `v*.*.*` tag can start, using
[trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): the workflow
exchanges its GitHub OIDC token for an API key that lives minutes. There is no NuGet API key stored
in this repository, so there is none to leak.

### GitHub Copilot CLI

Copilot CLI reads LSP servers from `~/.copilot/lsp-config.json` (yours, every repository) and
`.github/lsp.json` (the repository's, checked in and shared). The repository-level file wins where
both name the same server.

```json
{
  "lspServers": {
    "csharp": {
      "command": "/usr/local/bin/claude-roslyn-lsp",
      "args": ["lsp"],
      "fileExtensions": { ".cs": "csharp" },
      "requestTimeoutMs": 180000
    }
  }
}
```

`fileExtensions` is a **map** from extension to language id, not a list — a list is the mistake that
produces a server that loads and is never asked anything. `requestTimeoutMs` defaults to 90 s and is
raised here because a first request can be waiting on a solution load. `/lsp test csharp` checks the
entry and `/lsp reload` picks up an edit without restarting the CLI.

Copies of both files are in [`docs/clients/copilot-cli/`](docs/clients/copilot-cli), and
[`.github/lsp.json`](.github/lsp.json) in this repository is the same thing pointed at the published
package — this project uses itself.

For the MCP half, `~/.copilot/mcp-config.json`:

```json
{
  "mcpServers": {
    "roslyn": {
      "type": "local",
      "command": "/usr/local/bin/claude-roslyn-lsp",
      "args": ["mcp"],
      "tools": ["*"]
    }
  }
}
```

`type` is required.

### OpenCode

`opencode.json` in the project root, or `~/.config/opencode/opencode.json` globally:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "lsp": {
    "csharp": {
      "command": ["/usr/local/bin/claude-roslyn-lsp", "lsp"],
      "extensions": [".cs"]
    }
  }
}
```

The command is one array, executable and arguments together. Reusing the key `csharp` **replaces**
OpenCode's built-in C# server rather than adding a second one that also claims `.cs`; that is
deliberate. Note that OpenCode v2 parses the `lsp` block for forward compatibility and does not run
language servers at all, so this applies to v1.

### Neovim, Helix, Zed

All three are standard stdio LSP clients and the adapter is a standard stdio LSP server, so these
should work. They are **untested** — nobody has run them — and are written from each editor's
documentation. Reports welcome.

**Neovim** 0.11+, where `vim.lsp.config` is core and `nvim-lspconfig` is not required:

```lua
vim.lsp.config('claude_roslyn_lsp', {
  cmd = { '/usr/local/bin/claude-roslyn-lsp', 'lsp' },
  filetypes = { 'cs' },
  root_markers = { '.git' },
})

vim.lsp.enable('claude_roslyn_lsp')
```

`.git` is the marker on purpose: the adapter does its own solution discovery from the root it is
given, and the repository root is the root it wants.

**Helix**, in `~/.config/helix/languages.toml`:

```toml
[language-server.claude-roslyn-lsp]
command = "/usr/local/bin/claude-roslyn-lsp"
args = ["lsp"]

[[language]]
name = "c-sharp"
language-servers = ["claude-roslyn-lsp"]
```

The language is `c-sharp`, hyphenated, and `language-servers` **replaces** Helix's default list
rather than extending it — which is what you want here, but is worth knowing.

**Zed** cannot attach a language server it does not already know about; a new id is rejected by the
settings schema. What it can do is replace the binary behind the C# extension's `roslyn` server, so
install that extension first and then, in `~/.config/zed/settings.json`:

```json
{
  "lsp": {
    "roslyn": {
      "binary": {
        "path": "/usr/local/bin/claude-roslyn-lsp",
        "arguments": ["lsp"]
      }
    }
  },
  "languages": {
    "C#": { "language_servers": ["roslyn", "!omnisharp", "!csharp-ls"] }
  }
}
```

The path must be absolute.

### MCP-only clients

Codex CLI and Gemini CLI have no way to attach an LSP server at all; Cursor, VS Code and Claude
Desktop have their own C# story and want the refactoring tools rather than the navigation. All of
them launch the `mcp` verb over stdio. Every file below is also in
[`docs/clients/`](docs/clients).

**Codex CLI** — `~/.codex/config.toml`:

```toml
[mcp_servers.roslyn]
command = "/usr/local/bin/claude-roslyn-lsp"
args = ["mcp"]
startup_timeout_sec = 60
tool_timeout_sec = 180
```

The table is `mcp_servers`, with an underscore. Both timeouts are raised from their defaults of 10 s
and 60 s, because a first call can be waiting on a solution load for up to two minutes.

**Gemini CLI** — `~/.gemini/settings.json` (or `.gemini/settings.json` in the project):

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/usr/local/bin/claude-roslyn-lsp",
      "args": ["mcp"]
    }
  }
}
```

`mcpServers` stays at the top level; the separate `mcp` object holds policy, not server definitions.

**Cursor** — `.cursor/mcp.json` in the project, or `~/.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/usr/local/bin/claude-roslyn-lsp",
      "args": ["mcp"]
    }
  }
}
```

**VS Code** — `.vscode/mcp.json` in the workspace:

```json
{
  "servers": {
    "roslyn": {
      "type": "stdio",
      "command": "/usr/local/bin/claude-roslyn-lsp",
      "args": ["mcp"]
    }
  }
}
```

**Claude Desktop** — `claude_desktop_config.json` (`%APPDATA%\Claude\` on Windows,
`~/Library/Application Support/Claude/` on macOS):

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/usr/local/bin/claude-roslyn-lsp",
      "args": ["mcp"],
      "env": {
        "CLAUDE_ROSLYN_LSP_SOLUTION": "/home/you/src/YourApp/YourApp.slnx"
      }
    }
  }
}
```

Claude Desktop is the one client here with no workspace of its own, which has two consequences worth
stating before they are discovered. `CLAUDE_ROSLYN_LSP_SOLUTION` is not optional there, because
there is no working directory to discover a solution from. And the four mutating tools will refuse
to write: what bounds a write is the server's own working directory, and Claude Desktop's is not
your repository. Read-only work is what it is good for.

## MCP tools

Ten tools. Six answer questions, four change files. They exist because an LSP client's tool surface
is read-only and position-addressed: it needs a file, a line and a character for every call, which a
model does not have until it has searched for one — and searching for one is the grep habit this
project exists to replace.

| Tool | What it does | Annotations |
|---|---|---|
| `getWorkspaceStatus` | Which solution is open, how far it has loaded, load errors, the projects and their target frameworks, the Roslyn build in use, and the language server's process id and working set. The tool every `loading` note points at. | read-only, idempotent |
| `resolveSymbol` | Finds where a symbol is declared, semantically, with its hover signature and a 1-based position you can hand straight to an LSP tool. Ambiguity returns every candidate rather than a guess. | read-only, idempotent |
| `getTypeMembers` | Lists a type's members and their signatures without reading the file — including for a type that comes from a NuGet package rather than the checkout. | read-only, idempotent |
| `findReferences` | Every semantic reference across the solution, the source line beside each one, a per-file summary, de-duplicated across target frameworks, paged. | read-only, idempotent |
| `getDiagnostics` | Compiler errors and warnings — and, on request, IDE and CA analyzer diagnostics — for a file, a project or the whole solution. About a second for a file; the first solution-wide pass compiles every project and costs a minute or more. | read-only, idempotent |
| `getCodeActions` | The quick fixes and refactorings Roslyn offers at a position: the lightbulb list, each with a short stable id, the diagnostic ids it addresses, and the scopes its fix-all accepts. | read-only, idempotent |
| `applyCodeAction` | Applies one of them, optionally across the document, the project or the whole solution. | **destructive**, not idempotent |
| `renameSymbol` | A solution-wide semantic rename: overrides, interface implementations, other projects. Not a search and replace, and not a file rename. | write, **not** destructive, idempotent |
| `fixDiagnostics` | Roslyn's own fix-all for one diagnostic id (`IDE0005`, `CA1822`, …) across a file, a project or the solution. | **destructive**, idempotent |
| `formatCode` | Formats with Roslyn, honouring `.editorconfig`, optionally organising usings. Never shells out to `dotnet format`. | write, **not** destructive, idempotent |

Every tool declares `openWorldHint: false` — nothing here leaves the machine, let alone the
workspace — and returns structured content. `applyCodeAction` and `fixDiagnostics` are marked
destructive because a code action can delete a file and a fix-all rewrites a solution; `renameSymbol`
and `formatCode` deliberately are not, because putting a confirmation prompt in front of the two
operations this product most wants a model to reach for is how a user learns to click through the
prompts that matter. The full annotation table, and the reasoning for each judgement call, is in
[AGENTS.md](AGENTS.md).

Four conventions run through all ten:

- **Lines and columns are 1-based**, in and out, and paths are workspace-relative with forward
  slashes. LSP counts from zero; every editor, compiler error and stack trace in the C# world counts
  from one. The conversion happens here.
- **Anything taking a `symbol` accepts a name or a position** — `IScheduler.Start`, matched as a
  dot-segment suffix, or `src/Core/Calculator.cs:7:15`. The name form is the one that matters: it
  survives an edit that moved the declaration, and a model that can write it never greps for a line
  number first.
- **The four mutating tools take `preview: true`**, which writes nothing and returns a diff. Calling
  the same tool again with `preview: false` applies exactly what was previewed rather than
  recomputing it — and if a file moved underneath in between, the plan is dropped and re-resolved
  rather than applied to something else.
- **They write to disk immediately, and say so.** Every applied result ends with one fixed sentence
  asking you to re-read the changed files, because Claude Code's own `Edit` tool refuses a file that
  changed since its last read and its refusal does not explain why.

Until the solution has loaded, every tool answers `status: "loading"` rather than hanging, and
`getWorkspaceStatus` says how far it has got.

## The LSP tool

Claude Code's built-in `LSP` tool exposes nine read-only operations, and **all nine work** through
this adapter: hover, go to definition, go to type definition, go to implementation, find references,
document symbols, workspace symbols, incoming calls and outgoing calls. Call hierarchy is the one
worth naming, because it is the operation the alternatives most often lack; Roslyn has implemented it
since 5.8 and the pin is 5.12.

Two of those cross into code you do not have open. Go-to-definition on a BCL or NuGet symbol returns
a real `file:` path to decompiled source, which any editor and any agent's file reader can open —
not a custom URI scheme that renders as nothing. And call hierarchy on a multi-targeted project
reports each result once rather than once per target framework.

0.1.0 also ships the diagnostics bridge: Roslyn answers diagnostics only when *asked* (a pull model)
and Claude Code only listens for diagnostics that are *sent* (a push model), so without the bridge
the client gets none at all, forever, with no error to explain it. With it, editing a file produces
its diagnostics on the next turn.

The adapter advertises more than the nine to its own client — rename with prepare, code actions with
resolve, formatting, range formatting, signature help and completion — for the LSP clients that use
them. It deliberately advertises **no** semantic tokens, code lens or inlay hints: advertising a
capability is a promise to answer the requests it enables, Roslyn offers all three whether or not
anyone asked, and computing them for an agent that will never render them is work thrown away.

## How it works

The adapter terminates both sessions. It is a *server* to its client and a *client* to Roslyn, with
two independent handshakes and state of its own — not a byte relay with patches. A relay cannot
answer the client's `initialize` before Roslyn has answered its own, cannot answer the ~140
registrations and 80 configuration sections the client refuses, and has nowhere to keep a document
mirror or a queue of held requests.

### Acquisition and pinning

This binary contains no compiler. It runs Microsoft's `roslyn-language-server` as a child process and
fetches that server itself the first time it is needed — pinned to one exact version,
**5.12.0-1.26426.8**, whose SHA-512 for every published platform is checked into this repository and
regenerated only by a build target, never typed in by a person.

Bundling the server would multiply every release archive by an order of magnitude, and asking the
user to install it would reproduce the exact failure this project exists to fix: a plugin that
launches a binary nobody has. So the first run downloads about 70 MB, verifies it against the pin,
and extracts it into a per-user cache. Afterwards there is nothing to download and nothing to check.

The pin is one constant rather than a floating range because the server is prerelease-only with an
unstable command line: `--clientProcessId`, `--daemon` and `--daemonKeepAlive` exist in 5.12 and do
not exist in the builds a year older, and Roslyn *exits* on an option it does not recognise. A flag
passed to the wrong version is not a degraded feature, it is a child that never starts. A floating
version is a release that breaks on somebody else's schedule.

Where the server comes from, in order of trust — and only the last step spends bandwidth:

1. **`CLAUDE_ROSLYN_LSP_ROSLYN_PATH`**, if set. Its version is not checked: you asked for it. A value
   that does not resolve is an error, not a fall-through to a download — a variable that silently
   does nothing is the one configuration bug you cannot diagnose.
2. **The cache**, if a `.complete` marker there records the hash the pin expects.
3. **A global `dotnet tool` installation**, but only at exactly the pinned version. One at a
   different version is *reported* by `doctor` — present, not used, wrong version — and never used.
4. **nuget.org**, streamed straight to disk and hashed on the way rather than re-read afterwards,
   extracted into a staging directory and promoted with one rename so the cache is never
   half-populated. A lock file serialises the two processes Claude Code starts at the same instant,
   so a first run downloads once rather than twice.

Eight platforms are published — `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`,
`linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, `osx-arm64` — which is deliberately more than the
five this repository builds archives for, because the NuGet channel reaches platforms no archive is
built for. On anything else, `doctor` names the platform as unsupported rather than downloading a
payload that cannot run. `CLAUDE_ROSLYN_LSP_OFFLINE=1` turns step 4 into an explanation instead of a
fetch.

Roslyn is launched as `<dotnet> …/Microsoft.CodeAnalysis.LanguageServer.dll`, never through the
apphost beside it: that executable is a thin client that spawns its own daemon per instance, which
gave four processes and no shared workspace. The adapter talks to it over a named pipe by default,
because in pipe mode the protocol is on a channel nothing else holds a handle to — the server writes
a 646-byte banner to its own stdout at startup, which under `--stdio` would be protocol corruption.
`CLAUDE_ROSLYN_LSP_TRANSPORT=stdio` exists for environments with no usable named pipe.

### Which solution gets opened

`CLAUDE_ROSLYN_LSP_SOLUTION` decides it outright, and a path that is not there is an error rather
than a licence to guess: falling back to a scan would open a *different* solution and then answer
about it with total confidence.

Failing that, `.vscode/settings.json`'s `dotnet.defaultSolution` is honoured — any repository opened
in VS Code with the C# extension has already answered this question and checked the answer in —
including its `disable` sentinel, which means no solution on purpose. That file is parsed as JSONC,
because it has comments and trailing commas in it, and a malformed one is treated as absent rather
than as a startup failure: it is somebody else's file.

Otherwise the adapter looks: three directories deep, skipping `bin`, `obj`, `.git`, `node_modules`,
`.vs`, `artifacts` and `TestResults`, and **scores** what it finds rather than taking the first hit.
A name matching the repository folder is worth 100, `.slnx` over `.sln` is worth 10, and each project
listed is worth one; ties go to the shallower file. With no solution at all it opens every `.csproj`
it can find, up to 500 — test projects included, because an agent asked to fix a failing test needs
the test project loaded, and a symbol that resolves everywhere except in tests is worse than a
slower load. `doctor` prints the candidates and their scores, so a wrong choice is visible rather
than mysterious.

### The readiness gate

A request issued before Roslyn reports `workspace/projectInitializationComplete` is answered with an
*empty successful result* — never an error. An ungated client therefore reports "no definition found"
for the first several seconds of every session, which is indistinguishable from a correct answer and
teaches an agent that the server is useless. That single behaviour is most of why this project
exists.

So requests queue in arrival order and are released in that order once the workspace is loaded.
`CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS` (120 s, matching the plugin's own `startupTimeout`, so the
client and the server give up at the same moment) is a floor rather than a promise: after it, held
requests are passed through anyway, because a partially loaded workspace answering some questions
beats a server that has stopped answering. A cancellation for a held request is honoured; a backend
that failed answers with an error naming `doctor`; and a client left waiting is told every ten
seconds through `window/logMessage`, which is the one channel it renders.

The MCP half makes the opposite choice for the same reason. A language client has nowhere else to be,
so it waits; an MCP call is a turn of a conversation, so it answers `status: "loading"` with how far
the load has got. A model told "still loading, 3 of 8 projects" can do something else and come back.

### The three requests answered on the client's behalf

Roslyn asks its client questions, and Claude Code answers none of them usefully. The adapter answers
all three itself:

- **`client/registerCapability`.** Roslyn registers dynamically — ten diagnostic sources, and 135 to
  149 file watchers for a two-project solution, one request each. Claude Code refuses every one with
  `-32601`. The adapter accepts them, tracks them, and collapses the watcher set into something a
  client can actually act on.
- **`workspace/configuration`.** Roslyn asks for 80 sections across two batches, and the section
  names contain a pipe (`csharp|background_analysis…`) which the client's dotted settings lookup
  cannot address at all. It also only answers when a settings block happens to be present. The
  adapter answers both batches — the second arrives from the Razor subsystem after its own
  registrations, and a client that answers only the first leaves a request outstanding on the
  startup path.
- **`window/workDoneProgress/create`.** The solution load reports through a progress stream. Without
  a client that creates the token, the progress that says how far the load has got has nowhere to go —
  which is the number `getWorkspaceStatus` reports and the readiness gate waits on.

### Configuration defaults

The answers the adapter gives `workspace/configuration` assume an agent rather than a human with a
screen. Both diagnostic scopes are set to open files only, because a full-solution compiler scope is
what makes a large solution unusable and the opt-in workspace mode raises it deliberately. Automatic
restore is **on**, because Roslyn restores server-side and never asks, and a freshly cloned
repository would otherwise load with no references at all. Decompiled-source navigation is **on**, so
go-to-definition into a BCL symbol reaches real source. Reference-assembly symbol search, every inlay
hint, every code lens and auto-insert are **off**, because the adapter advertises none of them and
the work would be discarded — and the two families are matched by prefix rather than enumerated, so
a Roslyn release that adds a hint kind cannot quietly switch it back on.

`CLAUDE_ROSLYN_LSP_OPTIONS` takes a JSON object that overrides any of this, and a settings block from
the client wins over both.

<!-- WP4: how it works -->

### Diagnostics, file watching and crash recovery

### Waiting for the workspace, instead of answering wrongly

A request that reaches Roslyn before the solution has finished loading is answered with an **empty
successful result** — never an error. So an ungated client silently reports "no definition found"
for the first several seconds of every session, which is indistinguishable from a correct answer and
teaches the agent that the language server is useless.

The adapter therefore holds requests in arrival order and releases them, in that order, when
`workspace/projectInitializationComplete` arrives. The client's own handshake is answered
immediately out of a capability document written in this repository, so startup does not wait for
the backend: on Quartz.NET's 30-project solution `initialize` comes back in 22 ms and the workspace
is ready 5.9 s later, with everything asked in between held and then answered. If the load exceeds
`CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS` the gate opens anyway — a partly loaded workspace answering
some questions beats a server that has stopped answering — and says so. A client holding requests is
told every ten seconds through `window/logMessage`, which is the one channel Claude Code renders.

### Diagnostics: pulled from Roslyn, pushed at the client

Roslyn reports diagnostics only when asked (`textDocument/diagnostic`). Claude Code only understands
being told (`textDocument/publishDiagnostics`) and has no code path that asks. Pointed at each other
unmediated, the result is a language server that never reports a single error and is silent about it.
The bridge is that translation, and it is as much the reason this project exists as the readiness
gate is.

It pulls on open, on save, 400 ms after the last edit, when Roslyn asks for a refresh, and a second
after a project file changes on disk; one pull is in flight per file at a time with a dirty flag
behind it, and each published set carries the document version it was computed at, so a slow answer
cannot paint diagnostics at positions that have moved.

What is published is deliberately less than what Roslyn reports, because the client renders
diagnostics as a text attachment and cuts it — every entry kept pushes another out:

- **Warnings and errors only** by default. `CLAUDE_ROSLYN_LSP_DIAGNOSTIC_MIN_SEVERITY=information`
  (or `hint`) lowers the floor.
- **Anything tagged "unnecessary" that is not an error is dropped.** IDE0005 arrives as a hint at
  line 0, column 0, for the whole `using` block; it is the least useful entry available and the one
  most likely to read as "something is wrong at the top of this file".
- **Visual Studio's private diagnostic tags are stripped**, because no client outside VS knows what
  they mean and several drop a diagnostic that carries one.
- **Sorted by severity, then position, and capped at fifty per file.**

Closing a file publishes an empty set, so a diagnostic the user fixed and then closed does not stay
in the transcript forever. `CLAUDE_ROSLYN_LSP_DIAGNOSTICS=off` turns the whole thing off, which
leaves a navigation-only adapter.

**Files nobody has open** are reachable only through a whole-solution pull, and only with the
compiler analysis scope raised to `fullSolution` — the setting that makes a large solution expensive,
because it puts every project's compilation in memory. So it is opt-in:
`CLAUDE_ROSLYN_LSP_WORKSPACE_DIAGNOSTICS=errors` raises that one scope (and no other), asks after
each save, and publishes **compile errors only, for at most ten files, five each**, with the saved
file's own project first. Build output, project files and the duplicate copy a multi-targeted project
reports per framework are all filtered out, and a file that gets fixed is cleared explicitly.

### Watching the disk, because nothing else will

Roslyn has no file watcher of its own and no fallback: it registers watchers with its client and
waits to be told. Claude Code refuses every one of those registrations and never sends a watched-file
notification — so without this bridge, a file created by `git checkout` or by a shell command never
joins its project, and every later answer is silently *wrong* rather than missing.

Two things about it are worth knowing:

- **A new `.cs` file is not enough.** Roslyn takes the event and carries on; the new type stays
  invisible. What makes it re-evaluate is a *change* to the owning `.csproj`, so every appearance or
  disappearance of a source file is accompanied by a synthetic change event for the nearest project
  file above it. That single mapping is the difference between "a file created by Bash is found" and
  "restart your editor". On the test fixture the new type is findable 1.4 s after the file is
  written.
- **`obj/` is excluded, except for `project.assets.json`.** That file *is* the restore result, and it
  is load-bearing in a way that is easy to miss: on a freshly cloned repository Roslyn restores the
  solution itself and then waits to be told the restore happened. Without that one event the load
  never completes at all — three projects reported as loaded, a completed restore, and no readiness
  notification for as long as you care to wait.

Registrations rooted outside the workspace are dropped (on Quartz.NET that is 342 of 433, all in the
NuGet package cache), events are batched for 200 ms, and files the client has open are skipped
because it already owns their contents. Past 64 distinct directories the per-directory watchers are
replaced by a single recursive one on the workspace root, which covers all of them for one handle.
`CLAUDE_ROSLYN_LSP_FILE_WATCHER=off` disables it, and then the capability is withdrawn from the
handshake entirely so Roslyn registers nothing — advertising it and then not delivering would look
exactly like a working watcher until an answer turned out to be stale. On Linux, watching costs one
inotify instance per directory (`fs.inotify.max_user_instances`, 128 by default); each directory that
cannot be watched is reported by name with the sysctl to raise.

### When Roslyn dies

It is absorbed, not forwarded. The client's restart budget restarts *this* process, which throws away
the session, the document mirror, the readiness state and every answer in flight — and Roslyn dying is
not that kind of failure, because everything needed to rebuild the backend is held here.

So the gate closes again, the child is relaunched, every open document is replayed at its latest
text, the solution is re-opened, and requests that arrive in the gap are held exactly as they were at
startup. The client sees a slow answer and a log line, not an error: in the live test, a `kill -9`
mid-session is followed by a correct answer 2.9 s later. Two restarts are allowed per ten minutes —
an accident comes back and works, and a server that dies three times on the same solution is
deterministic, so a fourth attempt would be an infinite loop that also holds every request in it.
After that the session moves to its failure state, where each request is refused with a sentence
naming `claude-roslyn-lsp doctor`.

### When Roslyn cannot be started at all

Offline, no .NET 10 runtime, a proxy serving HTML where a package should be, a hash that does not
match the pin: all ordinary, and none of them kills the adapter. The process stays up, the handshake
stands, and every request is answered with an error naming `claude-roslyn-lsp doctor`; the client
gets one message it will surface and one it will file. A server that exits during startup leaves its
client with a dead pipe and no channel to be told why, because stdout **is** the channel.

### What is deliberately not advertised

The capability document the adapter presents is authored and static, and it is answered immediately
rather than derived from Roslyn's — Claude Code holds `initialize` open indefinitely if it is not
answered, and deriving one document from the other would make the client's startup wait on the
backend's. It is also not a copy of Roslyn's, which advertises semantic tokens, code lens, inlay
hints and a Visual Studio auto-insert provider statically whether or not anyone asked for them.

The adapter carries exactly what the mediation carries. It does not declare `workspace/applyEdit`
either: the LSP half of 0.1.0 writes no files at all, and the MCP half applies its own edits — which
makes `Edits/` the single place in this product that writes a source file, with one path guard in
front of it.

### One Roslyn per solution, shared between the two servers

Claude Code starts this plugin's MCP server when your session starts and its LSP server the first
time you touch a `.cs` file. Both of them want a loaded solution, and a Roslyn holding a solution is
by far the largest thing this product puts on a machine — so they share one.

Whichever process gets there first becomes the **host**: it launches Roslyn, publishes itself in
`<home>/sessions/<hash of the solution path>.json`, and listens on a named pipe. The other one
**attaches** and starts nothing at all. Attaching costs about a tenth of a second, against the
seconds or minutes a solution load costs, so the second server is answering questions as soon as it
is asked one.

The host multiplexes: requests from an attached client are renumbered onto the host's own connection
and held by the same readiness gate, documents are reference-counted so one client closing a file
does not take it away from the other, and the notifications an attached client needs — the log
lines, the "workspace loaded" signal, the "your diagnostics are stale" refresh — are fanned out to
everybody. Registrations, configuration and progress stay with the host, which is the one process
that owes Roslyn an answer to them.

Losing either half is a state, not a failure. If Roslyn dies, the host relaunches it and both halves
see a workspace that went briefly quiet; requests in flight at the time are asked again rather than
refused. If the *host* goes away, the attached process sees its backend disappear, finds the session
file gone, and becomes the host itself — on the fixture solution, about two and a half seconds later.

`getWorkspaceStatus` reports which of the two you are talking to: `engine: "owned"` for a Roslyn this
server launched, `engine: "attached"` with `hostProcessId` for one it is sharing. Every failure in
the rendezvous — an unwritable home directory, a platform that will not give out named pipes, a
session file naming a process that has gone — falls back to launching a private Roslyn, which is
what the first release did in every case. `CLAUDE_ROSLYN_LSP_SHARE=off` turns sharing off
deliberately.

`claude-roslyn-lsp doctor` lists the session directory, with the age and the verdict for each entry;
`doctor --fix` removes the stale ones, though a server starting up removes them by itself.

## Environment variables

Environment variables only — there is no configuration file and no configuration provider. A client
launches this binary with an environment block and nothing else, and reading configuration **never**
fails startup: a malformed value falls back to its documented default and says so on stderr, because
stdout is the protocol channel and a dead process has nowhere to explain itself. `doctor` prints
what is actually in effect and is the authority when this table and the binary disagree.

| Variable | Default | What it does |
|---|---|---|
| `CLAUDE_ROSLYN_LSP_SOLUTION` | discovered | The `.slnx`, `.sln` or `.csproj` to open. A path that does not exist **fails loudly** rather than falling back to discovery. |
| `CLAUDE_ROSLYN_LSP_ROSLYN_PATH` | — | A directory holding `Microsoft.CodeAnalysis.LanguageServer.dll`, that assembly, or another executable to run instead. First in the resolution chain; its version is not checked. Alias: `_SERVER_PATH`. |
| `CLAUDE_ROSLYN_LSP_ROSLYN_VERSION` | the pin | Fetch a different `roslyn-language-server` version. There is no hash for it, so `doctor` reports it as unverified, and the launcher drops the flags only the pinned build is known to accept. Alias: `_SERVER_VERSION`. |
| `CLAUDE_ROSLYN_LSP_ROSLYN_ARGS` | — | Extra arguments for the Roslyn child, split on whitespace. Development only. |
| `CLAUDE_ROSLYN_LSP_HOME` | platform cache dir | Where downloads, logs and lock files live. Falls back to `CLAUDE_PLUGIN_DATA`, then `%LOCALAPPDATA%`, `~/Library/Caches`, `$XDG_CACHE_HOME` or `~/.cache`. Around 140 MB extracted per Roslyn version per platform, so it is a cache by every definition an operating system has. |
| `CLAUDE_ROSLYN_LSP_CACHE_DIR` | under the home | Moves the downloaded servers only; logs and staged downloads stay under the home. |
| `CLAUDE_ROSLYN_LSP_OFFLINE` | `0` | Never download. A missing server becomes an explained failure instead of a 70 MB fetch. |
| `CLAUDE_ROSLYN_LSP_TRANSPORT` | `pipe` | `pipe` or `stdio`. The pipe keeps the protocol on a channel nothing else can write to. |
| `CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS` | `120` | How long a request waits for the workspace to load before passing through anyway. 1–3600. |
| `CLAUDE_ROSLYN_LSP_DIAGNOSTICS` | `1` | The pull-to-push diagnostics bridge. `lsp` only — an MCP client asks with `getDiagnostics`. |
| `CLAUDE_ROSLYN_LSP_DIAGNOSTIC_MIN_SEVERITY` | `warning` | The severity floor applied before diagnostics are published to an LSP client. |
| `CLAUDE_ROSLYN_LSP_WORKSPACE_DIAGNOSTICS` | off | Opt-in workspace-wide diagnostics for files that are not open. Needs a full-solution compiler scope, which is the expensive setting. |
| `CLAUDE_ROSLYN_LSP_FILE_WATCHER` | `1` | The file-watch bridge. Without it, a file created by a shell command or by git never joins its project and every later answer is silently stale. `lsp` only: the `mcp` half always watches, because it has no editor telling it what changed and because a repository that needs a restore does not finish loading without it. |
| `CLAUDE_ROSLYN_LSP_SHARE` | `1` | Whether the `lsp` and `mcp` servers share one Roslyn per solution. Off makes each of them launch one of its own, which loads the solution twice and pays for it twice in memory. `getWorkspaceStatus` reports `engine: "owned"` or `"attached"`, so which of the two happened is never a guess. |
| `CLAUDE_ROSLYN_LSP_GC` | `workstation` | Which garbage collector the Roslyn child runs with: `workstation` or `server`. Workstation peaks around 577 MB on a 239-project solution where server peaks around 2 GB, and costs about eight seconds of load time. An explicit `DOTNET_gcServer` in the environment wins over this and is left alone. |
| `CLAUDE_ROSLYN_LSP_LOG_LEVEL` | `Information` | This adapter's own stderr logger: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`. |
| `CLAUDE_ROSLYN_LSP_ROSLYN_LOG_LEVEL` | `Warning` | The level handed to Roslyn. Its `Information` is about twenty lines of narration per start. |
| `CLAUDE_ROSLYN_LSP_OPTIONS` | — | A JSON object merged into the answers given to Roslyn's `workspace/configuration` requests. A syntax error is a logged warning, never a startup failure. |

Booleans accept `1/true/yes/on` and `0/false/no/off`, in any case. A number out of range falls back
to the default rather than failing.

**The plugin-option rule.** Every one of these is also read as `CLAUDE_PLUGIN_OPTION_<NAME>`,
**first**, with a blank value treated as absent. A Claude Code plugin manifest substitutes an
unfilled option as the empty string rather than omitting it, so mapping one straight onto the plain
name would set it to `""` in the child process and shadow whatever your environment already said —
which is a real bug a sibling project shipped and had reported. You never set these by hand; the
plugin launcher does, and `doctor` reports which variable a value actually came from.
`CLAUDE_ROSLYN_LSP_HOME` is the single exception: the manifest sets it directly to
`${CLAUDE_PLUGIN_DATA}`, which is a real directory Claude Code owns and has no blank case to guard
against.

## doctor

```sh
claude-roslyn-lsp doctor          # report everything, then start Roslyn and complete a handshake
claude-roslyn-lsp install         # the same, but download the server if it is missing
claude-roslyn-lsp doctor --json   # the same report, machine-readable
```

`doctor` prints the adapter's version and platform, the `dotnet` host and its runtimes with the .NET
10 ones marked, the whole Roslyn resolution chain with the winner and whether its bytes were
verified, the cache directory and its free space, which solution would be opened and how it scored
against the other candidates, and whether Claude Code's language-server tool is enabled — plus a
warning if the official `csharp-lsp` plugin is also enabled, because the first plugin registered for
`.cs` wins.

Then it launches the server it just described and completes a real `initialize`/`shutdown`/`exit`,
reporting what the server said and how long each step took. **It exits 0 only if that worked.**
Every static check above it can pass on a machine where Roslyn still does not start, which is why
the exit code means the live thing and not the checklist.

`doctor` never downloads — a diagnostic that quietly spends 70 MB of somebody's tethered connection
is a diagnostic they will not run again. `install` is the same command with the download allowed
(`doctor --fix` is a synonym). Through `dnx`, use `doctor` rather than `--version`; `dnx` takes
`--version` for itself.

## Agent skill

The workflows are also shipped as an [Agent Skill](https://agentskills.io) — the open `SKILL.md`
format most coding agents now read — at
[`.claude/skills/claude-roslyn-lsp/SKILL.md`](.claude/skills/claude-roslyn-lsp/SKILL.md).

It matters more here than it does in a typical MCP server, because **the behaviour is the product**.
The complaint this project started from is that an agent greps and seds C# instead of refactoring it.
The built-in LSP tool's description is fixed by the client and says nothing about when to prefer it
over a text search; each MCP tool's schema describes one tool and cannot say what to reach for
instead of what. Only a skill can teach *order* and *substitution* — resolve the symbol before
searching for it, rename semantically instead of editing declarations, run a design-time diagnostic
pass instead of a build — and it teaches both tool families at once, including the pairing that makes
them compose: `resolveSymbol` reports the `path:line:col` the built-in LSP tool needs, so the model
never greps for a line number first.

The chosen solution and its score are logged **and** sent to the client once, because "which
solution did it open" is the first question of any report about a repository with more than one.

### Any other tool

```bash
npx skills add lahma/claude-roslyn-lsp --skill claude-roslyn-lsp -a codex -y
gh skill install lahma/claude-roslyn-lsp claude-roslyn-lsp --agent codex
```

Replace `codex` with `claude-code`, `cursor`, `gemini-cli` or `github-copilot`. Or copy the directory
by hand — it is one file. Working inside a checkout of this repository needs none of it:
`.claude/skills/` is loaded as a project skill automatically.

Both the skill and the plugin manifests are checked against the server on every build. One test
cross-references every tool the skill names against the real, reflected tool inventory in both
directions, so the skill can neither name a tool that does not exist nor quietly omit one that does;
another asserts that the plugin's skill path still resolves, that its version is the one in
`CHANGELOG.md`, that both server entries run the same pinned package with only their verb differing,
and that the `.cs` mapping that actually launches the LSP server is still there.

## Troubleshooting

Start with `claude-roslyn-lsp doctor`. Almost everything below is a line in its report.

**Claude Code never launches it, or says `Command 'csharp-ls' not found`.** The official
`csharp-lsp@claude-plugins-official` plugin has the `.cs` extension. The first plugin registered for
an extension wins and marketplace plugins register first, so this adapter is never asked.
`claude plugin disable csharp-lsp@claude-plugins-official`, or `/plugin` → *Manage plugins* →
*Disable*. `doctor` warns when both are enabled.

**Nothing works and the .NET 10 SDK is missing.** Roslyn is a .NET 10 application. `doctor` lists
every `dotnet` host it found and every runtime each one has, with the .NET 10 ones marked; if none
is marked, that is the whole problem. Install the SDK from
[dot.net](https://dotnet.microsoft.com/download) and run `doctor` again. If a client reports *the
command "dnx" was not found*, the same thing is missing.

**The first answers of a session are empty, or the first tool call says `loading`.** That is the
solution loading, and it is normal: a few seconds on a small repository, up to two minutes on a large
one, once per session. `getWorkspaceStatus` says how many projects are in and what failed. The LSP
half holds requests rather than answering them emptily, so a slow first answer is the gate working.

**Answers are confidently about the wrong code.** A monorepo with several solutions is the usual
cause: discovery scores what it finds and cannot know which one you are working in. `doctor` prints
the candidates and their scores. Fix it for everyone by setting `dotnet.defaultSolution` in
`.vscode/settings.json`, or per client with `CLAUDE_ROSLYN_LSP_SOLUTION` in the environment block
that launches the server. Either way the server has to be restarted; it reads its environment once.

**A symbol that is plainly in the checkout is not found.** Its project is not in the loaded solution,
or it did not restore. `getWorkspaceStatus` lists the loaded projects and the load errors. Roslyn
restores server-side, so the fix is usually to look at the restore error rather than to run
`dotnet restore` again.

**Behind a proxy, or offline.** The only thing the adapter downloads is the Roslyn payload from
nuget.org, through the standard .NET proxy environment. Run `claude-roslyn-lsp install` once on a
machine that can reach nuget.org, copy the resulting home directory, and point
`CLAUDE_ROSLYN_LSP_HOME` at it — or install the server yourself and set
`CLAUDE_ROSLYN_LSP_ROSLYN_PATH`. `CLAUDE_ROSLYN_LSP_OFFLINE=1` then makes a missing payload an
explained failure instead of a doomed fetch.

**You want to see what it is actually doing.** All logging goes to stderr — stdout is the protocol
channel — and Claude Code shows it with `claude --debug`. `CLAUDE_ROSLYN_LSP_LOG_LEVEL=Debug` for the
adapter's own narration, `CLAUDE_ROSLYN_LSP_ROSLYN_LOG_LEVEL=Information` for Roslyn's, which is
about twenty lines per start.

## Security

- **The only thing downloaded is Roslyn, from nuget.org, and it is hash-verified.** One pinned
  version, one SHA-512 per platform checked into this repository, hashed while streaming to disk
  rather than by re-reading the file afterwards, extracted into a staging directory and promoted with
  a single rename so a half-extracted cache can never look complete. Every entry in the archive is
  checked against the destination root before it is opened — a `..` segment, a rooted path, a drive
  qualifier or a backslash separator each reject the whole package. A version override you asked for
  has no hash, is verified by TLS alone, and is reported as unverified rather than quietly treated as
  equivalent.
- **Edits are written only inside the workspace.** Roslyn hands out URIs outside it as a matter of
  course — decompiled sources live in a temporary directory — so every write is checked for a `file:`
  scheme and a *resolved* path under the workspace root, compared the way the platform's own file
  system compares paths. Writes go through a temporary file beside the target, preserve the byte
  order mark and the dominant line ending, and refuse overlapping edits rather than merging them.
- **No telemetry, no analytics, no update check.** The Roslyn child is launched with
  `--telemetryLevel off`. Outside a first-run download the adapter talks to nothing.
- **The supply chain is three runtime packages**, all from Microsoft or the official MCP
  organisation, centrally pinned with transitive pinning on and the MCP SDK pinned to an exact
  version. Adding one requires a recorded decision in [AGENTS.md](AGENTS.md). There is no
  `Microsoft.CodeAnalysis.*` reference and no `StreamJsonRpc`: framing, process launch, download,
  hashing and named pipes are all plain BCL, which is what keeps the tree small enough for one person
  to audit.

## Prior art

Read for facts, credited here, and not copied from — the code in this repository is its own:

- [SamHurne/roslyn-lsp-adapter](https://github.com/SamHurne/roslyn-lsp-adapter) — an adapter with the
  same premise.
- [erinloy/claude-roslyn-lsp](https://github.com/erinloy/claude-roslyn-lsp) — the name collision is
  not an accident; it is the same idea, arrived at independently.
- `ClaudeCodeRoslynLspProxy` on nuget.org — a relay in front of the same server.
- [oraios/serena](https://github.com/oraios/serena) — semantic tooling for agents across many
  languages, and the clearest statement of why name-addressed beats position-addressed.
- [seblyng/roslyn.nvim](https://github.com/seblyng/roslyn.nvim) — the reference for how
  `roslyn-language-server` actually wants to be launched and talked to.
- [razzmatazz/csharp-language-server](https://github.com/razzmatazz/csharp-language-server)
  (`csharp-ls`) — the honest baseline this project has to beat, and the server the official plugin
  points at.

Microsoft's [`roslyn-language-server`](https://www.nuget.org/packages/roslyn-language-server) is the
engine. It is MIT-licensed, and it is downloaded, never vendored.

## Building from source

Needs the .NET 10 SDK (the exact version is pinned in `global.json`).

```bash
git clone https://github.com/lahma/claude-roslyn-lsp.git
cd claude-roslyn-lsp

./build.sh Test          # restore, compile, run the tests
./build.sh SmokeTest     # AOT publish, then three real stdio sessions against the binary
./build.sh Pack          # the NuGet tool package, into artifacts/packages

CLAUDE_ROSLYN_LSP_LIVE_TESTS=1 ./build.sh Test LiveTest   # + the real Roslyn, downloaded
```

```powershell
.\build.ps1 Test
.\build.ps1 SmokeTest
.\build.ps1 Pack
```

`LiveTest` is the only thing here that talks to Microsoft's actual server: it acquires the pinned
build, opens the never-restored fixture solution under `tests/fixtures/`, and waits for the
deliberate compile error in it to arrive as a pushed diagnostic. Everything else speaks to a scripted
backend, which proves the mediation and nothing at all about whether Roslyn still answers what this
adapter believes it answers. Without the variable the target reports *skipped* rather than passing.

The orchestrator is [Fallout](https://fallout.build); `build.ps1` / `build.sh` bootstrap the CLI from
`.config/dotnet-tools.json`, so nothing needs installing globally. `dotnet fallout PublishAot
--runtime linux-arm64` builds one platform; the executable lands in `artifacts/publish/{rid}/` and
the archive in `artifacts/archives/`.

`CHANGELOG.md` is the version authority — the build parses its top section and stamps that version
into the binary and the package. The plugin manifest, `.mcp/server.json` and this repository's own
`.github/lsp.json` restate it, and nothing in the build reads those back, so tests fail when any of
them falls behind. `dotnet fallout UpdateRoslynPin` is the only thing allowed to change the pinned
Roslyn version and its hashes.

`SmokeTest` is the check the unit tests cannot be: it publishes the Native AOT binary and drives two
real LSP sessions and one MCP handshake against it, reading stdout as *frames*, so any byte that is
not part of one fails the build. It needs no Roslyn download — a scripted backend reproducing a real
5.12 startup ships inside the binary behind a hidden verb, which is what lets every release platform
be proven on its own hardware.

## Contributing

Read [AGENTS.md](AGENTS.md) first. It records the design decisions with the argument for each one,
the package budget, the wire facts in
[`docs/roslyn-protocol-facts.md`](docs/roslyn-protocol-facts.md) that cost a spike each to find, and
the handful of hard rules that are easy to break by accident: never reference
`Microsoft.CodeAnalysis.*`, never transcribe the prior art, no `Console.*` outside `Cli/`, never
hand-edit the generated workflows, and never weaken a rule-enforcing test to make a change pass.

## License

MIT. See [LICENSE](LICENSE).
