# 0.1.0

- First release. One Native AOT binary that puts Microsoft's `roslyn-language-server` — pinned at
  **5.12.0-1.26426.8**, downloaded and hash-verified on first use — in front of coding agents two
  ways at once: `claude-roslyn-lsp lsp` mediates an LSP session so a client that refuses
  registrations, configuration and progress still gets a working C# language server, and
  `claude-roslyn-lsp mcp` exposes ten refactoring tools over MCP.
- The `lsp` verb answers `initialize` immediately from its own capability document, discovers and
  opens the solution, holds semantic requests until Roslyn reports the workspace loaded rather than
  answering them empty, answers the ~140 registrations and 80 configuration sections Roslyn asks
  about, turns Roslyn's pull diagnostics into the push a client renders, watches the workspace on
  Roslyn's behalf, and relaunches a Roslyn that dies without the client ever learning it happened —
  including replaying a request that was in flight at the time.
- The `mcp` verb runs a Roslyn of its own, started as soon as the client finishes the handshake so
  the solution load is paid before the first question rather than by it. Its tools are
  `getWorkspaceStatus`, `resolveSymbol`, `getTypeMembers`, `findReferences`, `getDiagnostics`,
  `getCodeActions`, `applyCodeAction`, `renameSymbol`, `fixDiagnostics` and `formatCode`. Symbols
  are addressed by name or by `path:line:col`, every number is 1-based, and the four tools that
  write files all take `preview` and preserve each file's byte-order mark and line endings.
- The Roslyn child runs with the workstation garbage collector by default, against its own
  configuration: about 0.44 GB instead of about 2 GB on a 239-project solution, for roughly eight
  seconds more on the load. `CLAUDE_ROSLYN_LSP_GC=server` takes the other side of that trade.
- `claude-roslyn-lsp doctor` reports the whole resolution chain — the runtime identifier, the
  `dotnet` host, where Roslyn was looked for and what won, the cache, which solution would be opened
  and why, whether anything else in Claude Code is claiming C#, and which garbage collector the
  child will use — and then launches Roslyn and completes a real handshake, so its exit code means
  "runnable right now" rather than "the paperwork is in order". `install` is the same with the
  download allowed.
- Ships as a Claude Code plugin carrying both servers and the agent skill, as a NuGet tool for
  `dnx`, and as a Native AOT archive per platform. Configuration files for Copilot CLI, OpenCode,
  Neovim, Helix, Zed, Codex, Gemini CLI, Cursor, VS Code and Claude Desktop are in `docs/clients/`.
- The two servers share **one Roslyn per solution**. Whichever starts first launches it, publishes
  itself under the adapter's home and serves a named pipe; the other attaches and starts nothing, in
  about a tenth of a second against the seconds a solution load costs. Requests from the attached
  side are renumbered onto the host's connection and held by the same readiness gate, documents are
  reference-counted so one server closing a file does not take it away from the other, and a Roslyn
  that dies is relaunched once for both of them. If the hosting process goes away the other becomes
  the host. `getWorkspaceStatus` reports `engine: "owned"` or `"attached"` with the host's process
  id, `doctor` lists the session directory, and `CLAUDE_ROSLYN_LSP_SHARE=off` keeps each server's
  Roslyn private — which is also what every failure in the rendezvous falls back to.
