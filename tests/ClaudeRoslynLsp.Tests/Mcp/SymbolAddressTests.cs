using ClaudeRoslynLsp.Mcp;

using Xunit;

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
    [Theory]
    [InlineData("src/Core/Calculator.cs:7:15", "src/Core/Calculator.cs", 7, 15)]
    [InlineData(@"C:\src\Core\Calculator.cs:7:15", @"C:\src\Core\Calculator.cs", 7, 15)]
    [InlineData("C:/src/Core/Calculator.cs:12:3", "C:/src/Core/Calculator.cs", 12, 3)]
    [InlineData("Calculator.cs:1:1", "Calculator.cs", 1, 1)]
    public void PositionFormIsParsedFromTheRight(string value, string path, int line, int column)
    {
        var address = SymbolAddress.Parse(value);

        Assert.True(address.IsPosition);
        Assert.Equal(path, address.Path);
        Assert.Equal(line, address.Line);
        Assert.Equal(column, address.Column);
    }

    /// <summary>
    /// The one conversion this type exists to own: what a model writes is 1-based, what goes on the
    /// wire is 0-based.
    /// </summary>
    [Fact]
    public void PositionConvertsToZeroBasedForTheWire()
    {
        var position = SymbolAddress.Parse("a.cs:7:15").ToPosition();

        Assert.Equal(6, position.Line);
        Assert.Equal(14, position.Character);
        Assert.Equal(7, position.OneBasedLine);
        Assert.Equal(15, position.OneBasedColumn);
    }

    [Theory]
    [InlineData("Calculator")]
    [InlineData("Quartz.IScheduler.Start")]
    [InlineData("IScheduler.Start")]
    [InlineData("List`1")]
    [InlineData(@"C:\src\Calculator.cs")]
    [InlineData("a.cs:notaline:15")]
    [InlineData("a.cs:7")]
    public void EverythingElseIsAName(string value)
    {
        var address = SymbolAddress.Parse(value);

        Assert.False(address.IsPosition);
        Assert.Equal(value, address.Name);
    }

    [Theory]
    [InlineData("Calculator", "Calculator")]
    [InlineData("Quartz.IScheduler.Start", "Start")]
    [InlineData("List`1", "List")]
    [InlineData("Quartz.Collections.List`1", "List")]
    public void SimpleNameIsWhatWorkspaceSymbolCanSearchFor(string value, string expected) =>
        Assert.Equal(expected, SymbolAddress.Parse(value).SimpleName);

    /// <summary>
    /// Suffix matching on dot boundaries is what lets a caller write as much of a name as it takes to
    /// be unambiguous. <c>Scheduler.Start</c> must not match <c>Quartz.IScheduler.Start</c>: it is a
    /// different type, and matching it would rename the wrong member.
    /// </summary>
    [Theory]
    [InlineData("Start", "Quartz.IScheduler.Start", true)]
    [InlineData("IScheduler.Start", "Quartz.IScheduler.Start", true)]
    [InlineData("Quartz.IScheduler.Start", "Quartz.IScheduler.Start", true)]
    [InlineData("Scheduler.Start", "Quartz.IScheduler.Start", false)]
    [InlineData("IScheduler.Stop", "Quartz.IScheduler.Start", false)]
    public void NameMatchingIsBySegmentSuffix(string address, string candidate, bool expected) =>
        Assert.Equal(expected, SymbolAddress.Parse(address).MatchesName(candidate));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptySymbolIsRefusedWithAReason(string? value)
    {
        Assert.False(SymbolAddress.TryParse(value, out _, out var reason));
        Assert.Contains("no symbol", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Zero is not a line number anywhere a model reads one, so accepting it would silently address
    /// the line before the one that was meant.
    /// </summary>
    [Theory]
    [InlineData("a.cs:0:1")]
    [InlineData("a.cs:1:0")]
    public void ZeroBasedNumbersAreRefusedRatherThanShifted(string value)
    {
        Assert.False(SymbolAddress.TryParse(value, out _, out var reason));
        Assert.Contains("1-based", reason, StringComparison.Ordinal);
    }
}
