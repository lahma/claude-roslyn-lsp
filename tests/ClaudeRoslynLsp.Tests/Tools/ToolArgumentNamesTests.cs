using System.IO.Pipelines;
using System.Text.Json;

using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Mcp;
using ClaudeRoslynLsp.Mcp.Tools;
using ClaudeRoslynLsp.Tests.Mcp;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Tools;

/// <summary>
/// D109: a call whose argument names are not the ones the tool advertised is answered before the tool
/// runs, with the name it most likely meant — instead of the SDK binding what it can and either
/// failing with no explanation (a required argument) or, for <c>preview</c>, writing to disk while the
/// caller believed it was looking at a diff.
/// </summary>
public class ToolArgumentNamesTests
{
    private static IEnumerable<string> ToolNames => ToolTestHost.Tools.Select(tool => tool.ProtocolTool.Name);

    /// <summary>The check reads the published schema, so every name a tool advertises must pass it.</summary>
    [TestCaseSource(nameof(ToolNames))]
    public void EveryAdvertisedArgumentNameIsAccepted(string name)
    {
        var tool = ToolTestHost.Find(name).ProtocolTool;
        var declared = tool.InputSchema.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Select(property => property.Name).ToList()
            : [];

        Assert.That(ToolArgumentNames.Check(tool, declared), Is.Null);
    }

    /// <summary>The case the filter exists for: without it, this rename would be applied.</summary>
    [TestCase("renameSymbol")]
    [TestCase("formatCode")]
    [TestCase("applyCodeAction")]
    [TestCase("fixDiagnostics")]
    public void AMisCasedPreviewIsRefusedRatherThanDropped(string name)
    {
        var tool = ToolTestHost.Find(name).ProtocolTool;
        var required = tool.InputSchema.TryGetProperty("required", out var names)
            ? names.EnumerateArray().Select(element => element.GetString()!).ToList()
            : [];

        var problem = ToolArgumentNames.Check(tool, [.. required, "Preview"]);

        Assert.That(problem, Does.StartWith($"{name} was not run: ").Using(StringComparison.Ordinal));
        Assert.That(problem, Contains.Substring("'Preview' is spelled 'preview': argument names are case-sensitive.")
            .Using(StringComparison.Ordinal));
    }

    [Test]
    public void AMisCasedRequiredArgumentIsNamedRatherThanReportedMissing()
    {
        var problem = ToolArgumentNames.Check(ToolTestHost.Find("resolveSymbol").ProtocolTool, ["Symbol"]);

        Assert.That(problem, Contains.Substring("'Symbol' is spelled 'symbol': argument names are case-sensitive.")
            .Using(StringComparison.Ordinal));
        Assert.That(problem, !Contains.Substring("required").Using(StringComparison.Ordinal));
        Assert.That(problem, Does.EndWith("Its arguments are symbol, kind, maxResults.").Using(StringComparison.Ordinal));
    }

    [TestCase("findReferences", "includeDeclarations", "includeDeclaration")]
    [TestCase("formatCode", "path", "paths")]
    [TestCase("formatCode", "organize", "organizeUsings")]
    [TestCase("getDiagnostics", "includeAnalyzer", "includeAnalyzers")]
    public void ANearMissIsAnsweredWithWhatItMostLikelyMeant(string tool, string sent, string meant)
    {
        var problem = ToolArgumentNames.Check(ToolTestHost.Find(tool).ProtocolTool, [sent]);

        Assert.That(problem, Contains.Substring($"'{sent}' is not one of its arguments; did you mean '{meant}'?")
            .Using(StringComparison.Ordinal));
    }

    [Test]
    public void AnUnrelatedNameGetsTheAcceptedNamesAndNoGuess()
    {
        var problem = ToolArgumentNames.Check(ToolTestHost.Find("getTypeMembers").ProtocolTool, ["type", "solution"]);

        Assert.That(problem, Contains.Substring("'solution' is not one of its arguments.").Using(StringComparison.Ordinal));
        Assert.That(problem, !Contains.Substring("did you mean").Using(StringComparison.Ordinal));
    }

    [Test]
    public void AMissingRequiredArgumentIsNamed()
    {
        var problem = ToolArgumentNames.Check(ToolTestHost.Find("renameSymbol").ProtocolTool, ["symbol"]);

        Assert.That(problem, Contains.Substring("'newName' is required and was not passed.").Using(StringComparison.Ordinal));
    }

    [Test]
    public void AnyNameIsRefusedByAToolThatTakesNone()
    {
        var problem = ToolArgumentNames.Check(ToolTestHost.Find("getWorkspaceStatus").ProtocolTool, ["verbose"]);

        Assert.That(problem, Does.EndWith("getWorkspaceStatus takes no arguments.").Using(StringComparison.Ordinal));
    }

    [Test]
    public void ACallWithNoArgumentsToAToolWithoutRequiredOnesPasses() =>
        Assert.That(ToolArgumentNames.Check(ToolTestHost.Find("getWorkspaceStatus").ProtocolTool, []), Is.Null);

    // ------------------------------------------------------------------ over the protocol

    /// <summary>
    /// The same refusal end to end: the production registration (<c>McpServerSetup.RegisterTools</c>)
    /// served over in-memory streams to a real client, so the filter is proven to be registered and to
    /// answer as a tool error the model can read and retry from.
    /// </summary>
    [Test]
    public async Task OverTheProtocolAMisCasedPreviewIsAToolErrorNamingTheRightSpelling()
    {
        await using var session = await InMemorySession.StartAsync(TestContext.CurrentContext.CancellationToken);

        var result = await session.Client.CallToolAsync(
            new CallToolRequestParams
            {
                Name = "renameSymbol",
                Arguments = Arguments(("symbol", "Circle"), ("newName", "Round"), ("Preview", true)),
            },
            TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsError, Is.True);
        Assert.That(Text(result), Contains.Substring("'Preview' is spelled 'preview'").Using(StringComparison.Ordinal));
        Assert.That(session.Engine.Calls, Is.Empty, "the tool must not have run, so Roslyn must not have been asked anything");
    }

    /// <summary>A correctly spelled call goes through to the tool.</summary>
    [Test]
    public async Task OverTheProtocolACorrectlySpelledCallReachesTheTool()
    {
        await using var session = await InMemorySession.StartAsync(TestContext.CurrentContext.CancellationToken);

        var result = await session.Client.CallToolAsync(
            new CallToolRequestParams { Name = "getWorkspaceStatus" },
            TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsError, Is.Not.True);
        Assert.That(Text(result), !Contains.Substring("was not run").Using(StringComparison.Ordinal));
    }

    private static Dictionary<string, JsonElement> Arguments(params (string Name, object Value)[] pairs) =>
        pairs.ToDictionary(
            pair => pair.Name,
            pair => pair.Value switch
            {
                bool flag => JsonSerializer.SerializeToElement(flag, ToolTestHost.SerializerOptions),
                _ => JsonSerializer.SerializeToElement((string)pair.Value, ToolTestHost.SerializerOptions),
            });

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    /// <summary>The production tool registration and a client, connected by two in-memory pipes.</summary>
    private sealed class InMemorySession : IAsyncDisposable
    {
        private readonly Pipe _toServer;
        private readonly Pipe _toClient;
        private readonly ServiceProvider _provider;
        private readonly CancellationTokenSource _stop;
        private readonly Task _server;

        private InMemorySession(
            Pipe toServer,
            Pipe toClient,
            ServiceProvider provider,
            CancellationTokenSource stop,
            Task server,
            McpClient client,
            FakeRoslynEngine engine)
        {
            _toServer = toServer;
            _toClient = toClient;
            _provider = provider;
            _stop = stop;
            _server = server;
            Client = client;
            Engine = engine;
        }

        internal McpClient Client { get; }

        internal FakeRoslynEngine Engine { get; }

        internal static async Task<InMemorySession> StartAsync(CancellationToken cancellationToken)
        {
            var toServer = new Pipe();
            var toClient = new Pipe();
            var engine = new FakeRoslynEngine();
            var options = ClaudeRoslynLspOptions.FromEnvironment(static _ => null);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(options);
            McpServerSetup.RegisterToolServices(services, options, _ => engine);

            var builder = services
                .AddMcpServer(serverOptions => serverOptions.ServerInfo = new Implementation
                {
                    Name = "claude-roslyn-lsp",
                    Version = "test",
                })
                .WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream());

            McpServerSetup.RegisterTools(builder, ToolTestHost.SerializerOptions);

            var provider = services.BuildServiceProvider();
            var stop = new CancellationTokenSource();
            var server = provider.GetRequiredService<McpServer>().RunAsync(stop.Token);

            var client = await McpClient.CreateAsync(
                new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
                cancellationToken: cancellationToken);

            return new InMemorySession(toServer, toClient, provider, stop, server, client, engine);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _stop.CancelAsync();

            try
            {
                await _server;
            }
            catch (OperationCanceledException)
            {
                // The cancellation above is how the server is stopped.
            }

            await _toServer.Writer.CompleteAsync();
            await _toClient.Writer.CompleteAsync();
            await _provider.DisposeAsync();
            _stop.Dispose();
        }
    }
}
