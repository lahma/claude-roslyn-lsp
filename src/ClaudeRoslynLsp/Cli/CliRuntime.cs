using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Cli;

/// <summary>
/// The process's three standard streams, and the stderr logger every mode shares. This is the
/// <b>only</b> file in the product that touches <see cref="Console"/> at all.
/// </summary>
/// <remarks>
/// <para>
/// Both server modes speak a JSON-RPC protocol over stdout: LSP with <c>Content-Length</c> framing,
/// MCP with newline-delimited JSON. One stray write corrupts the stream and the client sees a server
/// that has silently stopped working, so the rule is not "be careful with stdout" but "there is one
/// door to it". <c>NoStdoutWritesTest</c> enforces the directory; concentrating the actual
/// <see cref="Console"/> calls in this one file is what makes the rule reviewable rather than merely
/// asserted.
/// </para>
/// <para>
/// The protocol streams are the <em>raw</em> <see cref="Stream"/>s, never
/// <see cref="Console.In"/>/<see cref="Console.Out"/>. A <see cref="TextWriter"/> would apply an
/// encoding and, on Windows, translate <c>\n</c> to <c>\r\n</c> — which would corrupt a
/// <c>Content-Length</c> body and put the byte count the header promised out of step with the bytes
/// that arrive. They are opened once and cached because opening the same handle twice hands out two
/// independently buffered views of one stream.
/// </para>
/// </remarks>
internal static class CliRuntime
{
    // Lazy rather than a static field initialiser: a CLI mode that only prints its version must not
    // pay for opening the protocol handles, and a test host that never runs a server must not have
    // them opened underneath it at type-load time.
    private static readonly Lazy<Stream> LazyInput = new(Console.OpenStandardInput, isThreadSafe: true);
    private static readonly Lazy<Stream> LazyOutput = new(Console.OpenStandardOutput, isThreadSafe: true);

    /// <summary>The raw stdin stream — the inbound half of whichever protocol is running.</summary>
    internal static Stream StandardInput => LazyInput.Value;

    /// <summary>The raw stdout stream — the outbound half, and nothing else's.</summary>
    internal static Stream StandardOutput => LazyOutput.Value;

    /// <summary>Writes one line of command output to stdout. Only a non-protocol mode may call this.</summary>
    /// <param name="text">The line to write.</param>
    internal static void WriteOut(string text) => Console.Out.WriteLine(text);

    /// <summary>Writes one line to stderr, which is safe in every mode.</summary>
    /// <param name="text">The line to write.</param>
    internal static void WriteError(string text) => Console.Error.WriteLine(text);

    /// <summary>Writes a blank line to stderr.</summary>
    internal static void WriteErrorLine() => Console.Error.WriteLine();

    /// <summary>Builds the logger factory every mode shares. Every record, at every level, goes to stderr.</summary>
    /// <param name="minimumLevel">The configured minimum level.</param>
    internal static ILoggerFactory CreateLoggerFactory(LogLevel minimumLevel) =>
        LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(minimumLevel);

            // The one line that keeps the protocol stream clean: LogToStandardErrorThreshold at
            // Trace sends everything to stderr. stdout belongs to JSON-RPC alone.
            logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        });
}
