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

    private static SessionRegistry Registry(TempWorkspace home) =>
        SessionRegistry.ForHome(home.Root, NullLogger.Instance);

    private static SharedSession Session(string key, int? processId = null) =>
        new(
            processId ?? Environment.ProcessId,
            ServerVersion.Value,
            SharedSession.PipeTransport,
            "claude-roslyn-lsp-share-" + key,
            "/w/Fixture/Fixture.slnx",
            DateTimeOffset.UtcNow);
}
