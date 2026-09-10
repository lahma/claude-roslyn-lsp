using System.Runtime.InteropServices;
using System.Text.Json;

using ClaudeRoslynLsp.Cli;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Mcp.Models;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ClaudeRoslynLsp.Mcp;

/// <summary>
/// Builds and runs the stdio MCP server — the <c>mcp</c> verb.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in this file — or anything it starts — may write to stdout: stdout is the JSON-RPC channel
/// and a stray write corrupts the protocol stream. Logging goes to stderr, through the one factory
/// <see cref="CliRuntime"/> owns.
/// </para>
/// <para>
/// This work package's version completes a handshake and registers no tools. WP5 adds the refactoring
/// surface: the shared Roslyn session in the graph below, and one
/// <c>WithTools&lt;T&gt;(jsonOptions)</c> call per tool class in <see cref="RegisterTools"/>.
/// </para>
/// </remarks>
internal static class McpServerSetup
{
    /// <summary>
    /// Runs the server until stdin closes or the process is asked to shut down, then returns the
    /// process exit code.
    /// </summary>
    internal static async Task<int> RunStdioAsync()
    {
        using var shutdown = new CancellationTokenSource();

        // Cooperative shutdown: cancel the server loop rather than letting the runtime tear the
        // process down mid-write. There is no generic host here to own lifetime for us (D5).
        using var sigInt = RegisterShutdownSignal(PosixSignal.SIGINT, shutdown);
        using var sigTerm = RegisterShutdownSignal(PosixSignal.SIGTERM, shutdown);

        // Environment variables only. This is the one and only read; everything downstream takes the
        // resulting options object.
        var options = ClaudeRoslynLspOptions.FromEnvironment();

        // D5: a bare ServiceCollection, not Host.CreateApplicationBuilder - no configuration
        // providers, metrics or lifetime machinery in the cold-start path.
        var services = new ServiceCollection();

        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(options.LogLevel);

            // The one line that keeps the protocol stream clean: every log record, at every level,
            // goes to stderr. stdout belongs to JSON-RPC alone.
            logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        });

        services.AddSingleton(options);

        var jsonOptions = CreateToolSerializerOptions();

        var builder = services
            .AddMcpServer(serverOptions =>
            {
                serverOptions.ServerInfo = new Implementation
                {
                    Name = ServerVersion.Name,
                    Version = ServerVersion.Value,
                };

                // Sent to the client at initialize: the conventions no single tool description can
                // carry.
                serverOptions.ServerInstructions = ServerInstructions.Text;

                // A server with no tool registered at all advertises no `tools` capability and has
                // nothing to answer `tools/list` with, which would make the SmokeTest handshake fail
                // for a reason that has nothing to do with the transport. An empty collection is the
                // honest answer - "tools are supported, there are none yet" - and `??=` keeps the
                // line correct once WithTools<T> fills the collection in.
                serverOptions.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();
            })
            .WithStdioServerTransport();

        RegisterTools(builder, jsonOptions);

        await using var provider = services.BuildServiceProvider();

        // The SDK registers McpServer as a singleton; running it directly is the SDK's own AOT
        // test-app shape (D5).
        var server = provider.GetRequiredService<McpServer>();

        try
        {
            await server.RunAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Signalled shutdown is a normal exit.
        }

        return CliDispatcher.ExitSuccess;
    }

    /// <summary>
    /// Registers the tool classes. Empty in this work package, and the one place WP5 adds to.
    /// </summary>
    /// <remarks>
    /// Never <c>WithToolsFromAssembly()</c> — it is not AOT-safe (IL2026) and would be a build error
    /// under the analyzers this project turns on explicitly. One <c>WithTools&lt;T&gt;</c> per class,
    /// each given <paramref name="jsonOptions"/>, so every generated schema comes from the same
    /// resolver chain.
    /// </remarks>
    /// <param name="builder">The MCP server builder.</param>
    /// <param name="jsonOptions">The tool-facing serializer options from <see cref="CreateToolSerializerOptions"/>.</param>
    internal static void RegisterTools(IMcpServerBuilder builder, JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(jsonOptions);
    }

    /// <summary>
    /// Builds the tool-facing serializer options that every <c>WithTools&lt;T&gt;</c> registration —
    /// and therefore every generated tool schema — is created with.
    /// </summary>
    /// <remarks>
    /// Ours goes FIRST in the chain so that JIT and AOT resolve identically; the SDK resolver stays
    /// second for MCP protocol types, which our context returns null for. The chain is cleared first
    /// because copying the SDK's options copies its chain too, and a duplicate entry ahead of ours
    /// would decide the tie.
    /// <para>
    /// Factored out of <see cref="RunStdioAsync"/> so the tests can generate schemas with the exact
    /// options the server ships, rather than a hand-rolled copy that could drift out of step.
    /// </para>
    /// </remarks>
    internal static JsonSerializerOptions CreateToolSerializerOptions()
    {
        var jsonOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        jsonOptions.TypeInfoResolverChain.Clear();
        jsonOptions.TypeInfoResolverChain.Add(RoslynToolJsonContext.Default);
        jsonOptions.TypeInfoResolverChain.Add(McpJsonUtilities.DefaultOptions.TypeInfoResolver!);
        jsonOptions.MakeReadOnly();

        return jsonOptions;
    }

    private static PosixSignalRegistration? RegisterShutdownSignal(PosixSignal signal, CancellationTokenSource shutdown)
    {
        try
        {
            return PosixSignalRegistration.Create(signal, context =>
            {
                // Suppress the default action (immediate termination) and unwind the server loop.
                context.Cancel = true;

                try
                {
                    shutdown.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already shutting down.
                }
            });
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }
}
