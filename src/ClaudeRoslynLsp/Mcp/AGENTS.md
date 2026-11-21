# Agent instructions: the MCP tools

> **Read this when:** you add or change an MCP tool, its description or arguments, a result shape, the
> server instructions, or the shipped agent skill.
>
> One of the co-located instruction files indexed from the repository-root
> [`AGENTS.md`](../../../AGENTS.md); read that first. The engine behind the tools is in
> [`Engine/AGENTS.md`](Engine/AGENTS.md), and the edit applier the four mutating tools share in
> [`../Edits/AGENTS.md`](../Edits/AGENTS.md).

## The tool surface

Ten tools, frozen by D20. Names are camelCase verb-noun; lines and columns are **1-based** in and
out; paths are workspace-relative with forward slashes; every tool sets `UseStructuredContent` and
every result carries `status` (`ok` / `loading` / `failed`) and an optional `note` in the same place
(D66). Every `symbol`-shaped argument accepts a name or a `path:line:col` address (D63).

| Tool | Annotations | What it does |
|---|---|---|
| `getWorkspaceStatus` | readOnly, idempotent | The solution, how far it has loaded, load errors, projects with their target frameworks, the Roslyn build, and the language server's pid and working set (C45, C38). The tool every `loading` note points at. |
| `resolveSymbol` | readOnly, idempotent | `symbol`, `kind?`, `maxResults?` → declarations with 1-based positions and hover signatures. The replacement for grepping a declaration, and the feeder for Claude Code's own `LSP` tool. Ambiguity returns every candidate. |
| `getTypeMembers` | readOnly, idempotent | `type`, `maxResults?` → a type's members without reading the file. |
| `findReferences` | readOnly, idempotent | `symbol`, `includeDeclaration?`, `maxResults?`, `offset?` → semantic references with the source line beside each, a `byFile` summary, de-duplicated across target frameworks (C24), paged. |
| `getDiagnostics` | readOnly, idempotent | `scope?`, `path?`, `project?`, `minSeverity?`, `includeAnalyzers?`, `ids?`, `maxResults?` → compiler and (opt-in) analyzer diagnostics for a file, a project or the solution, in about a second (D67). Carries the design-time caveat. |
| `getCodeActions` | readOnly, idempotent | `path`, `line`, `col`, `endLine?`, `endCol?`, `kind?` → the lightbulb list, with a stable `id`, `diagnosticIds` and `fixAllScopes` per entry (D64). |
| `applyCodeAction` | **destructive**, not idempotent | `path`, `line`, `col`, `id?`, `title?`, `endLine?`, `endCol?`, `fixAllScope?`, `preview` → resolves and applies one action, optionally across the document, project or solution. |
| `renameSymbol` | mutating, not destructive, idempotent | `symbol`, `newName`, `preview` → a solution-wide semantic rename. Does not rename the file. |
| `fixDiagnostics` | **destructive**, idempotent | `diagnosticId`, `scope?`, `path?`, `project?`, `preview` → finds a site of the diagnostic, then Roslyn's own fix-all across that scope (D65). |
| `formatCode` | mutating, not destructive, idempotent | `paths?`, `project?`, `organizeUsings?`, `preview` → `textDocument/formatting`, honouring `.editorconfig` — its `end_of_line` and `insert_final_newline` included (D113) — never shelling out to `dotnet format`. |

All ten declare `openWorldHint: false`. The four mutating tools all take `preview` (default `false`),
return the same `EditResult` shape, and answer an applied edit with one fixed sentence (D62).

**D20 — The MCP tool table and its annotations.** Settled by WP5a; the table is *The tool surface*
above and the arguments are **D60-D69**. camelCase verb-noun names, 1-based positions,
workspace-relative paths, structured content, and a `preview` flag on every mutating tool.

## Adding a tool

**Adding a tool means editing seven places**, and the tests fail if any of them is missed:

1. the tool method itself, in one of the five classes under `src/ClaudeRoslynLsp/Mcp/Tools/`;
2. its result record in `Mcp/Models/ToolResults.cs`, **and** a `[JsonSerializable]` line for it in
   `Mcp/Models/RoslynToolJsonContext.cs` — without which the schema exporter cannot describe it and
   `dotnet test` fails rather than the AOT publish (D6);
3. `McpServerSetup.RegisterTools` and `McpServerSetup.ToolTypes`, if it is a new class;
4. the table in *The tool surface* above;
5. `ExpectedToolNames` in `build/Build.cs`, which is the only assertion made against the *published*
   binary;
6. `ToolInventoryTests.ExpectedToolNames` and its annotation rows, plus a row in
   `ToolSchemaTests`'s frozen schema table;
7. `.claude/skills/claude-roslyn-lsp/SKILL.md` — `AgentSkillTests` requires every tool to be named
   there, in backticks, and every tool-shaped name in it to exist (see *The agent skill* below).

## The server and its backend

**D5 — A bare `ServiceCollection` for the MCP server**, not `Host.CreateApplicationBuilder`. The SDK
registers `McpServer` as a singleton and running it directly is the SDK's own AOT test-app shape;
the generic host would add configuration providers, metrics and lifetime machinery to a cold start
that a client waits on. `PosixSignalRegistration` handles SIGINT/SIGTERM instead.

**D69 — A build with no backend registers `NotWiredRoslynEngine`, which fails with one sentence.**
The MCP handshake and `tools/list` must work with no Roslyn in the picture — that is the leg
`SmokeTest` drives against a published Native AOT binary on every release RID, and it is also what a
client sees in the seconds before anything is launched. Registering nothing would make the container
throw at the first call with the SDK's generic "An error occurred"; this names the problem and the
command that diagnoses it. `EnsureReadyAsync` is the one member that does not throw — it answers
`Failed`, a state every tool already handles, so the nine gated tools return a status object and
`getWorkspaceStatus` answers correctly instead of failing. WP5b replaces the factory, and nothing
above the seam changes.

**D80 — The `mcp` verb has the same backend vocabulary as `lsp`, plus one value for "none".** `mcp
--smoke` runs the scripted backend in this process, `CLAUDE_ROSLYN_LSP_FAKE_BACKEND=child` runs it
as a child, and `=none` registers `NotWiredRoslynEngine`. That last one used to be what happened by
default, which made `SmokeTest`'s claim about D69 accidental; with the eager start (D76) it would
also have become a 70 MB download on five release runners, so it is spelled out. `SmokeTest` now
drives **two** MCP legs: `=none`, which proves the handshake and `tools/list` do not depend on a
backend at all, and `--smoke`, which proves that starting one from the handshake puts no byte on
stdout — the channel it would corrupt.

## Addressing, statuses and argument names

**D63 — Symbols are addressed by name or by `path:line:col`, and every number a model sees is
1-based.** The name form is what fixes the behaviour this product exists to fix — a name survives an
edit that moves the declaration, and a model that can write `resolveSymbol("IScheduler.Start")`
never greps for a line number first. The position form is what makes these tools compose with Claude
Code's own `LSP` tool, which reports and consumes positions. `Mcp/SymbolAddress` parses the position
form from the **right**, because a Windows drive letter is a colon too. A dotted name matches as a
dot-segment suffix, so `IScheduler.Start` matches `Quartz.IScheduler.Start` and `Scheduler.Start`
does not; the qualifier is checked against Roslyn's container text (`in IScheduler (project …)`),
because `workspace/symbol` searches simple names and reports no namespace. LSP counts from zero and
every editor, compiler error and stack trace in the C# world counts from one, so the conversion
happens here and in the result mapping, and nowhere else.

**D66 — A workspace that is not ready produces a `status`, never a hang and never an error.** Every
result record carries `status` and `note` in the same place, so "is this a real answer" is one
property lookup across ten tools. Three behaviours were available: block, which is what the LSP half
does because a language client has nowhere else to be (D46); fail, which teaches a model the server
is broken; or answer with a status. An MCP call is a turn of a conversation — a model told "still
loading, 3 of 8 projects" can do something else and come back, and a model told nothing for two
minutes cannot. Every tool gates on `EnsureReadyAsync(CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS)`
first and asks Roslyn nothing until it passes, because a question asked early comes back empty
rather than wrong-looking (C27).

**D68 — The workspace root is the process's working directory.** Every MCP client — Claude Code,
Codex, Gemini CLI, Cursor — launches a stdio server with the workspace as its current directory, and
no protocol field carries a root. That directory bounds *writes*, not analysis: a solution
configured elsewhere still loads, and `getWorkspaceStatus` reports its real path so the discrepancy
is visible rather than mysterious.

**D109 — A call's argument names must be the advertised ones, exactly, or the tool does not run.**
The MCP SDK binds `tools/call` arguments by exact name and ignores the rest — verified over stdio on
ModelContextProtocol 2.2.0 and 2.4.0 alike in sonarqube-mcp. Here that is a safety issue, not a
usability one: the four mutating tools take `preview` (D62), so `"Preview": true` was dropped and
the call **wrote to disk** while the model believed it had previewed, and a mis-cased `symbol` or
`path` failed as the SDK's bare "An error occurred invoking …". `Mcp/Tools/ToolArgumentNames`, a
call-tool filter registered with the tools in `McpServerSetup.RegisterTools`, checks every call
against the input schema `tools/list` published and answers a tool error (`isError`, so the model
retries) naming each unknown name with the one it most likely meant — the same name in another case,
then the nearest within two edits, then the one name it is a prefix of — each required argument not
passed, and the accepted names. A case-only mismatch is **refused, not bound**: the boundary keeps
one spelling, and the refusal names it. `SmokeTest` sends a mis-cased `Preview` to every shipped
binary and requires the refusal, which needs no Roslyn because it comes before the tool runs.

## Code actions and diagnostics

**D64 — A code action's id is derived, and the `Fix All:` entries are folded away.** An action has
no identity on the wire — opaque `data`, a title in a natural language, a list regenerated per
request — but `getCodeActions` and `applyCodeAction` have to be two calls about the same thing. So
the id is eight hex characters over `UniqueIdentifier` and `CodeActionPath` (C18), the two members
that say *which fix* rather than *where*: stable across calls and processes, distinct between
siblings that differ only in path, short enough for a model to retype. Roslyn also offers `Fix All: <title>`
as a *separate* action carrying `FixAllFlavors`, so three real choices arrive as five rows; since
`codeAction/resolveFixAll` accepts the plain action's own `data` (C20, S4b), the fix-all entry
becomes `fixAllScopes` on the plain action and disappears. An orphan `Fix All:` entry with no plain
twin is kept, so nothing is ever silently dropped. Nested actions are read out of
`command.arguments[0].NestedCodeActions`, not out of `data`, and each child gets its own id.

**D65 — Fix-all goes through `codeAction/resolveFixAll`, never through the marker command.**
`executeCommandProvider.commands` is empty (C19) — `roslyn.client.fixAllCodeAction` is a client-side
marker, not something to execute — and `codeAction/resolve` on a `Fix All:` entry returns no `edit`
at all (C20). The scope is mandatory and case-sensitive: a lowercase spelling fails with "Sequence
contains no elements" and an omitted one with an `InvalidCastException` (S4b), so the enum is
`Document|Project|Solution` and the tool argument that maps onto it is validated rather than
forwarded.

**D67 — `getDiagnostics` is two completely different requests wearing one name, and it corrects what
comes back.** File scope goes through `textDocument/diagnostic` with no `identifier` (C9), which
needs the document *open* — a closed file answers zero items whatever the scope (C13) — so it opens
it with the bytes on disk and closes it again **unless it was already open**, because closing a
document the LSP half is mirroring would revert Roslyn's view to disk. Project and solution scope go
through `workspace/diagnostic`, which reports closed files only under a `fullSolution` compiler
scope (C14), so the scope is raised for the call and the answer is then cleaned of the three things
C15 says arrive uninvited: `obj/**` and `bin/**` sources, `.csproj` entries, and one copy per target
framework. Analyzer diagnostics are opt-in and the floor is `warning`, so the default answer is
"what stops this compiling" rather than everything an IDE would underline; an explicit `ids` list is
the caller being specific and overrides that. Every answer carries the design-time caveat, and a
workspace-scoped one also says that open files were skipped.

## An edit computed against other text

**D112 — An edit Roslyn computed against text that is not the file on disk is refused, not
written.** Roslyn answers about the text it holds, and for a document an LSP client has open that is
the client's text; the applier writes into the file as it is on disk (D61). When the two differ — an
editor sharing the engine with unsaved changes, or a file a shell command rewrote that the client
never re-sent, which the watcher cannot report either because open documents are excluded from it
(D56) — every edit lands at an offset Roslyn never meant, and the wrong file used to be written and
reported `ok`. Issue #3 was one instance (D111 removed that cause); this is the guard for all of
them. `RoslynToolContext.ApplyEditAsync`, the one place the four mutating tools plan, asks
`IRoslynEngine.OpenTextAsync` for every file that received text edits and refuses the plan — preview
included — when the held text differs from the disk text, naming the file and what to do. Line
endings are compared away, because LSP positions are a line and a character and a client holding the
same lines with other terminators cannot move an edit. The engine answers from its mirror when it
hosts or owns the Roslyn (D91 puts every attached client's documents there), and an attached engine
asks the host with `claude-roslyn-lsp/documentText`, answered from the host's mirror and never
forwarded; the mirror matches the file a URI names rather than its spelling
(`DocumentMirror.FindFile`), because a client may encode a drive letter differently from this
server. An answer that cannot be had is null, which is the behaviour before this decision: a check
that cannot judge does not refuse.

## The agent skill

`.claude/skills/claude-roslyn-lsp/SKILL.md` is one file and it is the **primary lever of this
project**, not an add-on. Everything else here is machinery for answering a question correctly; the
skill is the only thing that decides whether the question gets asked at all. The complaint this
repository started from is behavioural — an agent reaching for `grep` and `sed` on C# — and no
amount of correct `textDocument/references` fixes a model that never sends one.

D70-D72 below are recorded here rather than among the other decisions, because each of them is
about this file and would be read nowhere else; D73, the client configuration packs, is in
[`docs/AGENTS.md`](../../../docs/AGENTS.md).

### The budget of attention

Guidance can live in three places, and they are paid for at three different rates. Putting a
sentence in the wrong one is either a tax on every session that never touches C# or a rule the
caller never sees.

| Surface | Paid by | What belongs there |
|---|---|---|
| `Mcp/ServerInstructions.cs` | **every session** that attaches the MCP server, whether or not any C# work happens | Only what a caller needs *before* the first call and cannot learn from a schema: what the server is, what it costs to start, and the conventions that span all ten tools. Roughly ten lines, and it has to stay that way. |
| A tool's `[Description]`, and each parameter's | the client, per tool, when it renders or calls one | Everything true of *that* tool: what its arguments mean, what its defaults are, what it returns. |
| `SKILL.md` body | only a session that actually starts C# work — the frontmatter description is the only always-loaded part | The order the calls go in, and what to reach for *instead of* what. |

**D70 — the skill holds order and substitution, because a schema structurally cannot.** Every schema
describes one tool, so no schema can say "resolve the symbol before you search for it", "rename
semantically instead of editing the declaration and chasing the compiler", or "run a design-time
diagnostic pass instead of a build". Those are statements about *pairs* of tools, one of which is
usually a tool this server does not own — `Grep`, `Edit`, `Bash dotnet build`. The skill is the only
surface where a claim about two tools can be made at all.

**D71 — the skill teaches two tool families at once, and the pairing between them is the point.**
Claude Code's built-in `LSP` tool has nine read-only operations and needs a file, a line and a
character for every one of them; this server's tools are name-addressed and include the mutations.
A model that has only the first has to find a position before it can navigate, and the only way it
knows to find one is to search for it — which is the exact habit being replaced. So the skill
teaches the join: `resolveSymbol` reports the `path:line:col` the `LSP` tool consumes, and once that
sentence is in the skill the built-in tool stops being a reason to grep. This is why the skill names
a tool it does not own, and why `AgentSkillTests`' verb scan is written to ignore identifiers that
are not shaped like this server's names.

**D72 — one canonical copy, and a test that keeps it in step.** The file lives under
`.claude/skills/`, which is what Claude Code loads from a checkout, what the plugin manifest points
at, and what `npx skills add` and `gh skill install` read when installing into another agent. There
is no second copy anywhere; every other tool is pointed at this path. `AgentSkillTests`
cross-references it against the *reflected* tool inventory in both directions, which is what makes
the skill one of the seven places a new tool has to be added — nothing loads this file at build
time, so it is exactly the kind of document that would keep advertising a surface that has moved on.
The scaffold's escape hatch, which let the "every tool is named" direction pass while the skill was
an under-construction placeholder, was removed with WP6: naming no tool is no longer a state this
repository has.
