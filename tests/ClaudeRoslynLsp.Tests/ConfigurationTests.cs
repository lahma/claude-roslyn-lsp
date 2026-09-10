using ClaudeRoslynLsp.Configuration;

using Microsoft.Extensions.Logging;

using Xunit;

namespace ClaudeRoslynLsp.Tests;

/// <summary>
/// Covers <see cref="ClaudeRoslynLspOptions.FromEnvironment(Func{string, string?})"/>: every
/// variable's default, the plugin-option precedence, and what a bad value falls back to.
/// </summary>
/// <remarks>
/// <para>
/// Everything goes through the <see cref="Func{T, TResult}"/> overload, so no test mutates the real
/// process environment — which is global, shared with every parallel test, and would make these
/// results depend on execution order.
/// </para>
/// <para>
/// The rule under test throughout is that <b>reading configuration never throws</b>. A typo in a
/// client's environment block must not produce a server that dies at startup, because stdout is the
/// protocol channel in both modes and a dead process has nowhere to explain itself.
/// </para>
/// </remarks>
public class ConfigurationTests
{
    /// <summary>Values that mean true, and the ones that mean false.</summary>
    public static TheoryData<string, bool> BooleanValues => new()
    {
        { "1", true },
        { "true", true },
        { "TRUE", true },
        { "yes", true },
        { "on", true },
        { "0", false },
        { "false", false },
        { "no", false },
        { "off", false },
    };

    /// <summary>
    /// Every variable the design names, each with a value that differs from that variable's default.
    /// Used to prove the read is complete: a variable nobody reads is a documented knob that silently
    /// does nothing, which is the one configuration bug a user cannot diagnose.
    /// </summary>
    public static TheoryData<string, string> AllVariables => new()
    {
        { "CLAUDE_ROSLYN_LSP_SOLUTION", "/repo/App.slnx" },
        { "CLAUDE_ROSLYN_LSP_ROSLYN_PATH", "/roslyn" },
        { "CLAUDE_ROSLYN_LSP_SERVER_PATH", "/roslyn" },
        { "CLAUDE_ROSLYN_LSP_ROSLYN_VERSION", "5.12.0-1.26426.8" },
        { "CLAUDE_ROSLYN_LSP_SERVER_VERSION", "5.12.0-1.26426.8" },
        { "CLAUDE_ROSLYN_LSP_ROSLYN_ARGS", "fake-roslyn" },
        { "CLAUDE_ROSLYN_LSP_CACHE_DIR", "/cache" },
        { "CLAUDE_ROSLYN_LSP_HOME", "/home" },
        { "CLAUDE_ROSLYN_LSP_OFFLINE", "1" },
        { "CLAUDE_ROSLYN_LSP_TRANSPORT", "stdio" },
        { "CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS", "30" },
        { "CLAUDE_ROSLYN_LSP_DIAGNOSTICS", "0" },
        { "CLAUDE_ROSLYN_LSP_DIAGNOSTIC_MIN_SEVERITY", "error" },
        { "CLAUDE_ROSLYN_LSP_WORKSPACE_DIAGNOSTICS", "errors" },
        { "CLAUDE_ROSLYN_LSP_FILE_WATCHER", "0" },
        { "CLAUDE_ROSLYN_LSP_GC", "server" },
        { "CLAUDE_ROSLYN_LSP_LOG_LEVEL", "Trace" },
        { "CLAUDE_ROSLYN_LSP_ROSLYN_LOG_LEVEL", "Trace" },
        { "CLAUDE_ROSLYN_LSP_OPTIONS", "{}" },
    };

    [Fact]
    public void AnEmptyEnvironmentProducesEveryDocumentedDefault()
    {
        var options = Read();

        Assert.Null(options.Solution);
        Assert.Null(options.SolutionVariable);
        Assert.Null(options.RoslynPath);
        Assert.Null(options.RoslynPathVariable);
        Assert.Null(options.RoslynVersion);
        Assert.Null(options.RoslynArguments);
        Assert.Null(options.CacheDirectory);
        Assert.Null(options.Home);
        Assert.Null(options.Transport);
        Assert.Null(options.DiagnosticMinSeverity);
        Assert.Null(options.WorkspaceDiagnostics);
        Assert.Null(options.RoslynOptionsJson);
        Assert.Null(options.RoslynLogLevel);
        Assert.Null(options.GarbageCollector);

        Assert.False(options.Offline);
        Assert.True(options.Diagnostics);
        Assert.True(options.FileWatcher);
        Assert.Equal(LogLevel.Information, options.LogLevel);
        Assert.Equal(120, options.ReadyTimeoutSeconds);
    }

    [Fact]
    public void FromEnvironmentReadsTheRealProcessEnvironmentWithoutThrowing()
    {
        // The no-argument overload is what production calls; it must be exercised at least once,
        // whatever the machine's environment happens to hold.
        var options = ClaudeRoslynLspOptions.FromEnvironment();

        Assert.InRange(
            options.ReadyTimeoutSeconds,
            ClaudeRoslynLspOptions.MinReadyTimeoutSeconds,
            ClaudeRoslynLspOptions.MaxReadyTimeoutSeconds);
    }

    [Fact]
    public void ANullVariableSourceIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => ClaudeRoslynLspOptions.FromEnvironment(null!));
    }

    // ------------------------------------------------------------------ strings

    [Fact]
    public void StringValuesAreTrimmed()
    {
        var options = Read(
            ("CLAUDE_ROSLYN_LSP_SOLUTION", "  D:\\repo\\App.slnx  "),
            ("CLAUDE_ROSLYN_LSP_TRANSPORT", " pipe "));

        Assert.Equal("D:\\repo\\App.slnx", options.Solution);
        Assert.Equal("pipe", options.Transport);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ABlankValueIsTheSameAsAnUnsetOne(string value)
    {
        // A variable set to the empty string is how a client configuration expresses "not
        // configured"; treating it as a path would make the adapter look for a solution called "".
        var options = Read(
            ("CLAUDE_ROSLYN_LSP_SOLUTION", value),
            ("CLAUDE_ROSLYN_LSP_ROSLYN_PATH", value));

        Assert.Null(options.Solution);
        Assert.Null(options.SolutionVariable);
        Assert.Null(options.RoslynPath);
    }

    /// <summary>
    /// Every documented variable reaches a property, under its plain name and under the plugin
    /// prefix. A knob that is written down and never read is worse than one that does not exist: the
    /// user configures it, nothing happens, and there is no error to look up.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllVariables))]
    public void EveryDocumentedVariableIsActuallyRead(string name, string value)
    {
        var unset = Read();

        Assert.NotEqual(unset, Read((name, value)));

        // The plugin layer is not a special case for the four options the manifest happens to map
        // today: an option added to the manifest later must not need a code change here to take
        // effect.
        Assert.NotEqual(unset, Read((ClaudeRoslynLspOptions.PluginOptionPrefix + name, value)));
    }

    // ------------------------------------------------------------------ aliases

    /// <summary>
    /// The design document names the Roslyn override both ways. The plugin manifest and the smoke
    /// test use the <c>ROSLYN_</c> spelling, so that is canonical — but somebody who followed the
    /// other half of the document must not get a variable that silently does nothing.
    /// </summary>
    [Fact]
    public void TheServerPathAliasIsHonouredAndTheCanonicalSpellingWins()
    {
        Assert.Equal("/from-alias", Read(("CLAUDE_ROSLYN_LSP_SERVER_PATH", "/from-alias")).RoslynPath);
        Assert.Equal("1.2.3", Read(("CLAUDE_ROSLYN_LSP_SERVER_VERSION", "1.2.3")).RoslynVersion);

        var both = Read(
            ("CLAUDE_ROSLYN_LSP_ROSLYN_PATH", "/canonical"),
            ("CLAUDE_ROSLYN_LSP_SERVER_PATH", "/alias"));

        Assert.Equal("/canonical", both.RoslynPath);
        Assert.Equal("CLAUDE_ROSLYN_LSP_ROSLYN_PATH", both.RoslynPathVariable);
    }

    // ------------------------------------------------------------------ booleans, enums, numbers

    [Theory]
    [MemberData(nameof(BooleanValues))]
    public void FlagsAcceptTheDocumentedSpellings(string value, bool expected)
    {
        Assert.Equal(expected, Read(("CLAUDE_ROSLYN_LSP_OFFLINE", value)).Offline);
        Assert.Equal(expected, Read(("CLAUDE_ROSLYN_LSP_DIAGNOSTICS", value)).Diagnostics);
        Assert.Equal(expected, Read(("CLAUDE_ROSLYN_LSP_FILE_WATCHER", value)).FileWatcher);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("2")]
    [InlineData("")]
    public void AnUnrecognisedFlagKeepsItsDefault(string value)
    {
        // Defaults differ, which is the point: an unparsable value must land on the documented
        // default for that flag, not on "false" for all of them.
        Assert.False(Read(("CLAUDE_ROSLYN_LSP_OFFLINE", value)).Offline);
        Assert.True(Read(("CLAUDE_ROSLYN_LSP_DIAGNOSTICS", value)).Diagnostics);
        Assert.True(Read(("CLAUDE_ROSLYN_LSP_FILE_WATCHER", value)).FileWatcher);
    }

    [Theory]
    [InlineData("Debug", LogLevel.Debug)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("TRACE", LogLevel.Trace)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData("None", LogLevel.None)]
    public void TheLogLevelIsParsedCaseInsensitively(string value, LogLevel expected)
    {
        Assert.Equal(expected, Read(("CLAUDE_ROSLYN_LSP_LOG_LEVEL", value)).LogLevel);
        Assert.Equal(expected, Read(("CLAUDE_ROSLYN_LSP_ROSLYN_LOG_LEVEL", value)).RoslynLogLevel);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("-1")]
    [InlineData("Verbose")]
    [InlineData("")]
    public void AnUnrecognisedLogLevelFallsBack(string value)
    {
        // Enum.TryParse accepts bare numbers, so "42" would otherwise become a LogLevel of 42 and
        // silently suppress every log line.
        Assert.Equal(LogLevel.Information, Read(("CLAUDE_ROSLYN_LSP_LOG_LEVEL", value)).LogLevel);

        // Roslyn's level stays null rather than defaulting: the launcher owns that default, and an
        // unset variable must stay distinguishable from a deliberate choice.
        Assert.Null(Read(("CLAUDE_ROSLYN_LSP_ROSLYN_LOG_LEVEL", value)).RoslynLogLevel);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("30", 30)]
    [InlineData("3600", 3600)]
    public void TheReadinessTimeoutAcceptsItsRange(string value, int expected)
    {
        Assert.Equal(expected, Read(("CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS", value)).ReadyTimeoutSeconds);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("3601")]
    [InlineData("soon")]
    [InlineData("1e3")]
    public void AnOutOfRangeReadinessTimeoutFallsBackToTheDefault(string value)
    {
        // A zero-second gate would pass every request straight through during load, which is the
        // failure mode the gate exists to prevent - and it would do it silently.
        Assert.Equal(120, Read(("CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS", value)).ReadyTimeoutSeconds);
    }

    // ---------------------------------------------------------------------------------------
    // The plugin-option layer
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A Claude Code plugin substitutes an option the user never filled in as the <b>empty string</b>
    /// rather than omitting it, so an option mapped straight onto <c>CLAUDE_ROSLYN_LSP_SOLUTION</c>
    /// would set that variable to <c>""</c> in the child process and shadow a value the user already
    /// had. Blank from the plugin layer therefore means <em>absent</em>, and the plain variable still
    /// wins the day. This is sonarqube-mcp issue #1, adopted rather than re-learned.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyPluginOptionDoesNotShadowTheAmbientVariable(string pluginValue)
    {
        var options = Read(
            ("CLAUDE_PLUGIN_OPTION_CLAUDE_ROSLYN_LSP_SOLUTION", pluginValue),
            ("CLAUDE_PLUGIN_OPTION_CLAUDE_ROSLYN_LSP_ROSLYN_PATH", pluginValue),
            ("CLAUDE_PLUGIN_OPTION_CLAUDE_ROSLYN_LSP_ROSLYN_VERSION", pluginValue),
            ("CLAUDE_PLUGIN_OPTION_CLAUDE_ROSLYN_LSP_LOG_LEVEL", pluginValue),
            ("CLAUDE_ROSLYN_LSP_SOLUTION", "/ambient/App.slnx"),
            ("CLAUDE_ROSLYN_LSP_ROSLYN_PATH", "/ambient/roslyn"),
            ("CLAUDE_ROSLYN_LSP_ROSLYN_VERSION", "9.9.9"),
            ("CLAUDE_ROSLYN_LSP_LOG_LEVEL", "Debug"));

        Assert.Equal("/ambient/App.slnx", options.Solution);
        Assert.Equal("CLAUDE_ROSLYN_LSP_SOLUTION", options.SolutionVariable);
        Assert.Equal("/ambient/roslyn", options.RoslynPath);
        Assert.Equal("9.9.9", options.RoslynVersion);
        Assert.Equal(LogLevel.Debug, options.LogLevel);
    }

    /// <summary>A filled-in option is the point of the prompt, so it wins over the environment.</summary>
    [Fact]
    public void AConfiguredPluginOptionWinsOverTheAmbientVariable()
    {
        var options = Read(
            ("CLAUDE_PLUGIN_OPTION_CLAUDE_ROSLYN_LSP_SOLUTION", "/plugin/App.slnx"),
            ("CLAUDE_PLUGIN_OPTION_CLAUDE_ROSLYN_LSP_LOG_LEVEL", "Trace"),
            ("CLAUDE_ROSLYN_LSP_SOLUTION", "/ambient/App.slnx"),
            ("CLAUDE_ROSLYN_LSP_LOG_LEVEL", "Debug"));

        Assert.Equal("/plugin/App.slnx", options.Solution);
        Assert.Equal("CLAUDE_PLUGIN_OPTION_CLAUDE_ROSLYN_LSP_SOLUTION", options.SolutionVariable);
        Assert.Equal(LogLevel.Trace, options.LogLevel);
    }

    /// <summary>
    /// The precedence applies to the parsed readers too, not only the string ones — otherwise a
    /// plugin option for a flag or a number would be a prompt whose answer is thrown away.
    /// </summary>
    [Fact]
    public void ThePluginLayerCoversFlagsAndNumbersAsWell()
    {
        var options = Read(
            ("CLAUDE_PLUGIN_OPTION_CLAUDE_ROSLYN_LSP_DIAGNOSTICS", "0"),
            ("CLAUDE_PLUGIN_OPTION_CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS", "30"),
            ("CLAUDE_ROSLYN_LSP_DIAGNOSTICS", "1"),
            ("CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS", "90"));

        Assert.False(options.Diagnostics);
        Assert.Equal(30, options.ReadyTimeoutSeconds);
    }

    /// <summary>The plugin sets this one from <c>${CLAUDE_PLUGIN_DATA}</c>, unprefixed and by design.</summary>
    [Fact]
    public void TheHomeDirectoryIsReadFromThePlainVariable()
    {
        Assert.Equal("/plugin/data", Read(("CLAUDE_ROSLYN_LSP_HOME", "/plugin/data")).Home);
    }

    /// <summary>Builds a variable source from the pairs a test cares about; everything else is unset.</summary>
    private static ClaudeRoslynLspOptions Read(params (string Name, string Value)[] variables)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, value) in variables)
        {
            map[name] = value;
        }

        return ClaudeRoslynLspOptions.FromEnvironment(name => map.TryGetValue(name, out var value) ? value : null);
    }
}
