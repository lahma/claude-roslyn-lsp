using System.Runtime.InteropServices;
using System.Text.Json;

using ClaudeRoslynLsp.Cli;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Edits;
using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Mcp.Models;
using ClaudeRoslynLsp.Mcp.Tools;

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
/// The Roslyn session reaches the tool layer through an <c>engineFactory</c> seam, not a
/// hard reference (D60). A build with no backend registers <see cref="NotWiredRoslynEngine"/>, which
/// keeps the handshake and <c>tools/list</c> working and gives every tool one clear sentence to fail
/// with (D69); that is the shape <c>SmokeTest</c>'s MCP leg drives on every release RID.
/// </para>
/// </remarks>
internal static class McpServerSetup
{
    /// <summary>
    /// Runs the server until stdin closes or the process is asked to shut down, then returns the
    /// process exit code.
    /// </summary>
    /// <param name="engineFactory">
    /// Builds the Roslyn session the tools run against. <see langword="null"/> registers
    /// <see cref="NotWiredRoslynEngine"/>.
    /// </param>
    /// <param name="start">
    /// Called once the client has sent <c>notifications/initialized</c>, with the resolved engine.
    /// This is where the backend is launched (D76): every MCP client starts its servers when the
    /// session starts and calls a tool minutes later, so the 3-45 s solution load (C31, C53) is paid
    /// during time the user is already spending rather than by the first question.
    /// </param>
    internal static async Task<int> RunStdioAsync(
        Func<IServiceProvider, IRoslynEngine>? engineFactory = null,
        Action<IRoslynEngine>? start = null)
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
        RegisterToolServices(services, options, engineFactory);

        var jsonOptions = CreateToolSerializerOptions();

        // Resolved before the container is built so the notification handler can close over it: the
        // handler collection is enumerated once, when the server is constructed.
        IRoslynEngine? engine = null;

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
                // nothing to answer `tools/list` with. WithTools<T> fills this collection in, so the
                // `??=` is now only a guard against a future registration path that does not.
                serverOptions.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();

                if (start is null)
                {
                    return;
                }

                // The eager start (D76). `notifications/initialized` is the first moment there is a
                // client, and launching from here rather than from the first tool call is the whole
                // point: the client sends it seconds after spawning the process and may not call a
                // tool for minutes.
                serverOptions.Handlers.NotificationHandlers =
                [
                    new KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>(
                        NotificationMethods.InitializedNotification,
                        (_, _) =>
                        {
                            if (engine is { } resolved)
                            {
                                start(resolved);
                            }

                            return ValueTask.CompletedTask;
                        }),
                ];
            })
            .WithStdioServerTransport();

        RegisterTools(builder, jsonOptions);

        await using var provider = services.BuildServiceProvider();

        engine = provider.GetRequiredService<IRoslynEngine>();

        // The error funnel's one dependency. Resolved here rather than passed into every tool method,
        // so that no tool signature carries a parameter the schema then has to exclude.
        ToolErrors.UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>());

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
        finally
        {
            // The child is told to shut down rather than being left for the process to take with it:
            // Roslyn watches this pid and would exit anyway (D35), but an orderly `shutdown`/`exit`
            // lets it flush its own logs and closes the pipe on both sides.
            if (engine is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }

        return CliDispatcher.ExitSuccess;
    }

    /// <summary>
    /// The five classes the ten tools live in, in the order they are registered. Shared with the
    /// tests so the inventory they assert against is the inventory the server publishes.
    /// </summary>
    internal static IReadOnlyList<Type> ToolTypes { get; } =
    [
        typeof(WorkspaceTools),
        typeof(SymbolTools),
        typeof(DiagnosticTools),
        typeof(CodeActionTools),
        typeof(EditTools),
    ];

    /// <summary>
    /// Registers the tool classes.
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

        builder.WithTools<WorkspaceTools>(jsonOptions);
        builder.WithTools<SymbolTools>(jsonOptions);
        builder.WithTools<DiagnosticTools>(jsonOptions);
        builder.WithTools<CodeActionTools>(jsonOptions);
        builder.WithTools<EditTools>(jsonOptions);
    }

    /// <summary>
    /// Registers the one collaborator every tool method takes, and the Roslyn session behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every registration is an explicit factory rather than <c>AddSingleton&lt;T&gt;()</c>: the
    /// constructors are internal, which the container's reflection-based selection does not see, and
    /// writing the graph out by hand keeps it reflection-free for AOT and readable as the wiring
    /// diagram it is.
    /// </para>
    /// <para>
    /// Nothing here runs at startup. <see cref="RoslynToolContext"/> is constructed on the first tool
    /// call, and constructing it touches neither disk nor network — the engine decides for itself
    /// when to launch Roslyn, because a session that never asks a C# question should never pay for
    /// one.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The resolved configuration.</param>
    /// <param name="engineFactory">Builds the Roslyn session, or <see langword="null"/> for the not-wired default.</param>
    internal static void RegisterToolServices(
        IServiceCollection services,
        ClaudeRoslynLspOptions options,
        Func<IServiceProvider, IRoslynEngine>? engineFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(_ => new WorkspacePathGuard(ResolveWorkspaceRoot()));
        services.AddSingleton(sp => new WorkspaceEditApplier(sp.GetRequiredService<WorkspacePathGuard>()));
        services.AddSingleton(_ => new EditCache());
        services.AddSingleton<IRoslynEngine>(engineFactory ?? (static _ => new NotWiredRoslynEngine()));

        services.AddSingleton(sp => new RoslynToolContext(
            sp.GetRequiredService<IRoslynEngine>(),
            sp.GetRequiredService<ClaudeRoslynLspOptions>(),
            sp.GetRequiredService<WorkspacePathGuard>(),
            sp.GetRequiredService<WorkspaceEditApplier>(),
            sp.GetRequiredService<EditCache>()));
    }

    /// <summary>
    /// The directory every path a tool reports is relative to, and outside which nothing is written.
    /// </summary>
    /// <remarks>
    /// The process's working directory, because that is what every MCP client sets it to: Claude
    /// Code, Codex, Gemini CLI and Cursor all launch a stdio server with the workspace as its
    /// current directory, and there is no protocol field that carries a root. A solution configured
    /// outside that directory still loads — the guard bounds <em>writes</em>, not analysis — and
    /// <c>getWorkspaceStatus</c> reports its real path so the discrepancy is visible rather than
    /// mysterious.
    /// </remarks>
    internal static string ResolveWorkspaceRoot() => Environment.CurrentDirectory;

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
