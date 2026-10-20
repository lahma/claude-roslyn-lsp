using System.ComponentModel;
using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Adapter.Sharing;
using ClaudeRoslynLsp.Cli;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests;

/// <summary>
/// <c>doctor</c>'s "Shared engines" section: what it says about every host on the machine, in both
/// forms, and that it deletes nothing unless asked (D94, D103, D107).
/// </summary>
/// <remarks>
/// Against a real session directory with a scripted process sampler, which is the whole reason the
/// section is its own class: the rest of <c>doctor</c> launches a real Roslyn.
/// </remarks>
public class SharedEngineReportTests
{
    private const long Megabyte = 1024 * 1024;

    private static readonly DateTimeOffset Now = new(2026, 10, 19, 16, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A session file naming a process <c>doctor</c> may not look at is reported, not crashed on —
    /// and <c>--fix</c> leaves it, because a check that cannot judge must not delete (finding 1).
    /// </summary>
    [Fact]
    public void TheReportSurvivesAProcessItCannotLookAt()
    {
        const int Denied = int.MaxValue - 11;

        using var home = TempWorkspace.Create("doctor-denied");
        var registry = new SessionRegistry(
            Path.Combine(home.Root, SessionRegistry.DirectoryName),
            NullLogger.Instance,
            static pid => pid == Denied ? throw new Win32Exception(5, "Access is denied.") : true,
            sweepLocks: false,
            pidNamespace: null);

        var key = SessionRegistry.KeyFor("/w/Service/Service.slnx");

        registry.Write(key, new SharedSession(
            Denied,
            ServerVersion.Value,
            SharedSession.PipeTransport,
            "claude-roslyn-lsp-share-service",
            "/w/Service/Service.slnx",
            Now.AddHours(-1)));

        var report = SharedEngineReport.Gather(registry, sharingEnabled: true, fix: true, static _ => null);

        var entry = Assert.Single(report.Entries);

        Assert.True(entry.Report.Alive);
        Assert.Equal(0, report.Stale.Sessions);
        Assert.True(File.Exists(registry.FileFor(key)));
    }

    [Fact]
    public void TheJsonCarriesWhatEachLiveHostsRoslynHoldsAndATotal()
    {
        using var home = TempWorkspace.Create("doctor-shared");
        var registry = SessionRegistry.ForHome(home.Root, NullLogger.Instance);

        var live = SessionRegistry.KeyFor("/w/Live/Live.slnx");
        var dead = SessionRegistry.KeyFor("/w/Dead/Dead.slnx");

        registry.Write(live, new SharedSession(
            Environment.ProcessId,
            ServerVersion.Value,
            SharedSession.PipeTransport,
            "claude-roslyn-lsp-share-live",
            "/w/Live/Live.slnx",
            Now.AddHours(-3),
            RoslynProcessId: 4242,
            State: "dormant",
            LastActivityAt: Now.AddMinutes(-47),
            BaselineMegabytes: 410,
            Recycles: 2,
            LastRecycleAt: Now.AddMinutes(-27),
            AttachedClients: 1));

        registry.Write(dead, new SharedSession(
            int.MaxValue - 7,
            ServerVersion.Value,
            SharedSession.PipeTransport,
            "claude-roslyn-lsp-share-dead",
            "/w/Dead/Dead.slnx",
            Now.AddDays(-2)));

        var report = SharedEngineReport.Gather(
            registry,
            sharingEnabled: true,
            fix: false,
            pid => pid == 4242 ? new BackendMemorySample(300 * Megabyte, 700 * Megabyte) : null);

        using var json = Render(report);
        var sharing = json.RootElement.GetProperty("sharing");

        var session = sharing.GetProperty("sessions").EnumerateArray()
            .Single(x => x.GetProperty("key").GetString() == live);

        Assert.True(session.GetProperty("usable").GetBoolean());
        Assert.Equal(4242, session.GetProperty("roslynProcessId").GetInt32());
        Assert.Equal("dormant", session.GetProperty("state").GetString());
        Assert.Equal(47, session.GetProperty("idleMinutes").GetInt32());
        Assert.Equal(410, session.GetProperty("baselineMegabytes").GetInt32());
        Assert.Equal(2, session.GetProperty("recycles").GetInt32());
        Assert.Equal(1, session.GetProperty("attachedClients").GetInt32());
        Assert.Equal(300 * Megabyte, session.GetProperty("roslyn").GetProperty("workingSetBytes").GetInt64());
        Assert.Equal(700 * Megabyte, session.GetProperty("roslyn").GetProperty("privateBytes").GetInt64());

        // The stale one is listed, not measured: the process it names has gone.
        var stale = sharing.GetProperty("sessions").EnumerateArray()
            .Single(x => x.GetProperty("key").GetString() == dead);

        Assert.False(stale.GetProperty("usable").GetBoolean());
        Assert.False(stale.TryGetProperty("roslyn", out _));

        var totals = sharing.GetProperty("totals");

        Assert.Equal(1, totals.GetProperty("liveHosts").GetInt32());
        Assert.Equal(300 * Megabyte, totals.GetProperty("roslynWorkingSetBytes").GetInt64());
        Assert.Equal(700 * Megabyte, totals.GetProperty("roslynPrivateBytes").GetInt64());

        // Counted, and still there: plain doctor deletes nothing (D94).
        Assert.Equal(1, sharing.GetProperty("stale").GetProperty("sessions").GetInt32());
        Assert.True(File.Exists(registry.FileFor(dead)));

        Assert.Contains("CLAUDE_ROSLYN_LSP_SHARE=off", sharing.GetProperty("note").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTextNamesTheRoslynMemoryTheStateAndTheTotal()
    {
        using var home = TempWorkspace.Create("doctor-shared-text");
        var registry = SessionRegistry.ForHome(home.Root, NullLogger.Instance);
        var key = SessionRegistry.KeyFor("/w/Live/Live.slnx");

        registry.Write(key, new SharedSession(
            Environment.ProcessId,
            ServerVersion.Value,
            SharedSession.PipeTransport,
            "claude-roslyn-lsp-share-live",
            "/w/Live/Live.slnx",
            Now.AddHours(-1),
            RoslynProcessId: 4242,
            State: "running",
            LastActivityAt: Now.AddMinutes(-5),
            BaselineMegabytes: 410,
            AttachedClients: 0));

        var report = SharedEngineReport.Gather(
            registry,
            sharingEnabled: true,
            fix: false,
            _ => new BackendMemorySample(820 * Megabyte, 1100 * Megabyte));

        var lines = report.Lines(Now);

        var roslyn = Assert.Single(lines, line => line.Label.Trim() == "roslyn").Value;

        Assert.Contains("pid 4242", roslyn, StringComparison.Ordinal);
        Assert.Contains("820 MB working set, 1100 MB private", roslyn, StringComparison.Ordinal);
        Assert.Contains("running", roslyn, StringComparison.Ordinal);
        Assert.Contains("last used 5 min ago", roslyn, StringComparison.Ordinal);
        Assert.Contains("baseline 410 MB", roslyn, StringComparison.Ordinal);

        var total = Assert.Single(lines, line => line.Label == "total").Value;
        Assert.Contains("1 live host(s)", total, StringComparison.Ordinal);

        Assert.Contains(lines, line => line.Label == "not listed");
    }

    /// <summary><c>doctor --fix</c> sweeps first, so the listing is what stayed (D103).</summary>
    [Fact]
    public void FixSweepsBeforeListing()
    {
        using var home = TempWorkspace.Create("doctor-shared-fix");
        var registry = SessionRegistry.ForHome(home.Root, NullLogger.Instance);
        var dead = SessionRegistry.KeyFor("/w/Dead/Dead.slnx");

        registry.Write(dead, new SharedSession(
            int.MaxValue - 7,
            ServerVersion.Value,
            SharedSession.PipeTransport,
            "claude-roslyn-lsp-share-dead",
            "/w/Dead/Dead.slnx",
            Now.AddDays(-2)));

        var report = SharedEngineReport.Gather(registry, sharingEnabled: true, fix: true, static _ => null);

        Assert.Empty(report.Entries);
        Assert.Equal(1, report.Stale.Sessions);
        Assert.False(File.Exists(registry.FileFor(dead)));

        using var json = Render(report);
        Assert.Equal(1, json.RootElement.GetProperty("sharing").GetProperty("removed").GetProperty("sessions").GetInt32());
    }

    private static JsonDocument Render(SharedEngineReport report)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            report.WriteJson(writer, Now);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
