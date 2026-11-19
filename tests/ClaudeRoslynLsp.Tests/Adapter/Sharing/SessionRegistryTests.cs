using System.ComponentModel;

using ClaudeRoslynLsp.Adapter.Sharing;

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

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
    [Test]
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

        Assert.That(registry.Write(key, published), Is.True);

        var read = registry.TryRead(key);

        Assert.That(read, Is.Not.Null);
        Assert.That(read!.ProcessId, Is.EqualTo(published.ProcessId));
        Assert.That(read.PipeName, Is.EqualTo(published.PipeName));
        Assert.That(read.Solution, Is.EqualTo(published.Solution));
        Assert.That(read.Transport, Is.EqualTo(SharedSession.PipeTransport));
    }

    /// <summary>
    /// The key is a property of the solution, not of the string the caller happened to have.
    /// </summary>
    [Test]
    public void TheKeyIsStableAcrossSpellingsOfTheSamePath()
    {
        var direct = SessionRegistry.KeyFor(Path.Combine("w", "Fixture", "Fixture.slnx"));
        var indirect = SessionRegistry.KeyFor(Path.Combine("w", "Other", "..", "Fixture", "Fixture.slnx"));

        Assert.That(indirect, Is.EqualTo(direct));
        Assert.That(direct.Length, Is.EqualTo(16));
        Assert.That(SessionRegistry.KeyFor(Path.Combine("w", "Fixture", "Other.slnx")), Is.Not.EqualTo(direct));
    }

    /// <summary>
    /// Case folding follows the file system rather than a preference, which is the same rule
    /// <c>WorkspacePathGuard</c> applies (D61).
    /// </summary>
    [Test]
    public void CaseMattersExactlyWhereTheFileSystemSaysItDoes()
    {
        var lower = SessionRegistry.KeyFor(Path.Combine("w", "fixture", "fixture.slnx"));
        var upper = SessionRegistry.KeyFor(Path.Combine("w", "Fixture", "Fixture.slnx"));

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.That(upper, Is.EqualTo(lower));
        }
        else
        {
            Assert.That(upper, Is.Not.EqualTo(lower));
        }
    }

    /// <summary>
    /// A host that is gone leaves a file behind, and the next reader is the one that cleans it up —
    /// otherwise a home directory collects one entry per solution per reboot.
    /// </summary>
    [Test]
    public void ASessionNamingADeadProcessIsDiscardedAndDeleted()
    {
        using var home = TempWorkspace.Create("sessions-stale");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Dead/Dead.slnx");

        registry.Write(key, Session(key, processId: int.MaxValue - 7));

        var report = registry.Inspect(key);

        Assert.That(report.Usable, Is.False);
        Assert.That(report.Alive, Is.False);
        Assert.That(report.Verdict, Contains.Substring("is gone").Using(StringComparison.Ordinal));

        Assert.That(registry.TryRead(key), Is.Null);
        Assert.That(File.Exists(registry.FileFor(key)), Is.False);
    }

    /// <summary>
    /// Two versions of this adapter speak the same LSP right up to the moment one of them changes
    /// what it fans out; refusing to attach across versions costs one extra Roslyn on upgrade day.
    /// </summary>
    [Test]
    public void ASessionFromAnotherVersionIsDiscarded()
    {
        using var home = TempWorkspace.Create("sessions-version");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Old/Old.slnx");

        registry.Write(key, Session(key) with { Version = "0.0.1-from-the-past" });

        var report = registry.Inspect(key);

        Assert.That(report.Alive, Is.True);
        Assert.That(report.VersionMatches, Is.False);
        Assert.That(report.Verdict, Contains.Substring("0.0.1-from-the-past").Using(StringComparison.Ordinal));
        Assert.That(registry.TryRead(key), Is.Null);
    }

    [Test]
    public void AFileThatIsNotJsonIsTreatedAsAbsentRatherThanAsAFailure()
    {
        using var home = TempWorkspace.Create("sessions-garbage");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Bad/Bad.slnx");

        Directory.CreateDirectory(registry.Directory_);
        File.WriteAllText(registry.FileFor(key), "this is not a session file");

        Assert.That(registry.Inspect(key).Session, Is.Null);
        Assert.That(registry.TryRead(key), Is.Null);
    }

    /// <summary>
    /// The whole point of the lock (D86): the plugin starts both servers at the same instant, so the
    /// question "is anybody hosting" must have exactly one answerer at a time.
    /// </summary>
    [Test]
    public void OnlyOneHolderAtATimeGetsTheDecisionLock()
    {
        using var home = TempWorkspace.Create("sessions-lock");
        var first = Registry(home);
        var second = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Race/Race.slnx");

        using (var held = first.TryAcquireLock(key))
        {
            Assert.That(held, Is.Not.Null);
            Assert.That(second.TryAcquireLock(key), Is.Null);
        }

        // Released, so the loser of the race can take it on its next pass round the loop.
        using var afterwards = second.TryAcquireLock(key);
        Assert.That(afterwards, Is.Not.Null);
    }

    [Test]
    public void TheLockIsPerSolutionRatherThanPerHome()
    {
        using var home = TempWorkspace.Create("sessions-lock-key");
        var registry = Registry(home);

        using var one = registry.TryAcquireLock(SessionRegistry.KeyFor("/w/A/A.slnx"));
        using var two = registry.TryAcquireLock(SessionRegistry.KeyFor("/w/B/B.slnx"));

        Assert.That(one, Is.Not.Null);
        Assert.That(two, Is.Not.Null);
    }

    /// <summary><c>doctor</c>'s view: every file, whether or not it is any good (D94).</summary>
    [Test]
    public void ListingReportsLiveAndStaleSessionsAlike()
    {
        using var home = TempWorkspace.Create("sessions-list");
        var registry = Registry(home);

        var live = SessionRegistry.KeyFor("/w/Live/Live.slnx");
        var dead = SessionRegistry.KeyFor("/w/Dead/Dead.slnx");

        registry.Write(live, Session(live));
        registry.Write(dead, Session(dead, processId: int.MaxValue - 7));

        var reports = registry.List();

        Assert.That(reports.Count, Is.EqualTo(2));
        Assert.That(reports, Has.Exactly(1).Matches<SharedSessionReport>(report => report!.Usable));
        Assert.That(reports, Has.Exactly(1).Matches<SharedSessionReport>(report => !report!.Usable));

        // Listing does not tidy: a diagnostic that deletes things nobody asked it to is one people
        // stop running (D43's argument, applied here).
        Assert.That(File.Exists(registry.FileFor(dead)), Is.True);
    }

    [Test]
    public void AnEmptyHomeListsNothingRatherThanFailing()
    {
        using var home = TempWorkspace.Create("sessions-empty");

        Assert.That(Registry(home).List(), Is.Empty);
    }

    /// <summary>
    /// D102: the file is rewritten while live, so a write has to replace it whole — and leave nothing
    /// behind in the directory when it has.
    /// </summary>
    [Test]
    public void ARewriteReplacesTheFileWholeAndLeavesNoTemporaryFile()
    {
        using var home = TempWorkspace.Create("sessions-atomic");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Atomic/Atomic.slnx");

        Assert.That(registry.Write(key, Session(key)), Is.True);
        Assert.That(
            registry.Update(key, Session(key) with { State = "dormant", Recycles = 3, AttachedClients = 2 }),
            Is.EqualTo(SessionUpdate.Written));

        var read = registry.TryRead(key);

        Assert.That(read, Is.Not.Null);
        Assert.That(read!.State, Is.EqualTo("dormant"));
        Assert.That(read.Recycles, Is.EqualTo(3));
        Assert.That(read.AttachedClients, Is.EqualTo(2));

        // The session file and the decision lock the rewrite was made under — and no temporary file.
        Assert.That(Directory.GetFiles(registry.Directory_, "*.json"), Is.EqualTo([registry.FileFor(key)]));
        Assert.That(Directory.GetFiles(registry.Directory_, "*.tmp"), Is.Empty);
    }

    /// <summary>
    /// A file written before the new fields existed still reads, and one written with them still reads
    /// in a build that ignores them — the format is optional in both directions (D102).
    /// </summary>
    [Test]
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

        Assert.That(read, Is.Not.Null);
        Assert.That(read!.State, Is.Null);
        Assert.That(read.RoslynProcessId, Is.Null);
        Assert.That(read.Recycles, Is.EqualTo(0));
    }

    /// <summary>
    /// An unreadable file that is young may be an older build's write caught half done, so a reader
    /// leaves it; one that is old is debris, and goes (D102, D103).
    /// </summary>
    [Test]
    public void AnUnreadableFileIsDeletedOnlyOnceItIsOld()
    {
        using var home = TempWorkspace.Create("sessions-torn");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Torn/Torn.slnx");
        var path = registry.FileFor(key);

        Directory.CreateDirectory(registry.Directory_);
        File.WriteAllText(path, """{"pid":12, "vers""");

        Assert.That(registry.TryRead(key), Is.Null);
        Assert.That(File.Exists(path), Is.True);

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - SessionRegistry.DebrisAge - TimeSpan.FromMinutes(1));

        Assert.That(registry.TryRead(key), Is.Null);
        Assert.That(File.Exists(path), Is.False);
    }

    /// <summary>A write's temporary file is never mistaken for a session.</summary>
    [Test]
    public void ListingIgnoresTemporaryFiles()
    {
        using var home = TempWorkspace.Create("sessions-tmp");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Tmp/Tmp.slnx");

        registry.Write(key, Session(key));
        File.WriteAllText(Path.Combine(registry.Directory_, key + ".0123456789abcdef.tmp"), "{");
        File.WriteAllText(Path.Combine(registry.Directory_, key + ".json.tmp"), "{");

        Assert.That(registry.List(), Has.Exactly(1).Items);
        var report = registry.List().Single();

        Assert.That(report.Key, Is.EqualTo(key));
        Assert.That(report.Usable, Is.True);
    }

    /// <summary>
    /// D103's three rules, each unable to touch anything live: stale sessions go and live ones stay,
    /// orphaned locks go and a held one or one with a session beside it stays, old temporary files go
    /// and young ones stay.
    /// </summary>
    [Test]
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
        Assert.That(holding, Is.Not.Null);

        var oldTemporary = Path.Combine(directory, dead + ".00000000000000000000000000000001.tmp");
        var youngTemporary = Path.Combine(directory, live + ".00000000000000000000000000000002.tmp");

        File.WriteAllText(oldTemporary, "{");
        File.WriteAllText(youngTemporary, "{");
        File.SetLastWriteTimeUtc(oldTemporary, DateTime.UtcNow - SessionRegistry.DebrisAge - TimeSpan.FromMinutes(1));

        // Counting first: plain doctor deletes nothing (D94).
        var counted = registry.Sweep(apply: false);

        Assert.That(counted, Is.EqualTo(new SessionSweep(Sessions: 1, Locks: 1, Temporaries: 1)));
        Assert.That(File.Exists(registry.FileFor(dead)), Is.True);
        Assert.That(File.Exists(registry.LockFor(orphan)), Is.True);
        Assert.That(File.Exists(oldTemporary), Is.True);

        var swept = registry.Sweep(apply: true);

        Assert.That(swept, Is.EqualTo(counted));

        Assert.That(File.Exists(registry.FileFor(live)), Is.True);
        Assert.That(File.Exists(registry.FileFor(dead)), Is.False);

        Assert.That(File.Exists(registry.LockFor(live)), Is.True);
        Assert.That(File.Exists(registry.LockFor(orphan)), Is.False);
        Assert.That(File.Exists(registry.LockFor(held)), Is.True);

        Assert.That(File.Exists(oldTemporary), Is.False);
        Assert.That(File.Exists(youngTemporary), Is.True);

        // And the held lock still works as a lock: nobody else can take it.
        Assert.That(Registry(home).TryAcquireLock(held), Is.Null);
    }

    /// <summary>
    /// A live host written by another version is unusable to this build but is still the rendezvous
    /// for its own version's other half, so the sweep must leave it alone (D103).
    /// </summary>
    [Test]
    public void TheSweepLeavesALiveHostOfAnotherVersionAlone()
    {
        using var home = TempWorkspace.Create("sessions-sweep-version");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Older/Older.slnx");

        registry.Write(key, Session(key) with { Version = "0.0.1-older" });

        Assert.That(registry.Inspect(key).Usable, Is.False);
        Assert.That(registry.Sweep(apply: true).Total, Is.EqualTo(0));
        Assert.That(File.Exists(registry.FileFor(key)), Is.True);
    }

    /// <summary>
    /// A pid this process cannot look at — a Windows service that reused a stale file's id, which
    /// makes <c>HasExited</c> throw <see cref="Win32Exception"/> — is judged alive, so nothing
    /// deletes it and nothing throws (finding 1 of the 0.2.0 review, D103).
    /// </summary>
    [Test]
    public void AProcessThatCannotBeLookedAtIsAliveAndIsNeverSwept()
    {
        const int Denied = int.MaxValue - 11;

        using var home = TempWorkspace.Create("sessions-denied");
        var registry = RegistryWith(
            home,
            hasExited: static pid => pid == Denied ? throw new Win32Exception(5, "Access is denied.") : true);
        var key = SessionRegistry.KeyFor("/w/Denied/Denied.slnx");

        registry.Write(key, Session(key, processId: Denied));

        Assert.That(registry.Inspect(key).Alive, Is.True);
        Assert.That(registry.Sweep(apply: true).Sessions, Is.EqualTo(0));
        Assert.That(File.Exists(registry.FileFor(key)), Is.True);
        Assert.That(registry.TryRead(key), Is.Not.Null);
    }

    /// <summary>Only "no such process" and "it has exited" mean gone; every other answer means alive.</summary>
    [Test]
    public void OnlyNoSuchProcessAndHasExitedMeanGone()
    {
        const int Other = int.MaxValue - 13;

        Assert.That(SessionRegistry.IsAlive(Other, static _ => throw new ArgumentException("no such process")), Is.False);
        Assert.That(SessionRegistry.IsAlive(Other, static _ => throw new InvalidOperationException("it exited")), Is.False);
        Assert.That(SessionRegistry.IsAlive(Other, static _ => true), Is.False);
        Assert.That(SessionRegistry.IsAlive(Other, static _ => false), Is.True);
        Assert.That(SessionRegistry.IsAlive(Other, static _ => throw new Win32Exception(5, "Access is denied.")), Is.True);
        Assert.That(SessionRegistry.IsAlive(Other, static _ => throw new NotSupportedException()), Is.True);

        Assert.That(SessionRegistry.IsAlive(0, static _ => false), Is.False);
        Assert.That(SessionRegistry.IsAlive(Environment.ProcessId, static _ => throw new InvalidOperationException()), Is.True);
    }

    /// <summary>
    /// A rewrite touches the file only while it still names this host — this process <em>and</em>
    /// this pipe — and never creates one that has gone (finding 3, D102).
    /// </summary>
    [Test]
    public void ARewriteLeavesAFileThatNamesAnotherHostAlone()
    {
        using var home = TempWorkspace.Create("sessions-owner");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Owner/Owner.slnx");
        var ours = Session(key);
        var path = registry.FileFor(key);

        Assert.That(registry.Update(key, ours with { State = "running" }), Is.EqualTo(SessionUpdate.NotOurs));
        Assert.That(File.Exists(path), Is.False);

        registry.Write(key, ours with { PipeName = "claude-roslyn-lsp-share-newcomer" });
        var newcomer = File.ReadAllBytes(path);

        Assert.That(registry.Update(key, ours with { State = "dormant" }), Is.EqualTo(SessionUpdate.NotOurs));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(newcomer));

        registry.Write(key, ours with { ProcessId = ours.ProcessId + 1 });
        var other = File.ReadAllBytes(path);

        Assert.That(registry.Update(key, ours with { State = "dormant" }), Is.EqualTo(SessionUpdate.NotOurs));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(other));

        registry.Write(key, ours);

        Assert.That(registry.Update(key, ours with { State = "dormant" }), Is.EqualTo(SessionUpdate.Written));
        Assert.That(registry.TryRead(key)!.State, Is.EqualTo("dormant"));
    }

    /// <summary>
    /// While another process holds the decision lock it is deciding whether to take over, so a
    /// rewrite waits and a withdrawal leaves the file to it (D86, D102).
    /// </summary>
    [Test]
    public void NothingIsRewrittenOrWithdrawnWhileAnotherProcessIsDeciding()
    {
        using var home = TempWorkspace.Create("sessions-deciding");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Deciding/Deciding.slnx");
        var ours = Session(key);

        registry.Write(key, ours);

        using (var deciding = Registry(home).TryAcquireLock(key))
        {
            Assert.That(deciding, Is.Not.Null);
            Assert.That(registry.Update(key, ours with { State = "dormant" }), Is.EqualTo(SessionUpdate.Busy));
            Assert.That(registry.DeleteIfOwned(key, ours.ProcessId, ours.PipeName), Is.False);
        }

        Assert.That(registry.TryRead(key)!.State, Is.Null);
        Assert.That(registry.Update(key, ours with { State = "dormant" }), Is.EqualTo(SessionUpdate.Written));
    }

    /// <summary>A stopping host withdraws its own file and only its own (finding 3).</summary>
    [Test]
    public void AStoppingHostWithdrawsOnlyItsOwnFile()
    {
        using var home = TempWorkspace.Create("sessions-withdraw");
        var registry = Registry(home);
        var key = SessionRegistry.KeyFor("/w/Withdraw/Withdraw.slnx");
        var ours = Session(key);

        registry.Write(key, ours with { PipeName = "claude-roslyn-lsp-share-newcomer" });

        Assert.That(registry.DeleteIfOwned(key, ours.ProcessId, ours.PipeName), Is.False);
        Assert.That(File.Exists(registry.FileFor(key)), Is.True);

        registry.Write(key, ours);

        Assert.That(registry.DeleteIfOwned(key, ours.ProcessId, ours.PipeName), Is.True);
        Assert.That(File.Exists(registry.FileFor(key)), Is.False);
    }

    /// <summary>
    /// Orphaned locks are swept where a file pending deletion cannot be reopened — Windows — and left
    /// alone where an unlink races a lock, which is everywhere else (finding 8, D103).
    /// </summary>
    [Test]
    public void LockFilesAreSweptOnWindowsOnly()
    {
        using var home = TempWorkspace.Create("sessions-locks");
        var orphan = SessionRegistry.KeyFor("/w/Orphan/Orphan.slnx");

        var unix = RegistryWith(home, sweepLocks: false);

        using (unix.TryAcquireLock(orphan))
        {
        }

        Assert.That(unix.Sweep(apply: true).Locks, Is.EqualTo(0));
        Assert.That(File.Exists(unix.LockFor(orphan)), Is.True);

        Assert.That(Registry(home).Sweep(apply: false).Locks, Is.EqualTo(OperatingSystem.IsWindows() ? 1 : 0));

        var windows = RegistryWith(home, sweepLocks: true);

        Assert.That(windows.Sweep(apply: true).Locks, Is.EqualTo(1));
        Assert.That(File.Exists(windows.LockFor(orphan)), Is.False);
    }

    /// <summary>
    /// A file written in another pid namespace — another container sharing this home — is never
    /// judged by a pid that means nothing here; one with no namespace, or with ours, is judged as
    /// before (finding 8, D103).
    /// </summary>
    [Test]
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

        Assert.That(report.ForeignNamespace, Is.True);
        Assert.That(report.Verdict, Contains.Substring("another pid namespace").Using(StringComparison.Ordinal));
        Assert.That(registry.Inspect(local).ForeignNamespace, Is.False);

        Assert.That(registry.Sweep(apply: true).Sessions, Is.EqualTo(2));

        Assert.That(File.Exists(registry.FileFor(foreign)), Is.True);
        Assert.That(File.Exists(registry.FileFor(unmarked)), Is.False);
        Assert.That(File.Exists(registry.FileFor(local)), Is.False);
    }

    /// <summary>The namespace is a Linux fact, read from <c>/proc/self/ns/pid</c>; elsewhere there is none.</summary>
    [Test]
    public void ThePidNamespaceIsReadOnLinuxAndNowhereElse()
    {
        var current = SessionRegistry.CurrentPidNamespace();

        if (OperatingSystem.IsLinux())
        {
            Assert.That(current, Is.Not.Null);
            Assert.That(current, Does.StartWith("pid:[").Using(StringComparison.Ordinal));
        }
        else
        {
            Assert.That(current, Is.Null);
        }
    }

    [Test]
    public void SweepingADirectoryThatDoesNotExistFindsNothing()
    {
        using var home = TempWorkspace.Create("sessions-nothing");

        Assert.That(Registry(home).Sweep(apply: true).Total, Is.EqualTo(0));
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
