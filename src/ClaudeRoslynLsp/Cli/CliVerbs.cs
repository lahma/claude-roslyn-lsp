namespace ClaudeRoslynLsp.Cli;

/// <summary>
/// The verb names, as literals, in one place.
/// </summary>
/// <remarks>
/// They are spelled in more than one file — the dispatcher matches them, the MCP manifest is
/// asserted against one of them, and the child-process fake backend passes another to a copy of this
/// binary — and a verb that is a string literal in three places is a verb that eventually differs in
/// one of them.
/// </remarks>
internal static class CliVerbs
{
    /// <summary>The LSP server.</summary>
    internal const string Lsp = "lsp";

    /// <summary>The MCP server.</summary>
    internal const string Mcp = "mcp";

    /// <summary>The support report.</summary>
    internal const string Doctor = "doctor";

    /// <summary>The <c>install</c> verb: <c>doctor</c> with downloads allowed (D43).</summary>
    internal const string Install = "install";

    /// <summary>
    /// The scripted backend, hidden from the usage text because it is a test double rather than
    /// something a user has any reason to type.
    /// </summary>
    internal const string FakeRoslyn = "fake-roslyn";
}
