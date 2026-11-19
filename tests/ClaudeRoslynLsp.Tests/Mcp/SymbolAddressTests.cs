using ClaudeRoslynLsp.Mcp;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Mcp;

/// <summary>
/// The <c>symbol</c> argument's two forms (D63).
/// </summary>
/// <remarks>
/// The parsing rule is the interesting part: the position form is recognised from the <em>right</em>,
/// because a Windows path has a colon in it and splitting from the left would make the drive letter
/// the path.
/// </remarks>
public class SymbolAddressTests
{
    [TestCase("src/Core/Calculator.cs:7:15", "src/Core/Calculator.cs", 7, 15)]
    [TestCase(@"C:\src\Core\Calculator.cs:7:15", @"C:\src\Core\Calculator.cs", 7, 15)]
    [TestCase("C:/src/Core/Calculator.cs:12:3", "C:/src/Core/Calculator.cs", 12, 3)]
    [TestCase("Calculator.cs:1:1", "Calculator.cs", 1, 1)]
    public void PositionFormIsParsedFromTheRight(string value, string path, int line, int column)
    {
        var address = SymbolAddress.Parse(value);

        Assert.That(address.IsPosition, Is.True);
        Assert.That(address.Path, Is.EqualTo(path));
        Assert.That(address.Line, Is.EqualTo(line));
        Assert.That(address.Column, Is.EqualTo(column));
    }

    /// <summary>
    /// The one conversion this type exists to own: what a model writes is 1-based, what goes on the
    /// wire is 0-based.
    /// </summary>
    [Test]
    public void PositionConvertsToZeroBasedForTheWire()
    {
        var position = SymbolAddress.Parse("a.cs:7:15").ToPosition();

        Assert.That(position.Line, Is.EqualTo(6));
        Assert.That(position.Character, Is.EqualTo(14));
        Assert.That(position.OneBasedLine, Is.EqualTo(7));
        Assert.That(position.OneBasedColumn, Is.EqualTo(15));
    }

    [TestCase("Calculator")]
    [TestCase("Quartz.IScheduler.Start")]
    [TestCase("IScheduler.Start")]
    [TestCase("List`1")]
    [TestCase(@"C:\src\Calculator.cs")]
    [TestCase("a.cs:notaline:15")]
    [TestCase("a.cs:7")]
    public void EverythingElseIsAName(string value)
    {
        var address = SymbolAddress.Parse(value);

        Assert.That(address.IsPosition, Is.False);
        Assert.That(address.Name, Is.EqualTo(value));
    }

    [TestCase("Calculator", "Calculator")]
    [TestCase("Quartz.IScheduler.Start", "Start")]
    [TestCase("List`1", "List")]
    [TestCase("Quartz.Collections.List`1", "List")]
    public void SimpleNameIsWhatWorkspaceSymbolCanSearchFor(string value, string expected) =>
        Assert.That(SymbolAddress.Parse(value).SimpleName, Is.EqualTo(expected));

    /// <summary>
    /// Suffix matching on dot boundaries is what lets a caller write as much of a name as it takes to
    /// be unambiguous. <c>Scheduler.Start</c> must not match <c>Quartz.IScheduler.Start</c>: it is a
    /// different type, and matching it would rename the wrong member.
    /// </summary>
    [TestCase("Start", "Quartz.IScheduler.Start", true)]
    [TestCase("IScheduler.Start", "Quartz.IScheduler.Start", true)]
    [TestCase("Quartz.IScheduler.Start", "Quartz.IScheduler.Start", true)]
    [TestCase("Scheduler.Start", "Quartz.IScheduler.Start", false)]
    [TestCase("IScheduler.Stop", "Quartz.IScheduler.Start", false)]
    public void NameMatchingIsBySegmentSuffix(string address, string candidate, bool expected) =>
        Assert.That(SymbolAddress.Parse(address).MatchesName(candidate), Is.EqualTo(expected));

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void AnEmptySymbolIsRefusedWithAReason(string? value)
    {
        Assert.That(SymbolAddress.TryParse(value, out _, out var reason), Is.False);
        Assert.That(reason, Contains.Substring("no symbol").Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// Zero is not a line number anywhere a model reads one, so accepting it would silently address
    /// the line before the one that was meant.
    /// </summary>
    [TestCase("a.cs:0:1")]
    [TestCase("a.cs:1:0")]
    public void ZeroBasedNumbersAreRefusedRatherThanShifted(string value)
    {
        Assert.That(SymbolAddress.TryParse(value, out _, out var reason), Is.False);
        Assert.That(reason, Contains.Substring("1-based").Using(StringComparison.Ordinal));
    }
}
