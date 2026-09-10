using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Mcp;

namespace ClaudeRoslynLsp.Cli;

/// <summary>
/// Hand-rolled argv dispatch (D3). No <c>System.CommandLine</c>: the surface is four verbs and two
/// flags, and the package budget is a hard constraint.
/// </summary>
/// <remarks>
/// <para>
/// <b>No arguments is an error here</b>, unlike in the sibling MCP servers where a bare invocation
/// means "serve". This binary is two servers in one, and a client that forgot the subcommand would
/// otherwise be connected to the wrong protocol: an LSP client would receive newline-delimited MCP
/// JSON with no <c>Content-Length</c> header and hang, and an MCP client would receive framed LSP
/// bytes and fail to parse. Exiting 2 with the usage text on stderr is the only failure mode a
/// caller can act on.
/// </para>
/// <para>
/// This namespace is the only place in the product that may write to the console, and within it only
/// <see cref="CliRuntime"/> actually does.
/// </para>
/// </remarks>
internal static class CliDispatcher
{
    /// <summary>Command completed successfully.</summary>
    internal const int ExitSuccess = 0;

    /// <summary>Command ran but failed.</summary>
    internal const int ExitFailure = 1;

    /// <summary>The command line could not be understood.</summary>
    internal const int ExitUsage = 2;

    /// <summary>
    /// The verb exists and is spelled correctly, but this build does not implement it yet.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ExitUsage"/> on purpose: "you typed something I do not recognise"
    /// and "you typed something I recognise and cannot do yet" are different problems, and only the
    /// second one is fixed by upgrading. <c>doctor</c> answers with this until WP3 lands.
    /// </remarks>
    internal const int ExitNotImplemented = 3;

    internal static string UsageText { get; } =
        $"""
         {ServerVersion.Name} {ServerVersion.Value} - Roslyn C# language server adapter (LSP + MCP).

         Usage:
           {ServerVersion.Name} lsp                Run the LSP server over stdio, mediating Microsoft's
                                                roslyn-language-server. This is what an editor or an
                                                agent's LSP client launches.
           {ServerVersion.Name} mcp                Run the MCP server over stdio: semantic refactoring
                                                tools for a model that would otherwise use grep and
                                                sed.
           {ServerVersion.Name} doctor             Report the Roslyn resolution chain, the .NET host,
                                                the solution candidates and the integration state,
                                                then start Roslyn and complete one handshake with it.
                                                Exits 0 only if that works. Add --json for a machine-
                                                readable report, --fix to install what is missing.
           {ServerVersion.Name} install           Download and verify the pinned Roslyn server, then
                                                report exactly as doctor does. Same thing as
                                                `doctor --fix`; the servers do this on first use
                                                anyway, so this is for doing it deliberately.

         Options:
           -h, --help                           Show this help text.
           -v, --version                        Show the version. (Through `dnx`, use `doctor`
                                                instead: dnx consumes --version itself.)

         There is no default verb: one of the above is required, because the two servers speak
         different protocols on the same stdout and a client connected to the wrong one hangs.

         Configuration is environment variables only. The client-facing ones:
           CLAUDE_ROSLYN_LSP_SOLUTION           The .slnx/.sln/.csproj to open (default: discovered).
           CLAUDE_ROSLYN_LSP_ROSLYN_PATH        A directory holding an installed Roslyn server.
           CLAUDE_ROSLYN_LSP_ROSLYN_VERSION     Override the pinned roslyn-language-server version.
           CLAUDE_ROSLYN_LSP_LOG_LEVEL          Trace|Debug|Information|Warning|Error|Critical|None
                                                (default Information). Logs go to stderr.

         Run `{ServerVersion.Name} doctor` to see which of these are in effect; the README documents
         the full set.
         """;

    /// <summary>Dispatches <paramref name="args"/> and returns the process exit code.</summary>
    /// <param name="args">The process arguments, without the executable name.</param>
    internal static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            return Usage($"{ServerVersion.Name}: a verb is required.");
        }

        switch (args[0])
        {
            case CliVerbs.Lsp:
                return await LspAdapterServer.RunStdioAsync(args[1..]).ConfigureAwait(false);

            case CliVerbs.Mcp:
                return await RunMcpAsync(args[1..]).ConfigureAwait(false);

            case CliVerbs.Doctor:
                return await DoctorCommand.RunAsync(args[1..], fix: false).ConfigureAwait(false);

            // The same command with the download allowed (D43). A separate verb rather than only a
            // flag because "install it" is what a user wants to type, and a verb is what a README,
            // a CI step and a support answer can all name without explaining a flag first.
            case CliVerbs.Install:
                return await DoctorCommand.RunAsync(args[1..], fix: true).ConfigureAwait(false);

            // Hidden on purpose: it is a test double the smoke test launches, not something a user
            // has any reason to type, so it is absent from UsageText.
            case CliVerbs.FakeRoslyn:
                return await FakeRoslynCommand.RunAsync().ConfigureAwait(false);

            case "--version":
            case "-v":
                CliRuntime.WriteOut(ServerVersion.Value);
                return ExitSuccess;

            case "--help":
            case "-h":
                CliRuntime.WriteOut(UsageText);
                return ExitSuccess;

            default:
                return Usage($"{ServerVersion.Name}: unknown argument '{args[0]}'.");
        }
    }

    /// <summary>
    /// Runs the MCP server, having decided which Roslyn it is talking to.
    /// </summary>
    /// <remarks>
    /// The verb's only argument is the hidden <c>--smoke</c>, which swaps the launched Roslyn for the
    /// scripted backend in this process (D80). It is hidden for the same reason the <c>lsp</c> verb's
    /// is: it is what lets <c>SmokeTest</c> prove the tool surface of a published Native AOT binary
    /// on every release RID without downloading seventy megabytes of Roslyn onto five runners.
    /// </remarks>
    /// <param name="arguments">Whatever followed the verb.</param>
    private static async Task<int> RunMcpAsync(string[] arguments)
    {
        var smoke = false;

        foreach (var argument in arguments)
        {
            if (string.Equals(argument, McpBackendSelection.SmokeFlag, StringComparison.Ordinal))
            {
                smoke = true;
                continue;
            }

            CliRuntime.WriteError($"{ServerVersion.Name}: `mcp` does not take the argument '{argument}'.");
            return ExitUsage;
        }

        return await McpServerSetup
            .RunStdioAsync(McpBackendSelection.Resolve(smoke), McpBackendSelection.Start)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reports a command line that could not be understood: the reason, then the usage text, both on
    /// stderr.
    /// </summary>
    /// <remarks>
    /// Stderr rather than stdout even though this is the whole output of the run, because the caller
    /// that gets it wrong is usually a protocol client whose stdout is already a channel — writing
    /// usage text there would corrupt the stream it is complaining about.
    /// </remarks>
    /// <param name="reason">One line naming what was wrong.</param>
    private static int Usage(string reason)
    {
        CliRuntime.WriteError(reason);
        CliRuntime.WriteErrorLine();
        CliRuntime.WriteError(UsageText);

        return ExitUsage;
    }
}
