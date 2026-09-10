using System;
using System.Text;

namespace Hello.Core;

/// <summary>
/// The symbol every navigation test uses: called from <see cref="Caller"/> and from the app, so
/// <c>findReferences</c> has two answers and <c>incomingCalls</c> has two callers.
/// </summary>
/// <remarks>
/// The <c>using System.Text</c> above is deliberately unnecessary. Roslyn reports it as IDE0005 at
/// severity 4 (Hint), at position 0:0 for the whole using block (C16) — which is exactly the entry
/// the diagnostics bridge's severity floor and Unnecessary-tag rule exist to drop.
/// </remarks>
public sealed class Calculator
{
    /// <summary>The answer.</summary>
    public int Compute()
    {
        return 21 * 2;
    }

    /// <summary>
    /// A CA1822 candidate: an instance method that touches no instance state.
    /// </summary>
    /// <remarks>
    /// Reported at severity 3 (Information) with <c>Make static</c> and a Fix All entry beside it
    /// (C16). Making it static would remove the code-action site the tests use.
    /// </remarks>
    public int Describe()
    {
        return 7;
    }
}
