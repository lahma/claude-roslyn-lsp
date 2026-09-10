using System.Diagnostics;
using System.Text;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Edits;
using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Mcp.Tools;
using ClaudeRoslynLsp.Roslyn;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Live;

/// <summary>
/// One <see cref="OwnedRoslynEngine"/> over a private copy of <c>HelloSolution</c>, for the whole
/// MCP tool suite.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own copy, and its own Roslyn.</b> The tool tests write files — a rename rewrites three,
/// a fix-all deletes a using, a code action creates <c>Rectangle.cs</c> — so the checked-in fixture
/// would end up dirty and the next run's assertions pre-satisfied. It is also a separate copy from
/// <see cref="AdapterLiveFixture"/>'s, because the two suites edit the same files in different ways
/// and xunit runs collections in parallel.
/// </para>
/// <para>
/// <b>One file is converted to CRLF with a BOM on the way in (D81).</b> <c>.gitattributes</c> forces
/// <c>*.cs</c> to LF on checkout, so a CRLF fixture cannot be committed — and the property that
/// matters is precisely that a rename through <see cref="WorkspaceEditApplier"/> writes a file back
/// the way it found it. Converting <c>Caller.cs</c> here gives the assertion something real to hold
/// on to: it is one of the files the rename rewrites, and a codec that silently normalised it would
/// turn a one-word refactoring into a diff touching every line.
/// </para>
/// </remarks>
public sealed class McpToolsLiveFixture : IAsyncLifetime
{
    /// <summary>The fixture file that is stored CRLF with a UTF-8 BOM.</summary>
    internal const string CrlfBomFile = "Hello.Core/Caller.cs";

    private static readonly string[] SkippedDirectories = ["bin", "obj", ".vs"];

    private TempWorkspace? _home;
    private TempWorkspace? _workspace;
    private OwnedRoslynEngine? _engine;

    /// <summary>Whether the live tests were asked for.</summary>
    public bool Enabled { get; } =
        Environment.GetEnvironmentVariable(AdapterLiveFixture.EnableVariable) is "1" or "true" or "TRUE";

    /// <summary>The copied solution's root directory, which is also the workspace root (D68).</summary>
    public string WorkspaceRoot => _workspace?.Root ?? throw new InvalidOperationException("not initialised");

    /// <summary>The copied <c>HelloSolution.slnx</c>.</summary>
    public string SolutionPath => Path.Combine(WorkspaceRoot, "HelloSolution.slnx");

    /// <summary>How long acquiring Roslyn took.</summary>
    public TimeSpan AcquisitionElapsed { get; private set; }

    /// <summary>How long the workspace took to load, measured from the engine being started.</summary>
    public TimeSpan LoadElapsed { get; private set; }

    /// <summary>The state the first <c>EnsureReadyAsync</c> returned.</summary>
    internal WorkspaceState? Ready { get; private set; }

    /// <summary>The engine every tool call in this collection runs against.</summary>
    internal OwnedRoslynEngine Engine => _engine ?? throw new InvalidOperationException("not initialised");

    /// <summary>The tool context, wired exactly as <c>McpServerSetup</c> wires it.</summary>
    internal RoslynToolContext Context { get; private set; } = null!;

    /// <summary>Where the engine's own log goes; redirected per test.</summary>
    internal RelayLogger Relay { get; } = new();

    /// <summary>Ends the test as skipped when the live tests are switched off.</summary>
    public void SkipIfDisabled()
    {
        if (!Enabled)
        {
            Assert.Skip(
                $"Set {AdapterLiveFixture.EnableVariable}=1 to run the live MCP tool tests "
                + "(they start a real Roslyn).");
        }
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        _workspace = TempWorkspace.Create("live-mcp");
        CopyTree(RepositoryLayout.Path_("tests", "fixtures", "HelloSolution"), _workspace.Root);

        foreach (var name in new[] { "Directory.Build.props", "Directory.Packages.props" })
        {
            File.Copy(RepositoryLayout.Path_("tests", "fixtures", name), Path.Combine(_workspace.Root, name), true);
        }

        ConvertToCrlfWithBom(Path.Combine(_workspace.Root, CrlfBomFile.Replace('/', Path.DirectorySeparatorChar)));

        var configuredHome = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_LSP_HOME");
        string home;

        if (string.IsNullOrWhiteSpace(configuredHome))
        {
            _home = TempWorkspace.Create("live-mcp-home");
            home = _home.Root;
        }
        else
        {
            home = configuredHome;
        }

        var options = new ClaudeRoslynLspOptions
        {
            Home = home,
            Solution = SolutionPath,
            SolutionVariable = "CLAUDE_ROSLYN_LSP_SOLUTION",
            RoslynLogLevel = LogLevel.Information,
        };

        var paths = AdapterPaths.Resolve(options, static _ => null);

        using (var client = NuGetPayloadDownloader.CreateHttpClient())
        {
            var locator = new RoslynServerLocator(
                options,
                paths,
                NullLogger.Instance,
                downloaderFactory: () => new NuGetPayloadDownloader(client, paths, NullLogger.Instance));

            var acquiring = Stopwatch.StartNew();
            var resolution = await locator.ResolveAsync(allowDownload: true);
            acquiring.Stop();

            AcquisitionElapsed = acquiring.Elapsed;
            Assert.True(resolution.IsResolved, resolution.Failure);
        }

        var factory = new LaunchedRoslynFactory(options, paths, Relay);

        _engine = new OwnedRoslynEngine(
            factory,
            options,
            () => new WorkspaceSelection(SolutionPath, [], "the live MCP fixture's own copy"),
            WorkspaceRoot,
            TimeProvider.System,
            Relay,
            () => new RoslynBackendIdentity(RoslynServerManifest.Version, factory.ProcessId));

        var guard = new WorkspacePathGuard(WorkspaceRoot);

        Context = new RoslynToolContext(
            _engine,
            options,
            guard,
            new WorkspaceEditApplier(guard),
            new EditCache());

        // The tool layer's error funnel logs the stack trace of anything it did not expect, and
        // without this it logs it to a null logger — which turns a live failure into "failed
        // unexpectedly: Operation is not valid" with nothing behind it.
        ToolErrors.UseLoggerFactory(new RelayLoggerFactory(Relay));

        var loading = Stopwatch.StartNew();
        _engine.Start();

        // The same budget a tool call spends, and the same call: if this returns anything but Ready
        // the whole suite says so once rather than ten times.
        Ready = await _engine.EnsureReadyAsync(TimeSpan.FromSeconds(options.ReadyTimeoutSeconds), default);
        loading.Stop();

        LoadElapsed = loading.Elapsed;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
        }

        _workspace?.Dispose();
        _home?.Dispose();
    }

    /// <summary>The absolute path of a fixture file.</summary>
    /// <param name="relativePath">Its workspace-relative, forward-slashed path.</param>
    internal string Path_(string relativePath) =>
        Path.Combine(WorkspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Rewrites a file as CRLF with a UTF-8 BOM, whatever it was.</summary>
    private static void ConvertToCrlfWithBom(string path)
    {
        var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace("\n", "\r\n", StringComparison.Ordinal), new UTF8Encoding(true));
    }

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

/// <summary>An <see cref="ILoggerFactory"/> that answers with one <see cref="RelayLogger"/>.</summary>
/// <param name="relay">The relay every category resolves to.</param>
internal sealed class RelayLoggerFactory(RelayLogger relay) : ILoggerFactory
{
    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => relay;

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>
/// A logger whose destination can be changed after the object exists.
/// </summary>
/// <remarks>
/// The engine is built once, in the fixture, and lives longer than any one test; xunit's output
/// helper belongs to one test and throws once that test has finished. So the fixture holds this and
/// each test points it at its own output for the duration — which is what makes a live failure a
/// diagnosis (which directories were watched, what the acquisition chain decided) rather than a
/// mystery.
/// </remarks>
internal sealed class RelayLogger : ILogger
{
    private volatile ILogger _target = NullLogger.Instance;

    /// <summary>Sends everything to this logger until it is changed again.</summary>
    /// <param name="target">Where the lines go.</param>
    internal void PointAt(ILogger target) => _target = target;

    /// <summary>Stops writing anywhere.</summary>
    internal void Detach() => _target = NullLogger.Instance;

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => _target.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => _target.IsEnabled(logLevel);

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _target.Log(logLevel, eventId, state, exception, formatter);
}
