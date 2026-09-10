using System.Diagnostics;

using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Roslyn;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Live;

/// <summary>
/// The one test that acquires and launches the real pinned Roslyn.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in through <c>CLAUDE_ROSLYN_LSP_LIVE_TESTS=1</c>, because it downloads about 70 MB and starts
/// a quarter-gigabyte child process — neither of which belongs in a run somebody triggered by saving
/// a file. Everything it covers is covered in miniature by the unit tests; what it adds is the part
/// no stub can assert, which is that the layout inside the real package, the real command line and
/// the real pipe handshake are what this adapter believes they are.
/// </para>
/// <para>
/// Both transports are exercised, because they fail differently: the pipe leg proves that Roslyn
/// connects to a pipe the adapter created (C8), and the stdio leg proves that the same server's
/// stdout carries frames rather than the 646-byte banner the pipe leg has to drain (C7).
/// </para>
/// </remarks>
public class RoslynLaunchLiveTests : IClassFixture<RoslynLiveFixture>
{
    private readonly RoslynLiveFixture _fixture;
    private readonly ITestOutputHelper _output;

    public RoslynLaunchLiveTests(RoslynLiveFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData("pipe")]
    [InlineData("stdio")]
    public async Task TheRealServerStartsHandshakesAndExits(string transportName)
    {
        _fixture.SkipIfDisabled();

        // Named rather than typed because the enum is internal and a public test signature cannot
        // mention it - which also puts the real CLAUDE_ROSLYN_LSP_TRANSPORT parser on the live path.
        var transport = RoslynProcessLauncher.ParseTransport(transportName, NullLogger.Instance);

        var resolution = _fixture.Resolution!;

        Assert.True(resolution.IsResolved, resolution.Failure);
        Assert.True(File.Exists(resolution.LaunchTarget), resolution.LaunchTarget);

        using var guard = ChildProcessGuard.Create(NullLogger.Instance);
        var launcher = new RoslynProcessLauncher(NullLogger.Instance, guard);

        var request = new RoslynLaunchRequest
        {
            Server = resolution,
            Host = DotnetHostLocator.Resolve(),
            Paths = _fixture.Paths!,
            Transport = transport,
        };

        var stopwatch = Stopwatch.StartNew();
        var result = await RoslynHandshakeProbe.RunAsync(
            launcher, request, TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        stopwatch.Stop();

        _output.WriteLine($"transport            {transport}");
        _output.WriteLine($"connect              {result.ConnectElapsed.TotalMilliseconds:F0} ms");
        _output.WriteLine($"initialize           {result.InitializeRoundTrip.TotalMilliseconds:F0} ms");
        _output.WriteLine($"shutdown+exit        {result.ShutdownElapsed.TotalMilliseconds:F0} ms");
        _output.WriteLine($"total               {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        _output.WriteLine($"peak working set     {result.PeakWorkingSet / (1024 * 1024)} MB");
        _output.WriteLine($"serverInfo.name      {result.ServerName ?? "(none)"}");
        _output.WriteLine($"serverInfo.version   {result.ServerVersion ?? "(none)"}");
        _output.WriteLine($"capabilities         {string.Join(", ", result.Capabilities)}");
        _output.WriteLine($"window/logMessage    {result.LogMessages.Count} line(s)");

        Assert.True(result.Succeeded, result.Failure);
        Assert.NotEmpty(result.Capabilities);

        // C26: Roslyn advertises these statically whatever the client declared, which is why WP4's
        // authored capability document has to filter what it forwards. Asserted here so that a build
        // which stops doing it is noticed by this repository rather than by a user.
        Assert.Contains("textDocumentSync", result.Capabilities);
        Assert.Contains("semanticTokensProvider", result.Capabilities);

        Assert.NotNull(result.ExitCode);
        Assert.True(
            result.ShutdownElapsed < RoslynHandshakeProbe.ExitBudget,
            $"The server took {result.ShutdownElapsed.TotalSeconds:F1} s to exit after `exit`.");
    }

    [Fact]
    public void ThePayloadHasTheLayoutTheManifestDescribes()
    {
        _fixture.SkipIfDisabled();

        var directory = _fixture.Resolution!.Directory!;

        Assert.True(File.Exists(Path.Combine(directory, RoslynServerManifest.ServerAssemblyName)));
        Assert.True(File.Exists(Path.Combine(directory, "Microsoft.CodeAnalysis.LanguageServer.runtimeconfig.json")));

        // C1: the build hosts are what evaluate project files, so extracting only the top level would
        // give a server that loads no projects and says nothing about why.
        Assert.True(Directory.Exists(Path.Combine(directory, "BuildHost-netcore")));
        Assert.True(Directory.Exists(Path.Combine(directory, "BuildHost-net472")));

        var runtimeConfig = File.ReadAllText(
            Path.Combine(directory, "Microsoft.CodeAnalysis.LanguageServer.runtimeconfig.json"));

        Assert.Contains("\"rollForward\": \"Major\"", runtimeConfig, StringComparison.Ordinal);
        Assert.Contains("System.GC.Server", runtimeConfig, StringComparison.Ordinal);

        _output.WriteLine($"payload directory    {directory}");
        _output.WriteLine($"files                {Directory.GetFiles(directory).Length} at the top level");
        _output.WriteLine($"download             {_fixture.AcquisitionElapsed.TotalSeconds:F1} s ({_fixture.Resolution.Kind})");
    }
}

/// <summary>
/// Acquires the pinned server once for the whole live test class, into a temporary home directory.
/// </summary>
/// <remarks>
/// Honours an existing <c>CLAUDE_ROSLYN_LSP_HOME</c> so that a second local run does not pay for the
/// download again; with none set it downloads into a temp directory and deletes it afterwards, which
/// is what CI wants and what makes the acquisition path itself part of what is under test.
/// </remarks>
public sealed class RoslynLiveFixture : IAsyncLifetime
{
    /// <summary>The variable that turns these tests on.</summary>
    internal const string EnableVariable = "CLAUDE_ROSLYN_LSP_LIVE_TESTS";

    private TempWorkspace? _temp;

    /// <summary>Whether the live tests were asked for.</summary>
    public bool Enabled { get; } =
        Environment.GetEnvironmentVariable(EnableVariable) is "1" or "true" or "TRUE";

    /// <summary>Where the payload ended up.</summary>
    internal AdapterPaths? Paths { get; private set; }

    /// <summary>What the locator decided.</summary>
    internal RoslynResolution? Resolution { get; private set; }

    /// <summary>How long acquisition took, download included.</summary>
    public TimeSpan AcquisitionElapsed { get; private set; }

    /// <summary>Ends the test as skipped when the live tests are switched off.</summary>
    public void SkipIfDisabled()
    {
        if (!Enabled)
        {
            Assert.Skip($"Set {EnableVariable}=1 to run the live Roslyn tests (about 70 MB of download).");
        }
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        var configured = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_LSP_HOME");
        string home;

        if (string.IsNullOrWhiteSpace(configured))
        {
            _temp = TempWorkspace.Create("live");
            home = _temp.Root;
        }
        else
        {
            home = configured;
        }

        var options = new ClaudeRoslynLspOptions { Home = home };
        Paths = AdapterPaths.Resolve(options, static _ => null);

        using var client = NuGetPayloadDownloader.CreateHttpClient();

        var locator = new RoslynServerLocator(
            options,
            Paths,
            NullLogger.Instance,
            downloaderFactory: () => new NuGetPayloadDownloader(client, Paths, NullLogger.Instance));

        var stopwatch = Stopwatch.StartNew();
        Resolution = await locator.ResolveAsync(allowDownload: true);
        stopwatch.Stop();

        AcquisitionElapsed = stopwatch.Elapsed;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _temp?.Dispose();
        return ValueTask.CompletedTask;
    }
}
