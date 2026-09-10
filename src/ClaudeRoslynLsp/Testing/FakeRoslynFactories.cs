using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Cli;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Testing;

/// <summary>
/// A pair of in-memory streams wired back to back, so two LSP peers can talk inside one process.
/// </summary>
/// <remarks>
/// Built on <see cref="Pipe"/> rather than on <see cref="MemoryStream"/> because the exchange is a
/// genuine conversation: each side blocks reading until the other writes, and completing a writer is
/// what produces the clean end of stream that both peers treat as "the connection closed". A pair of
/// memory streams cannot express either.
/// </remarks>
internal static class DuplexStreamPair
{
    /// <summary>Creates the two halves of one connection.</summary>
    /// <returns>
    /// The streams as each side sees them: <c>Input</c> is what that side reads, <c>Output</c> is
    /// what it writes.
    /// </returns>
    internal static (DuplexEnd Left, DuplexEnd Right) Create()
    {
        var leftToRight = new Pipe();
        var rightToLeft = new Pipe();

        return (
            new DuplexEnd(rightToLeft.Reader.AsStream(), leftToRight.Writer.AsStream(), leftToRight.Writer),
            new DuplexEnd(leftToRight.Reader.AsStream(), rightToLeft.Writer.AsStream(), rightToLeft.Writer));
    }
}

/// <summary>One end of a <see cref="DuplexStreamPair"/>.</summary>
/// <param name="Input">What this end reads.</param>
/// <param name="Output">What this end writes.</param>
/// <param name="Writer">The underlying writer, so this end can signal that it is done.</param>
internal sealed record DuplexEnd(Stream Input, Stream Output, PipeWriter Writer)
{
    /// <summary>Signals a clean end of stream to the other end.</summary>
    internal void CompleteOutput() => Writer.Complete();
}

/// <summary>
/// The backend behind <c>lsp --smoke</c>: a <see cref="FakeRoslynServer"/> in this same process.
/// </summary>
/// <remarks>
/// What this proves is the mediation — framing, the readiness gate, the id map, shutdown — against a
/// published Native AOT binary, on the architecture that binary was compiled for, with no download.
/// What it deliberately does not prove is process plumbing; that is
/// <see cref="ChildProcessFakeRoslynFactory"/>'s job.
/// </remarks>
internal sealed class InProcessFakeRoslynFactory : IRoslynConnectionFactory
{
    private readonly ILogger _logger;
    private readonly FakeRoslynScript _script;

    /// <summary>Creates the factory over the standard 5.12 startup script.</summary>
    /// <param name="logger">The stderr log.</param>
    /// <param name="script">The script to run, or null for the standard one.</param>
    internal InProcessFakeRoslynFactory(ILogger logger, FakeRoslynScript? script = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _script = script ?? FakeRoslynScript.Roslyn512Startup(projectLoadDelay: TimeSpan.FromMilliseconds(50));
    }

    /// <summary>The server the connection is served by, once one has been made.</summary>
    internal FakeRoslynServer? Server { get; private set; }

    /// <inheritdoc />
    public string Description => "the scripted fake Roslyn backend, in this process";

    /// <inheritdoc />
    public Task<RoslynConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var (adapterEnd, serverEnd) = DuplexStreamPair.Create();
        var server = new FakeRoslynServer(_script, _logger);
        Server = server;

        var running = Task.Run(
            async () =>
            {
                try
                {
                    await server.RunAsync(serverEnd.Input, serverEnd.Output, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    // The adapter's reader sees end of stream, exactly as it would when a real
                    // backend process died.
                    serverEnd.CompleteOutput();
                }
            },
            CancellationToken.None);

        return Task.FromResult(new RoslynConnection(
            adapterEnd.Input,
            adapterEnd.Output,
            running,
            Description,
            () =>
            {
                adapterEnd.CompleteOutput();
                return ValueTask.CompletedTask;
            }));
    }
}

/// <summary>
/// The backend behind <c>CLAUDE_ROSLYN_LSP_FAKE_BACKEND=child</c>: this same binary, launched again
/// with the hidden <c>fake-roslyn</c> verb, spoken to over its stdio.
/// </summary>
/// <remarks>
/// <para>
/// This is the leg that proves the parts an in-process fake cannot: that a child can be started on
/// this RID at all, that its three handles are wired the right way round, that redirected stdio
/// survives Native AOT compilation, and that nothing the child does at startup writes a stray byte
/// onto what is now a protocol channel. That last one is not hypothetical — the real server writes a
/// 646-byte banner to stdout in one of its two transport modes (C7), and a launcher that assumed
/// otherwise would be broken in a way no unit test could show.
/// </para>
/// <para>
/// It is emphatically not WP3's launcher. There is no acquisition, no .NET host resolution, no pipe
/// transport and no process guard here; it spawns one known executable — itself — and that is the
/// whole of it. WP4 replaces this with a factory over WP3's real launcher, and this stays as the
/// smoke path.
/// </para>
/// </remarks>
internal sealed partial class ChildProcessFakeRoslynFactory : IRoslynConnectionFactory
{
    private readonly ILogger _logger;

    /// <summary>Creates the factory.</summary>
    /// <param name="logger">The stderr log.</param>
    internal ChildProcessFakeRoslynFactory(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public string Description => "the scripted fake Roslyn backend, in a child process";

    /// <inheritdoc />
    public Task<RoslynConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException(
                             "the running executable's path is not available, so it cannot launch itself");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        startInfo.ArgumentList.Add(CliVerbs.FakeRoslyn);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.Exited += (_, _) => exited.TrySetResult();

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"'{executable} {CliVerbs.FakeRoslyn}' did not start");
        }

        Log.Started(_logger, executable, process.Id);

        // Drained, not ignored: a child whose stderr buffer fills stops writing and then stops
        // running, which would present as a backend that mysteriously stopped answering.
        _ = PumpStandardErrorAsync(process, _logger);

        return Task.FromResult(new RoslynConnection(
            process.StandardOutput.BaseStream,
            process.StandardInput.BaseStream,
            exited.Task,
            Description,
            () => DisposeAsync(process, _logger)));
    }

    /// <summary>Copies the child's stderr into this process's log, line by line.</summary>
    private static async Task PumpStandardErrorAsync(Process process, ILogger logger)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > 0)
                {
                    Log.ChildStandardError(logger, line);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The child is gone. Its exit is reported through RoslynConnection.Exited.
        }
    }

    /// <summary>Closes the child's input and, if it will not go, kills it.</summary>
    private static async ValueTask DisposeAsync(Process process, ILogger logger)
    {
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Killing(logger, process.Id);
            TryKill(process);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>Kills the child, tolerating it having exited in the meantime.</summary>
    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // It exited between the check and the kill, which is the outcome that was wanted.
        }
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1200,
            Level = LogLevel.Information,
            Message = "Started the fake Roslyn backend as a child process: {Executable} (pid {ProcessId}).")]
        internal static partial void Started(ILogger logger, string executable, int processId);

        [LoggerMessage(EventId = 1201, Level = LogLevel.Debug, Message = "[fake-roslyn stderr] {Line}")]
        internal static partial void ChildStandardError(ILogger logger, string line);

        [LoggerMessage(
            EventId = 1202,
            Level = LogLevel.Warning,
            Message = "The fake Roslyn child (pid {ProcessId}) did not exit when its input was closed; " +
                      "killing it.")]
        internal static partial void Killing(ILogger logger, int processId);
    }
}
