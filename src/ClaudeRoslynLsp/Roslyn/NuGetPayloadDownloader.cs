using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Roslyn;

/// <summary>How far a payload download has got. Reported every few megabytes, never per chunk.</summary>
/// <param name="RuntimeIdentifier">The RID being fetched.</param>
/// <param name="Version">The Roslyn version being fetched.</param>
/// <param name="BytesRead">Bytes received so far.</param>
/// <param name="TotalBytes">The declared length, or <see langword="null"/> if the server sent none.</param>
internal sealed record RoslynDownloadProgress(
    string RuntimeIdentifier,
    string Version,
    long BytesRead,
    long? TotalBytes)
{
    /// <summary>A human-readable one-liner, shared by <c>doctor</c>, <c>install</c> and the log.</summary>
    internal string Describe() =>
        TotalBytes is { } total and > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{RuntimeIdentifier} {BytesRead / (1024 * 1024)} MB of {total / (1024 * 1024)} MB ({BytesRead * 100 / total}%)")
            : string.Create(CultureInfo.InvariantCulture, $"{RuntimeIdentifier} {BytesRead / (1024 * 1024)} MB");
}

/// <summary>
/// Acquisition failed in a way the user has to know about: a hash that did not match, a package that
/// is not there, a lock that never came free.
/// </summary>
/// <remarks>
/// Distinct from <see cref="IOException"/> on purpose. Everything this type throws is a condition with
/// an explanation and usually a <c>doctor</c> hint attached, and the callers upstream turn it into a
/// live failure state rather than a crash — a server that dies during startup leaves its client with
/// no channel to be told why (D10).
/// </remarks>
internal sealed class RoslynAcquisitionException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong, phrased for a user.</param>
    internal RoslynAcquisitionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong, phrased for a user.</param>
    /// <param name="innerException">The underlying failure.</param>
    internal RoslynAcquisitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Downloads one <c>roslyn-language-server.&lt;rid&gt;</c> payload from nuget.org's flat container,
/// verifies it against the pin, and extracts it into the cache — exactly once, however many adapter
/// processes ask for it at the same moment.
/// </summary>
/// <remarks>
/// <para>
/// <b>D28 — hash while streaming, extract to a staging directory, rename last.</b> The bytes are
/// hashed on the way to disk rather than by re-reading the file, because re-reading is a second
/// chance for the file to be different from the one that was checked. Extraction goes into a sibling
/// staging directory and is promoted with a single <see cref="Directory.Move(string, string)"/>, so
/// the cache directory either does not exist or is complete; a half-extracted directory that looks
/// finished is the failure mode that turns one bad network moment into a permanently broken install
/// which only a manual delete fixes.
/// </para>
/// <para>
/// <b>D29 — a lock file, not a mutex.</b> Claude Code starts the LSP server and the MCP server as two
/// processes at the same instant (D23 has not landed), and on a first run both would fetch the same
/// 70 MB. A named mutex would be the obvious answer and is the wrong one: its name is per-session on
/// Windows and it does not exist at all across containers sharing a mounted cache. A file opened with
/// <see cref="FileShare.None"/> is understood by every platform and by every filesystem that can host
/// the cache in the first place, and the loser of the race re-checks the cache and finds it warm.
/// </para>
/// <para>
/// <b>D30 — everything under <c>tools/net10.0/&lt;rid&gt;/</c>, and nothing outside it.</b> The
/// payload's other directories are NuGet metadata; the server's own directory is 163 files plus the
/// two <c>BuildHost-*</c> directories, <c>Targets/</c> and thirteen satellite-resource folders (C1),
/// and the build hosts are what evaluate project files. Every entry is checked against the destination
/// root before it is opened, because a zip is an archive of names somebody else chose.
/// </para>
/// </remarks>
internal sealed partial class NuGetPayloadDownloader
{
    /// <summary>How many bytes pass between progress reports.</summary>
    /// <remarks>
    /// Five megabytes is roughly a report a second on a domestic connection: often enough that a
    /// stalled download is visibly stalled, rare enough that the report itself is not the output.
    /// </remarks>
    internal const int ProgressIntervalBytes = 5 * 1024 * 1024;

    /// <summary>How long a second process waits for the first one's download.</summary>
    /// <remarks>
    /// Ten minutes is the pessimistic end of a 70 MB download, not a guess at a deadlock. If the
    /// holder died without releasing, the operating system dropped the handle and the wait ends on the
    /// next poll rather than at the timeout.
    /// </remarks>
    internal static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _client;
    private readonly AdapterPaths _paths;
    private readonly ILogger _logger;
    private readonly TimeSpan _lockTimeout;

    /// <summary>Creates a downloader.</summary>
    /// <param name="client">The client to fetch with. Not owned or disposed here.</param>
    /// <param name="paths">Where the cache, the staging area and the lock files live.</param>
    /// <param name="logger">Where progress and failures go. Never stdout.</param>
    /// <param name="lockTimeout">How long to wait for a concurrent download; the default is ten minutes.</param>
    internal NuGetPayloadDownloader(
        HttpClient client,
        AdapterPaths paths,
        ILogger logger,
        TimeSpan? lockTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _paths = paths;
        _logger = logger;
        _lockTimeout = lockTimeout ?? DefaultLockTimeout;
    }

    /// <summary>
    /// Builds the client acquisition uses: system proxy honoured, a real user agent, and a timeout
    /// long enough for the payload rather than for an API call.
    /// </summary>
    /// <remarks>
    /// The default handler already resolves the system proxy, which is the entire reason nothing here
    /// configures one: a corporate machine's <c>HTTPS_PROXY</c> is picked up without this repository
    /// having an opinion about proxy configuration. The user agent is set because nuget.org's front
    /// door has been known to answer a request without one before it looks at anything else — the
    /// publish path in <c>build/Build.Publish.cs</c> pays for the same lesson at the token endpoint.
    /// </remarks>
    internal static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            // 100 seconds — the default — is an API-call timeout, and this is a 70 MB body. The
            // clock covers the content reads as well as the response, so it has to bound the whole
            // transfer; cancellation is what stops it early.
            Timeout = TimeSpan.FromMinutes(20),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            ServerVersion.Name + "/" + ServerVersion.Value);

        return client;
    }

    /// <summary>
    /// Makes sure the payload for one version and RID is in the cache, downloading it if it is not,
    /// and returns the directory it lives in.
    /// </summary>
    /// <param name="runtimeIdentifier">One of <see cref="RuntimeIdentifier.All"/>.</param>
    /// <param name="version">The version to acquire — the pin unless overridden.</param>
    /// <param name="expectedSha512">
    /// The base64 SHA-512 the payload must hash to, or <see langword="null"/> for a version the pin
    /// knows nothing about. Null means the download is verified by TLS alone, which the caller reports
    /// as unverified rather than treating as equivalent.
    /// </param>
    /// <param name="progress">Receives a report every <see cref="ProgressIntervalBytes"/> bytes.</param>
    /// <param name="cancellationToken">Cancels the download or the wait for the lock.</param>
    /// <returns>The absolute path of the directory holding <c>Microsoft.CodeAnalysis.LanguageServer.dll</c>.</returns>
    internal async Task<string> EnsureAsync(
        string runtimeIdentifier,
        string version,
        string? expectedSha512,
        IProgress<RoslynDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var target = _paths.ServerDirectory(version, runtimeIdentifier);

        if (IsComplete(version, runtimeIdentifier, expectedSha512))
        {
            return target;
        }

        Directory.CreateDirectory(Path.Combine(_paths.RoslynCacheRoot, version));

        using var acquisitionLock = await AcquireLockAsync(version, runtimeIdentifier, cancellationToken)
            .ConfigureAwait(false);

        // Re-check under the lock: the process we queued behind was almost certainly downloading the
        // very thing we are about to download.
        if (IsComplete(version, runtimeIdentifier, expectedSha512))
        {
            Log.CacheWarmedByAnotherProcess(_logger, runtimeIdentifier, version);
            return target;
        }

        await DownloadAndExtractAsync(runtimeIdentifier, version, expectedSha512, target, progress, cancellationToken)
            .ConfigureAwait(false);

        return target;
    }

    /// <summary>
    /// Whether the cache holds a finished payload — the marker exists, it agrees with the expected
    /// hash, and the server assembly is actually there.
    /// </summary>
    /// <param name="version">The Roslyn version.</param>
    /// <param name="runtimeIdentifier">The RID.</param>
    /// <param name="expectedSha512">The pin's hash, or <see langword="null"/> when unknown.</param>
    internal bool IsComplete(string version, string runtimeIdentifier, string? expectedSha512)
    {
        var marker = _paths.CompleteMarker(version, runtimeIdentifier);

        if (!File.Exists(marker))
        {
            return false;
        }

        if (!File.Exists(Path.Combine(
                _paths.ServerDirectory(version, runtimeIdentifier),
                RoslynServerManifest.ServerAssemblyName)))
        {
            return false;
        }

        if (expectedSha512 is null)
        {
            return true;
        }

        // A marker written under a different hash means the pin moved while this cache entry stayed
        // put — a rebuilt package under the same version, or a hand-edited manifest. Re-acquiring is
        // cheap next to serving a payload nobody can attest to.
        string recorded;

        try
        {
            recorded = File.ReadAllText(marker).Trim();
        }
        catch (IOException)
        {
            return false;
        }

        return string.Equals(recorded, expectedSha512, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects an archive entry whose name would put it outside <paramref name="destinationRoot"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A zip entry name is attacker-controlled data even when the archive came from nuget.org over
    /// TLS, and this code runs before the hash has been checked in exactly one case — never, by
    /// construction, because extraction happens after verification. It is still guarded: "the caller
    /// checks first" is not a property a reviewer can see from here, and the cost is one comparison
    /// per entry.
    /// </para>
    /// <para>
    /// Four separate shapes have to be refused, and each one is a real CVE somewhere: a <c>..</c>
    /// segment, a rooted path, a Windows drive qualifier, and a backslash used as a separator by an
    /// archive written on Windows (the zip specification says forward slashes, so a backslash is
    /// either a literal filename character or an attempt to be interpreted differently on two
    /// platforms). The final full-path comparison catches everything the shape checks miss.
    /// </para>
    /// </remarks>
    /// <param name="entryName">The archive entry's full name.</param>
    /// <param name="relativePath">The entry's path relative to the payload prefix.</param>
    /// <param name="destinationRoot">The fully-qualified directory being extracted into.</param>
    /// <param name="destinationPath">The verified absolute destination.</param>
    /// <returns><see langword="true"/> when the entry is safe to write.</returns>
    internal static bool TryResolveEntryPath(
        string entryName,
        string relativePath,
        string destinationRoot,
        [NotNullWhen(true)] out string? destinationPath)
    {
        destinationPath = null;

        if (relativePath.Length == 0
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath)
            || relativePath.StartsWith('/')
            || relativePath.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = relativePath.Split('/');

        if (segments.Any(static segment => segment is ".."))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(destinationRoot, Path.Combine(segments)));
        var root = destinationRoot.EndsWith(Path.DirectorySeparatorChar)
            ? destinationRoot
            : destinationRoot + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(root, StringComparison.Ordinal))
        {
            return false;
        }

        // entryName is carried only so the failure message can name what was refused; using it for
        // anything else would defeat the point of validating the relative form.
        _ = entryName;
        destinationPath = candidate;
        return true;
    }

    /// <summary>
    /// Extracts every entry under <paramref name="payloadPrefix"/> into
    /// <paramref name="destinationRoot"/>, stripping the prefix.
    /// </summary>
    /// <param name="archive">The opened package.</param>
    /// <param name="payloadPrefix">The prefix to take and to strip, with its trailing slash.</param>
    /// <param name="destinationRoot">A fully-qualified, existing directory.</param>
    /// <returns>How many files were written.</returns>
    /// <exception cref="RoslynAcquisitionException">An entry would escape the destination.</exception>
    internal static int ExtractPayload(ZipArchive archive, string payloadPrefix, string destinationRoot)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var written = 0;

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(payloadPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = entry.FullName[payloadPrefix.Length..];

            // A directory entry — zero length and a trailing slash. The directories that matter are
            // created from the file paths, so these carry no information worth acting on.
            if (relative.Length == 0 || relative.EndsWith('/'))
            {
                continue;
            }

            if (!TryResolveEntryPath(entry.FullName, relative, destinationRoot, out var destination))
            {
                throw new RoslynAcquisitionException(
                    $"The package contains an entry that would be written outside the cache directory: '{entry.FullName}'. " +
                    "Nothing was extracted.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            written++;
        }

        return written;
    }

    private async Task DownloadAndExtractAsync(
        string runtimeIdentifier,
        string version,
        string? expectedSha512,
        string target,
        IProgress<RoslynDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var url = RoslynServerManifest.PackageUrl(runtimeIdentifier, version);

        Directory.CreateDirectory(_paths.TempDirectory);

        var nupkg = Path.Combine(_paths.TempDirectory, Guid.NewGuid().ToString("N") + ".nupkg");
        var staging = _paths.ServerDirectory(version, runtimeIdentifier) + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            Log.Downloading(_logger, runtimeIdentifier, version, url.AbsoluteUri);

            var actual = await DownloadAsync(url, nupkg, runtimeIdentifier, version, progress, cancellationToken)
                .ConfigureAwait(false);

            if (expectedSha512 is not null && !string.Equals(actual, expectedSha512, StringComparison.Ordinal))
            {
                throw new RoslynAcquisitionException(
                    $"The downloaded payload for {runtimeIdentifier} does not match the pin. Expected SHA-512 " +
                    $"'{expectedSha512}', got '{actual}'. Nothing was installed. If the pin is stale, run " +
                    "`dotnet fallout UpdateRoslynPin`; otherwise treat this as a compromised download.");
            }

            Directory.CreateDirectory(staging);

            using (var package = ZipFile.OpenRead(nupkg))
            {
                var files = ExtractPayload(package, RoslynServerManifest.PayloadPrefix(runtimeIdentifier), staging);

                if (!File.Exists(Path.Combine(staging, RoslynServerManifest.ServerAssemblyName)))
                {
                    throw new RoslynAcquisitionException(
                        $"The payload for {runtimeIdentifier} {version} does not contain " +
                        $"{RoslynServerManifest.PayloadPrefix(runtimeIdentifier)}{RoslynServerManifest.ServerAssemblyName} " +
                        $"({files} files extracted). This is not a roslyn-language-server package.");
                }

                Log.Extracted(_logger, files, runtimeIdentifier, version);
            }

            File.WriteAllText(Path.Combine(staging, ".complete"), actual);
            Promote(staging, target);
        }
        catch (HttpRequestException exception)
        {
            throw new RoslynAcquisitionException(
                $"Could not download {RoslynServerManifest.PackageId(runtimeIdentifier)} {version} from nuget.org: " +
                $"{exception.Message}", exception);
        }
        finally
        {
            TryDeleteFile(nupkg);
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// Streams the package to disk, hashing as it goes, and returns the base64 SHA-512 of what
    /// arrived.
    /// </summary>
    private async Task<string> DownloadAsync(
        Uri url,
        string nupkg,
        string runtimeIdentifier,
        string version,
        IProgress<RoslynDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = new byte[81920];
        long read = 0;
        long reported = 0;

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var destination = new FileStream(
            nupkg, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
        {
            int count;

            while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, count));
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                read += count;

                if (read - reported < ProgressIntervalBytes)
                {
                    continue;
                }

                reported = read;
                progress?.Report(new RoslynDownloadProgress(runtimeIdentifier, version, read, total));
            }
        }

        progress?.Report(new RoslynDownloadProgress(runtimeIdentifier, version, read, total));

        return Convert.ToBase64String(hash.GetHashAndReset());
    }

    /// <summary>
    /// Moves a finished staging directory into place, tolerating the one race the lock cannot cover.
    /// </summary>
    /// <remarks>
    /// The lock file serialises the processes that agree on where the cache is. Two adapters with
    /// different <c>CLAUDE_ROSLYN_LSP_HOME</c> values pointed at one shared directory — a container
    /// bind mount is the realistic case — do not, so a target that appeared while this one was
    /// extracting is treated as somebody else's finished work rather than as an error.
    /// </remarks>
    private static void Promote(string staging, string target)
    {
        try
        {
            Directory.Move(staging, target);
        }
        catch (IOException) when (Directory.Exists(target))
        {
        }
    }

    private async Task<FileStream> AcquireLockAsync(
        string version,
        string runtimeIdentifier,
        CancellationToken cancellationToken)
    {
        var path = _paths.LockFile(version, runtimeIdentifier);
        var deadline = DateTimeOffset.UtcNow + _lockTimeout;
        var logged = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new RoslynAcquisitionException(
                        $"Timed out after {_lockTimeout.TotalMinutes:F0} minutes waiting for another " +
                        $"claude-roslyn-lsp process to finish downloading Roslyn {version} for {runtimeIdentifier}. " +
                        $"If no such process exists, delete '{path}'.");
                }

                if (!logged)
                {
                    logged = true;
                    Log.WaitingForLock(_logger, runtimeIdentifier, version, path);
                }

                await Task.Delay(LockPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Source-generated log records; see the note in <c>LspStubServer</c> for why (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 100,
            Level = LogLevel.Information,
            Message = "Downloading roslyn-language-server {RuntimeIdentifier} {Version} from {Url}.")]
        internal static partial void Downloading(ILogger logger, string runtimeIdentifier, string version, string url);

        [LoggerMessage(
            EventId = 101,
            Level = LogLevel.Information,
            Message = "Extracted {FileCount} files for {RuntimeIdentifier} {Version}.")]
        internal static partial void Extracted(ILogger logger, int fileCount, string runtimeIdentifier, string version);

        [LoggerMessage(
            EventId = 102,
            Level = LogLevel.Information,
            Message = "Another process is downloading {RuntimeIdentifier} {Version}; waiting on {LockFile}.")]
        internal static partial void WaitingForLock(
            ILogger logger,
            string runtimeIdentifier,
            string version,
            string lockFile);

        [LoggerMessage(
            EventId = 103,
            Level = LogLevel.Information,
            Message = "{RuntimeIdentifier} {Version} was downloaded by another process while this one waited.")]
        internal static partial void CacheWarmedByAnotherProcess(
            ILogger logger,
            string runtimeIdentifier,
            string version);
    }
}
