using ClaudeRoslynLsp.Adapter;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// The glob dialect LSP 3.17 defines, against the patterns Roslyn actually registers.
/// </summary>
/// <remarks>
/// Worth testing at this level because every failure here is silent in the same direction: a
/// pattern that matches nothing produces a watcher that never fires, which presents as a workspace
/// that is quietly stale rather than as an error. The cases below are the ones observed on the wire
/// (C32), not a survey of the syntax.
/// </remarks>
public class GlobMatcherTests
{
    /// <summary>
    /// <c>**</c> spans zero segments. This is the one that matters most: it is Roslyn's commonest
    /// pattern and a project whose sources sit beside its <c>.csproj</c> depends on the zero case.
    /// </summary>
    [Theory]
    [InlineData("**/*.cs", "Program.cs")]
    [InlineData("**/*.cs", "Nested/Program.cs")]
    [InlineData("**/*.cs", "a/b/c/Program.cs")]
    public void DoubleStarSpansZeroOrMoreSegments(string pattern, string path) =>
        Assert.True(GlobMatcher.IsMatch(pattern, path));

    /// <summary>A single star never crosses a separator.</summary>
    [Fact]
    public void SingleStarDoesNotCrossASeparator()
    {
        Assert.True(GlobMatcher.IsMatch("*.cs", "Program.cs"));
        Assert.False(GlobMatcher.IsMatch("*.cs", "Nested/Program.cs"));
    }

    /// <summary>
    /// C32: Roslyn emits both <c>**/*{.cs,.razor,.cshtml}</c> and <c>**/*{.cs,.cshtml,.razor}</c>
    /// for the same intent, so alternation has to work whatever order the branches arrive in.
    /// </summary>
    [Theory]
    [InlineData("**/*{.cs,.razor,.cshtml}", "src/Program.cs")]
    [InlineData("**/*{.cs,.cshtml,.razor}", "src/Program.cs")]
    [InlineData("**/*{.cs,.razor,.cshtml}", "src/View.razor")]
    [InlineData("**/*{.cs,.cshtml,.razor}", "src/View.cshtml")]
    public void AlternationMatchesWhicheverOrderTheBranchesArriveIn(string pattern, string path) =>
        Assert.True(GlobMatcher.IsMatch(pattern, path));

    /// <summary>
    /// The star before an alternation has to stop in the one place that lets a branch line up, which
    /// is why the branch and the tail are matched as one pattern rather than in two steps.
    /// </summary>
    [Fact]
    public void AStarBeforeAnAlternationBacktracksIntoIt()
    {
        Assert.True(GlobMatcher.IsMatch("*{.cs,.razor}", "A.razor"));
        Assert.False(GlobMatcher.IsMatch("*{.cs,.razor}", "A.razorx"));
    }

    /// <summary>A question mark is exactly one character.</summary>
    [Fact]
    public void QuestionMarkIsOneCharacter()
    {
        Assert.True(GlobMatcher.IsMatch("?.cs", "A.cs"));
        Assert.False(GlobMatcher.IsMatch("?.cs", "AB.cs"));
        Assert.False(GlobMatcher.IsMatch("?.cs", ".cs"));
    }

    /// <summary>A bare file name matches only itself, which is how a project-file watcher works.</summary>
    [Fact]
    public void ABareNameMatchesOnlyItself()
    {
        Assert.True(GlobMatcher.IsMatch("Hello.Core.csproj", "Hello.Core.csproj"));
        Assert.False(GlobMatcher.IsMatch("Hello.Core.csproj", "Hello.App.csproj"));
        Assert.False(GlobMatcher.IsMatch("Hello.Core.csproj", "sub/Hello.Core.csproj"));
    }

    /// <summary>
    /// Matching is case-insensitive, because Windows and the default macOS filesystem are and the
    /// two mistakes do not cost the same: a spurious notification is one wasted message, a missed
    /// one is a stale workspace.
    /// </summary>
    [Fact]
    public void MatchingIsCaseInsensitive() =>
        Assert.True(GlobMatcher.IsMatch("**/*.cs", "Src/PROGRAM.CS"));

    /// <summary>Either separator is accepted, because a Windows event arrives with backslashes.</summary>
    [Fact]
    public void EitherSeparatorIsAccepted() =>
        Assert.True(GlobMatcher.IsMatch("**/*.cs", @"src\nested\Program.cs"));

    /// <summary>Nothing matches an empty pattern, and nothing throws on one.</summary>
    [Fact]
    public void AnEmptyPatternMatchesNothing() => Assert.False(GlobMatcher.IsMatch(string.Empty, "A.cs"));

    /// <summary>An unbalanced brace is a literal brace in a file name, not a broken alternation.</summary>
    [Fact]
    public void AnUnbalancedBraceIsALiteral()
    {
        Assert.True(GlobMatcher.IsMatch("{weird.cs", "{weird.cs"));
        Assert.False(GlobMatcher.IsMatch("{weird.cs", "weird.cs"));
    }

    /// <summary>Only a pattern that can descend makes its watcher recursive.</summary>
    [Fact]
    public void RecursionIsDecidedByTheDoubleStar()
    {
        Assert.True(GlobMatcher.IsRecursive("**/*.cs"));
        Assert.False(GlobMatcher.IsRecursive("Hello.Core.csproj"));
        Assert.False(GlobMatcher.IsRecursive("*.cs"));
    }

    /// <summary>The set form is an "any of", which is how a collapsed watcher is filtered.</summary>
    [Fact]
    public void AnyOfTheCollapsedPatternsIsEnough()
    {
        string[] patterns = ["Hello.Core.csproj", "**/*{.cs,.razor}"];

        Assert.True(GlobMatcher.IsMatchAny(patterns, "Hello.Core.csproj"));
        Assert.True(GlobMatcher.IsMatchAny(patterns, "sub/A.cs"));
        Assert.False(GlobMatcher.IsMatchAny(patterns, "sub/A.txt"));
    }
}
