using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Fallout.Common;
using Fallout.Common.IO;
using Fallout.Common.Utilities.Collections;

using Serilog;

/// <summary>
/// The pin maintenance target: downloads every RID payload of a roslyn-language-server release,
/// hashes it, asks the server itself which command-line options it has, and rewrites the generated
/// block of <c>src/ClaudeRoslynLsp/Roslyn/RoslynServerManifest.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the only way that file's hash table is ever allowed to change (D2). A hash typed in by a
/// person is a hash nobody verified, and the entire value of hashing a 70 MB download is that the
/// number came from the bytes.
/// </para>
/// <para>
/// It costs about 560 MB of download for the eight RIDs, which is why it is a target and not part of
/// any build: it runs when the pin moves, roughly once per Roslyn release somebody decides to adopt.
/// </para>
/// </remarks>
partial class Build
{
    [Parameter("The roslyn-language-server version to pin - defaults to the version currently pinned")]
    readonly string RoslynVersion;

    /// <summary>The generated block's opening marker in the manifest.</summary>
    const string PinBeginMarker = "// <generated pin>";

    /// <summary>The generated block's closing marker.</summary>
    const string PinEndMarker = "// </generated pin>";

    /// <summary>The eight RIDs Microsoft publishes the server for (C5).</summary>
    static readonly string[] RoslynRuntimeIdentifiers =
    [
        "win-x64",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "linux-musl-x64",
        "linux-musl-arm64",
        "osx-x64",
        "osx-arm64",
    ];

    AbsolutePath RoslynManifestFile =>
        SourceDirectory / "ClaudeRoslynLsp" / "Roslyn" / "RoslynServerManifest.cs";

    AbsolutePath RoslynPinDirectory => ArtifactsDirectory / "roslyn-pin";

    Target UpdateRoslynPin => _ => _
        .Description("Downloads every roslyn-language-server RID payload, hashes it, and rewrites the pin")
        .Executes(async () =>
        {
            var version = RoslynVersion ?? ReadPinnedVersion();
            var manifest = RoslynManifestFile.ReadAllText();

            Log.Information("Pinning roslyn-language-server {Version} for {Count} runtime identifiers",
                version, RoslynRuntimeIdentifiers.Length);

            RoslynPinDirectory.CreateDirectory();

            using var http = CreatePinHttpClient();

            var hashes = new List<(string Rid, string Sha512)>();

            foreach (var rid in RoslynRuntimeIdentifiers)
            {
                var nupkg = await DownloadPayload(http, rid, version);
                var sha512 = ComputeSha512(nupkg);

                // Cross-checked against nuget.org's own catalog leaf. The registration index carries no
                // hash at all (C3) - the leaf its catalogEntry.@id points at does. This does not replace
                // the download: the point of the pin is that these bytes hashed to this number here.
                var published = await ReadCatalogHash(http, rid, version);

                if (published is not null && !string.Equals(published, sha512, StringComparison.Ordinal))
                {
                    Assert.Fail(
                        $"The SHA-512 of the downloaded {rid} payload does not match nuget.org's published " +
                        $"packageHash. Downloaded '{sha512}', catalog says '{published}'. Nothing was written.");
                }

                Log.Information("  {Rid}  {Size:N0} bytes  {Hash}{Checked}",
                    rid, nupkg.Size, sha512, published is null ? "  (no catalog hash to compare)" : "  (matches catalog)");

                hashes.Add((rid, sha512));
            }

            var features = await ProbeFeatures(version) ?? ReadExistingFeatures(manifest);

            var rewritten = RewritePin(manifest, version, hashes, features);

            // UTF-8 without a BOM and with LF endings, both deliberately: .gitattributes declares
            // *.cs as eol=lf, and a BOM would make the regenerated file differ from every other
            // source file in the repository for no reason anyone could see in a diff.
            File.WriteAllText(RoslynManifestFile, rewritten, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Log.Information("Rewrote {File}", RoslynManifestFile);
            Log.Warning("CHANGELOG.md names the pinned version; update it in the same commit.");

            ReportSummary(_ => _
                .AddPair("Version", version)
                .AddPair("Runtimes", RoslynRuntimeIdentifiers.Length.ToString())
                .AddPair("clientProcessId", features.SupportsClientProcessId.ToString())
                .AddPair("daemon", features.SupportsDaemon.ToString())
                .AddPair("stdio", features.SupportsStdio.ToString()));
        });

    static HttpClient CreatePinHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{ProductName}-build/1.0");
        return client;
    }

    /// <summary>The version currently in the manifest, so the target can be run with no arguments.</summary>
    string ReadPinnedVersion()
    {
        var text = RoslynManifestFile.ReadAllText();
        const string marker = "internal const string Version = \"";
        var start = text.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(start >= 0, $"{RoslynManifestFile} has no Version constant to read");

        start += marker.Length;
        var end = text.IndexOf('"', start);

        return text[start..end];
    }

    async Task<(AbsolutePath Path, long Size)> DownloadPayload(HttpClient http, string rid, string version)
    {
        var id = $"roslyn-language-server.{rid}".ToLowerInvariant();
        var lowered = version.ToLowerInvariant();
        var url = $"https://api.nuget.org/v3-flatcontainer/{id}/{lowered}/{id}.{lowered}.nupkg";
        var file = RoslynPinDirectory / $"{id}.{lowered}.nupkg";

        if (file.FileExists())
        {
            // Re-runnable: a target that re-downloads 560 MB after a transient failure on the eighth
            // package is a target nobody runs twice.
            return (file, new FileInfo(file).Length);
        }

        Log.Information("  downloading {Url}", url);

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var temporary = file + ".partial";

        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var destination = File.Create(temporary))
        {
            await source.CopyToAsync(destination);
        }

        File.Move(temporary, file, overwrite: true);

        return (file, new FileInfo(file).Length);
    }

    static string ComputeSha512(( AbsolutePath Path, long Size) payload)
    {
        using var stream = File.OpenRead(payload.Path);
        using var sha512 = SHA512.Create();

        return Convert.ToBase64String(sha512.ComputeHash(stream));
    }

    /// <summary>
    /// Reads nuget.org's own SHA-512 for a package, by walking the registration index to the catalog
    /// leaf. Returns null when anything about that walk fails - it is a cross-check, not a source.
    /// </summary>
    static async Task<string> ReadCatalogHash(HttpClient http, string rid, string version)
    {
        try
        {
            var id = $"roslyn-language-server.{rid}".ToLowerInvariant();
            using var index = await GetJson(http, $"https://api.nuget.org/v3/registration5-gz-semver2/{id}/index.json");

            foreach (var page in index.RootElement.GetProperty("items").EnumerateArray())
            {
                JsonDocument fetched = null;

                try
                {
                    var items = page.TryGetProperty("items", out var inline)
                        ? inline
                        : (fetched = await GetJson(http, page.GetProperty("@id").GetString())).RootElement
                            .GetProperty("items");

                    foreach (var entry in items.EnumerateArray())
                    {
                        var catalogEntry = entry.GetProperty("catalogEntry");

                        if (!string.Equals(catalogEntry.GetProperty("version").GetString(), version,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        using var leaf = await GetJson(http, catalogEntry.GetProperty("@id").GetString());

                        return leaf.RootElement.TryGetProperty("packageHash", out var hash)
                            ? hash.GetString()
                            : null;
                    }
                }
                finally
                {
                    fetched?.Dispose();
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException
                                              or InvalidOperationException)
        {
            Log.Debug(exception, "Could not read the catalog hash for {Rid}", rid);
        }

        return null;
    }

    /// <summary>Fetches JSON, un-gzipping by hand because the registration endpoint always sends gzip.</summary>
    static async Task<JsonDocument> GetJson(HttpClient http, string url)
    {
        var bytes = await http.GetByteArrayAsync(url);

        if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            using var compressed = new MemoryStream(bytes);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var plain = new MemoryStream();
            await gzip.CopyToAsync(plain);
            bytes = plain.ToArray();
        }

        return JsonDocument.Parse(bytes);
    }

    /// <summary>
    /// Asks the server itself which options it accepts, by extracting the host RID's payload and
    /// running <c>--help</c>.
    /// </summary>
    /// <remarks>
    /// The alternative - writing the flags down from a changelog - is how a manifest ends up claiming
    /// a flag the binary does not have, which is not a degraded feature but a child process that exits
    /// on a parse error before it says anything (D26). Returns null when the host is not one of the
    /// eight RIDs or has no .NET to run the server with, in which case the existing flags are kept and
    /// the operator is told.
    /// </remarks>
    async Task<RoslynFeatureFlags> ProbeFeatures(string version)
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        var match = RoslynRuntimeIdentifiers.FirstOrDefault(x => string.Equals(x, rid, StringComparison.Ordinal));

        if (match is null)
        {
            Log.Warning("This host ({Rid}) is not one of the published runtime identifiers; the feature flags in " +
                        "the manifest were kept as they were. Re-run on one of {Rids} to refresh them.",
                rid, string.Join(", ", RoslynRuntimeIdentifiers));

            return null;
        }

        var id = $"roslyn-language-server.{match}".ToLowerInvariant();
        var nupkg = RoslynPinDirectory / $"{id}.{version.ToLowerInvariant()}.nupkg";
        var probe = RoslynPinDirectory / "probe";

        probe.CreateOrCleanDirectory();

        try
        {
            using (var archive = ZipFile.OpenRead(nupkg))
            {
                var prefix = $"tools/net10.0/{match}/";

                foreach (var entry in archive.Entries.Where(x =>
                             x.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                             && !x.FullName.EndsWith('/')))
                {
                    var destination = (AbsolutePath)Path.Combine(probe, entry.FullName[prefix.Length..]
                        .Replace('/', Path.DirectorySeparatorChar));

                    destination.Parent.CreateDirectory();
                    entry.ExtractToFile(destination, overwrite: true);
                }
            }

            var help = RunHelp(probe / "Microsoft.CodeAnalysis.LanguageServer.dll");

            if (help is null)
            {
                Log.Warning("Could not run the server's --help; the manifest's feature flags were kept.");
                return null;
            }

            Log.Information("Server --help reported {Length} characters of options", help.Length);

            return new RoslynFeatureFlags(
                help.Contains("--clientProcessId", StringComparison.Ordinal),
                help.Contains("--daemon", StringComparison.Ordinal),
                help.Contains("--stdio", StringComparison.Ordinal));
        }
        finally
        {
            // ~140 MB of extracted server that has answered its one question.
            probe.DeleteDirectory();
        }
    }

    static string RunHelp(AbsolutePath assembly)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("dotnet")
            {
                ArgumentList = { assembly, "--help" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(60));

            return stdout + stderr;
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    static RoslynFeatureFlags ReadExistingFeatures(string manifest) =>
        new(
            ReadBoolean(manifest, "SupportsClientProcessId"),
            ReadBoolean(manifest, "SupportsDaemon"),
            ReadBoolean(manifest, "SupportsStdio"));

    static bool ReadBoolean(string manifest, string name)
    {
        var marker = name + " = ";
        var index = manifest.IndexOf(marker, StringComparison.Ordinal);

        return index >= 0 && manifest[(index + marker.Length)..].StartsWith("true", StringComparison.Ordinal);
    }

    /// <summary>Replaces the manifest's generated block, leaving every hand-written line untouched.</summary>
    static string RewritePin(
        string manifest,
        string version,
        List<(string Rid, string Sha512)> hashes,
        RoslynFeatureFlags features)
    {
        var begin = manifest.IndexOf(PinBeginMarker, StringComparison.Ordinal);
        var end = manifest.IndexOf(PinEndMarker, StringComparison.Ordinal);

        Assert.True(begin >= 0 && end > begin,
            $"RoslynServerManifest.cs must contain '{PinBeginMarker}' and '{PinEndMarker}'");

        var block = new StringBuilder();

        block.Append(PinBeginMarker).AppendLine(" - rewritten by `dotnet fallout UpdateRoslynPin`. Do not edit by hand.");
        block.Append("    // Generated ")
            .Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
            .Append(" for roslyn-language-server ").Append(version).AppendLine(".");
        block.AppendLine("    // ---------------------------------------------------------------------------------------------");
        block.AppendLine();
        block.AppendLine("    /// <summary>The pinned <c>roslyn-language-server</c> version. One constant, on purpose (D2).</summary>");
        block.Append("    internal const string Version = \"").Append(version).AppendLine("\";");
        block.AppendLine();
        block.AppendLine("    /// <summary>When the block below was last regenerated, for <c>doctor</c> and for review.</summary>");
        block.Append("    internal const string PinGenerated = \"")
            .Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
            .AppendLine("\";");
        block.AppendLine();
        block.AppendLine("    /// <summary>");
        block.AppendLine("    /// SHA-512 of each RID payload's <c>.nupkg</c>, base64, as computed from the downloaded bytes.");
        block.AppendLine("    /// </summary>");
        block.AppendLine("    private static readonly (string RuntimeIdentifier, string Sha512)[] PinnedHashes =");
        block.AppendLine("    [");

        foreach (var (rid, sha512) in hashes)
        {
            block.Append("        (\"").Append(rid).Append("\", \"").Append(sha512).AppendLine("\"),");
        }

        block.AppendLine("    ];");
        block.AppendLine();
        block.AppendLine("    /// <summary>What the pinned build's command line accepts, as observed from its own <c>--help</c>.</summary>");
        block.AppendLine("    private static readonly RoslynFeatures PinnedFeatures = new()");
        block.AppendLine("    {");
        block.Append("        SupportsClientProcessId = ").Append(features.SupportsClientProcessId ? "true" : "false").AppendLine(",");
        block.Append("        SupportsDaemon = ").Append(features.SupportsDaemon ? "true" : "false").AppendLine(",");
        block.Append("        SupportsStdio = ").Append(features.SupportsStdio ? "true" : "false").AppendLine(",");
        block.AppendLine("    };");
        block.AppendLine();
        block.AppendLine("    // ---------------------------------------------------------------------------------------------");
        block.Append("    ").Append(PinEndMarker);

        // AppendLine writes the platform's newline; the repository's is LF (.gitattributes).
        var generated = block.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);

        return manifest[..begin] + generated + manifest[(end + PinEndMarker.Length)..];
    }

    /// <summary>The three command-line features the manifest records for a version (D26).</summary>
    sealed record RoslynFeatureFlags(bool SupportsClientProcessId, bool SupportsDaemon, bool SupportsStdio);
}
