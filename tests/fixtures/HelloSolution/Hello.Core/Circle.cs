using System;

namespace Hello.Core;

/// <summary>The first implementation of <see cref="IShape"/>.</summary>
public sealed class Circle : IShape
{
    private readonly double _radius;

    /// <summary>Creates a circle.</summary>
    /// <param name="radius">The radius.</param>
    public Circle(double radius) => _radius = radius;

    /// <inheritdoc />
    public double Area() => Math.PI * _radius * _radius;
}
