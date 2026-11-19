using ClaudeRoslynLsp.Cli;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Roslyn;

using NUnit.Framework;

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
    [Test]
    public void TheConserveLevelIsSetByDefault()
    {
        var environment = Environment_();

        RoslynProcessLauncher.ApplyGarbageCollectorEnvironment(
            environment,
            RoslynGarbageCollector.Workstation,
            ClaudeRoslynLspOptions.DefaultGcConserveMemory);

        Assert.That(environment[RoslynProcessLauncher.GcConserveMemoryVariable], Is.EqualTo("7"));
        Assert.That(environment["DOTNET_gcServer"], Is.EqualTo("0"));
    }

    /// <summary>Zero means "no opinion", which is not the same thing as setting zero.</summary>
    [Test]
    public void ZeroLeavesTheVariableUnset()
    {
        var environment = Environment_();

        RoslynProcessLauncher.ApplyGarbageCollectorEnvironment(environment, RoslynGarbageCollector.Workstation, 0);

        Assert.That(environment.ContainsKey(RoslynProcessLauncher.GcConserveMemoryVariable), Is.False);
    }

    /// <summary>Somebody who set it meant it — the same rule <c>DOTNET_gcServer</c> has always had.</summary>
    [Test]
    public void AnInheritedValueIsLeftAlone()
    {
        var environment = Environment_(
            (RoslynProcessLauncher.GcConserveMemoryVariable, "7"),
            ("DOTNET_gcServer", "1"));

        RoslynProcessLauncher.ApplyGarbageCollectorEnvironment(environment, RoslynGarbageCollector.Workstation, 5);

        Assert.That(environment[RoslynProcessLauncher.GcConserveMemoryVariable], Is.EqualTo("7"));
        Assert.That(environment["DOTNET_gcServer"], Is.EqualTo("1"));
    }

    [Test]
    public void TheConfiguredLevelReachesTheLaunchRequest()
    {
        var options = ClaudeRoslynLspOptions.FromEnvironment(
            name => name == "CLAUDE_ROSLYN_LSP_GC_CONSERVE" ? "3" : null);

        Assert.That(options.GcConserveMemory, Is.EqualTo(3));
        Assert.That(options.GcConserveMemoryVariable, Is.EqualTo("CLAUDE_ROSLYN_LSP_GC_CONSERVE"));
    }

    /// <summary><c>doctor</c> says the effective value and which of the three places it came from.</summary>
    [Test]
    public void DoctorSaysWhereTheConserveLevelCameFrom()
    {
        var defaults = ClaudeRoslynLspOptions.FromEnvironment(static _ => null);

        var byDefault = DoctorCommand.DescribeGcConserve(defaults, static _ => null);
        Assert.That(byDefault.Level, Is.EqualTo(7));
        Assert.That(byDefault.Source, Is.EqualTo("default"));

        var configured = DoctorCommand.DescribeGcConserve(defaults with { GcConserveMemory = 2, GcConserveMemoryVariable = "CLAUDE_ROSLYN_LSP_GC_CONSERVE" }, static _ => null);
        Assert.That(configured.Level, Is.EqualTo(2));
        Assert.That(configured.Source, Is.EqualTo("CLAUDE_ROSLYN_LSP_GC_CONSERVE"));

        var off = DoctorCommand.DescribeGcConserve(defaults with { GcConserveMemory = 0 }, static _ => null);
        Assert.That(off.Level, Is.Null);
        Assert.That(off.Text, Contains.Substring("not set").Using(StringComparison.Ordinal));

        var inherited = DoctorCommand.DescribeGcConserve(
            defaults with { GcConserveMemory = 2 },
            static name => name == RoslynProcessLauncher.GcConserveMemoryVariable ? "9" : null);
        Assert.That(inherited.Level, Is.EqualTo(9));
        Assert.That(inherited.Source, Is.EqualTo(RoslynProcessLauncher.GcConserveMemoryVariable));
    }

    /// <summary>
    /// The runtime reads every setting under <c>COMPlus_</c> as well as <c>DOTNET_</c>, so an inherited
    /// <c>COMPlus_</c> value is the user's choice too — and writing the <c>DOTNET_</c> one beside it
    /// would silently overrule it, since that spelling wins (finding 10 of the 0.2.0 review).
    /// </summary>
    [Test]
    public void TheOlderSpellingIsJustAsExplicit()
    {
        var environment = Environment_(("COMPlus_gcServer", "1"), ("COMPlus_GCConserveMemory", "0"));

        RoslynProcessLauncher.ApplyGarbageCollectorEnvironment(environment, RoslynGarbageCollector.Workstation, 7);

        Assert.That(environment.ContainsKey("DOTNET_gcServer"), Is.False);
        Assert.That(environment.ContainsKey(RoslynProcessLauncher.GcConserveMemoryVariable), Is.False);
        Assert.That(environment["COMPlus_gcServer"], Is.EqualTo("1"));
        Assert.That(environment["COMPlus_GCConserveMemory"], Is.EqualTo("0"));
    }

    /// <summary><c>doctor</c> names the spelling that is set, and the runtime's preference when both are.</summary>
    [Test]
    public void DoctorNamesWhicheverSpellingIsSet()
    {
        var defaults = ClaudeRoslynLspOptions.FromEnvironment(static _ => null);

        var older = DoctorCommand.DescribeGcConserve(
            defaults,
            static name => name == "COMPlus_GCConserveMemory" ? "0" : null);

        Assert.That(older.Level, Is.EqualTo(0));
        Assert.That(older.Source, Is.EqualTo("COMPlus_GCConserveMemory"));
        Assert.That(older.Text, Contains.Substring("COMPlus_GCConserveMemory").Using(StringComparison.Ordinal));

        var both = DoctorCommand.DescribeGcConserve(
            defaults,
            static name => name switch
            {
                "COMPlus_GCConserveMemory" => "0",
                "DOTNET_GCConserveMemory" => "5",
                _ => null,
            });

        Assert.That(both.Level, Is.EqualTo(5));
        Assert.That(both.Source, Is.EqualTo(RoslynProcessLauncher.GcConserveMemoryVariable));

        var server = DoctorCommand.DescribeGarbageCollector(
            defaults,
            static name => name == "COMPlus_gcServer" ? "1" : null);

        Assert.That(server, Does.StartWith("server").Using(StringComparison.Ordinal));
        Assert.That(server, Contains.Substring("COMPlus_gcServer=1").Using(StringComparison.Ordinal));

        Assert.That(
            DoctorCommand.DescribeGarbageCollector(defaults, static _ => null),
            Does.StartWith("workstation").Using(StringComparison.Ordinal));
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
