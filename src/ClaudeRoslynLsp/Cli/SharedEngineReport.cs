using System.Globalization;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Adapter.Sharing;

namespace ClaudeRoslynLsp.Cli;

/// <summary>One session file as the shared-engines section reports it, with the memory doctor measured itself.</summary>
/// <param name="Report">The session file and its verdict.</param>
/// <param name="Roslyn">The Roslyn process it names, measured now, when it is live and can be looked at.</param>
internal sealed record SharedEngineEntry(SharedSessionReport Report, BackendMemorySample? Roslyn);

/// <summary>
/// <c>doctor</c>'s "Shared engines" section: every claude-roslyn-lsp process on this machine that is
/// hosting a Roslyn, what each one holds, and what is stale (D107).
/// </summary>
/// <remarks>
/// <para>
/// <b>The report that answers "who is using my memory".</b> A machine running a dozen sessions has a
/// dozen Roslyn children named <c>dotnet</c>, and matching each to its session used to mean reading
/// the session files by hand and cross-referencing process trees. The files now say what each host's
/// backend is doing (D102), and this section measures each Roslyn itself — working set and private
/// bytes, by process id — so the numbers are the operating system's, not a figure a host wrote down
/// minutes ago.
/// </para>
/// <para>
/// Split from <see cref="DoctorCommand"/> so that the gathering and both renderings can be tested
/// without launching a real Roslyn: the process sampler is a parameter, and the rest is a pure
/// function of the session directory.
/// </para>
/// </remarks>
internal sealed class SharedEngineReport
{
    private SharedEngineReport(
        bool sharingEnabled,
        string directory,
        IReadOnlyList<SharedEngineEntry> entries,
        SessionSweep stale,
        bool fixedStale)
    {
        SharingEnabled = sharingEnabled;
        Directory = directory;
        Entries = entries;
        Stale = stale;
        Fixed = fixedStale;
    }

    /// <summary>Whether this process would share (CLAUDE_ROSLYN_LSP_SHARE).</summary>
    internal bool SharingEnabled { get; }

    /// <summary>The session directory.</summary>
    internal string Directory { get; }

    /// <summary>Every session file, live or not.</summary>
    internal IReadOnlyList<SharedEngineEntry> Entries { get; }

    /// <summary>What a sweep removed (with <see cref="Fixed"/>) or would remove (without it), D103.</summary>
    internal SessionSweep Stale { get; }

    /// <summary>Whether this run swept (<c>doctor --fix</c>, <c>install</c>).</summary>
    internal bool Fixed { get; }

    /// <summary>The live hosts, measured.</summary>
    internal IReadOnlyList<SharedEngineEntry> Live => [.. Entries.Where(static entry => entry.Report.Usable)];

    /// <summary>The Roslyn working set of every live host together.</summary>
    internal long TotalWorkingSetBytes => Live.Sum(static entry => entry.Roslyn?.WorkingSetBytes ?? 0);

    /// <summary>The Roslyn private bytes of every live host together.</summary>
    internal long TotalPrivateBytes => Live.Sum(static entry => entry.Roslyn?.PrivateBytes ?? 0);

    /// <summary>
    /// Gathers the section: sweeps when asked to, then lists what is left and measures each live
    /// host's Roslyn.
    /// </summary>
    /// <remarks>
    /// Plain <c>doctor</c> deletes nothing (D94) — it counts what a sweep would remove and says which
    /// command removes it; <c>doctor --fix</c> sweeps first, so the listing shows what stayed.
    /// </remarks>
    /// <param name="registry">The session directory.</param>
    /// <param name="sharingEnabled">Whether this process would share.</param>
    /// <param name="fix">Whether to sweep.</param>
    /// <param name="sample">Measures one process; <see cref="BackendMemory.Sample"/> outside the tests.</param>
    internal static SharedEngineReport Gather(
        SessionRegistry registry,
        bool sharingEnabled,
        bool fix,
        Func<int, BackendMemorySample?> sample)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sample);

        // The sweep is housekeeping, and a report that crashed on it would be the one diagnostic
        // somebody with a broken session directory could not run (D94, D103).
        SessionSweep stale;

        try
        {
            stale = registry.Sweep(apply: fix);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            stale = default;
        }

        var entries = registry.List()
            .Select(report => new SharedEngineEntry(
                report,
                report.Usable && report.Session?.RoslynProcessId is { } roslyn ? sample(roslyn) : null))
            .ToArray();

        return new SharedEngineReport(sharingEnabled, registry.Directory_, entries, stale, fix);
    }

    /// <summary>The text form, as label and value pairs in the order they are printed.</summary>
    /// <param name="now">The time ages are measured to.</param>
    internal IReadOnlyList<(string Label, string Value)> Lines(DateTimeOffset now)
    {
        var lines = new List<(string Label, string Value)>
        {
            ("sharing", SharingEnabled ? "on" : "off (CLAUDE_ROSLYN_LSP_SHARE)"),
            ("sessions", Directory),
        };

        if (Entries.Count == 0)
        {
            lines.Add(("state", "no claude-roslyn-lsp process is hosting a Roslyn for any solution"));
        }

        foreach (var entry in Entries)
        {
            lines.Add((
                entry.Report.Usable ? "session *" : "session",
                string.Create(CultureInfo.InvariantCulture, $"{entry.Report.Key}  {DescribeSession(entry.Report, now)}")));

            if (entry.Report.Usable && entry.Report.Session is { } published)
            {
                lines.Add(("  roslyn", DescribeBackend(published, entry.Roslyn, now)));
            }
        }

        var live = Live;

        lines.Add((
            "total",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{live.Count} live host(s); their Roslyn processes hold {Megabytes(TotalWorkingSetBytes)} MB working set, "
                + $"{Megabytes(TotalPrivateBytes)} MB private")));

        lines.Add((
            "not listed",
            "servers running with CLAUDE_ROSLYN_LSP_SHARE=off keep a private Roslyn and do not publish themselves"));

        if (Stale.Total > 0)
        {
            lines.Add((
                Fixed ? "cleaned" : "hint",
                Fixed
                    ? string.Create(CultureInfo.InvariantCulture, $"removed {DescribeSweep(Stale)}")
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"{DescribeSweep(Stale)} would be removed; `{ServerVersion.Name} doctor --fix` removes them "
                        + $"(a server that becomes a host removes them by itself, too)")));
        }

        return lines;
    }

    /// <summary>The <c>--json</c> form: the <c>sharing</c> object of the report.</summary>
    /// <param name="writer">Where to write it.</param>
    /// <param name="now">The time ages are measured to.</param>
    internal void WriteJson(Utf8JsonWriter writer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject("sharing");
        writer.WriteBoolean("enabled", SharingEnabled);
        writer.WriteString("sessionsDirectory", Directory);
        writer.WriteStartArray("sessions");

        foreach (var entry in Entries)
        {
            var session = entry.Report;

            writer.WriteStartObject();
            writer.WriteString("key", session.Key);
            writer.WriteString("path", session.Path);
            writer.WriteBoolean("usable", session.Usable);
            writer.WriteString("verdict", session.Verdict);

            if (session.Session is { } published)
            {
                writer.WriteNumber("pid", published.ProcessId);
                writer.WriteString("version", published.Version);
                writer.WriteString("solution", published.Solution);
                writer.WriteString("pipeName", published.PipeName);
                writer.WriteString("startedAt", Iso8601(published.StartedAt));
                WriteNullableString(writer, "state", published.State);
                WriteNullableNumber(writer, "roslynProcessId", published.RoslynProcessId);
                WriteNullableString(writer, "lastActivityAt", published.LastActivityAt is { } at ? Iso8601(at) : null);

                if (published.LastActivityAt is { } last)
                {
                    writer.WriteNumber("idleMinutes", Math.Max(0, (int) (now - last).TotalMinutes));
                }

                WriteNullableNumber(writer, "baselineMegabytes", published.BaselineMegabytes);
                writer.WriteNumber("recycles", published.Recycles);
                WriteNullableString(writer, "lastRecycleAt", published.LastRecycleAt is { } recycled ? Iso8601(recycled) : null);
                writer.WriteNumber("attachedClients", published.AttachedClients);
            }

            if (entry.Roslyn is { } memory)
            {
                writer.WriteStartObject("roslyn");
                writer.WriteNumber("workingSetBytes", memory.WorkingSetBytes);
                writer.WriteNumber("privateBytes", memory.PrivateBytes);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartObject("totals");
        writer.WriteNumber("liveHosts", Live.Count);
        writer.WriteNumber("roslynWorkingSetBytes", TotalWorkingSetBytes);
        writer.WriteNumber("roslynPrivateBytes", TotalPrivateBytes);
        writer.WriteEndObject();

        writer.WriteStartObject(Fixed ? "removed" : "stale");
        writer.WriteNumber("sessions", Stale.Sessions);
        writer.WriteNumber("locks", Stale.Locks);
        writer.WriteNumber("temporaryFiles", Stale.Temporaries);
        writer.WriteEndObject();

        writer.WriteString(
            "note",
            "Servers running with CLAUDE_ROSLYN_LSP_SHARE=off keep a private Roslyn and do not publish themselves, "
            + "so they are not listed here.");

        writer.WriteEndObject();
    }

    /// <summary>One session file's line: who, what, and how long ago.</summary>
    private static string DescribeSession(SharedSessionReport session, DateTimeOffset now)
    {
        if (session.Session is not { } published)
        {
            return session.Verdict;
        }

        var age = now - published.StartedAt;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{session.Verdict}, pid {published.ProcessId}, version {published.Version}, "
            + $"started {age.TotalMinutes:0} min ago, {published.Solution}");
    }

    /// <summary>What one live host's Roslyn is doing and holding.</summary>
    private static string DescribeBackend(SharedSession published, BackendMemorySample? memory, DateTimeOffset now)
    {
        var parts = new List<string>
        {
            published.RoslynProcessId is { } pid
                ? string.Create(CultureInfo.InvariantCulture, $"pid {pid}")
                : "no process",
        };

        if (memory is { } measured)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{Megabytes(measured.WorkingSetBytes)} MB working set, {Megabytes(measured.PrivateBytes)} MB private"));
        }

        parts.Add(published.State ?? "state not published");

        if (published.LastActivityAt is { } last)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"last used {(now - last).TotalMinutes:0} min ago"));
        }

        parts.Add(string.Create(CultureInfo.InvariantCulture, $"{published.Recycles} recycle(s)"));

        if (published.BaselineMegabytes is { } baseline)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"baseline {baseline} MB"));
        }

        parts.Add(string.Create(CultureInfo.InvariantCulture, $"{published.AttachedClients} attached client(s)"));

        return string.Join(", ", parts);
    }

    private static string DescribeSweep(SessionSweep sweep) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{sweep.Sessions} stale session file(s), {sweep.Locks} orphaned lock(s) and {sweep.Temporaries} temporary file(s)");

    private static long Megabytes(long bytes) => bytes / (1024 * 1024);

    private static string Iso8601(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}
