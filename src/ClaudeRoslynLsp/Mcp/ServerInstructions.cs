namespace ClaudeRoslynLsp.Mcp;

/// <summary>
/// The instructions sent to the client during <c>initialize</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the most expensive of the three places guidance can live, because every session that
/// attaches the server pays for it whether or not any C# work happens. So it holds only what a client
/// needs <em>before</em> the first call and cannot learn from a schema: what the server is, what it
/// costs to start, and the conventions that span every tool. Roughly ten lines. Anything that is true
/// of one tool belongs in that tool's description; anything about the order calls go in belongs in
/// the skill, which is only paid for by a session that actually starts C# work.
/// </para>
/// <para>
/// Every line below is a rule a caller gets wrong otherwise: positions are 1-based where LSP is
/// 0-based (D63); edits are written to disk immediately, which breaks Claude Code's own <c>Edit</c>
/// tool until the file is re-read (D62); <c>preview</c> writes nothing and the apply that follows
/// reuses exactly what was previewed; diagnostics are a design-time pass and not a build (D67); the
/// workspace takes seconds to load and every tool says <c>loading</c> rather than hanging (D66); and
/// <c>renameSymbol</c> renames the symbol, not the file.
/// </para>
/// </remarks>
internal static class ServerInstructions
{
    /// <summary>The instruction text.</summary>
    internal const string Text = """
        Semantic C# navigation and refactoring, backed by Microsoft's Roslyn language server, for the solution in this workspace.

        - Prefer these tools over grep, sed and hand edits for anything about C# symbols: they understand the code, and text tools do not.
        - Lines and columns are 1-based, in and out. Paths are relative to the workspace root, with forward slashes.
        - Any tool taking a `symbol` accepts a name (`IScheduler.Start`, matched as a dot-segment suffix) or a position (`src/Core/Calculator.cs:7:15`).
        - Mutating tools write to disk immediately. After one, re-read any file it lists before editing that file yourself.
        - Every mutating tool takes `preview: true`, which writes nothing and returns the diff; calling it again with `preview: false` applies exactly that edit.
        - `getDiagnostics` is a design-time pass, about a second, not a build: no source generators as a build runs them, no MSBuild errors, no tests. Run the real build before claiming the solution compiles.
        - The solution takes a few seconds to load and can take up to two minutes. Until it has, tools answer `status: "loading"` instead of hanging; `getWorkspaceStatus` says how far it has got.
        - `renameSymbol` renames a symbol and its uses. It does not rename the file - `getCodeActions` offers "Move type to X.cs" for that.
        """;
}
