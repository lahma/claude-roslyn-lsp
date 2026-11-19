using ClaudeRoslynLsp.Adapter;

using NUnit.Framework;

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
    [TestCase("**/*.cs", "Program.cs")]
    [TestCase("**/*.cs", "Nested/Program.cs")]
    [TestCase("**/*.cs", "a/b/c/Program.cs")]
    public void DoubleStarSpansZeroOrMoreSegments(string pattern, string path) =>
        Assert.That(GlobMatcher.IsMatch(pattern, path), Is.True);

    /// <summary>A single star never crosses a separator.</summary>
    [Test]
    public void SingleStarDoesNotCrossASeparator()
    {
        Assert.That(GlobMatcher.IsMatch("*.cs", "Program.cs"), Is.True);
        Assert.That(GlobMatcher.IsMatch("*.cs", "Nested/Program.cs"), Is.False);
    }

    /// <summary>
    /// C32: Roslyn emits both <c>**/*{.cs,.razor,.cshtml}</c> and <c>**/*{.cs,.cshtml,.razor}</c>
    /// for the same intent, so alternation has to work whatever order the branches arrive in.
    /// </summary>
    [TestCase("**/*{.cs,.razor,.cshtml}", "src/Program.cs")]
    [TestCase("**/*{.cs,.cshtml,.razor}", "src/Program.cs")]
    [TestCase("**/*{.cs,.razor,.cshtml}", "src/View.razor")]
    [TestCase("**/*{.cs,.cshtml,.razor}", "src/View.cshtml")]
    public void AlternationMatchesWhicheverOrderTheBranchesArriveIn(string pattern, string path) =>
        Assert.That(GlobMatcher.IsMatch(pattern, path), Is.True);

    /// <summary>
    /// The star before an alternation has to stop in the one place that lets a branch line up, which
    /// is why the branch and the tail are matched as one pattern rather than in two steps.
    /// </summary>
    [Test]
    public void AStarBeforeAnAlternationBacktracksIntoIt()
    {
        Assert.That(GlobMatcher.IsMatch("*{.cs,.razor}", "A.razor"), Is.True);
        Assert.That(GlobMatcher.IsMatch("*{.cs,.razor}", "A.razorx"), Is.False);
    }

    /// <summary>A question mark is exactly one character.</summary>
    [Test]
    public void QuestionMarkIsOneCharacter()
    {
        Assert.That(GlobMatcher.IsMatch("?.cs", "A.cs"), Is.True);
        Assert.That(GlobMatcher.IsMatch("?.cs", "AB.cs"), Is.False);
        Assert.That(GlobMatcher.IsMatch("?.cs", ".cs"), Is.False);
    }

    /// <summary>A bare file name matches only itself, which is how a project-file watcher works.</summary>
    [Test]
    public void ABareNameMatchesOnlyItself()
    {
        Assert.That(GlobMatcher.IsMatch("Hello.Core.csproj", "Hello.Core.csproj"), Is.True);
        Assert.That(GlobMatcher.IsMatch("Hello.Core.csproj", "Hello.App.csproj"), Is.False);
        Assert.That(GlobMatcher.IsMatch("Hello.Core.csproj", "sub/Hello.Core.csproj"), Is.False);
    }

    /// <summary>
    /// Matching is case-insensitive, because Windows and the default macOS filesystem are and the
    /// two mistakes do not cost the same: a spurious notification is one wasted message, a missed
    /// one is a stale workspace.
    /// </summary>
    [Test]
    public void MatchingIsCaseInsensitive() =>
        Assert.That(GlobMatcher.IsMatch("**/*.cs", "Src/PROGRAM.CS"), Is.True);

    /// <summary>Either separator is accepted, because a Windows event arrives with backslashes.</summary>
    [Test]
    public void EitherSeparatorIsAccepted() =>
        Assert.That(GlobMatcher.IsMatch("**/*.cs", @"src\nested\Program.cs"), Is.True);

    /// <summary>Nothing matches an empty pattern, and nothing throws on one.</summary>
    [Test]
    public void AnEmptyPatternMatchesNothing() => Assert.That(GlobMatcher.IsMatch(string.Empty, "A.cs"), Is.False);

    /// <summary>An unbalanced brace is a literal brace in a file name, not a broken alternation.</summary>
    [Test]
    public void AnUnbalancedBraceIsALiteral()
    {
        Assert.That(GlobMatcher.IsMatch("{weird.cs", "{weird.cs"), Is.True);
        Assert.That(GlobMatcher.IsMatch("{weird.cs", "weird.cs"), Is.False);
    }

    /// <summary>Only a pattern that can descend makes its watcher recursive.</summary>
    [Test]
    public void RecursionIsDecidedByTheDoubleStar()
    {
        Assert.That(GlobMatcher.IsRecursive("**/*.cs"), Is.True);
        Assert.That(GlobMatcher.IsRecursive("Hello.Core.csproj"), Is.False);
        Assert.That(GlobMatcher.IsRecursive("*.cs"), Is.False);
    }

    /// <summary>The set form is an "any of", which is how a collapsed watcher is filtered.</summary>
    [Test]
    public void AnyOfTheCollapsedPatternsIsEnough()
    {
        string[] patterns = ["Hello.Core.csproj", "**/*{.cs,.razor}"];

        Assert.That(GlobMatcher.IsMatchAny(patterns, "Hello.Core.csproj"), Is.True);
        Assert.That(GlobMatcher.IsMatchAny(patterns, "sub/A.cs"), Is.True);
        Assert.That(GlobMatcher.IsMatchAny(patterns, "sub/A.txt"), Is.False);
    }
}
