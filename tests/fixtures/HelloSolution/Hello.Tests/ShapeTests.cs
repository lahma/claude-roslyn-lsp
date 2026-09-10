using Hello.Core;

using Xunit;

namespace Hello.Tests;

/// <summary>
/// The fixture's own tests. Nothing in this repository ever runs them.
/// </summary>
/// <remarks>
/// They exist so the project is a real test project rather than a library with the word in its
/// name: it references xunit, it is discovered as one by a solution scan, and it gives
/// <c>findReferences</c> on <see cref="Calculator.Compute"/> a third call site in a third project —
/// which is how a live test can tell "loaded the solution" apart from "loaded the two projects the
/// app happens to reference".
/// </remarks>
public class ShapeTests
{
    /// <summary>A circle's area.</summary>
    [Fact]
    public void CircleHasAnArea()
    {
        Assert.True(new Circle(1.0).Area() > 3.14);
    }

    /// <summary>A square's area.</summary>
    [Fact]
    public void SquareHasAnArea()
    {
        Assert.Equal(4.0, new Square(2.0).Area());
    }

    /// <summary>The third call site of <see cref="Calculator.Compute"/>.</summary>
    [Fact]
    public void TheCalculatorComputes()
    {
        Assert.Equal(42, new Calculator().Compute());
    }

    /// <summary>And the one that reaches it through <see cref="Caller"/>.</summary>
    [Fact]
    public void TheCallerCallsOnce()
    {
        Assert.Equal(42, new Caller().CallOnce());
    }
}
