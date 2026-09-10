using ClaudeRoslynLsp.Configuration;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Roslyn;

/// <summary>Which step of the acquisition chain produced the server that is about to be launched.</summary>
internal enum RoslynSourceKind
{
    /// <summary>Nothing was resolved; see <see cref="RoslynResolution.Failure"/>.</summary>
    None,

    /// <summary><c>CLAUDE_ROSLYN_LSP_ROSLYN_PATH</c> named it.</summary>
    Override,

    /// <summary>It was already in this adapter's cache.</summary>
    Cache,

    /// <summary>A <c>dotnet tool</c> global installation at exactly the wanted version.</summary>
    ToolStore,

    /// <summary>It was downloaded from nuget.org during this resolution.</summary>
    Download,
}

/// <summary>What happened at one step of the chain.</summary>
internal enum RoslynResolutionStepOutcome
{
    /// <summary>The step is not configured, so there was nothing to look at.</summary>
    NotConfigured,

    /// <summary>Looked, found nothing.</summary>
    NotFound,

    /// <summary>Found something, but not something that may be used — usually the wrong version.</summary>
    Rejected,

    /// <summary>Not attempted, because an earlier step had already won or a rule forbade it.</summary>
    Skipped,

    /// <summary>This step supplied the server.</summary>
    Used,
}

/// <summary>One line of the resolution chain, for logs and for <c>doctor</c>.</summary>
/// <param name="Source">The step's name, as a user would recognise it.</param>
/// <param name="Detail">What was looked at and what was there.</param>
/// <param name="Outcome">How the step ended.</param>
internal sealed record RoslynResolutionStep(string Source, string Detail, RoslynResolutionStepOutcome Outcome);

/// <summary>How a resolved server is started.</summary>
internal enum RoslynLaunchKind
{
    /// <summary>A managed assembly, run through the <c>dotnet</c> host.</summary>
    Managed,

    /// <summary>A native executable, run directly. Only an override can produce this.</summary>
    Native,
}

/// <summary>
/// The outcome of the whole acquisition chain: what won, where it is, whether its bytes were checked
/// against the pin, and everywhere that was looked.
/// </summary>
internal sealed record RoslynResolution
{
    /// <summary>Which step won, or <see cref="RoslynSourceKind.None"/>.</summary>
    internal RoslynSourceKind Kind { get; init; } = RoslynSourceKind.None;

    /// <summary>The directory holding the server, or <see langword="null"/> when nothing resolved.</summary>
    internal string? Directory { get; init; }

    /// <summary>The file the launcher starts — an assembly, or an executable for a native override.</summary>
    internal string? LaunchTarget { get; init; }

    /// <summary>Whether <see cref="LaunchTarget"/> is run through the <c>dotnet</c> host.</summary>
    internal RoslynLaunchKind LaunchKind { get; init; } = RoslynLaunchKind.Managed;

    /// <summary>
    /// The version that was asked for, or <see langword="null"/> for an override, whose version is
    /// whatever the user put there.
    /// </summary>
    internal string? Version { get; init; }

    /// <summary>
    /// Whether the bytes were checked against a hash from this repository's pin.
    /// </summary>
    /// <remarks>
    /// False for an override (this adapter did not put those files there), false for a version
    /// override (there is no hash for a version the pin does not name), and false for a tool store
    /// (NuGet's own <c>.nupkg.sha512</c> beside the payload is not the hash of that payload — C4 — so
    /// there is nothing there to check against).
    /// </remarks>
    internal bool Verified { get; init; }

    /// <summary>What the launcher may pass on the command line for this version (D26).</summary>
    internal RoslynFeatures Features { get; init; } = RoslynFeatures.Conservative;

    /// <summary>Every step, in order.</summary>
    internal IReadOnlyList<RoslynResolutionStep> Chain { get; init; } = [];

    /// <summary>Why nothing resolved, or <see langword="null"/> when something did.</summary>
    internal string? Failure { get; init; }

    /// <summary>Whether there is a server to launch.</summary>
    internal bool IsResolved => LaunchTarget is not null;
}

/// <summary>
/// The acquisition chain of D2, in one place: explicit override, this adapter's cache, a global tool
/// installation at exactly the right version, and only then a download.
/// </summary>
/// <remarks>
/// <para>
/// <b>D32 — the order is by trust, and only the last step spends the user's bandwidth.</b> An
/// explicit path is a statement of intent and wins outright; the cache is this adapter's own verified
/// copy; the tool store is somebody else's copy of the same bytes and is used only when its version
/// matches exactly; downloading is the fallback because it is the only step that can be slow, can
/// fail, and can be refused (<c>CLAUDE_ROSLYN_LSP_OFFLINE</c>).
/// </para>
/// <para>
/// <b>D33 — a tool store at a different version is reported, never used.</b> This is the tempting
/// shortcut and it is wrong twice over: the CLI is prerelease and changes between builds (D26), and
/// the point of a pin is that a release is tested against one server. So a 5.5 installation on the
/// machine appears in <c>doctor</c>'s chain as "present, not used, wrong version", which is the single
/// most useful line that report can carry for somebody who is sure they installed it.
/// </para>
/// <para>
/// <b>D34 — an override that does not resolve is a failure, not a fall-through.</b> Falling back to a
/// download would be friendlier for about ten seconds and then indistinguishable from the variable
/// having been ignored, which is the configuration bug with no symptom (D10's argument, applied to a
/// path instead of a value).
/// </para>
/// </remarks>
internal sealed partial class RoslynServerLocator
{
    private readonly ClaudeRoslynLspOptions _options;
    private readonly AdapterPaths _paths;
    private readonly ILogger _logger;
    private readonly string? _toolStoreRoot;
    private readonly Func<NuGetPayloadDownloader>? _downloaderFactory;

    /// <summary>Creates a locator.</summary>
    /// <param name="options">The configured overrides.</param>
    /// <param name="paths">Where the cache lives.</param>
    /// <param name="logger">Where the chain is logged.</param>
    /// <param name="runtimeIdentifier">The RID to resolve for; defaults to this host's.</param>
    /// <param name="toolStoreRoot">
    /// The <c>.store</c> directory of a global tool installation; defaults to
    /// <c>~/.dotnet/tools/.store</c>. Injected so the tool-store branch is testable without one.
    /// </param>
    /// <param name="downloaderFactory">
    /// Builds the downloader when the chain reaches its last step. A factory rather than an instance
    /// so that a resolution which never downloads never creates an <see cref="HttpClient"/>.
    /// </param>
    internal RoslynServerLocator(
        ClaudeRoslynLspOptions options,
        AdapterPaths paths,
        ILogger logger,
        string? runtimeIdentifier = null,
        string? toolStoreRoot = null,
        Func<NuGetPayloadDownloader>? downloaderFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _paths = paths;
        _logger = logger;
        _downloaderFactory = downloaderFactory;
        _toolStoreRoot = toolStoreRoot ?? DefaultToolStoreRoot();

        RuntimeIdentifier = runtimeIdentifier ?? Roslyn.RuntimeIdentifier.Current;
        Version = options.RoslynVersion ?? RoslynServerManifest.Version;
    }

    /// <summary>The RID being resolved for.</summary>
    internal string RuntimeIdentifier { get; }

    /// <summary>The version being resolved — the pin unless <c>CLAUDE_ROSLYN_LSP_ROSLYN_VERSION</c> moved it.</summary>
    internal string Version { get; }

    /// <summary>Whether <see cref="Version"/> is the version this release was tested against.</summary>
    internal bool IsPinnedVersion =>
        string.Equals(Version, RoslynServerManifest.Version, StringComparison.OrdinalIgnoreCase);

    /// <summary>Walks the chain and returns what it found.</summary>
    /// <param name="allowDownload">
    /// Whether the last step may run. <c>doctor</c> passes <see langword="false"/> so that a report
    /// never silently spends 70 MB of somebody's connection; <c>install</c> and the servers pass
    /// <see langword="true"/>.
    /// </param>
    /// <param name="progress">Receives download progress, when there is a download.</param>
    /// <param name="cancellationToken">Cancels the download or the wait for another process's.</param>
    internal async Task<RoslynResolution> ResolveAsync(
        bool allowDownload,
        IProgress<RoslynDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var chain = new List<RoslynResolutionStep>();

        if (_options.RoslynPath is { } configured)
        {
            return ResolveOverride(configured, chain);
        }

        chain.Add(new RoslynResolutionStep(
            _options.RoslynPathVariable ?? "CLAUDE_ROSLYN_LSP_ROSLYN_PATH",
            "not set",
            RoslynResolutionStepOutcome.NotConfigured));

        var expectedHash = RoslynServerManifest.Sha512For(RuntimeIdentifier, Version);

        if (ResolveFromCache(chain, expectedHash) is { } cached)
        {
            return cached;
        }

        if (ResolveFromToolStore(chain) is { } tooled)
        {
            return tooled;
        }

        return await ResolveByDownloadAsync(chain, expectedHash, allowDownload, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Turns a configured path into a launch target: a directory holding the server, a
    /// <c>.dll</c> to run through the host, or any other file to run directly.
    /// </summary>
    /// <remarks>
    /// The "any other file" case exists for the smoke test, which points this variable at the
    /// adapter's own Native AOT binary and adds <c>CLAUDE_ROSLYN_LSP_ROSLYN_ARGS=fake-roslyn</c>. That
    /// is not a special case bolted on for a test: "front a program that speaks this protocol" is the
    /// general shape, and a release RID proving its own framing without a 70 MB download is what it
    /// buys.
    /// </remarks>
    /// <param name="configured">The raw value of the variable.</param>
    /// <param name="directory">The directory the server lives in.</param>
    /// <param name="target">The file to launch.</param>
    /// <param name="kind">How to launch it.</param>
    /// <returns><see langword="true"/> when the value names something launchable.</returns>
    internal static bool TryResolveOverridePath(
        string configured,
        out string? directory,
        out string? target,
        out RoslynLaunchKind kind)
    {
        directory = null;
        target = null;
        kind = RoslynLaunchKind.Managed;

        string full;

        try
        {
            full = Path.GetFullPath(configured);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (Directory.Exists(full))
        {
            var assembly = Path.Combine(full, RoslynServerManifest.ServerAssemblyName);

            if (!File.Exists(assembly))
            {
                return false;
            }

            directory = full;
            target = assembly;
            kind = RoslynLaunchKind.Managed;
            return true;
        }

        if (!File.Exists(full))
        {
            return false;
        }

        directory = Path.GetDirectoryName(full);
        target = full;
        kind = full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? RoslynLaunchKind.Managed
            : RoslynLaunchKind.Native;

        return true;
    }

    /// <summary>The default global tool store, or <see langword="null"/> when there is no home directory.</summary>
    internal static string? DefaultToolStoreRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".dotnet", "tools", ".store");
    }

    /// <summary>
    /// The directory a global <c>roslyn-language-server</c> tool installation puts the payload in.
    /// </summary>
    /// <remarks>
    /// About 290 characters deep on a typical Windows profile, which is why the adapter's own cache is
    /// flat: <see cref="System.Diagnostics.Process"/> refuses to start an executable at that depth
    /// with "file not found" for a file that exists (C1). It is still usable from here because the
    /// launcher starts the <em>managed assembly through the host</em> — the long path is an argument,
    /// not the image to load.
    /// </remarks>
    /// <param name="toolStoreRoot">The <c>.store</c> directory.</param>
    /// <param name="version">The version to look for.</param>
    /// <param name="runtimeIdentifier">The RID to look for.</param>
    internal static string ToolStorePayloadDirectory(string toolStoreRoot, string version, string runtimeIdentifier) =>
        Path.Combine(
            toolStoreRoot,
            RoslynServerManifest.ShimPackageId,
            version,
            RoslynServerManifest.PackageId(runtimeIdentifier),
            version,
            "tools",
            "net10.0",
            runtimeIdentifier);

    private RoslynResolution ResolveOverride(string configured, List<RoslynResolutionStep> chain)
    {
        var variable = _options.RoslynPathVariable ?? "CLAUDE_ROSLYN_LSP_ROSLYN_PATH";

        if (!TryResolveOverridePath(configured, out var directory, out var target, out var kind))
        {
            chain.Add(new RoslynResolutionStep(
                variable,
                $"{configured} (no {RoslynServerManifest.ServerAssemblyName} there)",
                RoslynResolutionStepOutcome.Rejected));

            return new RoslynResolution
            {
                Chain = chain,
                Failure =
                    $"{variable} is set to '{configured}', which is neither a directory containing " +
                    $"{RoslynServerManifest.ServerAssemblyName} nor a file that exists. Fix the value or unset it " +
                    "to let the adapter acquire the pinned server itself; it is deliberately not ignored.",
            };
        }

        chain.Add(new RoslynResolutionStep(
            variable,
            $"{target} (version not checked)",
            RoslynResolutionStepOutcome.Used));

        Log.UsingOverride(_logger, variable, target!);

        return new RoslynResolution
        {
            Kind = RoslynSourceKind.Override,
            Directory = directory,
            LaunchTarget = target,
            LaunchKind = kind,
            Version = _options.RoslynVersion,
            Verified = false,
            Features = RoslynServerManifest.FeaturesFor(_options.RoslynVersion ?? string.Empty),
            Chain = chain,
        };
    }

    private RoslynResolution? ResolveFromCache(List<RoslynResolutionStep> chain, string? expectedHash)
    {
        var directory = _paths.ServerDirectory(Version, RuntimeIdentifier);
        var assembly = Path.Combine(directory, RoslynServerManifest.ServerAssemblyName);
        var complete = _paths.CompleteMarker(Version, RuntimeIdentifier);

        if (!File.Exists(complete) || !File.Exists(assembly))
        {
            chain.Add(new RoslynResolutionStep("cache", $"{directory} (not present)", RoslynResolutionStepOutcome.NotFound));
            return null;
        }

        var recorded = ReadMarker(complete);

        if (expectedHash is not null && !string.Equals(recorded, expectedHash, StringComparison.Ordinal))
        {
            chain.Add(new RoslynResolutionStep(
                "cache",
                $"{directory} (marker records a different hash; will re-acquire)",
                RoslynResolutionStepOutcome.Rejected));

            return null;
        }

        chain.Add(new RoslynResolutionStep("cache", directory, RoslynResolutionStepOutcome.Used));

        return new RoslynResolution
        {
            Kind = RoslynSourceKind.Cache,
            Directory = directory,
            LaunchTarget = assembly,
            Version = Version,
            Verified = expectedHash is not null,
            Features = RoslynServerManifest.FeaturesFor(Version),
            Chain = chain,
        };
    }

    private RoslynResolution? ResolveFromToolStore(List<RoslynResolutionStep> chain)
    {
        if (_toolStoreRoot is null)
        {
            chain.Add(new RoslynResolutionStep(
                "global tool store", "no home directory", RoslynResolutionStepOutcome.NotFound));

            return null;
        }

        var wanted = ToolStorePayloadDirectory(_toolStoreRoot, Version, RuntimeIdentifier);
        var assembly = Path.Combine(wanted, RoslynServerManifest.ServerAssemblyName);

        if (File.Exists(assembly))
        {
            chain.Add(new RoslynResolutionStep("global tool store", wanted, RoslynResolutionStepOutcome.Used));
            Log.UsingToolStore(_logger, Version, wanted);

            return new RoslynResolution
            {
                Kind = RoslynSourceKind.ToolStore,
                Directory = wanted,
                LaunchTarget = assembly,
                Version = Version,

                // C4: the .nupkg.sha512 sitting beside a tool-store payload is not the hash of that
                // payload, so there is nothing here that could be verified even in principle.
                Verified = false,
                Features = RoslynServerManifest.FeaturesFor(Version),
                Chain = chain,
            };
        }

        var others = OtherToolStoreVersions();

        chain.Add(new RoslynResolutionStep(
            "global tool store",
            others.Count == 0
                ? $"{wanted} (not installed)"
                : $"installed at {string.Join(", ", others)} — not {Version}, so it is not used (D33)",
            others.Count == 0 ? RoslynResolutionStepOutcome.NotFound : RoslynResolutionStepOutcome.Rejected));

        if (others.Count > 0 && _logger.IsEnabled(LogLevel.Debug))
        {
            var installed = string.Join(", ", others);
            Log.ToolStoreVersionMismatch(_logger, installed, Version);
        }

        return null;
    }

    private async Task<RoslynResolution> ResolveByDownloadAsync(
        List<RoslynResolutionStep> chain,
        string? expectedHash,
        bool allowDownload,
        IProgress<RoslynDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Roslyn.RuntimeIdentifier.IsSupported(RuntimeIdentifier))
        {
            chain.Add(new RoslynResolutionStep(
                "nuget.org", $"no payload is published for {RuntimeIdentifier}", RoslynResolutionStepOutcome.Rejected));

            return new RoslynResolution
            {
                Chain = chain,
                Version = Version,
                Failure =
                    $"Microsoft does not publish roslyn-language-server for '{RuntimeIdentifier}'. The published " +
                    $"runtime identifiers are {string.Join(", ", Roslyn.RuntimeIdentifier.All)}. Set " +
                    "CLAUDE_ROSLYN_LSP_ROSLYN_PATH to a server you built or installed yourself.",
            };
        }

        if (_options.Offline)
        {
            chain.Add(new RoslynResolutionStep(
                "nuget.org", "CLAUDE_ROSLYN_LSP_OFFLINE is set", RoslynResolutionStepOutcome.Skipped));

            return new RoslynResolution
            {
                Chain = chain,
                Version = Version,
                Failure =
                    $"Roslyn {Version} for {RuntimeIdentifier} is not in the cache " +
                    $"({_paths.ServerDirectory(Version, RuntimeIdentifier)}) and CLAUDE_ROSLYN_LSP_OFFLINE is set, so " +
                    "it will not be downloaded. Unset it and run `claude-roslyn-lsp install`, or point " +
                    "CLAUDE_ROSLYN_LSP_ROSLYN_PATH at a copy you already have.",
            };
        }

        if (!allowDownload || _downloaderFactory is null)
        {
            chain.Add(new RoslynResolutionStep(
                "nuget.org",
                $"{RoslynServerManifest.PackageUrl(RuntimeIdentifier, Version).AbsoluteUri} (would download)",
                RoslynResolutionStepOutcome.Skipped));

            return new RoslynResolution
            {
                Chain = chain,
                Version = Version,
                Failure =
                    $"Roslyn {Version} for {RuntimeIdentifier} is not installed yet. Run " +
                    "`claude-roslyn-lsp install` to download it (about 70 MB), or start the server, which " +
                    "downloads it on first use.",
            };
        }

        try
        {
            var directory = await _downloaderFactory()
                .EnsureAsync(RuntimeIdentifier, Version, expectedHash, progress, cancellationToken)
                .ConfigureAwait(false);

            chain.Add(new RoslynResolutionStep(
                "nuget.org",
                expectedHash is null
                    ? $"downloaded {Version} (no pinned hash for this version — unverified)"
                    : $"downloaded {Version} and verified its SHA-512",
                RoslynResolutionStepOutcome.Used));

            return new RoslynResolution
            {
                Kind = RoslynSourceKind.Download,
                Directory = directory,
                LaunchTarget = Path.Combine(directory, RoslynServerManifest.ServerAssemblyName),
                Version = Version,
                Verified = expectedHash is not null,
                Features = RoslynServerManifest.FeaturesFor(Version),
                Chain = chain,
            };
        }
        catch (RoslynAcquisitionException exception)
        {
            chain.Add(new RoslynResolutionStep("nuget.org", exception.Message, RoslynResolutionStepOutcome.Rejected));

            return new RoslynResolution
            {
                Chain = chain,
                Version = Version,
                Failure = exception.Message,
            };
        }
    }

    /// <summary>Which versions of the tool are installed globally, whatever their RID.</summary>
    private List<string> OtherToolStoreVersions()
    {
        var root = Path.Combine(_toolStoreRoot!, RoslynServerManifest.ShimPackageId);

        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(root)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? ReadMarker(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Source-generated log records; see the note in <c>LspStubServer</c> for why (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 110,
            Level = LogLevel.Information,
            Message = "Using the Roslyn server named by {Variable}: {Path}. Its version is not checked.")]
        internal static partial void UsingOverride(ILogger logger, string variable, string path);

        [LoggerMessage(
            EventId = 111,
            Level = LogLevel.Information,
            Message = "Using the globally installed roslyn-language-server {Version} from {Path}.")]
        internal static partial void UsingToolStore(ILogger logger, string version, string path);

        [LoggerMessage(
            EventId = 112,
            Level = LogLevel.Debug,
            Message = "A global roslyn-language-server installation exists ({Installed}) but not at {Wanted}; " +
                      "it will not be used.")]
        internal static partial void ToolStoreVersionMismatch(ILogger logger, string installed, string wanted);
    }
}
