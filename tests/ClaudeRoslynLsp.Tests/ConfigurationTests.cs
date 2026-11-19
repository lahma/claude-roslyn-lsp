using ClaudeRoslynLsp.Configuration;

using Microsoft.Extensions.Logging;

using NUnit.Framework;

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
    public static IEnumerable<object[]> BooleanValues =>
    [
        ["1", true],
        ["true", true],
        ["TRUE", true],
        ["yes", true],
        ["on", true],
        ["0", false],
        ["false", false],
        ["no", false],
        ["off", false],
    ];

    /// <summary>
    /// Every variable the design names, each with a value that differs from that variable's default.
    /// Used to prove the read is complete: a variable nobody reads is a documented knob that silently
    /// does nothing, which is the one configuration bug a user cannot diagnose.
    /// </summary>
    public static IEnumerable<object[]> AllVariables =>
    [
        ["CLAUDE_ROSLYN_LSP_SOLUTION", "/repo/App.slnx"],
        ["CLAUDE_ROSLYN_LSP_ROSLYN_PATH", "/roslyn"],
        ["CLAUDE_ROSLYN_LSP_SERVER_PATH", "/roslyn"],
        ["CLAUDE_ROSLYN_LSP_ROSLYN_VERSION", "5.12.0-1.26426.8"],
        ["CLAUDE_ROSLYN_LSP_SERVER_VERSION", "5.12.0-1.26426.8"],
        ["CLAUDE_ROSLYN_LSP_ROSLYN_ARGS", "fake-roslyn"],
        ["CLAUDE_ROSLYN_LSP_CACHE_DIR", "/cache"],
        ["CLAUDE_ROSLYN_LSP_HOME", "/home"],
        ["CLAUDE_ROSLYN_LSP_OFFLINE", "1"],
        ["CLAUDE_ROSLYN_LSP_TRANSPORT", "stdio"],
        ["CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS", "30"],
        ["CLAUDE_ROSLYN_LSP_DIAGNOSTICS", "0"],
        ["CLAUDE_ROSLYN_LSP_DIAGNOSTIC_MIN_SEVERITY", "error"],
        ["CLAUDE_ROSLYN_LSP_WORKSPACE_DIAGNOSTICS", "errors"],
        ["CLAUDE_ROSLYN_LSP_FILE_WATCHER", "0"],
        ["CLAUDE_ROSLYN_LSP_SHARE", "0"],
        ["CLAUDE_ROSLYN_LSP_GC", "server"],
        ["CLAUDE_ROSLYN_LSP_GC_CONSERVE", "2"],
        ["CLAUDE_ROSLYN_LSP_IDLE_RECYCLE_MINUTES", "5"],
        ["CLAUDE_ROSLYN_LSP_RECYCLE_GROWTH_FACTOR", "3.5"],
        ["CLAUDE_ROSLYN_LSP_IDLE_SHUTDOWN_MINUTES", "240"],
        ["CLAUDE_ROSLYN_LSP_LOG_LEVEL", "Trace"],
        ["CLAUDE_ROSLYN_LSP_ROSLYN_LOG_LEVEL", "Trace"],
        ["CLAUDE_ROSLYN_LSP_OPTIONS", "{}"],
    ];

    [Test]
    public void AnEmptyEnvironmentProducesEveryDocumentedDefault()
    {
        var options = Read();

        Assert.That(options.Solution, Is.Null);
        Assert.That(options.SolutionVariable, Is.Null);
        Assert.That(options.RoslynPath, Is.Null);
        Assert.That(options.RoslynPathVariable, Is.Null);
        Assert.That(options.RoslynVersion, Is.Null);
        Assert.That(options.RoslynArguments, Is.Null);
        Assert.That(options.CacheDirectory, Is.Null);
        Assert.That(options.Home, Is.Null);
        Assert.That(options.Transport, Is.Null);
        Assert.That(options.DiagnosticMinSeverity, Is.Null);
        Assert.That(options.WorkspaceDiagnostics, Is.Null);
        Assert.That(options.RoslynOptionsJson, Is.Null);
        Assert.That(options.RoslynLogLevel, Is.Null);
        Assert.That(options.GarbageCollector, Is.Null);

        Assert.That(options.Offline, Is.False);
        Assert.That(options.Diagnostics, Is.True);
        Assert.That(options.FileWatcher, Is.True);
        Assert.That(options.Share, Is.True);
        Assert.That(options.LogLevel, Is.EqualTo(LogLevel.Information));
        Assert.That(options.ReadyTimeoutSeconds, Is.EqualTo(120));

        // The idle policy (D96, D100, D104, D106).
        Assert.That(options.IdleRecycleMinutes, Is.EqualTo(20));
        Assert.That(options.RecycleGrowthFactor, Is.EqualTo(2.0));
        Assert.That(options.IdleShutdownMinutes, Is.EqualTo(0));
        Assert.That(options.GcConserveMemory, Is.EqualTo(7));
        Assert.That(options.GcConserveMemoryVariable, Is.Null);
    }

    [Test]
    public void FromEnvironmentReadsTheRealProcessEnvironmentWithoutThrowing()
    {
        // The no-argument overload is what production calls; it must be exercised at least once,
        // whatever the machine's environment happens to hold.
        var options = ClaudeRoslynLspOptions.FromEnvironment();

        Assert.That(
            options.ReadyTimeoutSeconds,
            Is.InRange(ClaudeRoslynLspOptions.MinReadyTimeoutSeconds, ClaudeRoslynLspOptions.MaxReadyTimeoutSeconds));
    }

    [Test]
    public void ANullVariableSourceIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => ClaudeRoslynLspOptions.FromEnvironment(null!));
    }

    // ------------------------------------------------------------------ strings

    [Test]
    public void StringValuesAreTrimmed()
    {
        var options = Read(
            ("CLAUDE_ROSLYN_LSP_SOLUTION", "  D:\\repo\\App.slnx  "),
            ("CLAUDE_ROSLYN_LSP_TRANSPORT", " pipe "));

        Assert.That(options.Solution, Is.EqualTo("D:\\repo\\App.slnx"));
        Assert.That(options.Transport, Is.EqualTo("pipe"));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("\t")]
    public void ABlankValueIsTheSameAsAnUnsetOne(string value)
    {
        // A variable set to the empty string is how a client configuration expresses "not
        // configured"; treating it as a path would make the adapter look for a solution called "".
        var options = Read(
            ("CLAUDE_ROSLYN_LSP_SOLUTION", value),
            ("CLAUDE_ROSLYN_LSP_ROSLYN_PATH", value));

        Assert.That(options.Solution, Is.Null);
        Assert.That(options.SolutionVariable, Is.Null);
        Assert.That(options.RoslynPath, Is.Null);
    }

    /// <summary>
    /// Every documented variable reaches a property, under its plain name and under the plugin
    /// prefix. A knob that is written down and never read is worse than one that does not exist: the
    /// user configures it, nothing happens, and there is no error to look up.
    /// </summary>
    [TestCaseSource(nameof(AllVariables))]
    public void EveryDocumentedVariableIsActuallyRead(string name, string value)
    {
        var unset = Read();

        Assert.That(Read((name, value)), Is.Not.EqualTo(unset));

        // The plugin layer is not a special case for the four options the manifest happens to map
        // today: an option added to the manifest later must not need a code change here to take
        // effect.
        Assert.That(Read((ClaudeRoslynLspOptions.PluginOptionPrefix + name, value)), Is.Not.EqualTo(unset));
    }

    // ------------------------------------------------------------------ aliases

    /// <summary>
    /// The design document names the Roslyn override both ways. The plugin manifest and the smoke
    /// test use the <c>ROSLYN_</c> spelling, so that is canonical — but somebody who followed the
    /// other half of the document must not get a variable that silently does nothing.
    /// </summary>
    [Test]
    public void TheServerPathAliasIsHonouredAndTheCanonicalSpellingWins()
    {
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_SERVER_PATH", "/from-alias")).RoslynPath, Is.EqualTo("/from-alias"));
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_SERVER_VERSION", "1.2.3")).RoslynVersion, Is.EqualTo("1.2.3"));

        var both = Read(
            ("CLAUDE_ROSLYN_LSP_ROSLYN_PATH", "/canonical"),
            ("CLAUDE_ROSLYN_LSP_SERVER_PATH", "/alias"));

        Assert.That(both.RoslynPath, Is.EqualTo("/canonical"));
        Assert.That(both.RoslynPathVariable, Is.EqualTo("CLAUDE_ROSLYN_LSP_ROSLYN_PATH"));
    }

    // ------------------------------------------------------------------ booleans, enums, numbers

    [TestCaseSource(nameof(BooleanValues))]
    public void FlagsAcceptTheDocumentedSpellings(string value, bool expected)
    {
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_OFFLINE", value)).Offline, Is.EqualTo(expected));
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_DIAGNOSTICS", value)).Diagnostics, Is.EqualTo(expected));
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_FILE_WATCHER", value)).FileWatcher, Is.EqualTo(expected));
    }

    [TestCase("maybe")]
    [TestCase("2")]
    [TestCase("")]
    public void AnUnrecognisedFlagKeepsItsDefault(string value)
    {
        // Defaults differ, which is the point: an unparsable value must land on the documented
        // default for that flag, not on "false" for all of them.
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_OFFLINE", value)).Offline, Is.False);
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_DIAGNOSTICS", value)).Diagnostics, Is.True);
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_FILE_WATCHER", value)).FileWatcher, Is.True);
    }

    [TestCase("Debug", LogLevel.Debug)]
    [TestCase("debug", LogLevel.Debug)]
    [TestCase("TRACE", LogLevel.Trace)]
    [TestCase("Warning", LogLevel.Warning)]
    [TestCase("None", LogLevel.None)]
    public void TheLogLevelIsParsedCaseInsensitively(string value, LogLevel expected)
    {
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_LOG_LEVEL", value)).LogLevel, Is.EqualTo(expected));
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_ROSLYN_LOG_LEVEL", value)).RoslynLogLevel, Is.EqualTo(expected));
    }

    [TestCase("42")]
    [TestCase("-1")]
    [TestCase("Verbose")]
    [TestCase("")]
    public void AnUnrecognisedLogLevelFallsBack(string value)
    {
        // Enum.TryParse accepts bare numbers, so "42" would otherwise become a LogLevel of 42 and
        // silently suppress every log line.
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_LOG_LEVEL", value)).LogLevel, Is.EqualTo(LogLevel.Information));

        // Roslyn's level stays null rather than defaulting: the launcher owns that default, and an
        // unset variable must stay distinguishable from a deliberate choice.
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_ROSLYN_LOG_LEVEL", value)).RoslynLogLevel, Is.Null);
    }

    [TestCase("1", 1)]
    [TestCase("30", 30)]
    [TestCase("3600", 3600)]
    public void TheReadinessTimeoutAcceptsItsRange(string value, int expected)
    {
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS", value)).ReadyTimeoutSeconds, Is.EqualTo(expected));
    }

    [TestCase("0")]
    [TestCase("-5")]
    [TestCase("3601")]
    [TestCase("soon")]
    [TestCase("1e3")]
    public void AnOutOfRangeReadinessTimeoutFallsBackToTheDefault(string value)
    {
        // A zero-second gate would pass every request straight through during load, which is the
        // failure mode the gate exists to prevent - and it would do it silently.
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS", value)).ReadyTimeoutSeconds, Is.EqualTo(120));
    }

    // ---------------------------------------------------------------------------------------
    // The idle policy (D96, D100, D104, D106)
    // ---------------------------------------------------------------------------------------

    [TestCase("1.2", 1.2)]
    [TestCase("2.5", 2.5)]
    [TestCase("20", 20.0)]
    [TestCase("3e0", 3.0)]
    public void TheGrowthFactorIsADecimalReadWithTheInvariantCulture(string value, double expected)
    {
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_RECYCLE_GROWTH_FACTOR", value)).RecycleGrowthFactor, Is.EqualTo(expected));
    }

    /// <summary>
    /// A comma is not a decimal separator here, whatever the machine's culture says: read as one on a
    /// German box, <c>2,5</c> would be twenty-five and nothing would ever be recycled. And NaN has to
    /// fail the range check, which a pair of comparisons would let through.
    /// </summary>
    [TestCase("2,5")]
    [TestCase("1.1")]
    [TestCase("20.5")]
    [TestCase("NaN")]
    [TestCase("Infinity")]
    [TestCase("-3")]
    [TestCase("twice")]
    public void AnUnusableGrowthFactorFallsBackToTheDefault(string value)
    {
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_RECYCLE_GROWTH_FACTOR", value)).RecycleGrowthFactor, Is.EqualTo(2.0));
    }

    [TestCase("0", 0)]
    [TestCase("1", 1)]
    [TestCase("10080", 10080)]
    public void TheIdleWindowsAcceptZeroToAWeek(string value, int expected)
    {
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_IDLE_RECYCLE_MINUTES", value)).IdleRecycleMinutes, Is.EqualTo(expected));
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_IDLE_SHUTDOWN_MINUTES", value)).IdleShutdownMinutes, Is.EqualTo(expected));
    }

    [TestCase("-1")]
    [TestCase("10081")]
    [TestCase("soon")]
    public void AnOutOfRangeIdleWindowFallsBackToItsOwnDefault(string value)
    {
        // Different defaults, which is the point: the recycle is on by default and the shutdown off.
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_IDLE_RECYCLE_MINUTES", value)).IdleRecycleMinutes, Is.EqualTo(20));
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_IDLE_SHUTDOWN_MINUTES", value)).IdleShutdownMinutes, Is.EqualTo(0));
    }

    [TestCase("0", 0)]
    [TestCase("9", 9)]
    public void TheConserveLevelAcceptsZeroToNineAndSaysWhereItCameFrom(string value, int expected)
    {
        var options = Read(("CLAUDE_ROSLYN_LSP_GC_CONSERVE", value));

        Assert.That(options.GcConserveMemory, Is.EqualTo(expected));
        Assert.That(options.GcConserveMemoryVariable, Is.EqualTo("CLAUDE_ROSLYN_LSP_GC_CONSERVE"));
    }

    /// <summary>An unusable value is the default, and doctor must not credit the variable with it.</summary>
    [TestCase("10")]
    [TestCase("-1")]
    [TestCase("high")]
    public void AnUnusableConserveLevelFallsBackAndNamesNoSource(string value)
    {
        var options = Read(("CLAUDE_ROSLYN_LSP_GC_CONSERVE", value));

        Assert.That(options.GcConserveMemory, Is.EqualTo(ClaudeRoslynLspOptions.DefaultGcConserveMemory));
        Assert.That(options.GcConserveMemoryVariable, Is.Null);
    }

    [Test]
    public void TheConserveLevelGoesThroughThePluginLayerToo()
    {
        var options = Read(
            ("CLAUDE_PLUGIN_OPTION_CLAUDE_ROSLYN_LSP_GC_CONSERVE", "1"),
            ("CLAUDE_ROSLYN_LSP_GC_CONSERVE", "8"));

        Assert.That(options.GcConserveMemory, Is.EqualTo(1));
        Assert.That(options.GcConserveMemoryVariable, Is.EqualTo("CLAUDE_PLUGIN_OPTION_CLAUDE_ROSLYN_LSP_GC_CONSERVE"));
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
    [TestCase("")]
    [TestCase("   ")]
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

        Assert.That(options.Solution, Is.EqualTo("/ambient/App.slnx"));
        Assert.That(options.SolutionVariable, Is.EqualTo("CLAUDE_ROSLYN_LSP_SOLUTION"));
        Assert.That(options.RoslynPath, Is.EqualTo("/ambient/roslyn"));
        Assert.That(options.RoslynVersion, Is.EqualTo("9.9.9"));
        Assert.That(options.LogLevel, Is.EqualTo(LogLevel.Debug));
    }

    /// <summary>A filled-in option is the point of the prompt, so it wins over the environment.</summary>
    [Test]
    public void AConfiguredPluginOptionWinsOverTheAmbientVariable()
    {
        var options = Read(
            ("CLAUDE_PLUGIN_OPTION_CLAUDE_ROSLYN_LSP_SOLUTION", "/plugin/App.slnx"),
            ("CLAUDE_PLUGIN_OPTION_CLAUDE_ROSLYN_LSP_LOG_LEVEL", "Trace"),
            ("CLAUDE_ROSLYN_LSP_SOLUTION", "/ambient/App.slnx"),
            ("CLAUDE_ROSLYN_LSP_LOG_LEVEL", "Debug"));

        Assert.That(options.Solution, Is.EqualTo("/plugin/App.slnx"));
        Assert.That(options.SolutionVariable, Is.EqualTo("CLAUDE_PLUGIN_OPTION_CLAUDE_ROSLYN_LSP_SOLUTION"));
        Assert.That(options.LogLevel, Is.EqualTo(LogLevel.Trace));
    }

    /// <summary>
    /// The precedence applies to the parsed readers too, not only the string ones — otherwise a
    /// plugin option for a flag or a number would be a prompt whose answer is thrown away.
    /// </summary>
    [Test]
    public void ThePluginLayerCoversFlagsAndNumbersAsWell()
    {
        var options = Read(
            ("CLAUDE_PLUGIN_OPTION_CLAUDE_ROSLYN_LSP_DIAGNOSTICS", "0"),
            ("CLAUDE_PLUGIN_OPTION_CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS", "30"),
            ("CLAUDE_ROSLYN_LSP_DIAGNOSTICS", "1"),
            ("CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS", "90"));

        Assert.That(options.Diagnostics, Is.False);
        Assert.That(options.ReadyTimeoutSeconds, Is.EqualTo(30));
    }

    /// <summary>The plugin sets this one from <c>${CLAUDE_PLUGIN_DATA}</c>, unprefixed and by design.</summary>
    [Test]
    public void TheHomeDirectoryIsReadFromThePlainVariable()
    {
        Assert.That(Read(("CLAUDE_ROSLYN_LSP_HOME", "/plugin/data")).Home, Is.EqualTo("/plugin/data"));
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
