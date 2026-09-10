# tests/fixtures — provenance

Every fixture in this directory gets a row here: what it is, when it arrived, and **whether the
bytes came off the wire or were written by hand**. JSON cannot carry comments and a `.cs` file's
comments are part of what is under test, so this file is the only place that record can live. A
fixture whose provenance nobody wrote down is a fixture nobody dares to re-capture.

| Fixture | Kind | Added | Provenance |
|---|---|---|---|
| `HelloSolution/` | Hand-written | 2026-09-10 (WP4) | Grown from the WP0 spike solution (`scratchpad/spikes/Fixture`) that the C-numbered protocol facts were observed against, then extended with `Hello.Tests` and doc comments. No captured bytes. |
| `Directory.Build.props` | Hand-written | 2026-09-10 (WP4) | Empty on purpose — see the file. |
| `Directory.Packages.props` | Hand-written | 2026-09-10 (WP4) | Switches central package management off for the fixture — see the file. |

## HelloSolution

An ordinary three-project solution, shaped the way somebody else's repository is shaped, that the
live tests copy to a temporary directory and point a **real** Roslyn at. It is deliberately *not*
built by this repository's build: `HelloSolution.slnx` is absent from `claude-roslyn-lsp.slnx`, it
sits outside the test project's compile globs, and its own `Directory.Build.props` stops this
repository's conventions from reaching it. `Clean` deletes its `bin`/`obj` along with every other
`tests/**/bin` and `tests/**/obj`.

**It does not compile, and that is the point.** `Hello.App/Program.cs` contains `int x = "s";`,
which is CS0029 at severity 1. Everything else restores and builds; a `dotnet restore` of the
solution is green, which is what matters, because Roslyn restores server-side when `obj/` is
missing (C30) and a fixture that could not restore would load with no references.

### What each part is for

| Part | Exists so that |
|---|---|
| `Hello.Core` multi-targets `net10.0;netstandard2.0` | Roslyn loads a multi-targeted project once per TFM, which is what produces the duplicate call-hierarchy items the adapter de-duplicates (C24) and the per-TFM duplicates the workspace diagnostics filter collapses (C15). A single-target fixture lets both regress silently. |
| `IShape` with `Circle` and `Square` | `textDocument/implementation` has exactly **two** answers — a number a test can assert rather than "more than zero". |
| `Calculator.Compute()` called from `Caller.CallOnce()`, `Program.Main()` and `ShapeTests` | `findReferences` and `prepareCallHierarchy` + `incomingCalls` have several call sites across three projects, so "loaded the solution" is distinguishable from "loaded the two projects the app references". |
| `Calculator.cs`'s unused `using System.Text;` | IDE0005, which arrives at severity 4 (Hint) at position 0:0 for the whole using block (C16) — the exact entry the diagnostics bridge's severity floor and `Unnecessary`-tag rule drop. |
| `Calculator.Describe()` | A CA1822 site (`Make static`, severity 3) with a Fix All entry beside it (C16). |
| `Rectangle` inside `Square.cs` | Offers `Move type to Rectangle.cs`, which resolves to a `create` file operation followed by edits into a file that does not exist yet (C21) — the shape the MCP half's edit applier has to handle. |
| `Program.cs`'s `int x = "s";` | CS0029, at severity 1. The live tests assert it arrives as a `publishDiagnostics` after `didOpen` and **disappears** within 2 s of a `didChange` that fixes it. Without a real error the diagnostics bridge could be a no-op and every test would still pass. |
| `Hello.Tests` is a real xunit project | The fixture mirrors a normal repository: a second reference edge, a project the fallback's keep-the-tests rule (D42) can be observed on, and something a solution scan must not mistake for the repository's own solution. Nothing here ever runs these tests. |

### What the copies do to it

The live tests never use this directory in place; each copies it to a temporary tree first, because
they write to it. `AdapterLiveFixture` copies it for the LSP session, and `McpToolsLiveFixture`
copies it separately for the MCP tool session — separately because the two edit the same files in
different ways and xunit runs their collections in parallel.

`McpToolsLiveFixture` also rewrites its copy of `Hello.Core/Caller.cs` as **CRLF with a UTF-8
byte-order mark** (D81). It cannot be checked in that way — `.gitattributes` forces `*.cs` to LF on
checkout — and the property being asserted needs it: `Caller.cs` is one of the files a
`renameSymbol` rewrites, and the test proves it comes back CRLF with its mark intact. A codec that
silently normalised it would turn a one-word refactoring into a diff touching every line, and the
code would still compile.

### Rules for changing it

- **Do not fix the CS0029.** Four tests and one build target assert it.
- **Do not move `Rectangle` into its own file**, do not make `Describe()` static, do not remove the
  `using System.Text;`. Each is a code-action or diagnostic site something asserts on.
- **Do not add a `nuget.config`.** The repository's own reaches the fixture and is correct for it;
  a second one would be a second place to fix when a feed moves. (Checked: `dotnet restore` of
  `HelloSolution.slnx` is green without one.)
- **Keep the package versions equal to the adapter test project's.** A restore of the fixture then
  finds a warm cache instead of downloading three packages inside the live test's budget.
