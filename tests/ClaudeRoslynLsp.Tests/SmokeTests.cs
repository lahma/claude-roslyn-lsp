using ClaudeRoslynLsp.Cli;

using NUnit.Framework;

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
    [Test]
    public async Task HelpSucceedsAndDescribesEveryVerb()
    {
        var exitCode = await CliDispatcher.RunAsync(["--help"]);

        Assert.That(exitCode, Is.EqualTo(CliDispatcher.ExitSuccess));
        Assert.That(CliDispatcher.UsageText, Contains.Substring("lsp").Using(StringComparison.Ordinal));
        Assert.That(CliDispatcher.UsageText, Contains.Substring("mcp").Using(StringComparison.Ordinal));
        Assert.That(CliDispatcher.UsageText, Contains.Substring("doctor").Using(StringComparison.Ordinal));
        Assert.That(CliDispatcher.UsageText, Contains.Substring("--version").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>fake-roslyn</c> is a test double the smoke test launches, not a verb a user has any reason
    /// to type, so it stays out of the usage text. Asserted rather than assumed: a "helpful" edit
    /// that documents it would invite somebody to run it.
    /// </summary>
    [Test]
    public void TheHiddenVerbIsNotAdvertised()
    {
        Assert.That(CliDispatcher.UsageText, !Contains.Substring("fake-roslyn").Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task VersionSucceeds()
    {
        Assert.That(await CliDispatcher.RunAsync(["--version"]), Is.EqualTo(CliDispatcher.ExitSuccess));
        Assert.That(await CliDispatcher.RunAsync(["-v"]), Is.EqualTo(CliDispatcher.ExitSuccess));
    }

    [Test]
    public async Task NoArgumentsIsAUsageError()
    {
        Assert.That(await CliDispatcher.RunAsync([]), Is.EqualTo(CliDispatcher.ExitUsage));
    }

    [Test]
    public async Task UnknownArgumentExitsWithUsageCode()
    {
        Assert.That(await CliDispatcher.RunAsync(["not-a-command"]), Is.EqualTo(CliDispatcher.ExitUsage));
    }

    /// <summary>
    /// Both acquisition verbs are dispatched, and an option neither of them knows is a usage error
    /// rather than a silently ignored word.
    /// </summary>
    [TestCase("doctor")]
    [TestCase("install")]
    public async Task TheAcquisitionVerbsRejectAnUnknownOption(string verb)
    {
        Assert.That(await CliDispatcher.RunAsync([verb, "--wat"]), Is.EqualTo(CliDispatcher.ExitUsage));
    }

    [Test]
    public void TheUsageTextDocumentsTheInstallVerb()
    {
        Assert.That(CliDispatcher.UsageText, Contains.Substring("install").Using(StringComparison.Ordinal));
        Assert.That(CliDispatcher.UsageText, Contains.Substring("--json").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>lsp</c> takes only the hidden <c>--smoke</c> flag; anything else is a command line the
    /// caller has to fix rather than a request to serve. Accepting an unknown argument silently would
    /// connect a client to a server that then ignored what it was launched with.
    /// </summary>
    [Test]
    public async Task AnUnknownArgumentAfterTheLspVerbIsAUsageError()
    {
        Assert.That(await CliDispatcher.RunAsync(["lsp", "--not-a-flag"]), Is.EqualTo(CliDispatcher.ExitUsage));
    }
}
