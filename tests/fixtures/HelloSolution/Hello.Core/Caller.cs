namespace Hello.Core;

/// <summary>One of the two callers of <see cref="Calculator.Compute"/>.</summary>
public sealed class Caller
{
    /// <summary>Calls it once.</summary>
    public int CallOnce()
    {
        var calculator = new Calculator();
        return calculator.Compute();
    }
}
