using Microsoft.Extensions.Logging;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Live;

/// <summary>
/// The adapter's own stderr log, redirected into a test's output.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled, because the package budget has no logging test helper and does not need one — this
/// is twenty lines. What it buys is the difference between a live failure that says "the workspace
/// was still LoadTimedOut after 120 s" and one that also says which directories were watched, when,
/// and what the acquisition chain decided. The first is a mystery; the second is a diagnosis, and a
/// live test that fails on somebody else's machine only ever gets one attempt at explaining itself.
/// </para>
/// <para>
/// Writes are guarded: xunit rejects output after a test has finished, and the adapter's background
/// tasks outlive the assertion that failed.
/// </para>
/// </remarks>
internal sealed class TestOutputLogger : ILogger
{
    private readonly ITestOutputHelper _output;
    private readonly LogLevel _minimum;

    /// <summary>Creates a logger over one test's output.</summary>
    /// <param name="output">Where the lines go.</param>
    /// <param name="minimum">The lowest level to write.</param>
    internal TestOutputLogger(ITestOutputHelper output, LogLevel minimum = LogLevel.Debug)
    {
        _output = output;
        _minimum = minimum;
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimum;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (!IsEnabled(logLevel))
        {
            return;
        }

        try
        {
            var message = formatter(state, exception);
            _output.WriteLine($"[{logLevel.ToString()[..4].ToLowerInvariant()} {eventId.Id}] {message}");

            if (exception is not null)
            {
                _output.WriteLine("        " + exception);
            }
        }
        catch (InvalidOperationException)
        {
            // The test has finished and xunit no longer accepts output; a background task of the
            // adapter's is still running. Losing the line is the right outcome.
        }
    }
}
