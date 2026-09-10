---
name: claude-roslyn-lsp
description: >-
  C# and .NET navigation, refactoring and diagnostics through the claude-roslyn-lsp adapter, which
  fronts Microsoft's Roslyn language server. Use when a task touches C# symbols rather than C# text
  — renaming a type or member across a solution, finding callers or implementations, asking whether
  an edit still compiles, fixing warnings at scale, organising usings, or formatting — and the
  adapter is attached as an LSP server, an MCP server, or both. Under construction in this release:
  the adapter answers the protocol handshakes and nothing else yet, so this file deliberately names
  no tools and teaches no workflow.
license: MIT
---

# Roslyn C# workflow

**Under construction.** This skill ships with the repository so that the plugin, the marketplace
entry and the tests that keep them honest all have the file they point at from the first commit. It
carries no guidance yet, on purpose: guidance that names a tool the server does not answer to would
send a model at something that does not exist, and the tool surface is not frozen until the
refactoring work package lands.

What goes here then, and nowhere else:

- The order the calls go in — symbol questions before text search, and which navigation to run from
  a resolved position rather than from a guessed line number.
- The substitutions this project exists to teach: a semantic solution-wide rename instead of an
  editor-wide search and replace, a design-time diagnostic pass instead of a full build after every
  edit, and a fix-all by diagnostic id instead of the same hand edit repeated per file.
- Solution loading, and what to do while it is still going on.
- When not to reach for any of it: non-C# files, generated code, and a project that does not
  restore.

Until that lands, see [README.md](../../../README.md) for what the adapter currently does.
