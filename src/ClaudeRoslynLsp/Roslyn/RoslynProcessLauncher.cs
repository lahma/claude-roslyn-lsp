using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;

using ClaudeRoslynLsp.Configuration;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Roslyn;

/// <summary>How the adapter and the Roslyn child exchange framed LSP bytes.</summary>
internal enum RoslynTransport
{
    /// <summary>A named pipe the adapter creates and Roslyn connects to. The default.</summary>
    Pipe,

    /// <summary>The child's own standard input and output.</summary>
    Stdio,
}

/// <summary>Everything one launch needs. A record so a relaunch (WP4) is one <c>with</c> away.</summary>
internal sealed record RoslynLaunchRequest
{
    /// <summary>The resolved server — where it is, and what its command line accepts.</summary>
    internal required RoslynResolution Server { get; init; }

    /// <summary>The <c>dotnet</c> host that will run it, unless the server is a native override.</summary>
    internal required DotnetHostResult Host { get; init; }

    /// <summary>The adapter's directories, for the child's log directory.</summary>
    internal required AdapterPaths Paths { get; init; }

    /// <summary>Which transport to use.</summary>
    internal RoslynTransport Transport { get; init; } = RoslynTransport.Pipe;

    /// <summary>The level handed to Roslyn's own logger.</summary>
    internal LogLevel RoslynLogLevel { get; init; } = DefaultRoslynLogLevel;

    /// <summary>Extra arguments from <c>CLAUDE_ROSLYN_LSP_ROSLYN_ARGS</c>, inserted before the standard flags.</summary>
    internal IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>The process id Roslyn should watch, or <see langword="null"/> to leave it unset.</summary>
    internal int? ClientProcessId { get; init; } = Environment.ProcessId;

    /// <summary>The child's working directory; the adapter's own if unset.</summary>
    internal string? WorkingDirectory { get; init; }

    /// <summary>
    /// The default level for the child, which is deliberately quieter than the adapter's own.
    /// </summary>
    /// <remarks>
    /// Roslyn's <c>Information</c> is 21-22 <c>window/logMessage</c> lines during startup alone (C6),
    /// every one of which would arrive on this adapter's stderr with a <c>[roslyn]</c> prefix and drown
    /// the adapter's own record of what it did. <c>Warning</c> keeps the failures and drops the
    /// narration; <c>CLAUDE_ROSLYN_LSP_ROSLYN_LOG_LEVEL</c> turns it back up when somebody is actually
    /// debugging Roslyn rather than debugging this.
    /// </remarks>
    internal const LogLevel DefaultRoslynLogLevel = LogLevel.Warning;

    /// <summary>Builds a request from the parsed options.</summary>
    /// <param name="options">The configuration.</param>
    /// <param name="server">The resolved server.</param>
    /// <param name="host">The resolved <c>dotnet</c> host.</param>
    /// <param name="paths">The adapter's directories.</param>
    /// <param name="logger">Where an unrecognised transport is reported.</param>
    internal static RoslynLaunchRequest FromOptions(
        ClaudeRoslynLspOptions options,
        RoslynResolution server,
        DotnetHostResult host,
        AdapterPaths paths,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new RoslynLaunchRequest
        {
            Server = server,
            Host = host,
            Paths = paths,
            Transport = RoslynProcessLauncher.ParseTransport(options.Transport, logger),
            RoslynLogLevel = options.RoslynLogLevel ?? DefaultRoslynLogLevel,
            ExtraArguments = RoslynProcessLauncher.SplitArguments(options.RoslynArguments),
        };
    }
}

/// <summary>A started, connected Roslyn child: the byte channel, the process, and its ending.</summary>
internal sealed class RoslynConnection : IAsyncDisposable
{
    private readonly CancellationTokenSource _pumps = new();
    private readonly IDisposable? _transportResource;
    private readonly ILogger _logger;
    private int _disposed;

    internal RoslynConnection(
        Stream stream,
        Process process,
        Task<int> exited,
        RoslynTransport transport,
        string? pipeName,
        IDisposable? transportResource,
        ILogger logger)
    {
        Stream = stream;
        Process = process;
        Exited = exited;
        Transport = transport;
        PipeName = pipeName;
        _transportResource = transportResource;
        _logger = logger;
    }

    /// <summary>The duplex byte channel carrying framed LSP in both directions.</summary>
    internal Stream Stream { get; }

    /// <summary>The child process.</summary>
    internal Process Process { get; }

    /// <summary>Completes with the child's exit code when it ends, however it ends.</summary>
    internal Task<int> Exited { get; }

    /// <summary>Which transport this connection used.</summary>
    internal RoslynTransport Transport { get; }

    /// <summary>The pipe name, when <see cref="Transport"/> is <see cref="RoslynTransport.Pipe"/>.</summary>
    internal string? PipeName { get; }

    /// <summary>The token that stops the stdout/stderr pumps. Cancelled by <see cref="DisposeAsync"/>.</summary>
    internal CancellationToken PumpToken => _pumps.Token;

    /// <summary>
    /// Ends the session: stops the pumps, closes the channel, and kills the child if it is still
    /// running.
    /// </summary>
    /// <remarks>
    /// A hard kill, not a request. By the time this runs the caller has already had its chance to send
    /// <c>shutdown</c>/<c>exit</c>, and a Roslyn that did not act on those is a Roslyn that is not
    /// reading its channel — waiting politely for it would mean holding a quarter of a gigabyte open
    /// for a process that has stopped participating. The whole tree goes, because the child spawns
    /// build hosts of its own.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _pumps.CancelAsync().ConfigureAwait(false);

        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or NotSupportedException)
        {
        }

        Stream.Dispose();
        _transportResource?.Dispose();
        Process.Dispose();
        _pumps.Dispose();

        _ = _logger;
    }
}

/// <summary>Starts the Roslyn child and hands back a connected channel.</summary>
/// <remarks>
/// An interface so WP2's scripted fake and WP4's crash-recovery loop can both stand where the real
/// launcher stands, without either of them having to start a process.
/// </remarks>
internal interface IRoslynLauncher
{
    /// <summary>Starts Roslyn and waits until it is connected.</summary>
    /// <param name="request">What to start and how.</param>
    /// <param name="cancellationToken">Cancels the start and the wait for the connection.</param>
    Task<RoslynConnection> LaunchAsync(RoslynLaunchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The real launcher: a named pipe (or the child's stdio), a process, two drained text streams and a
/// job object.
/// </summary>
/// <remarks>
/// <para>
/// <b>D37 — a named pipe by default, stdio as the documented fallback.</b> Roslyn accepts both and the
/// pipe is better for one specific reason: with <c>--stdio</c> the protocol shares the child's stdout
/// with anything else that writes there, and this adapter has no control over what a future Roslyn,
/// an analyzer, or a build host decides to print. On the pipe, the protocol is a channel nothing else
/// has a handle to — and indeed the pinned build writes a 646-byte banner to stdout in pipe mode (C7),
/// which in stdio mode would have been protocol corruption. <c>CLAUDE_ROSLYN_LSP_TRANSPORT=stdio</c>
/// exists for the environments where a named pipe is not available at all.
/// </para>
/// <para>
/// <b>The adapter creates the pipe and Roslyn connects to it</b> (C8), which is the opposite of the
/// arrangement the flag's name suggests. The server stream must therefore exist before the process is
/// started, or the child's connect attempt races a pipe that is not there yet.
/// </para>
/// <para>
/// <b>D38 — all three of the child's standard streams are redirected, in both transports.</b> stdout
/// and stderr are pumped into the logger (D36). stdin is redirected even though pipe-mode Roslyn never
/// reads it, because an inherited stdin is <em>this process's</em> stdin, which in <c>lsp</c> mode is
/// the client's half of the protocol: a child that read one byte from it would silently steal it.
/// </para>
/// </remarks>
internal sealed partial class RoslynProcessLauncher : IRoslynLauncher
{
    /// <summary>How long Roslyn is given to connect to the pipe before the launch is failed.</summary>
    /// <remarks>
    /// Connect plus <c>initialize</c> was 0.6-0.7 s on the spike machine (C8). Thirty seconds is not a
    /// tuning value, it is the point at which "slow" has become "never" — a cold NTFS scan of a
    /// 140 MB payload behind a corporate scanner is the reason it is not three.
    /// </remarks>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger;
    private readonly ChildProcessGuard _guard;

    /// <summary>Creates a launcher.</summary>
    /// <param name="logger">Where the child's output and this launcher's own records go.</param>
    /// <param name="guard">
    /// The lifetime guard to enrol the child in. Shared across launches, because it is the handle's
    /// lifetime that kills the children, not the launcher's.
    /// </param>
    internal RoslynProcessLauncher(ILogger logger, ChildProcessGuard guard)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(guard);

        _logger = logger;
        _guard = guard;
    }

    /// <summary>
    /// Parses <c>CLAUDE_ROSLYN_LSP_TRANSPORT</c>. Unrecognised values warn and fall back to the
    /// default, per D10 — a typo must not stop the server from starting.
    /// </summary>
    /// <param name="value">The configured value, or <see langword="null"/>.</param>
    /// <param name="logger">Where an unrecognised value is reported.</param>
    internal static RoslynTransport ParseTransport(string? value, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        switch (value?.ToUpperInvariant())
        {
            case null or "PIPE":
                return RoslynTransport.Pipe;

            case "STDIO":
                return RoslynTransport.Stdio;

            default:
                Log.UnknownTransport(logger, value!);
                return RoslynTransport.Pipe;
        }
    }

    /// <summary>
    /// Splits <c>CLAUDE_ROSLYN_LSP_ROSLYN_ARGS</c> on whitespace.
    /// </summary>
    /// <remarks>
    /// Deliberately not a shell parser: no quoting, no escapes. The variable exists so the smoke test
    /// can add one word (<c>fake-roslyn</c>), and a half-implemented quoting dialect is a worse
    /// contract than an honestly simple one.
    /// </remarks>
    /// <param name="value">The configured value, or <see langword="null"/>.</param>
    internal static IReadOnlyList<string> SplitArguments(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// A pipe name unique to this process and this launch.
    /// </summary>
    /// <remarks>
    /// The process id alone is not enough: WP4 relaunches Roslyn after a crash, and a name reused
    /// while the previous server is still shutting down connects the new client to the old server. The
    /// random half is what makes each launch its own channel.
    /// </remarks>
    internal static string CreatePipeName() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{ServerVersion.Name}-{Environment.ProcessId}-{Random.Shared.Next():x8}");

    /// <summary>
    /// Builds the child's command line: the program, then the extra arguments, then the standard
    /// flags.
    /// </summary>
    /// <param name="request">The launch request.</param>
    /// <param name="pipeName">The pipe name, or <see langword="null"/> for stdio.</param>
    internal static List<string> BuildArguments(RoslynLaunchRequest request, string? pipeName)
    {
        ArgumentNullException.ThrowIfNull(request);

        var arguments = new List<string>();

        if (request.Server.LaunchKind == RoslynLaunchKind.Managed)
        {
            arguments.Add(request.Server.LaunchTarget!);
        }

        arguments.AddRange(request.ExtraArguments);

        if (pipeName is null)
        {
            arguments.Add("--stdio");
        }
        else
        {
            arguments.Add("--pipe");
            arguments.Add(pipeName);
        }

        arguments.Add("--logLevel");
        arguments.Add(request.RoslynLogLevel.ToString());

        arguments.Add("--extensionLogDirectory");
        arguments.Add(request.Paths.RoslynLogDirectory);

        // Roslyn's telemetry is opt-in through this flag and the adapter never opts in: nothing about
        // an agent's use of a language server is this repository's to report to anyone.
        arguments.Add("--telemetryLevel");
        arguments.Add("off");

        if (request.Server.Features.SupportsClientProcessId && request.ClientProcessId is { } pid)
        {
            arguments.Add("--clientProcessId");
            arguments.Add(pid.ToString(CultureInfo.InvariantCulture));
        }

        return arguments;
    }

    /// <inheritdoc />
    public async Task<RoslynConnection> LaunchAsync(
        RoslynLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Server.LaunchTarget is null)
        {
            throw new RoslynAcquisitionException(
                "No Roslyn server has been resolved; the launcher has nothing to start. Run `claude-roslyn-lsp doctor`.");
        }

        if (request.Server.LaunchKind == RoslynLaunchKind.Managed && !request.Host.IsUsable)
        {
            throw new RoslynAcquisitionException(
                request.Host.Failure
                ?? "No usable .NET 10 host was found, and the Roslyn server is a managed assembly that needs one.");
        }

        Directory.CreateDirectory(request.Paths.RoslynLogDirectory);

        return request.Transport == RoslynTransport.Pipe
            ? await LaunchOverPipeAsync(request, cancellationToken).ConfigureAwait(false)
            : LaunchOverStdio(request);
    }

    private async Task<RoslynConnection> LaunchOverPipeAsync(
        RoslynLaunchRequest request,
        CancellationToken cancellationToken)
    {
        var pipeName = CreatePipeName();

        // Created before the process starts: Roslyn is the client here (C8), so a pipe that does not
        // exist yet is a child that fails to connect rather than one that waits.
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        Process? process = null;

        try
        {
            process = Start(request, pipeName);

            var exited = WatchExitAsync(process);

            PumpChildStreams(process, RoslynTransport.Pipe, out var pumps);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);

            var connect = pipe.WaitForConnectionAsync(timeout.Token);
            var completed = await Task.WhenAny(connect, exited).ConfigureAwait(false);

            if (completed == exited)
            {
                throw new RoslynAcquisitionException(
                    $"The Roslyn server exited with code {await exited.ConfigureAwait(false)} before it connected to " +
                    $"the pipe. Its output was logged with the '{RoslynStderrPump.Prefix}' prefix; run " +
                    "`claude-roslyn-lsp doctor` for the resolution chain.");
            }

            await connect.ConfigureAwait(false);

            Log.Connected(_logger, process.Id, pipeName);

            var connection = new RoslynConnection(pipe, process, exited, RoslynTransport.Pipe, pipeName, null, _logger);
            pumps(connection.PumpToken);
            return connection;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillQuietly(process);
            await pipe.DisposeAsync().ConfigureAwait(false);

            throw new RoslynAcquisitionException(
                $"The Roslyn server did not connect to the named pipe within {ConnectTimeout.TotalSeconds:F0} seconds. " +
                "Try CLAUDE_ROSLYN_LSP_TRANSPORT=stdio, and see `claude-roslyn-lsp doctor`.");
        }
        catch
        {
            KillQuietly(process);
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private RoslynConnection LaunchOverStdio(RoslynLaunchRequest request)
    {
        var process = Start(request, pipeName: null);
        var exited = WatchExitAsync(process);

        // Only stderr is pumped here: stdout is the protocol (C7 — in stdio mode it carries frames
        // and nothing else), so it belongs to the framing reader.
        var stream = new DuplexStream(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        var connection = new RoslynConnection(stream, process, exited, RoslynTransport.Stdio, null, null, _logger);

        _ = RoslynStderrPump.PumpAsync(process.StandardError, _logger, LogLevel.Information, connection.PumpToken);

        Log.Connected(_logger, process.Id, "stdio");

        return connection;
    }

    private Process Start(RoslynLaunchRequest request, string? pipeName)
    {
        var program = request.Server.LaunchKind == RoslynLaunchKind.Managed
            ? request.Host.Path!
            : request.Server.LaunchTarget!;

        var startInfo = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory,
        };

        foreach (var argument in BuildArguments(request, pipeName))
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.Host.DotnetRoot is { } root)
        {
            // Set rather than inherited: a Native AOT adapter has no host of its own to inherit a
            // correct value from, and a stale DOTNET_ROOT in the user's profile would send the child
            // looking for a runtime in a directory nobody has used since an SDK uninstall.
            startInfo.Environment["DOTNET_ROOT"] = root;
        }

        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        var process = Process.Start(startInfo)
            ?? throw new RoslynAcquisitionException($"Could not start '{program}'.");

        process.EnableRaisingEvents = true;
        _guard.Add(process, _logger);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            var arguments = string.Join(' ', startInfo.ArgumentList);
            Log.Started(_logger, process.Id, program, arguments);
        }

        return process;
    }

    /// <summary>
    /// Starts both text pumps once the connection object (and therefore the pump token) exists.
    /// </summary>
    /// <remarks>
    /// Deferred through a callback rather than started immediately because the token that stops them
    /// belongs to the connection, and the connection cannot be created until the pipe has connected.
    /// The pipes are small; the few milliseconds between process start and connection cannot fill
    /// them.
    /// </remarks>
    private void PumpChildStreams(Process process, RoslynTransport transport, out Action<CancellationToken> start)
    {
        start = token =>
        {
            _ = RoslynStderrPump.PumpAsync(process.StandardError, _logger, LogLevel.Information, token);

            if (transport == RoslynTransport.Pipe)
            {
                // The 646-byte startup banner (C7) plus anything else the child prints. Debug, because
                // it says the same thing on every single successful start.
                _ = RoslynStderrPump.PumpAsync(process.StandardOutput, _logger, LogLevel.Debug, token);
            }
        };
    }

    private static async Task<int> WatchExitAsync(Process process)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private static void KillQuietly(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or NotSupportedException)
        {
        }

        process.Dispose();
    }

    /// <summary>Source-generated log records; see the note in <c>LspStubServer</c> for why (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 140,
            Level = LogLevel.Information,
            Message = "Started Roslyn as process {ProcessId}: {Program} {Arguments}")]
        internal static partial void Started(ILogger logger, int processId, string program, string arguments);

        [LoggerMessage(
            EventId = 141,
            Level = LogLevel.Information,
            Message = "Roslyn process {ProcessId} is connected over {Channel}.")]
        internal static partial void Connected(ILogger logger, int processId, string channel);

        [LoggerMessage(
            EventId = 142,
            Level = LogLevel.Warning,
            Message = "CLAUDE_ROSLYN_LSP_TRANSPORT='{Value}' is not 'pipe' or 'stdio'; using the named pipe.")]
        internal static partial void UnknownTransport(ILogger logger, string value);
    }
}
