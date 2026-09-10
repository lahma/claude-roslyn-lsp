using System.Globalization;

namespace ClaudeRoslynLsp.Roslyn;

/// <summary>
/// The pin: which <c>roslyn-language-server</c> release this adapter fronts, what each RID payload
/// hashes to, and which command-line options that release understands.
/// </summary>
/// <remarks>
/// <para>
/// <b>D2</b> says the server is acquired at runtime and pinned hard, and this file is the pin. The
/// region between the <c>generated pin</c> markers below is written by
/// <c>dotnet fallout UpdateRoslynPin</c>, which downloads all eight RID payloads from nuget.org,
/// hashes them, cross-checks each hash against nuget.org's own catalog leaf and rewrites the block.
/// <b>Never edit that region by hand</b> — a hash typed in by a person is a hash nobody verified, and
/// the entire value of hashing a 70 MB download is that the number came from the bytes.
/// </para>
/// <para>
/// <b>D25 — the hash table is per RID, and a version override has no hash at all.</b> Overriding
/// <c>CLAUDE_ROSLYN_LSP_ROSLYN_VERSION</c> is supported because a user debugging against a newer
/// build should not have to fork the adapter, but the download is then verified only by TLS. That is
/// a weaker guarantee than the pin's, so it is carried through the resolution result as
/// <c>Verified = false</c> and printed by <c>doctor</c>, rather than being quietly equivalent.
/// </para>
/// <para>
/// <b>D26 — feature flags are per version, not per build of this adapter.</b> Roslyn's CLI is
/// prerelease and moves: <c>--clientProcessId</c>, <c>--daemon</c> and <c>--daemonKeepAlive</c> exist
/// in the pinned 5.12 build and do not exist in the 5.5 builds still sitting in people's tool stores.
/// Roslyn parses its command line with <c>System.CommandLine</c>, which rejects an unknown option by
/// exiting immediately — so a flag passed to the wrong version is not a degraded feature, it is a
/// child process that never starts, with the explanation on a stderr nobody is reading yet. Hence
/// <see cref="FeaturesFor"/>: the pin's flags for the pin, and a conservative set for anything else.
/// </para>
/// </remarks>
internal static class RoslynServerManifest
{
    /// <summary>
    /// The shim package id. The shim itself is 33 KB and is never downloaded: it exists to depend on
    /// the right <c>&lt;id&gt;.&lt;rid&gt;</c> payload, and this adapter knows its own RID already.
    /// </summary>
    internal const string ShimPackageId = "roslyn-language-server";

    /// <summary>
    /// The managed entry point inside the payload, and the file whose presence means "a Roslyn server
    /// lives in this directory".
    /// </summary>
    /// <remarks>
    /// Launched through the <c>dotnet</c> host rather than through the <c>roslyn-language-server</c>
    /// apphost that sits beside it: that executable is a <em>thin client</em> which spawns its own
    /// daemon per instance, so two of them produce four processes and no shared workspace (C29). The
    /// daemon is not a shortcut to D23's shared engine, it is a second copy of the problem.
    /// </remarks>
    internal const string ServerAssemblyName = "Microsoft.CodeAnalysis.LanguageServer.dll";

    /// <summary>
    /// nuget.org's flat container, which is the only endpoint acquisition needs: package content is
    /// addressable from the id and the version alone, with no search or registration round trip.
    /// </summary>
    internal const string FlatContainerBaseAddress = "https://api.nuget.org/v3-flatcontainer/";

    // ---------------------------------------------------------------------------------------------
    // <generated pin> - rewritten by `dotnet fallout UpdateRoslynPin`. Do not edit by hand.
    // Generated 2026-09-10T10:15:14Z for roslyn-language-server 5.12.0-1.26426.8.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The pinned <c>roslyn-language-server</c> version. One constant, on purpose (D2).</summary>
    internal const string Version = "5.12.0-1.26426.8";

    /// <summary>When the block below was last regenerated, for <c>doctor</c> and for review.</summary>
    internal const string PinGenerated = "2026-09-10T10:15:14Z";

    /// <summary>
    /// SHA-512 of each RID payload's <c>.nupkg</c>, base64, as computed from the downloaded bytes.
    /// </summary>
    private static readonly (string RuntimeIdentifier, string Sha512)[] PinnedHashes =
    [
        ("win-x64", "J9xYa91ZTVj3LSHvApK+4Pg2ufHI6we3OhbyAS36FBnoHC51ugVPc7PD4aPL53rPKVbhQ9bIxpiEKm2ML7PV5A=="),
        ("win-arm64", "RInfzL5nQycqvJSLUOpey36ewmM60kR515tsXHFwmwC+eZjlPWgaumZOzQ2D2+dKXWsniZpAwcj3yTh1PAQc/w=="),
        ("linux-x64", "OsLq46o913u+XEkWU6v7X1ajBry+gt/m3XMpD+l7718EC9e4Me9OLKT3JhKLVyeJTL0nip4SvTd9/8v5JElsPA=="),
        ("linux-arm64", "UrNZV+AE6lp/qcNOjdkCH6YgsgOPvGCy+cGFrfDu4Pl8m5Bp0EzIQSRAxN5qpZX+bO6xDt90v0HKbtSJ/J+grQ=="),
        ("linux-musl-x64", "QDOhTXJnAGsMx6aQUBbBnwyfVvfEvkU/ngxJdSQqvQDMC2Hy/CKP7QEbd1s0P5JwXksZYrsJZtd2HbLBYYSQUg=="),
        ("linux-musl-arm64", "DHJh2YPc/ghklRTet6kTE9O6RrDmchoh+yR0rDT6Npp+CdRZgo6UNOHORWzFYSxnj1eTehwRArFVjH5aMB3iZQ=="),
        ("osx-x64", "A0O/Cy4txjYsTxCTovHCBbO6NLbBLFBPhVxvb8AO4VsvqC+3bWkP4xlrINtQQimwapgeBUnkpRGsw3N4XQ1ayg=="),
        ("osx-arm64", "1xllMuGZQyfRHWaeNRs7yjVOq3IlonzJuaHs1V0nublYcCnPjSvMzagUV8wVUbL48Shbxu9lQNsEQdhJNg7yvg=="),
    ];

    /// <summary>What the pinned build's command line accepts, as observed from its own <c>--help</c>.</summary>
    private static readonly RoslynFeatures PinnedFeatures = new()
    {
        SupportsClientProcessId = true,
        SupportsDaemon = true,
        SupportsStdio = true,
    };

    // ---------------------------------------------------------------------------------------------
    // </generated pin>
    // ---------------------------------------------------------------------------------------------

    /// <summary>The pinned payload hashes, keyed by RID.</summary>
    internal static IReadOnlyDictionary<string, string> Sha512ByRuntimeIdentifier { get; } =
        PinnedHashes.ToDictionary(x => x.RuntimeIdentifier, x => x.Sha512, StringComparer.Ordinal);

    /// <summary>The payload package id for a RID — <c>roslyn-language-server.win-x64</c> and friends.</summary>
    /// <param name="runtimeIdentifier">One of <see cref="RuntimeIdentifier.All"/>.</param>
    internal static string PackageId(string runtimeIdentifier) =>
        ShimPackageId + "." + runtimeIdentifier;

    /// <summary>
    /// The path prefix inside the <c>.nupkg</c> that holds the server, with its trailing slash.
    /// </summary>
    /// <remarks>
    /// Everything under it is extracted, all 163 top-level files plus <c>BuildHost-net472/</c>,
    /// <c>BuildHost-netcore/</c>, <c>Targets/</c> and thirteen satellite-resource folders (C1). An
    /// earlier sketch extracted only the top level; the build hosts are what evaluate project files,
    /// so a server without them loads no projects and says nothing about why.
    /// </remarks>
    /// <param name="runtimeIdentifier">One of <see cref="RuntimeIdentifier.All"/>.</param>
    internal static string PayloadPrefix(string runtimeIdentifier) =>
        "tools/net10.0/" + runtimeIdentifier + "/";

    /// <summary>The flat-container URL of one RID payload.</summary>
    /// <remarks>
    /// The flat container lower-cases both the id and the version in the path. The pinned version is
    /// already lower-case, but an override need not be, and a request for
    /// <c>…/5.12.0-PREVIEW/…</c> is a 404 rather than a redirect.
    /// </remarks>
    /// <param name="runtimeIdentifier">One of <see cref="RuntimeIdentifier.All"/>.</param>
    /// <param name="version">The version to fetch — the pin unless overridden.</param>
    internal static Uri PackageUrl(string runtimeIdentifier, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var id = PackageId(runtimeIdentifier).ToLowerInvariant();
        var lowered = version.ToLowerInvariant();

        return new Uri(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{FlatContainerBaseAddress}{id}/{lowered}/{id}.{lowered}.nupkg"),
            UriKind.Absolute);
    }

    /// <summary>
    /// The expected SHA-512 for a RID at a version, or <see langword="null"/> when nothing is known —
    /// which is every version but the pin.
    /// </summary>
    /// <param name="runtimeIdentifier">One of <see cref="RuntimeIdentifier.All"/>.</param>
    /// <param name="version">The version being fetched.</param>
    internal static string? Sha512For(string runtimeIdentifier, string version) =>
        string.Equals(version, Version, StringComparison.OrdinalIgnoreCase)
        && Sha512ByRuntimeIdentifier.TryGetValue(runtimeIdentifier, out var hash)
            ? hash
            : null;

    /// <summary>
    /// The command-line features a version is known to have. See D26 in the type remarks for why an
    /// unknown version gets the conservative answer rather than the pin's.
    /// </summary>
    /// <param name="version">The version about to be launched.</param>
    internal static RoslynFeatures FeaturesFor(string version) =>
        string.Equals(version, Version, StringComparison.OrdinalIgnoreCase)
            ? PinnedFeatures
            : RoslynFeatures.Conservative;
}

/// <summary>
/// Which optional command-line options a given <c>roslyn-language-server</c> build understands.
/// </summary>
/// <remarks>
/// Only the flags whose <em>absence</em> would break a launch are modelled. Roslyn parses its command
/// line strictly, so this type answers exactly one question per flag: may the launcher pass it?
/// </remarks>
internal sealed record RoslynFeatures
{
    /// <summary>
    /// What is assumed about a version this pin knows nothing about: the least that still starts.
    /// </summary>
    /// <remarks>
    /// <c>--stdio</c> is the one option that has been present in every published build, and
    /// <c>--pipe</c> alongside it, so a conservative launch is still a working launch — it just gives
    /// up the process-death watchdog that <c>--clientProcessId</c> provides.
    /// </remarks>
    internal static RoslynFeatures Conservative { get; } = new();

    /// <summary>
    /// <c>--clientProcessId &lt;pid&gt;</c>: the server exits when that process dies.
    /// </summary>
    /// <remarks>
    /// Roslyn also takes the same duty from <c>initializeParams.processId</c>, so this flag is
    /// belt-and-braces — but it is armed before <c>initialize</c> rather than after it, which is
    /// exactly the window in which a crashing adapter would otherwise strand a 250 MB child.
    /// </remarks>
    internal bool SupportsClientProcessId { get; init; }

    /// <summary>
    /// <c>--daemon</c> / <c>--daemonKeepAlive</c>: a server that outlives one client.
    /// </summary>
    /// <remarks>
    /// Recorded, not used. The daemon is per client instance and shares no workspace (C29), so it
    /// would double the memory it appears to save; D23's shared engine is a session file and a pipe
    /// in <em>this</em> repository, not a Roslyn flag.
    /// </remarks>
    internal bool SupportsDaemon { get; init; }

    /// <summary>
    /// <c>--stdio</c>: the fallback transport, and the only one that works where named pipes do not.
    /// </summary>
    /// <remarks>
    /// Always true in practice; modelled anyway so that <c>doctor</c> reports a capability rather than
    /// an assumption, and so the pipe/stdio choice has something to consult.
    /// </remarks>
    internal bool SupportsStdio { get; init; } = true;
}
