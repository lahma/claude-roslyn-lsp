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
/// WP5 replaces this with the real text, against the frozen tool table: positions are 1-based, edits
/// are written to disk immediately and a file must be re-read before it is edited again, every
/// mutating tool has a preview flag, and diagnostics are a design-time pass rather than a build.
/// Until there are tools, promising any of that would be false.
/// </para>
/// </remarks>
internal static class ServerInstructions
{
    /// <summary>The instruction text.</summary>
    internal const string Text = """
        C# refactoring backed by Microsoft's Roslyn language server, for solutions this workspace contains.

        - This build is a scaffold: it completes the MCP handshake and registers no tools yet, so there is nothing here to call.
        - When the tools land they are name-addressed - a symbol is named, not pointed at with a line number - and they write their edits to disk immediately.
        """;
}
