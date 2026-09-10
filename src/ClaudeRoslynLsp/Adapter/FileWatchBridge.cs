using System.Buffers;
using System.Text.Json;

using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// Watches the workspace on Roslyn's behalf, because the client refuses to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Without this the adapter is stale and silent about it.</b> Roslyn has no in-process file
/// watcher and no fallback (C34): it registers 135-149 <c>workspace/didChangeWatchedFiles</c>
/// watchers with its client and relies on being told. Claude Code answers every one of those
/// registrations with <c>-32601</c> and never sends a watched-file notification, so a file created
/// by Bash or pulled in by <c>git checkout</c> never joins its project — and every later answer is
/// silently wrong rather than missing, which is the worst failure mode available to a language
/// server.
/// </para>
/// <para>
/// <b>The C33 rule is the whole reason this is more than a loop over FileSystemWatcher.</b> A
/// <c>Created</c> event for a new <c>.cs</c> file does <em>nothing</em> — Roslyn takes it and
/// carries on, and the new type stays invisible. What makes it re-evaluate is a <c>Changed</c> event
/// for the owning <c>.csproj</c>, even with the project file untouched on disk; the symbol then
/// appears 1.6-2.1 s later. So every appearance or disappearance of a <c>.cs</c> file is
/// accompanied by a synthetic <c>Changed</c> for the nearest project file above it. That single
/// mapping is the difference between "a file created by Bash is found" and "restart your editor".
/// </para>
/// <para>
/// <b>113 of Roslyn's watchers are one per reference assembly under the NuGet cache</b> (C32), and
/// standing up a filesystem watcher on the package cache would be absurd — it is shared, enormous,
/// and written to by every build on the machine. Bases outside the workspace root are therefore
/// dropped. The one thing that survives the exclusion list is <c>project.assets.json</c> under a
/// project's <c>obj</c>: <c>obj</c> is otherwise ignored as build output, but that file <em>is</em>
/// the restore result, and Roslyn reloads a project's references when it changes.
/// </para>
/// <para>
/// <b>Open documents are excluded.</b> The client is already telling Roslyn about those through
/// <c>didChange</c>, at a version number; a watched-file event for the same path would make Roslyn
/// re-read the copy on disk and quietly discard the unsaved buffer it was given.
/// </para>
/// </remarks>
internal sealed partial class FileWatchBridge : IDisposable
{
    /// <summary>How long events are batched before one notification goes out.</summary>
    /// <remarks>
    /// A <c>git checkout</c> or a build produces hundreds of events in a few milliseconds, and each
    /// one Roslyn receives separately is a separate piece of work on the solution-load path. Two
    /// hundred milliseconds collapses a checkout into one notification and is still below the point
    /// where a person notices.
    /// </remarks>
    internal static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How long after the <em>first</em> pending registration change the watcher set is rebuilt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Roslyn sends its ~140 registrations as ~140 separate <c>client/registerCapability</c>
    /// requests during startup, so rebuilding on each one would create and tear down the same
    /// watchers a hundred times over. But the delay is measured from the first pending change and
    /// <b>not extended</b> by the ones that follow, which is the whole point: registrations keep
    /// arriving for as long as the session lives (C51), so a trailing debounce postpones the first
    /// rebuild indefinitely — and the events lost while nothing is watching include the restore that
    /// the very first load is waiting for (C48).
    /// </para>
    /// </remarks>
    internal static readonly TimeSpan RebuildDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long after the watchers come up a workspace that still has not loaded is re-synchronised.
    /// </summary>
    /// <remarks>
    /// The belt to the debounce's braces, and the cheapest insurance against C48 there is. Anything
    /// that happened on disk before the first watcher existed is simply gone — and on a repository
    /// that has never been restored, the thing that happened is Roslyn writing
    /// <c>project.assets.json</c> and then waiting to be told about it. Five seconds after the
    /// watchers are up, a workspace that still has not reported itself loaded gets one synthetic
    /// change for every project file, which is exactly what unblocks it. A workspace that loaded
    /// normally pays nothing, because the check is skipped.
    /// </remarks>
    internal static readonly TimeSpan CatchUpDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The point at which per-directory watching is abandoned for one recursive watcher on the
    /// workspace root.
    /// </summary>
    /// <remarks>
    /// <b>A switch, not a cap.</b> It was a cap, and Quartz.NET showed why that was wrong: 30
    /// projects produce 91 distinct watch directories inside the root (C49), so a limit of 64 left
    /// 27 of them unwatched — silently, and in exactly the way this class exists to prevent. One
    /// recursive watcher on the root covers all of them for one handle and one buffer; the extra
    /// events it sees are removed by the same exclusion list and glob filter every other event goes
    /// through. Per-directory watching is still preferred below the threshold because on a monorepo
    /// the root subtree is far larger than the part Roslyn asked about.
    /// </remarks>
    internal const int MaxWatchers = 64;

    /// <summary>LSP <c>FileChangeType.Created</c>.</summary>
    internal const int Created = 1;

    /// <summary>LSP <c>FileChangeType.Changed</c>.</summary>
    internal const int Changed = 2;

    /// <summary>LSP <c>FileChangeType.Deleted</c>.</summary>
    internal const int Deleted = 3;

    /// <summary>
    /// Directories whose contents are build output, machine state or somebody else's package.
    /// </summary>
    /// <remarks>
    /// The same list the solution scan skips (D41), for the same reason: what is in them is not
    /// anybody's source. <c>obj</c> is on it and is also the one with an exception, because
    /// <c>project.assets.json</c> lives there and is the only evidence a restore happened.
    /// </remarks>
    internal static readonly string[] ExcludedDirectories =
        ["bin", "obj", ".git", "node_modules", ".vs", "artifacts", "TestResults"];

    /// <summary>The one file under an excluded directory that is still worth a notification.</summary>
    internal const string RestoreResultFile = "project.assets.json";

    /// <summary>The files a resynchronisation reports, when individual events were lost.</summary>
    private static readonly string[] ResyncPatterns =
        ["*.csproj", "*.vbproj", "*.fsproj", "*.props", "*.targets", RestoreResultFile];

    private readonly Lock _lock = new();
    private readonly Lock _rootLock = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly List<FileChange> _pending = [];
    private readonly HashSet<string> _pendingKeys = new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<string?> _rootSource;
    private readonly DocumentMirror _mirror;
    private readonly IAdapterChannel _channel;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private ITimer? _batchTimer;
    private ITimer? _rebuildTimer;
    private ITimer? _catchUpTimer;
    private IReadOnlyList<CollapsedWatcher> _registered = [];
    private string? _root;
    private bool _rebuildPending;
    private bool _caughtUp;
    private bool _disposed;

    /// <summary>Creates the bridge. Nothing is watched until <see cref="Schedule"/> runs.</summary>
    /// <param name="workspaceRoot">
    /// Returns the workspace root as a local path, or null when there is not one yet.
    /// </param>
    /// <param name="mirror">The open documents, which are excluded from every batch.</param>
    /// <param name="channel">Where the notification goes.</param>
    /// <param name="time">The clock, so the batch window is testable.</param>
    /// <param name="logger">The stderr log.</param>
    internal FileWatchBridge(
        Func<string?> workspaceRoot,
        DocumentMirror mirror,
        IAdapterChannel channel,
        TimeProvider time,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(workspaceRoot);
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _rootSource = workspaceRoot;
        _mirror = mirror;
        _channel = channel;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The workspace root, resolved on first use and remembered.
    /// </summary>
    /// <remarks>
    /// <b>A delegate, not a value, and the reason cost a live test.</b> The bridge is built with the
    /// rest of the session, which happens before the client has sent <c>initialize</c> — so the root
    /// does not exist yet. Reading it in the constructor produced a bridge that watched nothing at
    /// all, silently, for the whole session: no error, no warning, and every answer about a file
    /// created outside the editor quietly stale, which is precisely the failure this class exists to
    /// remove.
    /// </remarks>
    private string? Root
    {
        get
        {
            lock (_rootLock)
            {
                return _root ??= NormaliseRoot(_rootSource());
            }
        }
    }

    /// <summary>Raised when a project, props or targets file changed, so diagnostics can be re-pulled.</summary>
    internal event Action? ProjectFilesChanged;

    /// <summary>How many filesystem watchers are live.</summary>
    internal int WatcherCount
    {
        get
        {
            lock (_lock)
            {
                return _watchers.Count;
            }
        }
    }

    /// <summary>Queues a rebuild from the current registration set, after the settle delay.</summary>
    /// <param name="watchers">What Roslyn has registered, collapsed by base URI (C32).</param>
    internal void Schedule(IReadOnlyList<CollapsedWatcher> watchers)
    {
        ArgumentNullException.ThrowIfNull(watchers);

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _registered = watchers;

            if (_rebuildPending)
            {
                // Already armed. Deliberately not re-armed: see RebuildDelay.
                return;
            }

            _rebuildPending = true;

            _rebuildTimer ??= _time.CreateTimer(
                static x => ((FileWatchBridge) x!).Rebuild(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);

            _rebuildTimer.Change(RebuildDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Tells Roslyn that every project file in the workspace changed.
    /// </summary>
    /// <remarks>
    /// The answer to a <see cref="FileSystemWatcher"/> that reported an error or overflowed its
    /// buffer. Once events have been lost there is no way to know <em>which</em> were lost, and the
    /// C33 rule gives the recovery for free: a <c>Changed</c> for every project file makes Roslyn
    /// re-evaluate all of them, which picks up whatever appeared or vanished while nobody was
    /// looking. It costs a reload; the alternative costs a workspace that is wrong until somebody
    /// restarts the editor.
    /// </remarks>
    internal void Resynchronise()
    {
        if (Root is not { } root)
        {
            return;
        }

        var count = 0;

        foreach (var pattern in ResyncPatterns)
        {
            foreach (var path in EnumerateWorkspace(root, pattern))
            {
                Enqueue(path, Changed, syntheticFor: null);
                count++;
            }
        }

        // No direct ProjectFilesChanged here: everything just queued IS a project file, so the
        // batch flush raises it once with the rest. Raising it here as well would only make the
        // diagnostics bridge arm its one-second debounce twice for the same event.
        Log.Resynchronised(_logger, count);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            DisposeWatchers();
            _batchTimer?.Dispose();
            _rebuildTimer?.Dispose();
            _catchUpTimer?.Dispose();
            _batchTimer = null;
            _rebuildTimer = null;
            _catchUpTimer = null;
        }
    }

    /// <summary>Whether a path lies under a directory this bridge never reports.</summary>
    /// <param name="path">The full path.</param>
    internal static bool IsExcluded(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (string.Equals(Path.GetFileName(path), RestoreResultFile, StringComparison.OrdinalIgnoreCase))
        {
            // The restore result, which is exactly what a consumer of obj/ wants to hear about.
            return false;
        }

        foreach (var segment in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var excluded in ExcludedDirectories)
            {
                if (string.Equals(segment, excluded, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether a change to this file needs the C33 synthetic project-file event.</summary>
    /// <param name="path">The full path.</param>
    /// <param name="changeType">The LSP change type.</param>
    internal static bool NeedsProjectNudge(string path, int changeType) =>
        changeType != Changed
        && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>The nearest project file above a path, or null when there is none under the root.</summary>
    /// <param name="path">The file that appeared or vanished.</param>
    /// <param name="root">The workspace root, which the walk never climbs past.</param>
    internal static string? NearestProjectFile(string path, string? root)
    {
        if (root is not { Length: > 0 })
        {
            return null;
        }

        var directory = Path.GetDirectoryName(path);

        while (directory is { Length: > 0 } && directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var project in Directory.EnumerateFiles(directory, "*.csproj"))
                {
                    return project;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            var parent = Path.GetDirectoryName(directory);

            if (string.Equals(parent, directory, StringComparison.Ordinal))
            {
                return null;
            }

            directory = parent;
        }

        return null;
    }

    /// <summary>Rebuilds the watcher set from the registrations recorded so far.</summary>
    private void Rebuild()
    {
        lock (_lock)
        {
            _rebuildPending = false;
        }

        if (Root is not { } root)
        {
            Log.NoRoot(_logger);
            return;
        }

        IReadOnlyList<CollapsedWatcher> registered;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            registered = _registered;
        }

        var kept = new List<WatchTarget>();
        var outside = 0;

        foreach (var watcher in registered)
        {
            var directory = BaseDirectoryOf(watcher.BaseUri) ?? root;

            if (!IsUnderRoot(root, directory))
            {
                outside++;
                continue;
            }

            if (!Directory.Exists(directory))
            {
                continue;
            }

            var recursive = watcher.Patterns.Any(GlobMatcher.IsRecursive);
            var existing = kept.FindIndex(x =>
                string.Equals(x.Directory, directory, StringComparison.OrdinalIgnoreCase));

            if (existing >= 0)
            {
                kept[existing] = kept[existing] with
                {
                    Patterns = [.. kept[existing].Patterns, .. watcher.Patterns],
                    Recursive = kept[existing].Recursive || recursive,
                };

                continue;
            }

            kept.Add(new WatchTarget(directory, watcher.Patterns, recursive));
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            DisposeWatchers();

            if (kept.Count > MaxWatchers)
            {
                Log.CollapsingToRoot(_logger, kept.Count, MaxWatchers);
                kept = [CollapseToRoot(root, kept)];
            }

            foreach (var target in kept)
            {
                if (Create(target) is { } created)
                {
                    _watchers.Add(created);
                }
            }

            Log.Watching(_logger, _watchers.Count, outside);
            ArmCatchUp();
        }
    }

    /// <summary>Arms the one-shot catch-up, the first time there is anything to catch up from.</summary>
    /// <remarks>Called under the lock.</remarks>
    private void ArmCatchUp()
    {
        if (_caughtUp || _watchers.Count == 0)
        {
            return;
        }

        _catchUpTimer ??= _time.CreateTimer(
            static x => ((FileWatchBridge) x!).CatchUp(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        _catchUpTimer.Change(CatchUpDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Re-synchronises a workspace that has not managed to finish loading (C48).</summary>
    private void CatchUp()
    {
        lock (_lock)
        {
            if (_caughtUp || _disposed)
            {
                return;
            }

            _caughtUp = true;
        }

        if (_channel.Readiness is ReadinessState.ProjectsLoaded or ReadinessState.LoadTimedOut)
        {
            // It loaded on its own. Nothing to do, and nothing paid.
            return;
        }

        Log.CatchingUp(_logger, CatchUpDelay.TotalSeconds);
        Resynchronise();
    }

    /// <summary>
    /// Folds every kept target into one recursive watcher on the workspace root.
    /// </summary>
    /// <remarks>
    /// Each pattern is re-based rather than reused: a bare <c>Quartz.csproj</c> is relative to its
    /// own project directory and would match nothing measured from the root, so anything that does
    /// not already begin with <c>**/</c> gets one. That widens each pattern to "anywhere below the
    /// root", which is exactly what a single root watcher can honour and is the same set the
    /// per-directory watchers would have covered between them.
    /// </remarks>
    private static WatchTarget CollapseToRoot(string root, List<WatchTarget> kept)
    {
        var patterns = new List<string>();

        foreach (var target in kept)
        {
            foreach (var pattern in target.Patterns)
            {
                var rebased = pattern.StartsWith("**/", StringComparison.Ordinal) ? pattern : "**/" + pattern;

                if (!patterns.Contains(rebased, StringComparer.OrdinalIgnoreCase))
                {
                    patterns.Add(rebased);
                }
            }
        }

        return new WatchTarget(root, patterns, Recursive: true);
    }

    /// <summary>Stands up one <see cref="FileSystemWatcher"/>, or reports why it could not.</summary>
    private FileSystemWatcher? Create(WatchTarget target)
    {
        try
        {
            var watcher = new FileSystemWatcher(target.Directory)
            {
                IncludeSubdirectories = target.Recursive,
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size,

                // The default 8 KB overflows on a git checkout of any size, and an overflow costs a
                // full resynchronisation. 64 KB is the documented maximum that stays in non-paged
                // pool on Windows.
                InternalBufferSize = 64 * 1024,
            };

            var patterns = target.Patterns;

            watcher.Created += (_, e) => OnEvent(e.FullPath, Created, patterns, target.Directory);
            watcher.Changed += (_, e) => OnEvent(e.FullPath, Changed, patterns, target.Directory);
            watcher.Deleted += (_, e) => OnEvent(e.FullPath, Deleted, patterns, target.Directory);

            watcher.Renamed += (_, e) =>
            {
                // A rename is a disappearance and an appearance, and Roslyn has no other way to
                // learn that the old name is gone.
                OnEvent(e.OldFullPath, Deleted, patterns, target.Directory);
                OnEvent(e.FullPath, Created, patterns, target.Directory);
            };

            watcher.Error += (_, e) =>
            {
                Log.WatcherError(_logger, target.Directory, e.GetException());
                Resynchronise();
            };

            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
                                              or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // On Linux this is where an exhausted inotify instance budget surfaces. Reported once
            // per directory rather than swallowed, because the consequence — a stale workspace — is
            // otherwise invisible.
            Log.WatcherRefused(_logger, target.Directory, exception.Message);
            return null;
        }
    }

    /// <summary>Filters one filesystem event and queues what survives.</summary>
    private void OnEvent(string fullPath, int changeType, IReadOnlyList<string> patterns, string baseDirectory)
    {
        if (IsExcluded(fullPath))
        {
            return;
        }

        var relative = Relative(baseDirectory, fullPath);

        if (relative is null || !GlobMatcher.IsMatchAny(patterns, relative))
        {
            return;
        }

        Enqueue(fullPath, changeType, syntheticFor: null);
    }

    /// <summary>Adds a change to the batch, with the C33 project nudge when one is due.</summary>
    /// <param name="fullPath">The file.</param>
    /// <param name="changeType">The LSP change type.</param>
    /// <param name="syntheticFor">
    /// Set when this entry <em>is</em> the nudge, so a nudge cannot recursively nudge.
    /// </param>
    private void Enqueue(string fullPath, int changeType, string? syntheticFor)
    {
        string uri;

        try
        {
            uri = new Uri(fullPath).AbsoluteUri;
        }
        catch (UriFormatException)
        {
            return;
        }

        if (syntheticFor is null && _mirror.Find(uri) is not null)
        {
            // The client owns this file's contents right now. Telling Roslyn to re-read it from
            // disk would discard the unsaved buffer the client already sent.
            return;
        }

        var isProjectFile = IsProjectFile(fullPath);

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_pendingKeys.Add(uri + " " + changeType))
            {
                _pending.Add(new FileChange(uri, changeType, isProjectFile));
            }

            _batchTimer ??= _time.CreateTimer(
                static x => ((FileWatchBridge) x!).Flush(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);

            _batchTimer.Change(BatchWindow, Timeout.InfiniteTimeSpan);
        }

        if (syntheticFor is null && NeedsProjectNudge(fullPath, changeType))
        {
            // C33: the only thing that makes Roslyn notice a .cs file that appeared or vanished.
            if (NearestProjectFile(fullPath, Root) is { } project)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    var file = Path.GetFileName(fullPath);
                    var name = Path.GetFileName(project);
                    Log.Nudging(_logger, file, name);
                }

                Enqueue(project, Changed, syntheticFor: fullPath);
            }
            else
            {
                Log.NoProjectFor(_logger, fullPath);
            }
        }
    }

    /// <summary>Sends the batch as one <c>workspace/didChangeWatchedFiles</c>.</summary>
    private void Flush()
    {
        FileChange[] batch;

        lock (_lock)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            batch = [.. _pending];
            _pending.Clear();
            _pendingKeys.Clear();
        }

        if (!_channel.BackendConnected)
        {
            // Nothing to tell. A relaunched Roslyn re-registers its watchers and reloads the
            // solution from disk anyway (D57), so the events are not lost, only redundant.
            return;
        }

        _channel.NotifyServer(BuildNotification(batch));
        Log.Sent(_logger, batch.Length);

        if (Array.Exists(batch, x => x.IsProjectFile))
        {
            ProjectFilesChanged?.Invoke();
        }
    }

    /// <summary>Renders the batch.</summary>
    private static byte[] BuildNotification(IReadOnlyList<FileChange> batch)
    {
        var buffer = new ArrayBufferWriter<byte>(64 + (batch.Count * 96));

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("changes"u8);
            writer.WriteStartArray();

            foreach (var change in batch)
            {
                writer.WriteStartObject();
                writer.WriteString("uri"u8, change.Uri);
                writer.WriteNumber("type"u8, change.Type);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsonRpcErrors.Notification("workspace/didChangeWatchedFiles", buffer.WrittenSpan);
    }

    /// <summary>Everything under the root matching one pattern, skipping the excluded directories.</summary>
    /// <remarks>
    /// Materialised rather than streamed: the enumeration is lazy, so a directory that vanishes
    /// mid-walk would throw at the <c>foreach</c> in the caller rather than here, and the caller is
    /// a recovery path that must not be able to fail.
    /// </remarks>
    private static List<string> EnumerateWorkspace(string root, string pattern)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        var found = new List<string>();

        try
        {
            foreach (var path in Directory.EnumerateFiles(root, pattern, options))
            {
                if (!IsExcluded(path))
                {
                    found.Add(path);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return found;
    }

    /// <summary>Closes every live watcher. Called under the lock.</summary>
    private void DisposeWatchers()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception exception) when (exception is ObjectDisposedException or IOException)
            {
            }
        }

        _watchers.Clear();
    }

    /// <summary>Whether a path is a build file whose change re-evaluates a project.</summary>
    private static bool IsProjectFile(string path)
    {
        var name = Path.GetFileName(path);

        return string.Equals(name, RestoreResultFile, StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Turns a watcher's <c>baseUri</c> into a local directory, or null when it is not one.</summary>
    private static string? BaseDirectoryOf(string baseUri)
    {
        if (baseUri.Length == 0)
        {
            return null;
        }

        if (!Uri.TryCreate(baseUri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(parsed.LocalPath).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The path relative to a base directory, or null when it is not under it.</summary>
    private static string? Relative(string baseDirectory, string fullPath)
    {
        try
        {
            var relative = Path.GetRelativePath(baseDirectory, fullPath);
            return relative.StartsWith("..", StringComparison.Ordinal) ? null : relative;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Whether a directory is the workspace root or below it.</summary>
    private static bool IsUnderRoot(string root, string directory) =>
        directory.StartsWith(root, StringComparison.OrdinalIgnoreCase);

    /// <summary>Expands the workspace root once, tolerating a client that sent nothing usable.</summary>
    private static string? NormaliseRoot(string? root)
    {
        if (root is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>One directory and the patterns registered against it.</summary>
    private sealed record WatchTarget(string Directory, IReadOnlyList<string> Patterns, bool Recursive);

    /// <summary>One change waiting to go out.</summary>
    private sealed record FileChange(string Uri, int Type, bool IsProjectFile);

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1400,
            Level = LogLevel.Information,
            Message = "Watching {Count} directory(ies) for Roslyn; {Outside} registration(s) were outside " +
                      "the workspace root and dropped (C32).")]
        internal static partial void Watching(ILogger logger, int count, int outside);

        [LoggerMessage(
            EventId = 1401,
            Level = LogLevel.Debug,
            Message = "Sent {Count} watched-file change(s) to Roslyn.")]
        internal static partial void Sent(ILogger logger, int count);

        [LoggerMessage(
            EventId = 1402,
            Level = LogLevel.Debug,
            Message = "{File} appeared or vanished; reporting {Project} as changed so Roslyn re-evaluates " +
                      "it (C33).")]
        internal static partial void Nudging(ILogger logger, string file, string project);

        [LoggerMessage(
            EventId = 1403,
            Level = LogLevel.Debug,
            Message = "{Path} has no project file above it inside the workspace, so nothing will pick it up.")]
        internal static partial void NoProjectFor(ILogger logger, string path);

        [LoggerMessage(
            EventId = 1404,
            Level = LogLevel.Warning,
            Message = "The filesystem watcher on {Directory} failed; every project file in the workspace " +
                      "is being reported as changed so nothing is left stale.")]
        internal static partial void WatcherError(ILogger logger, string directory, Exception exception);

        [LoggerMessage(
            EventId = 1405,
            Level = LogLevel.Warning,
            Message = "Could not watch {Directory} ({Reason}). On Linux this is usually the inotify " +
                      "instance or watch limit; raise fs.inotify.max_user_instances and " +
                      "fs.inotify.max_user_watches, or set CLAUDE_ROSLYN_LSP_FILE_WATCHER=off.")]
        internal static partial void WatcherRefused(ILogger logger, string directory, string reason);

        [LoggerMessage(
            EventId = 1406,
            Level = LogLevel.Information,
            Message = "Reported {Count} project file(s) as changed after a lost-event resynchronisation.")]
        internal static partial void Resynchronised(ILogger logger, int count);

        [LoggerMessage(
            EventId = 1407,
            Level = LogLevel.Warning,
            Message = "The client sent no workspace root, so nothing can be watched; a file created " +
                      "outside the editor will not join its project.")]
        internal static partial void NoRoot(ILogger logger);

        [LoggerMessage(
            EventId = 1409,
            Level = LogLevel.Information,
            Message = "The workspace still had not finished loading {Seconds:0} s after the watchers " +
                      "came up; reporting every project file as changed, because anything that " +
                      "happened on disk before then - a server-side restore, most likely - was never " +
                      "delivered to Roslyn and it may be waiting for exactly that (C48).")]
        internal static partial void CatchingUp(ILogger logger, double seconds);

        [LoggerMessage(
            EventId = 1408,
            Level = LogLevel.Information,
            Message = "Roslyn registered {Requested} distinct watch directories, more than the {Limit} " +
                      "this adapter opens individually; watching the workspace root recursively " +
                      "instead, which covers all of them for one handle.")]
        internal static partial void CollapsingToRoot(ILogger logger, int requested, int limit);
    }
}
