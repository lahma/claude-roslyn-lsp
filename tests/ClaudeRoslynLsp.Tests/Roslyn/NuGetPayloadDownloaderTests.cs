using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Roslyn;
using ClaudeRoslynLsp.Tests.Http;

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Roslyn;

/// <summary>
/// Covers acquisition against a hand-built <c>.nupkg</c> served by a stub handler: what gets
/// extracted, what happens when the hash does not match, what happens when the archive tries to
/// write outside the cache, and what a second process does while the first one downloads.
/// </summary>
/// <remarks>
/// The package is built rather than captured because every property under test is about the
/// <em>shape</em> of a package — a prefix to strip, an entry to refuse — and a 70 MB fixture would
/// make each of those cases a 70 MB file. The real payload's layout is asserted once, live, by
/// <c>RoslynLaunchLiveTests</c>.
/// </remarks>
public class NuGetPayloadDownloaderTests
{
    private const string Rid = "win-x64";
    private const string Version = "5.12.0-1.26426.8";
    private const string ServerAssembly = RoslynServerManifest.ServerAssemblyName;

    /// <summary>The test's own cancellation token, so that cancelling the test reaches a hung await.</summary>
    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    [Test]
    public async Task AMatchingHashExtractsThePayloadAndWritesTheMarker()
    {
        using var temp = TempWorkspace.Create("download-ok");
        var (paths, handler, downloader) = Arrange(temp);

        var package = BuildPackage(
            ($"tools/net10.0/{Rid}/{ServerAssembly}", "server"),
            ($"tools/net10.0/{Rid}/BuildHost-netcore/BuildHost.dll", "build host"),
            ($"tools/net10.0/{Rid}/cs/Resources.dll", "satellite"),
            ("[Content_Types].xml", "<Types/>"),
            ($"roslyn-language-server.{Rid}.nuspec", "<package/>"),
            ($"tools/net10.0/linux-x64/{ServerAssembly}", "wrong rid"));

        var hash = Sha512(package);
        handler.EnqueueBytes(package);

        var directory = await downloader.EnsureAsync(Rid, Version, hash, progress: null, cancellationToken: Ct);

        Assert.That(directory, Is.EqualTo(paths.ServerDirectory(Version, Rid)));
        Assert.That(File.Exists(Path.Combine(directory, ServerAssembly)), Is.True);

        // The whole subtree, not only the top level: the build hosts are what evaluate project files.
        Assert.That(File.Exists(Path.Combine(directory, "BuildHost-netcore", "BuildHost.dll")), Is.True);
        Assert.That(File.Exists(Path.Combine(directory, "cs", "Resources.dll")), Is.True);

        // And nothing from outside tools/net10.0/<rid>/.
        Assert.That(File.Exists(Path.Combine(directory, "[Content_Types].xml")), Is.False);
        Assert.That(Directory.Exists(Path.Combine(directory, "linux-x64")), Is.False);

        Assert.That(File.ReadAllText(paths.CompleteMarker(Version, Rid)), Is.EqualTo(hash));

        // The staged download is not left behind.
        Assert.That(Directory.GetFiles(paths.TempDirectory, "*.nupkg"), Is.Empty);
    }

    [Test]
    public async Task ASecondCallServesTheCacheWithoutFetchingAnything()
    {
        using var temp = TempWorkspace.Create("download-cached");
        var (_, handler, downloader) = Arrange(temp);

        var package = BuildPackage(($"tools/net10.0/{Rid}/{ServerAssembly}", "server"));
        var hash = Sha512(package);
        handler.EnqueueBytes(package);

        await downloader.EnsureAsync(Rid, Version, hash, progress: null, cancellationToken: Ct);
        await downloader.EnsureAsync(Rid, Version, hash, progress: null, cancellationToken: Ct);

        Assert.That(handler.Requests, Has.Exactly(1).Items);
    }

    [Test]
    public async Task AHashThatDoesNotMatchInstallsNothing()
    {
        using var temp = TempWorkspace.Create("download-bad-hash");
        var (paths, handler, downloader) = Arrange(temp);

        var package = BuildPackage(($"tools/net10.0/{Rid}/{ServerAssembly}", "server"));
        handler.EnqueueBytes(package);

        var expected = Sha512("something else"u8.ToArray());

        var exception = await Assert.ThrowsAsync<RoslynAcquisitionException>(
            () => downloader.EnsureAsync(Rid, Version, expected, progress: null, cancellationToken: Ct));

        Assert.That(exception!.Message, Contains.Substring("does not match the pin").Using(StringComparison.Ordinal));
        Assert.That(Directory.Exists(paths.ServerDirectory(Version, Rid)), Is.False);
        Assert.That(Directory.GetFiles(paths.TempDirectory, "*.nupkg"), Is.Empty);
    }

    /// <summary>
    /// A zip entry name is data somebody else chose, and four separate shapes of it are each a real
    /// CVE somewhere. The guard refuses the whole package rather than skipping the entry: a payload
    /// that contains one is not a payload to serve half of.
    /// </summary>
    [Test]
    public async Task AnEntryThatWouldEscapeTheCacheIsRefused()
    {
        using var temp = TempWorkspace.Create("download-traversal");
        var (paths, handler, downloader) = Arrange(temp);

        var package = BuildPackage(
            ($"tools/net10.0/{Rid}/{ServerAssembly}", "server"),
            ($"tools/net10.0/{Rid}/../../../../escaped.txt", "pwned"));

        handler.EnqueueBytes(package);

        var exception = await Assert.ThrowsAsync<RoslynAcquisitionException>(
            () => downloader.EnsureAsync(Rid, Version, Sha512(package), progress: null, cancellationToken: Ct));

        Assert.That(exception!.Message, Contains.Substring("outside the cache directory").Using(StringComparison.Ordinal));
        Assert.That(Directory.Exists(paths.ServerDirectory(Version, Rid)), Is.False);
        Assert.That(File.Exists(Path.Combine(temp.Root, "escaped.txt")), Is.False);
        Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(temp.Root)!, "escaped.txt")), Is.False);
    }

    [TestCase("../escaped.txt")]
    [TestCase("a/../../escaped.txt")]
    [TestCase("/etc/passwd")]
    [TestCase(@"C:\Windows\System32\evil.dll")]
    [TestCase(@"sub\escaped.txt")]
    [TestCase("")]
    public void TheEntryGuardRefusesEveryEscapingShape(string relative)
    {
        Assert.That(
            NuGetPayloadDownloader.TryResolveEntryPath("entry", relative, Path.GetFullPath("/root"), out _), Is.False);
    }

    [TestCase("Microsoft.CodeAnalysis.LanguageServer.dll")]
    [TestCase("BuildHost-netcore/BuildHost.dll")]
    [TestCase("cs/Microsoft.CodeAnalysis.resources.dll")]
    public void TheEntryGuardAcceptsOrdinaryPayloadEntries(string relative)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "guard-root"));

        Assert.That(NuGetPayloadDownloader.TryResolveEntryPath("entry", relative, root, out var destination), Is.True);
        Assert.That(destination, Does.StartWith(root + Path.DirectorySeparatorChar).Using(StringComparison.Ordinal));
    }

    /// <summary>
    /// D29: two adapter processes start at the same instant on a first run, and only one of them may
    /// download. The loser waits on the lock file, then finds the cache warm and fetches nothing.
    /// </summary>
    [Test]
    public async Task AConcurrentDownloadIsWaitedForAndThenFoundInTheCache()
    {
        using var temp = TempWorkspace.Create("download-lock");
        var (paths, handler, downloader) = Arrange(temp);

        Directory.CreateDirectory(Path.Combine(paths.RoslynCacheRoot, Version));

        var held = new FileStream(
            paths.LockFile(Version, Rid), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var waiting = Task.Run(() => downloader.EnsureAsync(Rid, Version, "expected-hash", progress: null, cancellationToken: Ct), Ct);

        // Long enough for several poll intervals: the point is that it is waiting, not racing.
        await Task.Delay(TimeSpan.FromMilliseconds(750), Ct);
        Assert.That(waiting.IsCompleted, Is.False);

        // What the other process would have left behind.
        var directory = paths.ServerDirectory(Version, Rid);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ServerAssembly), "server");
        File.WriteAllText(paths.CompleteMarker(Version, Rid), "expected-hash");

        await held.DisposeAsync();

        Assert.That(await waiting, Is.EqualTo(directory));

        // The stub has no queued response at all, so a request here would have thrown - but assert it
        // explicitly, because "did not download" is the property, not "did not crash".
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public void ACacheEntryWrittenUnderADifferentHashIsNotTrusted()
    {
        using var temp = TempWorkspace.Create("download-stale");
        var (paths, _, downloader) = Arrange(temp);

        var directory = paths.ServerDirectory(Version, Rid);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ServerAssembly), "server");
        File.WriteAllText(paths.CompleteMarker(Version, Rid), "an-older-hash");

        Assert.That(downloader.IsComplete(Version, Rid, "the-current-hash"), Is.False);
        Assert.That(downloader.IsComplete(Version, Rid, "an-older-hash"), Is.True);

        // A version override has no hash to check against, so the marker's presence is the answer.
        Assert.That(downloader.IsComplete(Version, Rid, expectedSha512: null), Is.True);
    }

    [Test]
    public void ADirectoryWithoutTheMarkerIsNotAFinishedDownload()
    {
        using var temp = TempWorkspace.Create("download-partial");
        var (paths, _, downloader) = Arrange(temp);

        var directory = paths.ServerDirectory(Version, Rid);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ServerAssembly), "server");

        Assert.That(downloader.IsComplete(Version, Rid, expectedSha512: null), Is.False);
    }

    [Test]
    public void TheClientIdentifiesItselfAndHonoursTheSystemProxy()
    {
        using var client = NuGetPayloadDownloader.CreateHttpClient();

        Assert.That(
            client.DefaultRequestHeaders.UserAgent,
            Has.Some.Matches<ProductInfoHeaderValue>(product => product!.Product?.Name == "claude-roslyn-lsp"));

        Assert.That(client.Timeout > TimeSpan.FromMinutes(1), Is.True);
    }

    [Test]
    public async Task AMissingPackageIsReportedAsAnAcquisitionFailure()
    {
        using var temp = TempWorkspace.Create("download-404");
        var (_, handler, downloader) = Arrange(temp);

        handler.EnqueueStatus(System.Net.HttpStatusCode.NotFound);

        var exception = await Assert.ThrowsAsync<RoslynAcquisitionException>(
            () => downloader.EnsureAsync(Rid, "9.9.9-nope", expectedSha512: null, progress: null, cancellationToken: Ct));

        Assert.That(exception!.Message, Contains.Substring("nuget.org").Using(StringComparison.Ordinal));
    }

    [Test]
    public async Task ProgressIsReportedAtLeastOnceForEveryDownload()
    {
        using var temp = TempWorkspace.Create("download-progress");
        var (_, handler, downloader) = Arrange(temp);

        var package = BuildPackage(($"tools/net10.0/{Rid}/{ServerAssembly}", "server"));
        handler.EnqueueBytes(package);

        var reports = new List<RoslynDownloadProgress>();
        var progress = new Progress<RoslynDownloadProgress>(reports.Add);

        await downloader.EnsureAsync(Rid, Version, Sha512(package), progress, Ct);

        // Progress<T> posts to the synchronization context, so give it a moment to arrive.
        for (var attempt = 0; attempt < 50 && reports.Count == 0; attempt++)
        {
            await Task.Delay(20, Ct);
        }

        Assert.That(reports, Is.Not.Empty);
        Assert.That(reports[^1].RuntimeIdentifier, Is.EqualTo(Rid));
        Assert.That(reports[^1].BytesRead, Is.EqualTo(package.Length));
        Assert.That(reports[^1].Describe(), Contains.Substring("MB").Using(StringComparison.Ordinal));
    }

    private static (AdapterPaths Paths, StubHttpMessageHandler Handler, NuGetPayloadDownloader Downloader)
        Arrange(TempWorkspace temp)
    {
        var options = new ClaudeRoslynLspOptions { Home = temp.Root };
        var paths = AdapterPaths.Resolve(options, static _ => null);
        var handler = new StubHttpMessageHandler();

#pragma warning disable CA2000 // The client is owned by the downloader for the lifetime of the test.
        var client = new HttpClient(handler);
#pragma warning restore CA2000

        return (paths, handler, new NuGetPayloadDownloader(
            client,
            paths,
            NullLogger.Instance,
            lockTimeout: TimeSpan.FromSeconds(30)));
    }

    private static byte[] BuildPackage(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream);
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static string Sha512(byte[] content) => Convert.ToBase64String(SHA512.HashData(content));
}
