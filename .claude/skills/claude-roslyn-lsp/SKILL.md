---
name: claude-roslyn-lsp
description: >-
  Navigate, refactor and check C# through the claude-roslyn-lsp adapter, which fronts Microsoft's
  Roslyn language server. Use when a task touches C# symbols rather than C# text — renaming a type
  or member across a solution, finding callers or implementations, listing what a type has on it,
  asking whether an edit still compiles, fixing one warning everywhere it occurs, organising usings
  or formatting — and the adapter is attached as an MCP server (`resolveSymbol`, `findReferences`,
  `getTypeMembers`, `getDiagnostics`, `getCodeActions`, `applyCodeAction`, `fixDiagnostics`,
  `renameSymbol`, `formatCode`, `getWorkspaceStatus`), as an LSP server behind the built-in `LSP`
  tool, or both. Covers the order the calls go in: the symbol before the grep, a semantic rename
  before an edit, a design-time diagnostic pass before a build, and what to do while the solution
  is still loading.
license: MIT
compatibility: Requires the claude-roslyn-lsp adapter attached as an MCP server, as an LSP server, or both, plus the .NET 10 SDK. C# and Visual Basic solutions only.
---

# Roslyn C# workflow

The MCP server's `initialize` instructions already state the conventions that span every tool —
1-based positions, workspace-relative paths, symbols addressed by name or by `path:line:col`, edits
written straight to disk — and each tool's schema describes its own arguments. Neither can say which
tool to reach for *instead of* the one you were about to reach for, or what order the calls go in.
That is this file.

## Before you grep

A question about a C# **symbol** has an exact answer, and text search does not know it. Grep finds a
name; it cannot tell an override from a coincidence, it misses every call through an interface, and
it reports the same identifier in a comment, a string and an unrelated namespace.

- *Where is this declared, and what is its signature?* — `resolveSymbol`. It takes a name
  (`IScheduler.Start`, matched as a dot-segment suffix), so you do not need a line number to start
  from, and ambiguity comes back as every candidate rather than as a guess.
- *Who calls it? What breaks if I change it?* — `findReferences`, with the source line beside each
  hit and a per-file summary. This is the call that decides whether a change is a two-line edit or a
  refactoring.
- *What does this type have on it?* — `getTypeMembers`, which answers without reading the file at
  all, and answers for a type in a NuGet package or the BCL just as well as for one in the checkout.
- *Then the built-in `LSP` tool, by position* — hover, go to definition, type definition,
  implementation, incoming and outgoing calls. It needs a file, a line and a character for every
  call. **`resolveSymbol` is where those come from.** Resolving the symbol first and passing the
  reported `path:line:col` straight into the `LSP` tool is the whole pairing: no grep, no guessed
  line number, no reading a file to count lines.

Text search is still the right tool for string literals, comments, `.csproj`, `.props`, `.json`,
`.md`, a name that may not be C# at all, and "does this word appear anywhere in the repository".

## Renames and moves

`renameSymbol` is a solution-wide semantic rename: it crosses projects, follows overrides and
interface implementations, and touches nothing that merely shares the name. A search and replace
does the opposite of all three.

- **Never rename a C# symbol with `sed`, `perl -pi`, or an `Edit` per file.** The declaration is the
  easy part; the call sites in another project, the override two types down and the interface member
  are what a text pass gets wrong, and it gets them wrong silently until the build.
- **Never rename by editing the declaration and letting the compiler find the rest.** That turns one
  operation into a compile-error hunt, and the errors only cover the projects that compile at all.
- `renameSymbol` renames the *symbol*, not the file. To move a type into a file of its own, call
  `getCodeActions` at the declaration and `applyCodeAction` on the "Move type to X.cs" entry — it
  creates the file, moves the declaration and keeps the usings.
- The same route covers "extract an interface", "make this static", "use an expression body" and
  everything else the IDE lightbulb offers: `getCodeActions` at the position, then `applyCodeAction`
  by the `id` the list reported.

## After every edit

Attached as an LSP server, the adapter pushes diagnostics for a file you just edited on its own —
you do not have to ask for the file you are working in. What is worth asking for is everything else:

1. `getDiagnostics` with `scope: "file"` immediately after an edit, whenever the edit was not
   trivial. About a second.
2. `getDiagnostics` with `scope: "solution"` and `minSeverity: "error"` **before claiming the work is
   done**. That is the difference between "my file compiles" and "the solution compiles". It is the
   expensive one: the first solution-wide pass of a session compiles every project, which is about a
   minute on thirty of them and can exceed the budget on hundreds — so use it once, at the end, and
   use `scope: "project"` while you are still working. If it refuses because the pass did not
   finish, it says so and names the cheaper scope; that is not an empty result.

`getDiagnostics` is a *design-time* pass — the same analysis an IDE underlines with — not a build. It
does not run source generators the way a build does, it has no MSBuild errors in it, and it runs no
tests. Run the real build when the question is any of those: a generator's output, a packaging or
target-framework problem, an MSBuild task, or whether the tests pass. Run it once, at the end,
instead of after every edit.

## Fixing warnings at scale

One diagnostic id repeated across forty files is one call, not forty edits.

- `fixDiagnostics` with the id and a scope — file, project or solution — applies Roslyn's own fix-all
  for it. `IDE0005` (unnecessary usings) and `CA1822` (member can be static) are the usual ones.
- `getCodeActions` then `applyCodeAction` when the fix is one site, or when you want to see which
  fixes Roslyn actually offers before picking one. Entries that support a fix-all say so, and
  `applyCodeAction` takes the scope to apply it across.
- `formatCode` for whitespace, honouring the repository's own `.editorconfig`; set `organizeUsings`
  when using directives are the point. It never shells out to `dotnet format`, so it costs a second
  rather than a restore and a build.
- Fix by id, in one pass, rather than by hand per file: the hand pass is where the fortieth file
  quietly gets a different treatment from the first.

## Preview first when the blast radius is unknown

Every mutating tool takes `preview: true`, which writes nothing and returns the diff. Calling the
same tool again with `preview: false` applies exactly what was previewed, not a fresh computation of
it.

Preview when the scope is a project or the solution, when the symbol is a common word, or when you
are about to fix an id you have not seen the sites of. Skip it for a rename you have already
confirmed with `findReferences` — a preview that is always run is a step nobody reads.

## Solution loading and status

Roslyn loads the whole solution before it can answer anything semantically. That is a few seconds on
a small repository and up to two minutes on a large one, and it happens once per session.

- Tools answer `status: "loading"` rather than hanging. That is not a failure and not an empty
  result — do something else and come back.
- `getWorkspaceStatus` says which solution is open, how many projects have loaded, and what failed
  to load. Call it when an answer looks wrong, when a symbol you can see in a file is not found, or
  when the first call reports `loading`.
- **A monorepo usually loads the wrong solution.** Discovery scores what it finds; it does not know
  which one you are working in. Fix it once, for everybody, with `.vscode/settings.json`'s
  `dotnet.defaultSolution`, or per client with the `CLAUDE_ROSLYN_LSP_SOLUTION` environment variable
  in the configuration that launches the server. Neither takes effect until the server restarts.
- **A symbol that is missing everywhere** usually means its project is not in the loaded solution, or
  did not restore. `getWorkspaceStatus` lists the projects; `claude-roslyn-lsp doctor` reports the
  .NET host, the Roslyn server it resolved, and which solution it would open, and exits non-zero if
  the server cannot start at all.

## When not to use it

- **Non-C# files.** Razor, XAML, JSON, YAML, MSBuild files and shell scripts are not in the semantic
  model. Read and edit them directly.
- **Generated code under `obj/`, `bin/` or a generator's output directory.** Diagnostics from there
  are filtered out on purpose, and editing it is editing something that is about to be overwritten.
- **A project that does not restore.** Nothing semantic works until it does; run the restore or the
  build first and read the error it gives you.
- **A question the checkout already answers.** Whether a file exists, what it contains, what the last
  commit changed — the file system and `git` are instant and free.

## After the tools changed files

Every applied edit says which files it wrote. **Re-read each of them before editing it yourself.**
Claude Code's `Edit` tool refuses a file that changed since it was last read, and its refusal does
not say that the change was yours from one tool ago. This is the single most common way a
refactoring turn stalls.
