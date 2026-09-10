using ClaudeRoslynLsp.Cli;

using Xunit;

namespace ClaudeRoslynLsp.Tests;

/// <summary>
/// The argv contract: which verbs exist, and what a caller that gets it wrong is told.
/// </summary>
/// <remarks>
/// The exit codes are the interesting half. A bare invocation is an <em>error</em> here, unlike in
/// the sibling MCP servers where it means "serve", because this binary is two servers on one stdout
/// and a client that forgot its subcommand would otherwise be connected to the wrong protocol and
/// simply hang. That is a decision a test has to hold in place: "no arguments starts the server" is
/// exactly the convenience somebody would add back.
/// </remarks>
public class SmokeTests
{
    [Fact]
    public async Task HelpSucceedsAndDescribesEveryVerb()
    {
        var exitCode = await CliDispatcher.RunAsync(["--help"]);

        Assert.Equal(CliDispatcher.ExitSuccess, exitCode);
        Assert.Contains("lsp", CliDispatcher.UsageText, StringComparison.Ordinal);
        Assert.Contains("mcp", CliDispatcher.UsageText, StringComparison.Ordinal);
        Assert.Contains("doctor", CliDispatcher.UsageText, StringComparison.Ordinal);
        Assert.Contains("--version", CliDispatcher.UsageText, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>fake-roslyn</c> is a test double the smoke test launches, not a verb a user has any reason
    /// to type, so it stays out of the usage text. Asserted rather than assumed: a "helpful" edit
    /// that documents it would invite somebody to run it.
    /// </summary>
    [Fact]
    public void TheHiddenVerbIsNotAdvertised()
    {
        Assert.DoesNotContain("fake-roslyn", CliDispatcher.UsageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionSucceeds()
    {
        Assert.Equal(CliDispatcher.ExitSuccess, await CliDispatcher.RunAsync(["--version"]));
        Assert.Equal(CliDispatcher.ExitSuccess, await CliDispatcher.RunAsync(["-v"]));
    }

    [Fact]
    public async Task NoArgumentsIsAUsageError()
    {
        Assert.Equal(CliDispatcher.ExitUsage, await CliDispatcher.RunAsync([]));
    }

    [Fact]
    public async Task UnknownArgumentExitsWithUsageCode()
    {
        Assert.Equal(CliDispatcher.ExitUsage, await CliDispatcher.RunAsync(["not-a-command"]));
    }

    /// <summary>
    /// A verb that exists but is not built yet exits 3, not 2. The distinction is what tells a user
    /// whether to fix their command line or to upgrade.
    /// </summary>
    /// <remarks>
    /// <c>doctor</c> was on this list until the acquisition work package landed. It is not, and must
    /// not be, added back with a different expectation: exit 3 means "recognised, not implemented",
    /// and <c>doctor</c>'s exit code now means "Roslyn is runnable" (0) or "it is not" (1).
    /// </remarks>
    [Theory]
    [InlineData("fake-roslyn")]
    public async Task AVerbThisBuildDoesNotImplementYetSaysSo(string verb)
    {
        Assert.Equal(CliDispatcher.ExitNotImplemented, await CliDispatcher.RunAsync([verb]));
    }

    /// <summary>
    /// Both acquisition verbs are dispatched, and an option neither of them knows is a usage error
    /// rather than a silently ignored word.
    /// </summary>
    [Theory]
    [InlineData("doctor")]
    [InlineData("install")]
    public async Task TheAcquisitionVerbsRejectAnUnknownOption(string verb)
    {
        Assert.Equal(CliDispatcher.ExitUsage, await CliDispatcher.RunAsync([verb, "--wat"]));
    }

    [Fact]
    public void TheUsageTextDocumentsTheInstallVerb()
    {
        Assert.Contains("install", CliDispatcher.UsageText, StringComparison.Ordinal);
        Assert.Contains("--json", CliDispatcher.UsageText, StringComparison.Ordinal);
    }
}
