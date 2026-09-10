namespace ClaudeRoslynLsp.Roslyn;

/// <summary>
/// Where this adapter keeps everything it did not ship with: the downloaded Roslyn payloads, the
/// partial downloads, the lock files and the child's log directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>D27 — one home directory, resolved once, with the plugin's own data directory in the middle of
/// the chain.</b> <c>CLAUDE_ROSLYN_LSP_HOME</c> wins; then <c>CLAUDE_PLUGIN_DATA</c>, which Claude
/// Code substitutes with a real directory it owns and cleans up with the plugin (the manifest maps it
/// straight through, and it is the single documented exception to the plugin-option prefix rule);
/// then the platform's own cache location.
/// </para>
/// <para>
/// The platform default is the OS cache directory — <c>%LOCALAPPDATA%</c>, <c>~/Library/Caches</c>,
/// <c>$XDG_CACHE_HOME</c> or <c>~/.cache</c> — and deliberately <em>not</em> a
/// <c>~/.claude-roslyn-lsp</c> dotfile, which is what the scaffold's placeholder documentation said.
/// The payload is 70 MB compressed and about 140 MB extracted per version per RID; that is cache,
/// by every definition an operating system has. On Windows it also keeps it out of a roaming profile,
/// where a corporate policy would otherwise try to synchronise a Roslyn build across the network at
/// every logon.
/// </para>
/// <para>
/// <c>CLAUDE_ROSLYN_LSP_CACHE_DIR</c> overrides the <em>payload</em> location only, not the home
/// directory: a user who points the cache at a large disk still wants their logs and temp files where
/// the rest of the adapter's state lives.
/// </para>
/// </remarks>
internal sealed record AdapterPaths
{
    /// <summary>The directory name used under whichever platform cache root applies.</summary>
    private const string DirectoryName = "claude-roslyn-lsp";

    /// <summary>The adapter's per-user state directory.</summary>
    internal required string Home { get; init; }

    /// <summary>
    /// Which variable (or which platform rule) produced <see cref="Home"/>, for <c>doctor</c>.
    /// </summary>
    internal required string HomeSource { get; init; }

    /// <summary>The root under which downloaded Roslyn payloads are cached, one directory per version.</summary>
    internal required string RoslynCacheRoot { get; init; }

    /// <summary>Where a download is streamed to before it has been hashed and accepted.</summary>
    /// <remarks>
    /// Under <see cref="Home"/> rather than under the system temp directory so that the final move
    /// into the cache stays on one volume. <see cref="Directory.Move(string, string)"/> across volumes
    /// throws, and the whole point of staging is that the last step is atomic.
    /// </remarks>
    internal string TempDirectory => Path.Combine(Home, "tmp");

    /// <summary>Where the Roslyn child is told to put its extension logs.</summary>
    /// <remarks>
    /// Passed as <c>--extensionLogDirectory</c>, which in the pinned build wrote nothing at all at
    /// <c>Information</c> (C6) — the server's logging arrives as <c>window/logMessage</c> instead. The
    /// argument is still passed: it is the documented knob, it costs nothing, and a build that starts
    /// honouring it should write somewhere this adapter can point a user at rather than into the
    /// working directory of whatever launched us.
    /// </remarks>
    internal string RoslynLogDirectory => Path.Combine(Home, "logs", "roslyn");

    /// <summary>The cache directory for one version of one RID.</summary>
    /// <remarks>
    /// Deliberately flat — <c>&lt;root&gt;/&lt;version&gt;/&lt;rid&gt;</c> and then the payload's own
    /// files — because the layout it replaces is not. A global tool store nests the same payload about
    /// 290 characters deep, and at that length <see cref="System.Diagnostics.Process"/> fails to start
    /// the executable with "file not found" for a file that plainly exists (C1).
    /// </remarks>
    /// <param name="version">The Roslyn version.</param>
    /// <param name="runtimeIdentifier">The RID.</param>
    internal string ServerDirectory(string version, string runtimeIdentifier) =>
        Path.Combine(RoslynCacheRoot, version, runtimeIdentifier);

    /// <summary>
    /// The marker written after a successful extraction, holding the hash the payload was accepted
    /// under.
    /// </summary>
    /// <remarks>
    /// A directory full of files is not evidence of a finished download — a process killed mid-extract
    /// leaves exactly that. The marker is written last, so its presence is the only thing that means
    /// "complete", and its contents let a later run notice that the pin's hash has moved underneath a
    /// cache entry.
    /// </remarks>
    /// <param name="version">The Roslyn version.</param>
    /// <param name="runtimeIdentifier">The RID.</param>
    internal string CompleteMarker(string version, string runtimeIdentifier) =>
        Path.Combine(ServerDirectory(version, runtimeIdentifier), ".complete");

    /// <summary>The lock file that serialises first runs for one version and RID.</summary>
    /// <remarks>
    /// Beside the version directory rather than inside the RID directory, because the RID directory is
    /// created, filled and renamed by the very operation the lock protects.
    /// </remarks>
    /// <param name="version">The Roslyn version.</param>
    /// <param name="runtimeIdentifier">The RID.</param>
    internal string LockFile(string version, string runtimeIdentifier) =>
        Path.Combine(RoslynCacheRoot, version, runtimeIdentifier + ".lock");

    /// <summary>Resolves the paths from the process environment and the parsed options.</summary>
    /// <param name="options">The options, for the two variables that can move these directories.</param>
    internal static AdapterPaths Resolve(Configuration.ClaudeRoslynLspOptions options) =>
        Resolve(options, static name => Environment.GetEnvironmentVariable(name));

    /// <summary>Resolves the paths against an arbitrary variable source, so tests need no real HOME.</summary>
    /// <param name="options">The options, for <c>CLAUDE_ROSLYN_LSP_HOME</c> and <c>_CACHE_DIR</c>.</param>
    /// <param name="read">Returns the raw value of a variable, or <see langword="null"/> if unset.</param>
    internal static AdapterPaths Resolve(Configuration.ClaudeRoslynLspOptions options, Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(read);

        var (home, source) = ResolveHome(options, read);

        return new AdapterPaths
        {
            Home = home,
            HomeSource = source,
            RoslynCacheRoot = options.CacheDirectory is { } cache
                ? Path.GetFullPath(cache)
                : Path.Combine(home, "roslyn"),
        };
    }

    private static (string Home, string Source) ResolveHome(
        Configuration.ClaudeRoslynLspOptions options,
        Func<string, string?> read)
    {
        if (options.Home is { } configured)
        {
            return (Path.GetFullPath(configured), "CLAUDE_ROSLYN_LSP_HOME");
        }

        if (Trimmed(read("CLAUDE_PLUGIN_DATA")) is { } pluginData)
        {
            return (Path.GetFullPath(pluginData), "CLAUDE_PLUGIN_DATA");
        }

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Trimmed(read("LOCALAPPDATA"))
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return (Path.Combine(localAppData, DirectoryName), "%LOCALAPPDATA%");
        }

        var profile = Trimmed(read("HOME"))
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsMacOS())
        {
            return (Path.Combine(profile, "Library", "Caches", DirectoryName), "~/Library/Caches");
        }

        // XDG_CACHE_HOME is the specification's own spelling of ~/.cache, and honouring it is what
        // makes a container that redirects the cache actually redirect this one too.
        return Trimmed(read("XDG_CACHE_HOME")) is { } xdg
            ? (Path.Combine(xdg, DirectoryName), "$XDG_CACHE_HOME")
            : (Path.Combine(profile, ".cache", DirectoryName), "~/.cache");
    }

    /// <summary>Blank is absent here too — see the plugin-option rule in AGENTS.md.</summary>
    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
