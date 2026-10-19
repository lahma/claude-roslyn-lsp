using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter.Sharing;

/// <summary>
/// The <c>&lt;home&gt;/sessions</c> directory: who is hosting which solution, and the lock that
/// stops two processes deciding they both are.
/// </summary>
/// <remarks>
/// <para>
/// <b>D86 — a lock file, and the winner publishes before it launches.</b> Claude Code starts the
/// <c>mcp</c> and <c>lsp</c> servers at the same instant, so "read the file, and write it if it is
/// missing" is a race both processes win. The lock is the same primitive D29 already chose for the
/// download, for the same reasons: a named mutex is per-session on Windows and does not exist across
/// containers sharing a mounted home, whereas a file opened <see cref="FileShare.None"/> is
/// understood by every platform that can host the directory.
/// </para>
/// <para>
/// What is deliberately <em>not</em> inside the lock is the launch. The winner starts its pipe
/// server, writes this file and releases the lock in milliseconds; acquiring and starting Roslyn —
/// which on a first run is a 70 MB download — happens afterwards, with the loser already attached
/// and its requests held by the host's readiness gate (D46). Holding the lock across the launch
/// would make the loser wait out the whole cold start before it could even find out where to
/// connect.
/// </para>
/// <para>
/// <b>The pid check is a filter, not a proof.</b> A pid can be reused, and a file naming a live
/// process that is not this adapter would pass it. That is fine because it is not the last check: a
/// connect that fails deletes the file and hosts instead (D88), so the worst case of a reused pid is
/// one extra connect attempt.
/// </para>
/// </remarks>
internal sealed partial class SessionRegistry
{
    /// <summary>The directory under the adapter's home that holds them.</summary>
    internal const string DirectoryName = "sessions";

    /// <summary>
    /// How old an unreadable session file or a leftover temporary file has to be before it is
    /// deleted (D103).
    /// </summary>
    /// <remarks>
    /// Ten minutes. Every write this build makes is atomic, so an unreadable file is either one an
    /// older build was in the middle of writing or genuine debris — and the first kind is gone within
    /// milliseconds. Waiting is what keeps a reader from deleting a live host's record because it
    /// happened to catch an older build's non-atomic write half done.
    /// </remarks>
    internal static readonly TimeSpan DebrisAge = TimeSpan.FromMinutes(10);

    /// <summary>The extension of the temporary file a write goes through.</summary>
    private const string TemporaryExtension = ".tmp";

    private readonly string _directory;
    private readonly ILogger _logger;

    /// <summary>Creates a registry over one directory.</summary>
    /// <param name="directory">The <c>sessions</c> directory. It is created on first write.</param>
    /// <param name="logger">The stderr log.</param>
    internal SessionRegistry(string directory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(logger);

        _directory = directory;
        _logger = logger;
    }

    /// <summary>The directory this registry reads and writes.</summary>
    internal string Directory_ => _directory;

    /// <summary>Creates a registry under an adapter home.</summary>
    /// <param name="home">The adapter's home directory (D27).</param>
    /// <param name="logger">The stderr log.</param>
    internal static SessionRegistry ForHome(string home, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(home);

        return new SessionRegistry(Path.Combine(home, DirectoryName), logger);
    }

    /// <summary>
    /// The file name one solution's session lives under: the first sixteen hex characters of the
    /// SHA-256 of its normalised path.
    /// </summary>
    /// <remarks>
    /// Hashed rather than escaped because the path is somebody else's and may contain anything a
    /// file system allows, including characters this one does not; sixteen characters because the
    /// key is a rendezvous between two processes on one machine rather than a security boundary, and
    /// a name a human can still copy out of an error message is worth more here than the other
    /// forty-eight. Normalisation matches the rule the file systems themselves use: case-insensitive
    /// on Windows and macOS, case-sensitive elsewhere (the same rule as
    /// <see cref="Edits.WorkspacePathGuard"/>).
    /// </remarks>
    /// <param name="solutionPath">The solution or project file the session has open.</param>
    internal static string KeyFor(string solutionPath)
    {
        ArgumentNullException.ThrowIfNull(solutionPath);

        var normalised = Normalise(solutionPath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));

        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>The session file for one key.</summary>
    /// <param name="key">The key from <see cref="KeyFor"/>.</param>
    internal string FileFor(string key) => Path.Combine(_directory, key + ".json");

    /// <summary>The lock file for one key.</summary>
    /// <param name="key">The key from <see cref="KeyFor"/>.</param>
    internal string LockFor(string key) => Path.Combine(_directory, key + ".lock");

    /// <summary>
    /// Reads one session, returning it only when a process of this version is still behind it.
    /// </summary>
    /// <remarks>
    /// A file that fails any of the three checks is <em>deleted</em> rather than merely ignored, so
    /// the next process to look does not have to repeat the same work — and so a home directory does
    /// not accumulate one file per solution per reboot. The one exception is a file that could not be
    /// read at all and is younger than <see cref="DebrisAge"/>: that may be a live host's record
    /// caught mid-write by a build that did not write atomically, and the caller simply hosts in its
    /// place and overwrites it.
    /// </remarks>
    /// <param name="key">The key from <see cref="KeyFor"/>.</param>
    internal SharedSession? TryRead(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var report = Inspect(key);

        if (report.Usable)
        {
            return report.Session;
        }

        if (report.Session is not null || (File.Exists(report.Path) && IsDebris(report.Path)))
        {
            Log.Stale(_logger, report.Path, report.Verdict);
            Delete(key);
        }

        return null;
    }

    /// <summary>Reads one session and says what is wrong with it, without deleting anything.</summary>
    /// <param name="key">The key from <see cref="KeyFor"/>.</param>
    internal SharedSessionReport Inspect(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var path = FileFor(key);
        var session = TryParse(path);

        if (session is null)
        {
            return new SharedSessionReport(key, path, null, Alive: false, VersionMatches: false);
        }

        return new SharedSessionReport(
            key,
            path,
            session,
            IsAlive(session.ProcessId),
            string.Equals(session.Version, ServerVersion.Value, StringComparison.Ordinal));
    }

    /// <summary>Every session file in the directory, for <c>doctor</c>.</summary>
    internal IReadOnlyList<SharedSessionReport> List()
    {
        string[] files;

        try
        {
            files = Directory.Exists(_directory) ? Directory.GetFiles(_directory, "*.json") : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Unreadable(_logger, _directory, exception.Message);
            return [];
        }

        Array.Sort(files, StringComparer.Ordinal);

        var reports = new List<SharedSessionReport>(files.Length);

        foreach (var file in files)
        {
            // Checked by hand rather than trusted to the pattern: a session file is exactly
            // `<key>.json`, and nothing else in this directory — a write's temporary file above all —
            // may ever be mistaken for one.
            if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            reports.Add(Inspect(Path.GetFileNameWithoutExtension(file)));
        }

        return reports;
    }

    /// <summary>Publishes this process as the host of one solution.</summary>
    /// <param name="key">The key from <see cref="KeyFor"/>.</param>
    /// <param name="session">What to write.</param>
    /// <returns>Whether the file was written; a failure is logged and is not fatal.</returns>
    internal bool Write(string key, SharedSession session)
    {
        var path = FileFor(key);

        if (!WriteAtomically(key, session, out var failure))
        {
            // Not fatal: a process that cannot publish itself simply keeps its Roslyn private, which
            // is what every version before this one did.
            Log.NotPublished(_logger, path, failure!);
            return false;
        }

        Log.Published(_logger, path, session.PipeName);
        return true;
    }

    /// <summary>
    /// Rewrites a published session with what has changed since — the backend's state, its memory,
    /// its recycles, how many clients are attached (D102).
    /// </summary>
    /// <remarks>
    /// The same atomic write as <see cref="Write"/>, logged quietly: it happens on every state change
    /// and once a minute while clients are active, and a failure costs only a stale line in
    /// <c>doctor</c>.
    /// </remarks>
    /// <param name="key">The key from <see cref="KeyFor"/>.</param>
    /// <param name="session">What to write.</param>
    internal bool Update(string key, SharedSession session)
    {
        var path = FileFor(key);

        if (!WriteAtomically(key, session, out var failure))
        {
            Log.NotUpdated(_logger, path, failure!);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Writes a session file so that a reader sees either the old content or the new, never a mix
    /// (D102).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Atomic, because the file is now rewritten while it is live.</b> It used to be written once,
    /// at publish, before anybody could be reading it; with the backend's state in it, it is rewritten
    /// on every change, and <see cref="TryRead"/> deletes what it cannot parse. A torn read would
    /// therefore make an attaching process delete a live host's record and host a second Roslyn beside
    /// it — exactly what the file exists to prevent. So the bytes go to a temporary file in the same
    /// directory, which a rename can only replace whole, and then over the real name.
    /// </para>
    /// <para>
    /// The rename is retried twice, briefly. On Windows replacing a file fails while another process
    /// holds it open without delete sharing — which this build's own readers no longer do, but a
    /// reader from an earlier one does for the instant of its read.
    /// </para>
    /// </remarks>
    private bool WriteAtomically(string key, SharedSession session, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(session);

        var path = FileFor(key);
        var temporary = Path.Combine(
            _directory,
            string.Create(CultureInfo.InvariantCulture, $"{key}.{Guid.NewGuid():N}{TemporaryExtension}"));

        try
        {
            Directory.CreateDirectory(_directory);

            File.WriteAllBytes(
                temporary,
                JsonSerializer.SerializeToUtf8Bytes(session, SharedSessionJsonContext.Default.SharedSession));

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    failure = null;
                    return true;
                }
                catch (Exception exception) when (attempt < 3 && exception is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(25);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or NotSupportedException)
        {
            TryDelete(temporary);
            failure = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Removes what nobody will ever use again: stale session files, orphaned locks and leftover
    /// temporary files (D103).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a sweep at all.</b> A stale session file used to be removed only by a process that
    /// happened to look up that same solution, and a lock file was never removed by anyone — which is
    /// how one machine came to hold 287 files for 17 live hosts. So the sweep runs whenever a process
    /// becomes a host, and from <c>doctor --fix</c>.
    /// </para>
    /// <para>
    /// Three rules, each of them unable to touch something live. A session file goes when its verdict
    /// names a process that is gone — a live host of another version keeps it — or when it cannot be read and is older
    /// than <see cref="DebrisAge"/>. A lock file goes only when there is no session file beside it
    /// <em>and</em> it can be opened <see cref="FileShare.None"/>; it is deleted through that same
    /// handle with <see cref="FileOptions.DeleteOnClose"/>, so a lock some process holds is never
    /// removed — it cannot be opened in the first place. A temporary file goes when it is older than
    /// <see cref="DebrisAge"/>, because a write that is still going on takes milliseconds.
    /// </para>
    /// <para>
    /// <paramref name="apply"/> false counts and deletes nothing, which is what plain <c>doctor</c>
    /// reports (D94's rule that a diagnostic which destroys things is one people stop running).
    /// </para>
    /// </remarks>
    /// <param name="apply">Whether to delete, or only to count.</param>
    internal SessionSweep Sweep(bool apply)
    {
        if (!Directory.Exists(_directory))
        {
            return default;
        }

        var sessions = 0;
        var locks = 0;
        var temporaries = 0;

        try
        {
            foreach (var report in List())
            {
                // Gone, not merely unusable to this build: a live host of another version is
                // the rendezvous its own version's other half attaches through, and during a
                // plugin upgrade both versions run side by side. Deleting its file would make
                // that half launch a second Roslyn for the same solution (D103).
                var stale = report.Session is null ? IsDebris(report.Path) : !report.Alive;

                if (!stale)
                {
                    continue;
                }

                sessions++;

                if (apply)
                {
                    Delete(report.Key);
                }
            }

            foreach (var lockFile in Directory.GetFiles(_directory, "*.lock"))
            {
                if (File.Exists(FileFor(Path.GetFileNameWithoutExtension(lockFile))) || !TryClaimLock(lockFile, apply))
                {
                    continue;
                }

                locks++;
            }

            foreach (var temporary in Directory.GetFiles(_directory, "*" + TemporaryExtension))
            {
                if (!IsDebris(temporary))
                {
                    continue;
                }

                temporaries++;

                if (apply)
                {
                    TryDelete(temporary);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Unreadable(_logger, _directory, exception.Message);
        }

        var sweep = new SessionSweep(sessions, locks, temporaries);

        if (apply && sweep.Total > 0)
        {
            Log.Swept(_logger, sessions, locks, temporaries);
        }

        return sweep;
    }

    /// <summary>
    /// Opens a lock file exclusively, which proves nobody holds it, and deletes it through that handle
    /// when <paramref name="delete"/> says so.
    /// </summary>
    private static bool TryClaimLock(string path, bool delete)
    {
        try
        {
            using var claimed = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                delete ? FileOptions.DeleteOnClose : FileOptions.None);

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Held by a process deciding whether to host right now, or gone already. Either way it
            // is not this sweep's to remove.
            return false;
        }
    }

    /// <summary>Whether a file is old enough that nothing can still be in the middle of writing it.</summary>
    private static bool IsDebris(string path)
    {
        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > DebrisAge;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Deletes a file, tolerating it having gone already.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes a session file, tolerating it having gone already.</summary>
    /// <param name="key">The key from <see cref="KeyFor"/>.</param>
    internal void Delete(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var path = FileFor(key);

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.NotDeleted(_logger, path, exception.Message);
        }
    }

    /// <summary>
    /// Takes the decision lock for one key, or returns null when another process already has it.
    /// </summary>
    /// <remarks>
    /// Non-blocking on purpose. The caller's loop is "attach, else try the lock, else wait a moment
    /// and look again", and a blocking acquire would turn the loser into a process that cannot even
    /// re-read the file the winner is about to write.
    /// </remarks>
    /// <param name="key">The key from <see cref="KeyFor"/>.</param>
    internal IDisposable? TryAcquireLock(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        try
        {
            Directory.CreateDirectory(_directory);

            return new FileStream(
                LockFor(key),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException)
        {
            // Somebody else holds it. Not an error: it is the answer this method exists to give.
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            Log.NoLock(_logger, LockFor(key), exception.Message);
            return null;
        }
    }

    /// <summary>Whether a process id still names a running process.</summary>
    private static bool IsAlive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        if (processId == Environment.ProcessId)
        {
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                              or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Reads and parses one session file, treating anything unreadable as absent.</summary>
    private SharedSession? TryParse(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // Shared for writing and deleting too, so a host replacing this file while it is read here
            // is not refused by this read (D102) — the reader sees the old bytes or the new, never a
            // failed rename on the other side.
            byte[] bytes;

            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       bufferSize: 1))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }

            var session = JsonSerializer.Deserialize(bytes, SharedSessionJsonContext.Default.SharedSession);

            return session is { PipeName.Length: > 0, Version.Length: > 0 } ? session : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or JsonException or NotSupportedException)
        {
            Log.Unreadable(_logger, path, exception.Message);
            return null;
        }
    }

    /// <summary>Expands a path and folds its case where the file system would.</summary>
    private static string Normalise(string path)
    {
        string expanded;

        try
        {
            expanded = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException
                                              or NotSupportedException)
        {
            expanded = path;
        }

        expanded = expanded.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? expanded.ToLower(CultureInfo.InvariantCulture)
            : expanded;
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 5300,
            Level = LogLevel.Information,
            Message = "Published this process as the shared Roslyn host in {Path}, serving pipe {PipeName}.")]
        internal static partial void Published(ILogger logger, string path, string pipeName);

        [LoggerMessage(
            EventId = 5301,
            Level = LogLevel.Debug,
            Message = "Discarding the session file {Path}: {Verdict}.")]
        internal static partial void Stale(ILogger logger, string path, string verdict);

        [LoggerMessage(
            EventId = 5302,
            Level = LogLevel.Debug,
            Message = "The session file {Path} could not be read ({Reason}); treating it as absent.")]
        internal static partial void Unreadable(ILogger logger, string path, string reason);

        [LoggerMessage(
            EventId = 5303,
            Level = LogLevel.Warning,
            Message = "This process could not publish itself as the shared Roslyn host ({Path}: {Reason}); " +
                      "it will keep its own Roslyn instead of sharing one.")]
        internal static partial void NotPublished(ILogger logger, string path, string reason);

        [LoggerMessage(
            EventId = 5304,
            Level = LogLevel.Debug,
            Message = "The session file {Path} could not be deleted ({Reason}).")]
        internal static partial void NotDeleted(ILogger logger, string path, string reason);

        [LoggerMessage(
            EventId = 5305,
            Level = LogLevel.Warning,
            Message = "The session lock {Path} could not be opened ({Reason}); this process will not " +
                      "become the shared host.")]
        internal static partial void NoLock(ILogger logger, string path, string reason);

        [LoggerMessage(
            EventId = 5306,
            Level = LogLevel.Debug,
            Message = "The session file {Path} could not be rewritten ({Reason}); it keeps what it said before.")]
        internal static partial void NotUpdated(ILogger logger, string path, string reason);

        [LoggerMessage(
            EventId = 5307,
            Level = LogLevel.Information,
            Message = "Swept the session directory: {Sessions} stale session file(s), {Locks} orphaned " +
                      "lock(s) and {Temporaries} leftover temporary file(s) removed (D103).")]
        internal static partial void Swept(ILogger logger, int sessions, int locks, int temporaries);
    }
}

/// <summary>What a sweep of the session directory removed, or would remove (D103).</summary>
/// <param name="Sessions">Stale or unreadable session files.</param>
/// <param name="Locks">Lock files with no session beside them and nobody holding them.</param>
/// <param name="Temporaries">Temporary files a write left behind.</param>
internal readonly record struct SessionSweep(int Sessions, int Locks, int Temporaries)
{
    /// <summary>Everything together.</summary>
    internal int Total => Sessions + Locks + Temporaries;
}
