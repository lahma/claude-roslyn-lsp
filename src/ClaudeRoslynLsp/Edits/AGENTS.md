# Agent instructions: writing source files

> **Read this when:** you change how a source file is read, edited or written back — the codec, the
> offsets, the path guard, the planner and the applier, the diff, the preview cache, or the
> `.editorconfig` rules `formatCode` applies.
>
> One of the co-located instruction files indexed from the repository-root
> [`AGENTS.md`](../../../AGENTS.md); read that first. Two guards against writing the wrong text sit
> outside this directory, where the text enters: the byte order mark a client sends is stripped by the
> mediation (D111, [`../Adapter/AGENTS.md`](../Adapter/AGENTS.md)), and an edit computed against text
> that is not on disk is refused by the tools (D112, [`../Mcp/AGENTS.md`](../Mcp/AGENTS.md)).

## The only writer of source files

**D21 — The MCP server applies its own edits.** Settled by WP5a as **D61** and **D62**. It is the
LSP *client* in that direction, so it writes files itself, preserving BOM and line endings, and then
tells Roslyn what changed — including C33's synthetic project event, which WP5b's engine owns (D75).

**D61 — Reading and writing a source file is a component, not two `File` calls.**
`Edits/TextFileCodec` decodes bytes to a string and reports how to write them back;
`Edits/TextOffsets` turns LSP positions into UTF-16 offsets; `Edits/WorkspacePathGuard` decides what
may be written at all. A round trip through `File.ReadAllText`/`WriteAllText` silently drops a UTF-8
mark and re-encodes a UTF-16 file, which turns a one-word refactoring into a diff that touches every
line — invisibly, because the code still compiles. The guard is the other half: an edit is an
instruction from another process, and Roslyn hands out URIs outside the workspace as a matter of
course (C25's `MetadataAsSource` temp files), so the rule is `file:` scheme plus a *resolved* path
under the root, compared case-insensitively on Windows and macOS and case-sensitively elsewhere, the
way the file systems themselves do.

**D62 — `preview` runs the identical planning pass and simply never writes; the apply that follows
reuses what was previewed.** Planning and applying are separate methods over one
`WorkspaceEditPlan`, so a preview's diff is not a description of a different operation.
`Edits/EditCache` then remembers the resolved edit under a key of tool plus answer-changing
arguments — `preview` itself deliberately excluded, or the two calls would never meet — stamped with
every read file's last write time and length. If any of them moved, the entry is dropped and the
apply re-resolves: that turns "silently applied a different edit than the one that was approved"
into "slightly slower", which is the right way round. The diff is budgeted (`maxDiffLines`, 200)
because a solution-wide fix-all can rewrite two hundred files, and what is cut is **named** — "… 14
more hunks" is information, silence is not. Every applied result carries one fixed sentence, *Files
were changed on disk; re-read a file before editing it.*, because Claude Code's own `Edit` tool
refuses a file that changed since its last `Read` and the refusal is undiagnosable from its message.

## Layout rules

**D113 — `formatCode` applies `end_of_line` and `insert_final_newline` itself, after Roslyn's
edits.** Roslyn's `textDocument/formatting` changes indentation, spacing and wrapping and never
converts a file's line endings or its final newline — `dotnet format` applies those two rules with
formatters of its own — so a repository gating CI on `dotnet format --verify-no-changes` still
failed after the tool that described itself as its replacement (issue #4). `Edits/EditorConfig`
resolves the `.editorconfig` chain for each formatted file by the specification (nearest first,
stopping at `root = true`, later sections winning, `unset`, globs without a `/` matching in any
directory, `*` `**` `?` `[seq]` `{a,b}` `{n1..n2}`, case-sensitive as Roslyn's own matching is), and
`WorkspaceEditApplier.Plan` takes the resulting `TextLayout` per file: every terminator normalised
to `end_of_line`, the final newline added or removed, the file then written in that convention. A
file Roslyn has nothing to change in is still planned when it has layout rules, and a layout change
counts as one edit so the answer reports it. Only these two: encoding and the byte order mark stay
as the file has them (D61), and nothing else `dotnet format` does is taken on. The chain is read
again on every call, the cached apply included, because the rules live in files on disk. A pure-BCL
reader (hard rules 1 and 3), with a `Regex` per section rather than `Adapter/GlobMatcher`, whose LSP
syntax is segment-wise and has no character classes or numeric ranges.
