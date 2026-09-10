namespace Hello.Core;

/// <summary>The second implementation of <see cref="IShape"/>.</summary>
public sealed class Square : IShape
{
    private readonly double _side;

    /// <summary>Creates a square.</summary>
    /// <param name="side">The length of one side.</param>
    public Square(double side) => _side = side;

    /// <inheritdoc />
    public double Area() => _side * _side;
}

/// <summary>
/// A second type in a file named after the first one, so <c>Move type to Rectangle.cs</c> is offered.
/// </summary>
/// <remarks>
/// That refactoring is the one that resolves to a <c>create</c> file operation followed by edits into
/// a file that does not exist yet (C21), which is the shape the MCP half's edit applier has to
/// handle. Do not move it into its own file.
/// </remarks>
public sealed class Rectangle
{
    /// <summary>The width.</summary>
    public double Width { get; set; }

    /// <summary>The height.</summary>
    public double Height { get; set; }
}
