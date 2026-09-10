using System.Diagnostics;
using System.Globalization;

namespace ClaudeRoslynLsp.Roslyn;

/// <summary>One line of <c>dotnet --list-runtimes</c>.</summary>
/// <param name="Name">The shared framework, e.g. <c>Microsoft.NETCore.App</c>.</param>
/// <param name="Version">Its version, exactly as printed, prerelease suffix included.</param>
/// <param name="Location">The directory the framework was found in.</param>
internal sealed record DotnetRuntimeEntry(string Name, string Version, string Location)
{
    /// <summary>The shared framework Roslyn needs. ASP.NET and WindowsDesktop are noise here.</summary>
    internal const string NetCoreApp = "Microsoft.NETCore.App";

    /// <summary>Whether this entry is a .NET 10 <see cref="NetCoreApp"/> runtime.</summary>
    /// <remarks>
    /// Major version 10 exactly, not "10 or later". The payload's <c>runtimeconfig.json</c> asks for
    /// <c>net10.0</c> with <c>rollForward: Major</c>, so a future .NET 11 would in fact run it — but
    /// "would in fact run" is a claim this adapter cannot make about a runtime that does not exist
    /// yet, and reporting a green tick for it would be a diagnosis, not an observation. When 11 ships
    /// and the pin is re-verified against it, this is the line that changes.
    /// </remarks>
    internal bool IsNet10 =>
        string.Equals(Name, NetCoreApp, StringComparison.Ordinal) && MajorVersion == 10;

    /// <summary>The major version, or -1 when the text does not begin with a number.</summary>
    internal int MajorVersion
    {
        get
        {
            var dot = Version.IndexOf('.', StringComparison.Ordinal);
            var head = dot < 0 ? Version : Version[..dot];

            return int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ? major : -1;
        }
    }

    /// <summary>How the entry is printed by <c>doctor</c>.</summary>
    public override string ToString() => Name + " " + Version;
}

/// <summary>What the search for a <c>dotnet</c> host found, including everywhere it looked.</summary>
internal sealed record DotnetHostResult
{
    /// <summary>The host executable, or <see langword="null"/> when none was found.</summary>
    internal string? Path { get; init; }

    /// <summary>Which step of the chain produced <see cref="Path"/>.</summary>
    internal string? Source { get; init; }

    /// <summary>Every runtime the host listed.</summary>
    internal IReadOnlyList<DotnetRuntimeEntry> Runtimes { get; init; } = [];

    /// <summary>The newest .NET 10 shared framework, which is the one Roslyn will run on.</summary>
    internal DotnetRuntimeEntry? SelectedRuntime { get; init; }

    /// <summary>Every place that was looked at, in order, with what was found there.</summary>
    internal IReadOnlyList<string> Chain { get; init; } = [];

    /// <summary>Why this host cannot be used, or <see langword="null"/> when it can.</summary>
    internal string? Failure { get; init; }

    /// <summary>Whether Roslyn can actually be launched with this result.</summary>
    internal bool IsUsable => Path is not null && SelectedRuntime is not null;

    /// <summary>
    /// The value to put in the child's <c>DOTNET_ROOT</c>: the directory the host lives in.
    /// </summary>
    /// <remarks>
    /// Set explicitly rather than inherited because the adapter is a Native AOT binary in most
    /// deployments — it has no host of its own, so there is nothing for the child to inherit a correct
    /// <c>DOTNET_ROOT</c> from, and a stale one in the user's profile pointing at an uninstalled SDK
    /// is a failure that reads as "the runtime is missing" when it is merely misaddressed.
    /// </remarks>
    internal string? DotnetRoot => Path is null ? null : System.IO.Path.GetDirectoryName(Path);
}

/// <summary>The environment the search runs against. Injected so the chain is testable off-host.</summary>
/// <param name="ReadEnvironmentVariable">Reads a variable, or <see langword="null"/> when unset.</param>
/// <param name="FileExists">Whether a file exists at a path.</param>
/// <param name="ListRuntimes">
/// Runs <c>--list-runtimes</c> for a host and returns its stdout, or <see langword="null"/> when the
/// process could not be run at all.
/// </param>
/// <param name="IsWindows">Whether host executables are named <c>dotnet.exe</c>.</param>
internal sealed record DotnetProbe(
    Func<string, string?> ReadEnvironmentVariable,
    Func<string, bool> FileExists,
    Func<string, string?> ListRuntimes,
    bool IsWindows)
{
    /// <summary>The real environment.</summary>
    internal static DotnetProbe Default { get; } = new(
        Environment.GetEnvironmentVariable,
        File.Exists,
        DotnetHostLocator.RunListRuntimes,
        OperatingSystem.IsWindows());
}

/// <summary>
/// Finds the <c>dotnet</c> host that will run the Roslyn payload, and proves it has a .NET 10
/// runtime before anything tries to launch anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>D31 — the adapter resolves the host itself, and asks it what it has, once.</b> Roslyn is
/// launched as <c>&lt;dotnet&gt; …/Microsoft.CodeAnalysis.LanguageServer.dll</c> (C29 — the apphost
/// beside it is a thin client that spawns its own daemon). Letting the operating system resolve
/// <c>dotnet</c> from <c>PATH</c> at launch time would mean a missing or too-old runtime surfaces as
/// a child that exits with a status code and a message on a stderr the client never sees. Resolving
/// it here turns the same condition into a live, non-crashing failure state with a <c>doctor</c>
/// hint, which is what D10 requires of every unusable configuration.
/// </para>
/// <para>
/// The chain is <c>DOTNET_ROOT</c> → <c>PATH</c> → the platform's install locations, and it is
/// recorded even when it succeeds: "which dotnet did it pick" is the first question of every report
/// about a machine with more than one, and a chain printed only on failure is a chain that is never
/// there when it is wanted.
/// </para>
/// <para>
/// <b>The result is cached for the process.</b> <c>--list-runtimes</c> costs 60-100 ms of process
/// start, and the answer cannot change under a running adapter in any way that would help — a runtime
/// installed mid-session does not retroactively fix a Roslyn that failed to start, and the restart
/// path re-reads it in a new process anyway.
/// </para>
/// </remarks>
internal static class DotnetHostLocator
{
    private static readonly Lock Gate = new();
    private static DotnetHostResult? _cached;

    /// <summary>The host for this process, resolved on first use and remembered.</summary>
    internal static DotnetHostResult Resolve()
    {
        lock (Gate)
        {
            return _cached ??= Locate(DotnetProbe.Default);
        }
    }

    /// <summary>Forgets the cached result. Tests only; the product resolves once and keeps it.</summary>
    internal static void ResetCache()
    {
        lock (Gate)
        {
            _cached = null;
        }
    }

    /// <summary>Runs the whole search against <paramref name="probe"/>.</summary>
    /// <param name="probe">The environment to search.</param>
    internal static DotnetHostResult Locate(DotnetProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var chain = new List<string>();
        var executable = probe.IsWindows ? "dotnet.exe" : "dotnet";

        foreach (var (source, candidate) in Candidates(probe, executable))
        {
            if (candidate is null)
            {
                chain.Add($"{source}: not set");
                continue;
            }

            if (!probe.FileExists(candidate))
            {
                chain.Add($"{source}: {candidate} (not found)");
                continue;
            }

            chain.Add($"{source}: {candidate}");

            var output = probe.ListRuntimes(candidate);

            if (output is null)
            {
                chain.Add($"{source}: {candidate} could not be run");
                continue;
            }

            var runtimes = ParseRuntimes(output);
            var selected = SelectNet10(runtimes);

            return new DotnetHostResult
            {
                Path = candidate,
                Source = source,
                Runtimes = runtimes,
                SelectedRuntime = selected,
                Chain = chain,
                Failure = selected is not null
                    ? null
                    : $"'{candidate}' has no {DotnetRuntimeEntry.NetCoreApp} 10.x runtime. Roslyn " +
                      $"{RoslynServerManifest.Version} targets net10.0; install the .NET 10 runtime (or SDK) " +
                      "from https://dotnet.microsoft.com/download, or point DOTNET_ROOT at an installation that has one.",
            };
        }

        return new DotnetHostResult
        {
            Chain = chain,
            Failure =
                "No 'dotnet' host was found. Roslyn runs on the .NET 10 runtime, which this adapter does not " +
                "bundle. Install .NET 10 from https://dotnet.microsoft.com/download, or set DOTNET_ROOT to an " +
                "existing installation.",
        };
    }

    /// <summary>
    /// Parses <c>dotnet --list-runtimes</c> output into entries.
    /// </summary>
    /// <remarks>
    /// The format is <c>&lt;name&gt; &lt;version&gt; [&lt;path&gt;]</c>, one per line. It is parsed
    /// positionally rather than with a regular expression because the path is bracketed and can itself
    /// contain spaces and brackets — so the only reliable anchors are the first two space-separated
    /// tokens and the first <c>[</c> after them. Lines that do not fit are skipped rather than treated
    /// as an error: this output has carried banners and warnings before.
    /// </remarks>
    /// <param name="output">The command's standard output.</param>
    internal static IReadOnlyList<DotnetRuntimeEntry> ParseRuntimes(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var entries = new List<DotnetRuntimeEntry>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0)
            {
                continue;
            }

            var firstSpace = line.IndexOf(' ', StringComparison.Ordinal);

            if (firstSpace <= 0)
            {
                continue;
            }

            var rest = line[(firstSpace + 1)..].TrimStart();
            var secondSpace = rest.IndexOf(' ', StringComparison.Ordinal);

            if (secondSpace <= 0)
            {
                continue;
            }

            var version = rest[..secondSpace];
            var tail = rest[(secondSpace + 1)..].Trim();

            var open = tail.IndexOf('[', StringComparison.Ordinal);
            var close = tail.LastIndexOf(']');

            if (open != 0 || close <= open)
            {
                continue;
            }

            entries.Add(new DotnetRuntimeEntry(line[..firstSpace], version, tail[(open + 1)..close]));
        }

        return entries;
    }

    /// <summary>
    /// Picks the newest .NET 10 shared framework, comparing by version rather than by list order.
    /// </summary>
    /// <remarks>
    /// <c>--list-runtimes</c> sorts as text, which puts 10.0.10 before 10.0.9. A stable release also
    /// beats a prerelease of the same numbers, which is what dropping everything after <c>-</c> and
    /// then preferring the entry without a suffix achieves.
    /// </remarks>
    /// <param name="runtimes">Every runtime the host listed.</param>
    internal static DotnetRuntimeEntry? SelectNet10(IReadOnlyList<DotnetRuntimeEntry> runtimes)
    {
        ArgumentNullException.ThrowIfNull(runtimes);

        DotnetRuntimeEntry? best = null;
        Version? bestVersion = null;
        var bestIsStable = false;

        foreach (var entry in runtimes)
        {
            if (!entry.IsNet10)
            {
                continue;
            }

            var dash = entry.Version.IndexOf('-', StringComparison.Ordinal);
            var numeric = dash < 0 ? entry.Version : entry.Version[..dash];
            var stable = dash < 0;

            if (!Version.TryParse(numeric, out var parsed))
            {
                continue;
            }

            if (best is null
                || parsed > bestVersion
                || (parsed == bestVersion && stable && !bestIsStable))
            {
                best = entry;
                bestVersion = parsed;
                bestIsStable = stable;
            }
        }

        return best;
    }

    /// <summary>Runs <c>--list-runtimes</c> and returns stdout, or null when the host will not run.</summary>
    /// <param name="hostPath">The <c>dotnet</c> executable.</param>
    internal static string? RunListRuntimes(string hostPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(hostPath, "--list-runtimes")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();

            // Drained so a chatty host cannot fill the pipe buffer and deadlock the wait below.
            _ = process.StandardError.ReadToEnd();

            return process.WaitForExit(TimeSpan.FromSeconds(30)) && process.ExitCode == 0 ? output : null;
        }
        catch (Exception exception) when (exception is IOException
                                              or System.ComponentModel.Win32Exception
                                              or InvalidOperationException
                                              or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The search order, as (label, candidate path) pairs; a null path means "not set".</summary>
    private static IEnumerable<(string Source, string? Path)> Candidates(DotnetProbe probe, string executable)
    {
        var root = Trimmed(probe.ReadEnvironmentVariable("DOTNET_ROOT"));

        yield return ("DOTNET_ROOT", root is null ? null : Path.Combine(root, executable));

        // Every match on PATH, not only the first. A wrapper script or an SDK shim that is on PATH
        // and cannot be run is a real machine, and stopping at it would report "no dotnet" on a host
        // that has a perfectly good one two entries later.
        var onPath = EnumerateOnPath(probe, executable).ToList();

        if (onPath.Count == 0)
        {
            yield return ("PATH", null);
        }

        foreach (var candidate in onPath)
        {
            yield return ("PATH", candidate);
        }

        foreach (var directory in DefaultInstallDirectories(probe))
        {
            yield return ("default install location", Path.Combine(directory, executable));
        }
    }

    private static IEnumerable<string> DefaultInstallDirectories(DotnetProbe probe)
    {
        if (probe.IsWindows)
        {
            var programFiles = Trimmed(probe.ReadEnvironmentVariable("ProgramFiles")) ?? @"C:\Program Files";
            yield return Path.Combine(programFiles, "dotnet");
        }
        else
        {
            yield return "/usr/share/dotnet";
            yield return "/usr/lib/dotnet";
            yield return "/usr/local/share/dotnet";
        }

        var home = Trimmed(probe.ReadEnvironmentVariable(probe.IsWindows ? "USERPROFILE" : "HOME"));

        if (home is not null)
        {
            yield return Path.Combine(home, ".dotnet");
        }
    }

    private static IEnumerable<string> EnumerateOnPath(DotnetProbe probe, string executable)
    {
        var path = Trimmed(probe.ReadEnvironmentVariable("PATH"));

        if (path is null)
        {
            yield break;
        }

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // A PATH entry may be quoted on Windows and may be malformed anywhere; Path.Combine
            // throws on invalid characters, and one bad entry must not end the search.
            string candidate;

            try
            {
                candidate = Path.Combine(entry.Trim().Trim('"'), executable);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (probe.FileExists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
