using System;

using Hello.Core;

namespace Hello.App;

/// <summary>
/// The application, and the file that does not compile.
/// </summary>
/// <remarks>
/// <b>The <c>int x = "s";</c> below is deliberate and must stay.</b> It is CS0029 — "cannot
/// implicitly convert type 'string' to 'int'" — at severity 1, and it is what the live tests and the
/// <c>LiveTest</c> build target assert arrives as a <c>publishDiagnostics</c> after a
/// <c>didOpen</c>, and disappears within two seconds of a <c>didChange</c> that fixes it. A fixture
/// that compiled cleanly could not prove that the diagnostics bridge does anything at all.
/// </remarks>
public static class Program
{
    /// <summary>Runs it.</summary>
    public static void Main()
    {
        int x = "s";
        Console.WriteLine(x);

        var calculator = new Calculator();
        Console.WriteLine(calculator.Compute());

        IShape shape = new Circle(2.0);
        Console.WriteLine(shape.Area());
    }
}
