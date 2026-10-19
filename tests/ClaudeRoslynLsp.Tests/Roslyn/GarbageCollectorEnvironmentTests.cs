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

        var byDefault = DoctorCommand.DescribeGcConserve(defaults, inherited: null);
        Assert.Equal(7, byDefault.Level);
        Assert.Equal("default", byDefault.Source);

        var configured = DoctorCommand.DescribeGcConserve(defaults with { GcConserveMemory = 2, GcConserveMemoryVariable = "CLAUDE_ROSLYN_LSP_GC_CONSERVE" }, inherited: null);
        Assert.Equal(2, configured.Level);
        Assert.Equal("CLAUDE_ROSLYN_LSP_GC_CONSERVE", configured.Source);

        var off = DoctorCommand.DescribeGcConserve(defaults with { GcConserveMemory = 0 }, inherited: null);
        Assert.Null(off.Level);
        Assert.Contains("not set", off.Text, StringComparison.Ordinal);

        var inherited = DoctorCommand.DescribeGcConserve(defaults with { GcConserveMemory = 2 }, inherited: "9");
        Assert.Equal(9, inherited.Level);
        Assert.Equal(RoslynProcessLauncher.GcConserveMemoryVariable, inherited.Source);
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
