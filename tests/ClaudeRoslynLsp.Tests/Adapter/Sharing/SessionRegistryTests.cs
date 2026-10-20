using System.ComponentModel;

using ClaudeRoslynLsp.Adapter.Sharing;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Adapter.Sharing;

/// <summary>
/// The rendezvous file: what it says, when it is believed, and what stops two processes writing it
/// at once (D85, D86).
/// </summary>
/// <remarks>
/// Against the real file system in a temporary directory, for the reason the acquisition tests give:
/// every property under test here <em>is</em> file-system behaviour — a
/// <see cref="FileShare.None"/> handle, a delete that races a read, a path whose case the platform
/// folds — and a fake would get exactly those wrong.
/// </remarks>
public class SessionRegistryTests
{
    [Fact]
    public void APublishedSessionComesBackWithEverythingItWasGiven()
    {
        using var home = TempWorkspace.Create("sessions");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Fixture/Fixture.slnx");

        var published = new SharedSession(
            Environment.ProcessId,
            ServerVersion.Value,
            SharedSession.PipeTransport,
            "claude-roslyn-lsp-share-1-deadbeef",
            "/w/Fixture/Fixture.slnx",
            DateTimeOffset.UtcNow);

        Assert.True(registry.Write(key, published));

        var read = registry.TryRead(key);

        Assert.NotNull(read);
        Assert.Equal(published.ProcessId, read.ProcessId);
        Assert.Equal(published.PipeName, read.PipeName);
        Assert.Equal(published.Solution, read.Solution);
        Assert.Equal(SharedSession.PipeTransport, read.Transport);
    }

    /// <summary>
    /// The key is a property of the solution, not of the string the caller happened to have.
    /// </summary>
    [Fact]
    public void TheKeyIsStableAcrossSpellingsOfTheSamePath()
    {
        var direct = SessionRegistry.KeyFor(Path.Combine("w", "Fixture", "Fixture.slnx"));
        var indirect = SessionRegistry.KeyFor(Path.Combine("w", "Other", "..", "Fixture", "Fixture.slnx"));

        Assert.Equal(direct, indirect);
        Assert.Equal(16, direct.Length);
        Assert.NotEqual(direct, SessionRegistry.KeyFor(Path.Combine("w", "Fixture", "Other.slnx")));
    }

    /// <summary>
    /// Case folding follows the file system rather than a preference, which is the same rule
    /// <c>WorkspacePathGuard</c> applies (D61).
    /// </summary>
    [Fact]
    public void CaseMattersExactlyWhereTheFileSystemSaysItDoes()
    {
        var lower = SessionRegistry.KeyFor(Path.Combine("w", "fixture", "fixture.slnx"));
        var upper = SessionRegistry.KeyFor(Path.Combine("w", "Fixture", "Fixture.slnx"));

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Equal(lower, upper);
        }
        else
        {
            Assert.NotEqual(lower, upper);
        }
    }

    /// <summary>
    /// A host that is gone leaves a file behind, and the next reader is the one that cleans it up —
    /// otherwise a home directory collects one entry per solution per reboot.
    /// </summary>
    [Fact]
    public void ASessionNamingADeadProcessIsDiscardedAndDeleted()
    {
        using var home = TempWorkspace.Create("sessions-stale");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Dead/Dead.slnx");

        registry.Write(key, Session(key, processId: int.MaxValue - 7));

        var report = registry.Inspect(key);

        Assert.False(report.Usable);
        Assert.False(report.Alive);
        Assert.Contains("is gone", report.Verdict, StringComparison.Ordinal);

        Assert.Null(registry.TryRead(key));
        Assert.False(File.Exists(registry.FileFor(key)));
    }

    /// <summary>
    /// Two versions of this adapter speak the same LSP right up to the moment one of them changes
    /// what it fans out; refusing to attach across versions costs one extra Roslyn on upgrade day.
    /// </summary>
    [Fact]
    public void ASessionFromAnotherVersionIsDiscarded()
    {
        using var home = TempWorkspace.Create("sessions-version");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Old/Old.slnx");

        registry.Write(key, Session(key) with { Version = "0.0.1-from-the-past" });

        var report = registry.Inspect(key);

        Assert.True(report.Alive);
        Assert.False(report.VersionMatches);
        Assert.Contains("0.0.1-from-the-past", report.Verdict, StringComparison.Ordinal);
        Assert.Null(registry.TryRead(key));
    }

    [Fact]
    public void AFileThatIsNotJsonIsTreatedAsAbsentRatherThanAsAFailure()
    {
        using var home = TempWorkspace.Create("sessions-garbage");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Bad/Bad.slnx");

        Directory.CreateDirectory(registry.Directory_);
        File.WriteAllText(registry.FileFor(key), "this is not a session file");

        Assert.Null(registry.Inspect(key).Session);
        Assert.Null(registry.TryRead(key));
    }

    /// <summary>
    /// The whole point of the lock (D86): the plugin starts both servers at the same instant, so the
    /// question "is anybody hosting" must have exactly one answerer at a time.
    /// </summary>
    [Fact]
    public void OnlyOneHolderAtATimeGetsTheDecisionLock()
    {
        using var home = TempWorkspace.Create("sessions-lock");
        var first = Registry(home);
        var second = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Race/Race.slnx");

        using (var held = first.TryAcquireLock(key))
        {
            Assert.NotNull(held);
            Assert.Null(second.TryAcquireLock(key));
        }

        // Released, so the loser of the race can take it on its next pass round the loop.
        using var afterwards = second.TryAcquireLock(key);
        Assert.NotNull(afterwards);
    }

    [Fact]
    public void TheLockIsPerSolutionRatherThanPerHome()
    {
        using var home = TempWorkspace.Create("sessions-lock-key");
        var registry = Registry(home);

        using var one = registry.TryAcquireLock(SessionRegistry.KeyFor("/w/A/A.slnx"));
        using var two = registry.TryAcquireLock(SessionRegistry.KeyFor("/w/B/B.slnx"));

        Assert.NotNull(one);
        Assert.NotNull(two);
    }

    /// <summary><c>doctor</c>'s view: every file, whether or not it is any good (D94).</summary>
    [Fact]
    public void ListingReportsLiveAndStaleSessionsAlike()
    {
        using var home = TempWorkspace.Create("sessions-list");
        var registry = Registry(home);

        var live = SessionRegistry.KeyFor("/w/Live/Live.slnx");
        var dead = SessionRegistry.KeyFor("/w/Dead/Dead.slnx");

        registry.Write(live, Session(live));
        registry.Write(dead, Session(dead, processId: int.MaxValue - 7));

        var reports = registry.List();

        Assert.Equal(2, reports.Count);
        Assert.Single(reports, report => report.Usable);
        Assert.Single(reports, report => !report.Usable);

        // Listing does not tidy: a diagnostic that deletes things nobody asked it to is one people
        // stop running (D43's argument, applied here).
        Assert.True(File.Exists(registry.FileFor(dead)));
    }

    [Fact]
    public void AnEmptyHomeListsNothingRatherThanFailing()
    {
        using var home = TempWorkspace.Create("sessions-empty");

        Assert.Empty(Registry(home).List());
    }

    /// <summary>
    /// D102: the file is rewritten while live, so a write has to replace it whole — and leave nothing
    /// behind in the directory when it has.
    /// </summary>
    [Fact]
    public void ARewriteReplacesTheFileWholeAndLeavesNoTemporaryFile()
    {
        using var home = TempWorkspace.Create("sessions-atomic");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Atomic/Atomic.slnx");

        Assert.True(registry.Write(key, Session(key)));
        Assert.Equal(
            SessionUpdate.Written,
            registry.Update(key, Session(key) with { State = "dormant", Recycles = 3, AttachedClients = 2 }));

        var read = registry.TryRead(key);

        Assert.NotNull(read);
        Assert.Equal("dormant", read.State);
        Assert.Equal(3, read.Recycles);
        Assert.Equal(2, read.AttachedClients);

        // The session file and the decision lock the rewrite was made under — and no temporary file.
        Assert.Equal([registry.FileFor(key)], Directory.GetFiles(registry.Directory_, "*.json"));
        Assert.Empty(Directory.GetFiles(registry.Directory_, "*.tmp"));
    }

    /// <summary>
    /// A file written before the new fields existed still reads, and one written with them still reads
    /// in a build that ignores them — the format is optional in both directions (D102).
    /// </summary>
    [Fact]
    public void AFileWithoutTheBackendFieldsStillReads()
    {
        using var home = TempWorkspace.Create("sessions-old");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Old/Old.slnx");

        Directory.CreateDirectory(registry.Directory_);
        File.WriteAllText(
            registry.FileFor(key),
            $$"""
            {"pid":{{Environment.ProcessId}},"version":"{{ServerVersion.Value}}","transport":"pipe",
             "pipeName":"claude-roslyn-lsp-share-old","solution":"/w/Old/Old.slnx","startedAt":"2026-10-19T10:00:00+00:00"}
            """);

        var read = registry.TryRead(key);

        Assert.NotNull(read);
        Assert.Null(read.State);
        Assert.Null(read.RoslynProcessId);
        Assert.Equal(0, read.Recycles);
    }

    /// <summary>
    /// An unreadable file that is young may be an older build's write caught half done, so a reader
    /// leaves it; one that is old is debris, and goes (D102, D103).
    /// </summary>
    [Fact]
    public void AnUnreadableFileIsDeletedOnlyOnceItIsOld()
    {
        using var home = TempWorkspace.Create("sessions-torn");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Torn/Torn.slnx");
        var path = registry.FileFor(key);

        Directory.CreateDirectory(registry.Directory_);
        File.WriteAllText(path, """{"pid":12, "vers""");

        Assert.Null(registry.TryRead(key));
        Assert.True(File.Exists(path));

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - SessionRegistry.DebrisAge - TimeSpan.FromMinutes(1));

        Assert.Null(registry.TryRead(key));
        Assert.False(File.Exists(path));
    }

    /// <summary>A write's temporary file is never mistaken for a session.</summary>
    [Fact]
    public void ListingIgnoresTemporaryFiles()
    {
        using var home = TempWorkspace.Create("sessions-tmp");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Tmp/Tmp.slnx");

        registry.Write(key, Session(key));
        File.WriteAllText(Path.Combine(registry.Directory_, key + ".0123456789abcdef.tmp"), "{");
        File.WriteAllText(Path.Combine(registry.Directory_, key + ".json.tmp"), "{");

        var report = Assert.Single(registry.List());

        Assert.Equal(key, report.Key);
        Assert.True(report.Usable);
    }

    /// <summary>
    /// D103's three rules, each unable to touch anything live: stale sessions go and live ones stay,
    /// orphaned locks go and a held one or one with a session beside it stays, old temporary files go
    /// and young ones stay.
    /// </summary>
    [Fact]
    public void TheSweepRemovesOnlyWhatNothingCanStillBeUsing()
    {
        using var home = TempWorkspace.Create("sessions-sweep");

        // The Windows lock rule, on every platform: LockFilesAreSweptOnWindowsOnly is the other half.
        var registry = RegistryWith(home, sweepLocks: true);
        var directory = registry.Directory_;

        var live = SessionRegistry.KeyFor("/w/Live/Live.slnx");
        var dead = SessionRegistry.KeyFor("/w/Dead/Dead.slnx");
        var orphan = SessionRegistry.KeyFor("/w/Orphan/Orphan.slnx");
        var held = SessionRegistry.KeyFor("/w/Held/Held.slnx");

        registry.Write(live, Session(live));
        registry.Write(dead, Session(dead, processId: int.MaxValue - 7));

        // Locks: one beside a live session, one with nothing beside it, one somebody is holding.
        using (registry.TryAcquireLock(live))
        {
        }

        using (registry.TryAcquireLock(orphan))
        {
        }

        using var holding = registry.TryAcquireLock(held);
        Assert.NotNull(holding);

        var oldTemporary = Path.Combine(directory, dead + ".00000000000000000000000000000001.tmp");
        var youngTemporary = Path.Combine(directory, live + ".00000000000000000000000000000002.tmp");

        File.WriteAllText(oldTemporary, "{");
        File.WriteAllText(youngTemporary, "{");
        File.SetLastWriteTimeUtc(oldTemporary, DateTime.UtcNow - SessionRegistry.DebrisAge - TimeSpan.FromMinutes(1));

        // Counting first: plain doctor deletes nothing (D94).
        var counted = registry.Sweep(apply: false);

        Assert.Equal(new SessionSweep(Sessions: 1, Locks: 1, Temporaries: 1), counted);
        Assert.True(File.Exists(registry.FileFor(dead)));
        Assert.True(File.Exists(registry.LockFor(orphan)));
        Assert.True(File.Exists(oldTemporary));

        var swept = registry.Sweep(apply: true);

        Assert.Equal(counted, swept);

        Assert.True(File.Exists(registry.FileFor(live)));
        Assert.False(File.Exists(registry.FileFor(dead)));

        Assert.True(File.Exists(registry.LockFor(live)));
        Assert.False(File.Exists(registry.LockFor(orphan)));
        Assert.True(File.Exists(registry.LockFor(held)));

        Assert.False(File.Exists(oldTemporary));
        Assert.True(File.Exists(youngTemporary));

        // And the held lock still works as a lock: nobody else can take it.
        Assert.Null(Registry(home).TryAcquireLock(held));
    }

    /// <summary>
    /// A live host written by another version is unusable to this build but is still the rendezvous
    /// for its own version's other half, so the sweep must leave it alone (D103).
    /// </summary>
    [Fact]
    public void TheSweepLeavesALiveHostOfAnotherVersionAlone()
    {
        using var home = TempWorkspace.Create("sessions-sweep-version");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Older/Older.slnx");

        registry.Write(key, Session(key) with { Version = "0.0.1-older" });

        Assert.False(registry.Inspect(key).Usable);
        Assert.Equal(0, registry.Sweep(apply: true).Total);
        Assert.True(File.Exists(registry.FileFor(key)));
    }

    /// <summary>
    /// A pid this process cannot look at — a Windows service that reused a stale file's id, which
    /// makes <c>HasExited</c> throw <see cref="Win32Exception"/> — is judged alive, so nothing
    /// deletes it and nothing throws (finding 1 of the 0.2.0 review, D103).
    /// </summary>
    [Fact]
    public void AProcessThatCannotBeLookedAtIsAliveAndIsNeverSwept()
    {
        const int Denied = int.MaxValue - 11;

        using var home = TempWorkspace.Create("sessions-denied");
        var registry = RegistryWith(
            home,
            hasExited: static pid => pid == Denied ? throw new Win32Exception(5, "Access is denied.") : true);
        var key = SessionRegistry.KeyFor("/w/Denied/Denied.slnx");

        registry.Write(key, Session(key, processId: Denied));

        Assert.True(registry.Inspect(key).Alive);
        Assert.Equal(0, registry.Sweep(apply: true).Sessions);
        Assert.True(File.Exists(registry.FileFor(key)));
        Assert.NotNull(registry.TryRead(key));
    }

    /// <summary>Only "no such process" and "it has exited" mean gone; every other answer means alive.</summary>
    [Fact]
    public void OnlyNoSuchProcessAndHasExitedMeanGone()
    {
        const int Other = int.MaxValue - 13;

        Assert.False(SessionRegistry.IsAlive(Other, static _ => throw new ArgumentException("no such process")));
        Assert.False(SessionRegistry.IsAlive(Other, static _ => throw new InvalidOperationException("it exited")));
        Assert.False(SessionRegistry.IsAlive(Other, static _ => true));
        Assert.True(SessionRegistry.IsAlive(Other, static _ => false));
        Assert.True(SessionRegistry.IsAlive(Other, static _ => throw new Win32Exception(5, "Access is denied.")));
        Assert.True(SessionRegistry.IsAlive(Other, static _ => throw new NotSupportedException()));

        Assert.False(SessionRegistry.IsAlive(0, static _ => false));
        Assert.True(SessionRegistry.IsAlive(Environment.ProcessId, static _ => throw new InvalidOperationException()));
    }

    /// <summary>
    /// A rewrite touches the file only while it still names this host — this process <em>and</em>
    /// this pipe — and never creates one that has gone (finding 3, D102).
    /// </summary>
    [Fact]
    public void ARewriteLeavesAFileThatNamesAnotherHostAlone()
    {
        using var home = TempWorkspace.Create("sessions-owner");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Owner/Owner.slnx");
        var ours = Session(key);
        var path = registry.FileFor(key);

        Assert.Equal(SessionUpdate.NotOurs, registry.Update(key, ours with { State = "running" }));
        Assert.False(File.Exists(path));

        registry.Write(key, ours with { PipeName = "claude-roslyn-lsp-share-newcomer" });
        var newcomer = File.ReadAllBytes(path);

        Assert.Equal(SessionUpdate.NotOurs, registry.Update(key, ours with { State = "dormant" }));
        Assert.Equal(newcomer, File.ReadAllBytes(path));

        registry.Write(key, ours with { ProcessId = ours.ProcessId + 1 });
        var other = File.ReadAllBytes(path);

        Assert.Equal(SessionUpdate.NotOurs, registry.Update(key, ours with { State = "dormant" }));
        Assert.Equal(other, File.ReadAllBytes(path));

        registry.Write(key, ours);

        Assert.Equal(SessionUpdate.Written, registry.Update(key, ours with { State = "dormant" }));
        Assert.Equal("dormant", registry.TryRead(key)!.State);
    }

    /// <summary>
    /// While another process holds the decision lock it is deciding whether to take over, so a
    /// rewrite waits and a withdrawal leaves the file to it (D86, D102).
    /// </summary>
    [Fact]
    public void NothingIsRewrittenOrWithdrawnWhileAnotherProcessIsDeciding()
    {
        using var home = TempWorkspace.Create("sessions-deciding");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Deciding/Deciding.slnx");
        var ours = Session(key);

        registry.Write(key, ours);

        using (var deciding = Registry(home).TryAcquireLock(key))
        {
            Assert.NotNull(deciding);
            Assert.Equal(SessionUpdate.Busy, registry.Update(key, ours with { State = "dormant" }));
            Assert.False(registry.DeleteIfOwned(key, ours.ProcessId, ours.PipeName));
        }

        Assert.Null(registry.TryRead(key)!.State);
        Assert.Equal(SessionUpdate.Written, registry.Update(key, ours with { State = "dormant" }));
    }

    /// <summary>A stopping host withdraws its own file and only its own (finding 3).</summary>
    [Fact]
    public void AStoppingHostWithdrawsOnlyItsOwnFile()
    {
        using var home = TempWorkspace.Create("sessions-withdraw");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Withdraw/Withdraw.slnx");
        var ours = Session(key);

        registry.Write(key, ours with { PipeName = "claude-roslyn-lsp-share-newcomer" });

        Assert.False(registry.DeleteIfOwned(key, ours.ProcessId, ours.PipeName));
        Assert.True(File.Exists(registry.FileFor(key)));

        registry.Write(key, ours);

        Assert.True(registry.DeleteIfOwned(key, ours.ProcessId, ours.PipeName));
        Assert.False(File.Exists(registry.FileFor(key)));
    }

    /// <summary>
    /// Orphaned locks are swept where a file pending deletion cannot be reopened — Windows — and left
    /// alone where an unlink races a lock, which is everywhere else (finding 8, D103).
    /// </summary>
    [Fact]
    public void LockFilesAreSweptOnWindowsOnly()
    {
        using var home = TempWorkspace.Create("sessions-locks");
        var orphan = SessionRegistry.KeyFor("/w/Orphan/Orphan.slnx");

        var unix = RegistryWith(home, sweepLocks: false);

        using (unix.TryAcquireLock(orphan))
        {
        }

        Assert.Equal(0, unix.Sweep(apply: true).Locks);
        Assert.True(File.Exists(unix.LockFor(orphan)));

        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, Registry(home).Sweep(apply: false).Locks);

        var windows = RegistryWith(home, sweepLocks: true);

        Assert.Equal(1, windows.Sweep(apply: true).Locks);
        Assert.False(File.Exists(windows.LockFor(orphan)));
    }

    /// <summary>
    /// A file written in another pid namespace — another container sharing this home — is never
    /// judged by a pid that means nothing here; one with no namespace, or with ours, is judged as
    /// before (finding 8, D103).
    /// </summary>
    [Fact]
    public void TheSweepLeavesAFileFromAnotherPidNamespaceAlone()
    {
        const int Gone = int.MaxValue - 7;
        const string Ours = "pid:[4026531836]";

        using var home = TempWorkspace.Create("sessions-namespace");
        var registry = RegistryWith(home, pidNamespace: Ours);

        var foreign = SessionRegistry.KeyFor("/w/Foreign/Foreign.slnx");
        var unmarked = SessionRegistry.KeyFor("/w/Unmarked/Unmarked.slnx");
        var local = SessionRegistry.KeyFor("/w/Local/Local.slnx");

        registry.Write(foreign, Session(foreign, processId: Gone) with { PidNamespace = "pid:[4026532999]" });
        registry.Write(unmarked, Session(unmarked, processId: Gone));
        registry.Write(local, Session(local, processId: Gone) with { PidNamespace = Ours });

        var report = registry.Inspect(foreign);

        Assert.True(report.ForeignNamespace);
        Assert.Contains("another pid namespace", report.Verdict, StringComparison.Ordinal);
        Assert.False(registry.Inspect(local).ForeignNamespace);

        Assert.Equal(2, registry.Sweep(apply: true).Sessions);

        Assert.True(File.Exists(registry.FileFor(foreign)));
        Assert.False(File.Exists(registry.FileFor(unmarked)));
        Assert.False(File.Exists(registry.FileFor(local)));
    }

    /// <summary>The namespace is a Linux fact, read from <c>/proc/self/ns/pid</c>; elsewhere there is none.</summary>
    [Fact]
    public void ThePidNamespaceIsReadOnLinuxAndNowhereElse()
    {
        var current = SessionRegistry.CurrentPidNamespace();

        if (OperatingSystem.IsLinux())
        {
            Assert.NotNull(current);
            Assert.StartsWith("pid:[", current, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(current);
        }
    }

    [Fact]
    public void SweepingADirectoryThatDoesNotExistFindsNothing()
    {
        using var home = TempWorkspace.Create("sessions-nothing");

        Assert.Equal(0, Registry(home).Sweep(apply: true).Total);
    }

    private static SessionRegistry Registry(TempWorkspace home) =>
        SessionRegistry.ForHome(home.Root, NullLogger.Instance);

    /// <summary>A registry with its platform facts supplied: every other pid is gone unless told otherwise.</summary>
    private static SessionRegistry RegistryWith(
        TempWorkspace home,
        Func<int, bool>? hasExited = null,
        bool? sweepLocks = null,
        string? pidNamespace = null) =>
        new(
            Path.Combine(home.Root, SessionRegistry.DirectoryName),
            NullLogger.Instance,
            hasExited ?? (static _ => true),
            sweepLocks ?? OperatingSystem.IsWindows(),
            pidNamespace);

    private static SharedSession Session(string key, int? processId = null) =>
        new(
            processId ?? Environment.ProcessId,
            ServerVersion.Value,
            SharedSession.PipeTransport,
            "claude-roslyn-lsp-share-" + key,
            "/w/Fixture/Fixture.slnx",
            DateTimeOffset.UtcNow);
}
