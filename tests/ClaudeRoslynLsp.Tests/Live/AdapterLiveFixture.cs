using System.Diagnostics;

using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Roslyn;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Live;

/// <summary>
/// Acquires the pinned Roslyn once and copies <c>tests/fixtures/HelloSolution</c> somewhere it can
/// be edited.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fixture is copied, not used in place, and the copy is what makes the tests honest.</b> The
/// live run writes a new file into the solution and edits another, which against the checked-in
/// copy would leave the repository dirty and the next run's assertions pre-satisfied. A temporary
/// directory also gives the watcher a tree nothing else on the machine is touching.
/// </para>
/// <para>
/// The Roslyn home honours an existing <c>CLAUDE_ROSLYN_LSP_HOME</c> so a second local run reuses a
/// warm cache; with none set it downloads into a temporary home, which is what CI wants and what
/// puts the acquisition path itself under test.
/// </para>
/// <para>
/// <b>The copy is deliberately <em>not</em> restored first.</b> <c>obj/</c> is skipped, so Roslyn has
/// to restore it server-side (C30) exactly as it would for a freshly cloned repository — and that
/// turned out to be the path that needs the watch bridge most (C48): the restore writes
/// <c>project.assets.json</c>, and unless somebody tells Roslyn that happened, the load never
/// completes at all.
/// </para>
/// </remarks>
public sealed class AdapterLiveFixture : IAsyncLifetime
{
    /// <summary>The variable that turns the live tests on.</summary>
    internal const string EnableVariable = "CLAUDE_ROSLYN_LSP_LIVE_TESTS";

    /// <summary>Directories that are never copied, because they are build output.</summary>
    private static readonly string[] SkippedDirectories = ["bin", "obj", ".vs"];

    private TempWorkspace? _home;
    private TempWorkspace? _workspace;

    /// <summary>Whether the live tests were asked for.</summary>
    public bool Enabled { get; } =
        Environment.GetEnvironmentVariable(EnableVariable) is "1" or "true" or "TRUE";

    /// <summary>The copied solution's root directory.</summary>
    public string WorkspaceRoot => _workspace?.Root ?? throw new InvalidOperationException("not initialised");

    /// <summary>The copied <c>HelloSolution.slnx</c>.</summary>
    public string SolutionPath => Path.Combine(WorkspaceRoot, "HelloSolution.slnx");

    /// <summary>How long acquiring Roslyn took.</summary>
    public TimeSpan AcquisitionElapsed { get; private set; }

    /// <summary>The options every live session is built from.</summary>
    internal ClaudeRoslynLspOptions Options { get; private set; } = new();

    /// <summary>Where the payload lives.</summary>
    internal AdapterPaths? Paths { get; private set; }

    /// <summary>What the acquisition chain decided.</summary>
    internal RoslynResolution? Resolution { get; private set; }

    /// <summary>Ends the test as skipped when the live tests are switched off.</summary>
    public void SkipIfDisabled()
    {
        if (!Enabled)
        {
            Assert.Skip($"Set {EnableVariable}=1 to run the live adapter tests (they start a real Roslyn).");
        }
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        _workspace = TempWorkspace.Create("live-solution");
        CopyTree(RepositoryLayout.Path_("tests", "fixtures", "HelloSolution"), _workspace.Root);

        // The fixture's own props files live one directory above it and stop this repository's
        // conventions from reaching it; the copy needs them for the same reason.
        foreach (var name in new[] { "Directory.Build.props", "Directory.Packages.props" })
        {
            File.Copy(RepositoryLayout.Path_("tests", "fixtures", name), Path.Combine(_workspace.Root, name), true);
        }

        var configuredHome = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_LSP_HOME");
        string home;

        if (string.IsNullOrWhiteSpace(configuredHome))
        {
            _home = TempWorkspace.Create("live-home");
            home = _home.Root;
        }
        else
        {
            home = configuredHome;
        }

        Options = new ClaudeRoslynLspOptions
        {
            Home = home,
            Solution = SolutionPath,
            SolutionVariable = "CLAUDE_ROSLYN_LSP_SOLUTION",

            // Honoured so a failing live run can be re-run with Roslyn narrating: at the default
            // Warning it says nothing at all about a load that is simply taking a long time (C6).
            RoslynLogLevel = ParseLogLevel(Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_LSP_ROSLYN_LOG_LEVEL")),
        };

        Paths = AdapterPaths.Resolve(Options, static _ => null);

        using var client = NuGetPayloadDownloader.CreateHttpClient();

        var locator = new RoslynServerLocator(
            Options,
            Paths,
            NullLogger.Instance,
            downloaderFactory: () => new NuGetPayloadDownloader(client, Paths, NullLogger.Instance));

        var stopwatch = Stopwatch.StartNew();
        Resolution = await locator.ResolveAsync(allowDownload: true);
        stopwatch.Stop();

        AcquisitionElapsed = stopwatch.Elapsed;

        Assert.True(Resolution.IsResolved, Resolution.Failure);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _workspace?.Dispose();
        _home?.Dispose();

        return ValueTask.CompletedTask;
    }

    /// <summary>Parses a log level name, treating anything unrecognised as unset.</summary>
    private static Microsoft.Extensions.Logging.LogLevel? ParseLogLevel(string? value) =>
        Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(value, ignoreCase: true, out var level)
        && Enum.IsDefined(level)
            ? level
            : null;

    /// <summary>Copies a directory tree, skipping build output.</summary>
    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(directory);

            if (SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyTree(directory, Path.Combine(destination, name));
        }
    }
}
