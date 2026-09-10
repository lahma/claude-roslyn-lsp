using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Configuration;

using Microsoft.Extensions.Logging;

using AdapterConnection = ClaudeRoslynLsp.Adapter.RoslynConnection;
using ChildConnection = ClaudeRoslynLsp.Roslyn.RoslynConnection;

namespace ClaudeRoslynLsp.Roslyn;

/// <summary>
/// The real backend: acquire the pinned Roslyn, resolve the .NET host, launch the child, hand the
/// mediation a framed byte channel.
/// </summary>
/// <remarks>
/// <para>
/// This is the one class that joins the two halves the earlier work packages built separately. Above
/// it, <see cref="IRoslynConnectionFactory"/> is all the mediation knows; below it,
/// <see cref="RoslynServerLocator"/>, <see cref="DotnetHostLocator"/> and
/// <see cref="RoslynProcessLauncher"/> know nothing about LSP. Neither side had to change to be
/// joined, which is what the seam was for.
/// </para>
/// <para>
/// <b>Every failure here is a state, never an exception that escapes.</b> Acquisition can fail for
/// reasons that are entirely ordinary — an offline laptop, a machine with no .NET 10 runtime, a
/// corporate proxy serving HTML where a nupkg should be, a hash that does not match the pin — and
/// each of them arrives as a <see cref="RoslynAcquisitionException"/> that the session turns into
/// <see cref="ReadinessState.Failed"/>: the adapter stays alive, answers the handshake, and refuses
/// each request with a code and a <c>doctor</c> hint. The alternative is a process that dies during
/// startup, which leaves its client with no channel to be told anything on, and Claude Code with a
/// plugin that "did not start" and no reason.
/// </para>
/// <para>
/// <b>The resolution is cached after the first success.</b> The supervisor relaunches Roslyn after a
/// crash (D57), and re-walking the acquisition chain each time would re-stat the cache, re-read the
/// tool store and — on a version override with no cache entry — consider downloading again. What
/// changes between launches is the process, not where the server lives.
/// </para>
/// <para>
/// <b>Progress is reported to the client, not only to stderr.</b> A first run downloads about 70 MB
/// before anything can answer; a client that is told nothing shows a language server that hung for a
/// minute. One line every eight megabytes is enough to read as progress and few enough not to fill a
/// transcript.
/// </para>
/// </remarks>
internal sealed partial class LaunchedRoslynFactory : IRoslynConnectionFactory
{
    /// <summary>How much has to arrive before another progress line is worth sending.</summary>
    /// <remarks>
    /// Eight megabytes: about nine lines for the whole payload on a connection where it matters, and
    /// on a fast one the download finishes before the third.
    /// </remarks>
    internal const long ProgressStep = 8L * 1024 * 1024;

    private readonly Lock _lock = new();
    private readonly ClaudeRoslynLspOptions _options;
    private readonly AdapterPaths _paths;
    private readonly IRoslynLauncher _launcher;
    private readonly ChildProcessGuard _guard;
    private readonly ILogger _logger;
    private readonly Action<int, string>? _tellClient;

    private RoslynResolution? _resolved;
    private DotnetHostResult? _host;
    private long _lastProgressReport;

    /// <summary>Creates the factory over the process's own configuration.</summary>
    /// <param name="options">The environment configuration.</param>
    /// <param name="paths">Where the cache and the child's log directory live.</param>
    /// <param name="logger">The stderr log.</param>
    /// <param name="tellClient">
    /// Sends a <c>window/logMessage</c> (or, for a failure, a <c>window/showMessage</c>) to the
    /// client. Null when there is nobody to tell, which is how <c>doctor</c> uses this class's
    /// pieces without one.
    /// </param>
    /// <param name="launcher">The launcher, injected so a test can stand where the process would.</param>
    /// <param name="guard">The child lifetime guard, shared across relaunches.</param>
    internal LaunchedRoslynFactory(
        ClaudeRoslynLspOptions options,
        AdapterPaths paths,
        ILogger logger,
        Action<int, string>? tellClient = null,
        IRoslynLauncher? launcher = null,
        ChildProcessGuard? guard = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _paths = paths;
        _logger = logger;
        _tellClient = tellClient;
        _guard = guard ?? ChildProcessGuard.Create(logger);
        _launcher = launcher ?? new RoslynProcessLauncher(logger, _guard);
    }

    /// <inheritdoc />
    public string Description => _resolved?.LaunchTarget is { } target
        ? $"roslyn-language-server at {target}"
        : $"roslyn-language-server {_options.RoslynVersion ?? RoslynServerManifest.Version}";

    /// <summary>What the acquisition chain decided, once it has run.</summary>
    internal RoslynResolution? Resolution => _resolved;

    /// <summary>The process id of the launched child, or null when none is running.</summary>
    internal int? ProcessId { get; private set; }

    /// <inheritdoc />
    public async Task<AdapterConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(cancellationToken).ConfigureAwait(false);

        if (!resolution.IsResolved)
        {
            // Thrown, and caught one frame up by the session, which turns it into the gate's Failed
            // state with a doctor hint. Nothing here writes to a stream.
            throw new RoslynAcquisitionException(
                resolution.Failure ?? "no roslyn-language-server could be resolved");
        }

        var host = ResolveHost();

        var request = RoslynLaunchRequest.FromOptions(_options, resolution, host, _paths, _logger)
            with
            {
                WorkingDirectory = _options.Solution is { Length: > 0 } solution
                    ? Path.GetDirectoryName(Path.GetFullPath(solution))
                    : null,
            };

        var child = await _launcher.LaunchAsync(request, cancellationToken).ConfigureAwait(false);

        ProcessId = child.Process.Id;

        if (_logger.IsEnabled(LogLevel.Information))
        {
            var transport = child.Transport.ToString();
            var source = resolution.Kind.ToString();
            Log.Launched(_logger, child.Process.Id, transport, source);
        }

        // The child's stdout is already drained by the launcher in pipe mode (C7, D36); what crosses
        // here is the transport stream, which is the same object in both directions for a pipe and a
        // DuplexStream over the child's handles for stdio.
        return new AdapterConnection(
            child.Stream,
            child.Stream,
            child.Exited,
            Description,
            () => DisposeChildAsync(child));
    }

    /// <summary>Walks the acquisition chain once and remembers what it found.</summary>
    private async Task<RoslynResolution> ResolveAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_resolved is { IsResolved: true } cached)
            {
                return cached;
            }
        }

        using var client = NuGetPayloadDownloader.CreateHttpClient();

        var locator = new RoslynServerLocator(
            _options,
            _paths,
            _logger,
            downloaderFactory: () => new NuGetPayloadDownloader(client, _paths, _logger));

        var progress = new Progress<RoslynDownloadProgress>(Report);

        var resolution = await locator
            .ResolveAsync(allowDownload: true, progress, cancellationToken)
            .ConfigureAwait(false);

        lock (_lock)
        {
            _resolved = resolution;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var step in resolution.Chain)
            {
                var outcome = step.Outcome.ToString();
                Log.ChainStep(_logger, step.Source, outcome, step.Detail);
            }
        }

        if (resolution.IsResolved && !resolution.Verified)
        {
            // Not an error: an override or a version override is a deliberate act. But "this is not
            // the build this release was tested against" is a fact worth carrying to whoever reads
            // the log after something behaves oddly (D25).
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                var source = resolution.Kind.ToString();
                Log.Unverified(_logger, source);
            }
        }

        return resolution;
    }

    /// <summary>Resolves the <c>dotnet</c> host once (D31).</summary>
    private DotnetHostResult ResolveHost()
    {
        lock (_lock)
        {
            _host ??= DotnetHostLocator.Resolve();
            return _host;
        }
    }

    /// <summary>Turns download progress into an occasional line the client will render.</summary>
    private void Report(RoslynDownloadProgress progress)
    {
        bool due;

        lock (_lock)
        {
            due = progress.BytesRead - _lastProgressReport >= ProgressStep;

            if (due)
            {
                _lastProgressReport = progress.BytesRead;
            }
        }

        if (!due)
        {
            return;
        }

        var described = progress.Describe();
        var line = $"{ServerVersion.Name}: downloading roslyn-language-server — {described}";

        Log.Downloading(_logger, described);
        _tellClient?.Invoke(LogMessageSeverity.Info, line);
    }

    /// <summary>Kills the child and its build hosts.</summary>
    private static async ValueTask DisposeChildAsync(ChildConnection child)
    {
        await child.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The <c>window/logMessage</c> severities, so the numbers are not bare.</summary>
    internal static class LogMessageSeverity
    {
        /// <summary>An error the user has to act on.</summary>
        internal const int Error = 1;

        /// <summary>Information.</summary>
        internal const int Info = 3;
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1600,
            Level = LogLevel.Information,
            Message = "Roslyn is running as process {ProcessId} over {Transport}, acquired from {Source}.")]
        internal static partial void Launched(ILogger logger, int processId, string transport, string source);

        [LoggerMessage(
            EventId = 1601,
            Level = LogLevel.Debug,
            Message = "Acquisition chain: {Source} — {Outcome} — {Detail}")]
        internal static partial void ChainStep(ILogger logger, string source, string outcome, string detail);

        [LoggerMessage(EventId = 1602, Level = LogLevel.Information, Message = "Downloading Roslyn: {Progress}")]
        internal static partial void Downloading(ILogger logger, string progress);

        [LoggerMessage(
            EventId = 1603,
            Level = LogLevel.Warning,
            Message = "The Roslyn server was taken from {Source} and its bytes were not checked against " +
                      "this release's pinned hash; it is not the build this version was tested against.")]
        internal static partial void Unverified(ILogger logger, string source);
    }
}
