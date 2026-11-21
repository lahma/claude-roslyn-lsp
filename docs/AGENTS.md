# Agent instructions: the documents

> **Read this when:** you record a wire fact in `roslyn-protocol-facts.md`, add or change a client
> configuration pack under `clients/`, or change the README snippet that quotes one.
>
> One of the co-located instruction files indexed from the repository-root [`AGENTS.md`](../AGENTS.md);
> read that first.

## The wire facts

The **C-numbered** findings live in [`docs/roslyn-protocol-facts.md`](roslyn-protocol-facts.md):
C1-C47 were observed on the wire against the pinned server on 2026-09-10 (WP0 spikes, WP3),
C63-C65 by WP9 with two halves of this product sharing one server, C66 by WP10 on what an idle
child's memory is made of and what a forced compacting collection gives back, and
C48-C54 by WP4 against the fixture solution and against real Quartz.NET (30 projects) and
OrchardCore (239 projects) sessions through Claude Code 2.1.267. C55, C56 and C62 are *host* findings rather
than protocol ones — a BCL path function that behaves differently off Windows, and the order two
operating systems report a dead backend in — kept in the same file because they are cited from the
same code and there is nowhere else they would be re-verified from. They are cited from code
comments and tests by number. Add to that file, never restate a fact in an instruction file; a
finding that changes on a pin bump gets re-verified there with the new date.

**If a solution ever stops finishing its load, read C52 and then C48**, in that order. They are two
independent causes of the same silence — every project reported "successfully loaded", a restore
that visibly restored everything, and then no `projectInitializationComplete` until the readiness
budget runs out. C52 is MSBuild node reuse holding the restore child's output pipe open, and its fix
is one environment variable on the child; C48 is the restore's `project.assets.json` write never
reaching Roslyn because nothing was watching for it. Both were found by the live tests, and neither
would ever have been found by a scripted backend.

## The client configuration packs

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
