using ClaudeRoslynLsp.Cli;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Roslyn;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Roslyn;

/// <summary>
/// The two garbage-collector variables the Roslyn child is started with, and the one rule they share:
/// an explicit value already in the environment is never overwritten (D79, D104).
/// </summary>
/// <remarks>
/// Against the dictionary <see cref="System.Diagnostics.ProcessStartInfo.Environment"/> is, rather
/// than a started process, because the property is about what the launcher writes and the only way
/// to observe a real child's environment would be to make the child report it.
/// </remarks>
public class GarbageCollectorEnvironmentTests
{
    [Fact]
    public void TheConserveLevelIsSetByDefault()
    {
        var environment = Environment_();

        RoslynProcessLauncher.ApplyGarbageCollectorEnvironment(
            environment,
            RoslynGarbageCollector.Workstation,
            ClaudeRoslynLspOptions.DefaultGcConserveMemory);

        Assert.Equal("7", environment[RoslynProcessLauncher.GcConserveMemoryVariable]);
        Assert.Equal("0", environment["DOTNET_gcServer"]);
    }

    /// <summary>Zero means "no opinion", which is not the same thing as setting zero.</summary>
    [Fact]
    public void ZeroLeavesTheVariableUnset()
    {
        var environment = Environment_();

        RoslynProcessLauncher.ApplyGarbageCollectorEnvironment(environment, RoslynGarbageCollector.Workstation, 0);

        Assert.False(environment.ContainsKey(RoslynProcessLauncher.GcConserveMemoryVariable));
    }

    /// <summary>Somebody who set it meant it — the same rule <c>DOTNET_gcServer</c> has always had.</summary>
    [Fact]
    public void AnInheritedValueIsLeftAlone()
    {
        var environment = Environment_(
            (RoslynProcessLauncher.GcConserveMemoryVariable, "7"),
            ("DOTNET_gcServer", "1"));

        RoslynProcessLauncher.ApplyGarbageCollectorEnvironment(environment, RoslynGarbageCollector.Workstation, 5);

        Assert.Equal("7", environment[RoslynProcessLauncher.GcConserveMemoryVariable]);
        Assert.Equal("1", environment["DOTNET_gcServer"]);
    }

    [Fact]
    public void TheConfiguredLevelReachesTheLaunchRequest()
    {
        var options = ClaudeRoslynLspOptions.FromEnvironment(
            name => name == "CLAUDE_ROSLYN_LSP_GC_CONSERVE" ? "3" : null);

        Assert.Equal(3, options.GcConserveMemory);
        Assert.Equal("CLAUDE_ROSLYN_LSP_GC_CONSERVE", options.GcConserveMemoryVariable);
    }

    /// <summary><c>doctor</c> says the effective value and which of the three places it came from.</summary>
    [Fact]
    public void DoctorSaysWhereTheConserveLevelCameFrom()
    {
        var defaults = ClaudeRoslynLspOptions.FromEnvironment(static _ => null);

        var byDefault = DoctorCommand.DescribeGcConserve(defaults, static _ => null);
        Assert.Equal(7, byDefault.Level);
        Assert.Equal("default", byDefault.Source);

        var configured = DoctorCommand.DescribeGcConserve(defaults with { GcConserveMemory = 2, GcConserveMemoryVariable = "CLAUDE_ROSLYN_LSP_GC_CONSERVE" }, static _ => null);
        Assert.Equal(2, configured.Level);
        Assert.Equal("CLAUDE_ROSLYN_LSP_GC_CONSERVE", configured.Source);

        var off = DoctorCommand.DescribeGcConserve(defaults with { GcConserveMemory = 0 }, static _ => null);
        Assert.Null(off.Level);
        Assert.Contains("not set", off.Text, StringComparison.Ordinal);

        var inherited = DoctorCommand.DescribeGcConserve(
            defaults with { GcConserveMemory = 2 },
            static name => name == RoslynProcessLauncher.GcConserveMemoryVariable ? "9" : null);
        Assert.Equal(9, inherited.Level);
        Assert.Equal(RoslynProcessLauncher.GcConserveMemoryVariable, inherited.Source);
    }

    /// <summary>
    /// The runtime reads every setting under <c>COMPlus_</c> as well as <c>DOTNET_</c>, so an inherited
    /// <c>COMPlus_</c> value is the user's choice too — and writing the <c>DOTNET_</c> one beside it
    /// would silently overrule it, since that spelling wins (finding 10 of the 0.2.0 review).
    /// </summary>
    [Fact]
    public void TheOlderSpellingIsJustAsExplicit()
    {
        var environment = Environment_(("COMPlus_gcServer", "1"), ("COMPlus_GCConserveMemory", "0"));

        RoslynProcessLauncher.ApplyGarbageCollectorEnvironment(environment, RoslynGarbageCollector.Workstation, 7);

        Assert.False(environment.ContainsKey("DOTNET_gcServer"));
        Assert.False(environment.ContainsKey(RoslynProcessLauncher.GcConserveMemoryVariable));
        Assert.Equal("1", environment["COMPlus_gcServer"]);
        Assert.Equal("0", environment["COMPlus_GCConserveMemory"]);
    }

    /// <summary><c>doctor</c> names the spelling that is set, and the runtime's preference when both are.</summary>
    [Fact]
    public void DoctorNamesWhicheverSpellingIsSet()
    {
        var defaults = ClaudeRoslynLspOptions.FromEnvironment(static _ => null);

        var older = DoctorCommand.DescribeGcConserve(
            defaults,
            static name => name == "COMPlus_GCConserveMemory" ? "0" : null);

        Assert.Equal(0, older.Level);
        Assert.Equal("COMPlus_GCConserveMemory", older.Source);
        Assert.Contains("COMPlus_GCConserveMemory", older.Text, StringComparison.Ordinal);

        var both = DoctorCommand.DescribeGcConserve(
            defaults,
            static name => name switch
            {
                "COMPlus_GCConserveMemory" => "0",
                "DOTNET_GCConserveMemory" => "5",
                _ => null,
            });

        Assert.Equal(5, both.Level);
        Assert.Equal(RoslynProcessLauncher.GcConserveMemoryVariable, both.Source);

        var server = DoctorCommand.DescribeGarbageCollector(
            defaults,
            static name => name == "COMPlus_gcServer" ? "1" : null);

        Assert.StartsWith("server", server, StringComparison.Ordinal);
        Assert.Contains("COMPlus_gcServer=1", server, StringComparison.Ordinal);

        Assert.StartsWith("workstation", DoctorCommand.DescribeGarbageCollector(defaults, static _ => null), StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> Environment_(params (string Name, string Value)[] inherited)
    {
        // Case-insensitive, as ProcessStartInfo's is on Windows; the rule must hold either way.
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in inherited)
        {
            environment[name] = value;
        }

        return environment;
    }
}
