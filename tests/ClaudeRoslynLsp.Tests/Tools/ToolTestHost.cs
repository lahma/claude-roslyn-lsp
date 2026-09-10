using System.Reflection;
using System.Text.Json;

using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Edits;
using ClaudeRoslynLsp.Mcp;
using ClaudeRoslynLsp.Mcp.Tools;
using ClaudeRoslynLsp.Tests.Mcp;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Server;

namespace ClaudeRoslynLsp.Tests.Tools;

/// <summary>
/// Shared scaffolding for the tool-layer tests: the five tool classes, the ten
/// <see cref="McpServerTool"/> instances built exactly the way the server builds them, and a context
/// wired to a scriptable engine.
/// </summary>
/// <remarks>
/// <para>
/// The tools are constructed through
/// <see cref="McpServerTool.Create(MethodInfo, object?, McpServerToolCreateOptions)"/> with
/// <c>Services</c> and <c>SerializerOptions</c> set the same way <c>WithTools&lt;T&gt;(jsonOptions)</c>
/// sets them, so the schemas and annotations these tests inspect are the ones an MCP client receives
/// from <c>tools/list</c>. The serializer options come from
/// <see cref="McpServerSetup.CreateToolSerializerOptions"/> — the production factory itself, not a
/// copy that could drift.
/// </para>
/// <para>
/// The service provider only has to contain the types the tool methods expect to be injected: the
/// SDK asks <c>IServiceProviderIsService</c> which parameters to leave out of the generated schema,
/// so a missing registration shows up as an extra schema property rather than as a binding failure.
/// </para>
/// </remarks>
internal static class ToolTestHost
{
    /// <summary>
    /// Serialises tool construction across the whole test assembly.
    /// </summary>
    /// <remarks>
    /// The SDK caches the reflected function descriptor for a method process-wide, so two test
    /// classes building tools for the same methods at the same time — which xunit's parallelism makes
    /// possible and the server itself never does — race on that cache.
    /// </remarks>
    private static readonly Lock BuildGate = new();

    /// <summary>The production tool-facing serializer options, created once for the whole assembly.</summary>
    internal static JsonSerializerOptions SerializerOptions { get; } = McpServerSetup.CreateToolSerializerOptions();

    /// <summary>The five classes registered with <c>WithTools&lt;T&gt;</c>, from the production list.</summary>
    internal static IReadOnlyList<Type> ToolTypes { get; } = McpServerSetup.ToolTypes;

    /// <summary>Every <c>[McpServerTool]</c> method, discovered the way the SDK discovers them.</summary>
    internal static IReadOnlyList<MethodInfo> ToolMethods { get; } = DiscoverToolMethods(ToolTypes);

    /// <summary>The built tools, ordered by MCP name so a failure names a stable tool.</summary>
    internal static IReadOnlyList<McpServerTool> Tools { get; } = BuildTools(ToolTypes);

    /// <summary>Finds a built tool by its MCP name.</summary>
    /// <param name="name">The tool's MCP name.</param>
    internal static McpServerTool Find(string name) =>
        Tools.Single(tool => string.Equals(tool.ProtocolTool.Name, name, StringComparison.Ordinal));

    /// <summary>Finds a tool method by the MCP name its attribute declares.</summary>
    /// <param name="name">The tool's MCP name.</param>
    internal static MethodInfo FindMethod(string name) =>
        ToolMethods.Single(method =>
            string.Equals(method.GetCustomAttribute<McpServerToolAttribute>()!.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// A tool context over a real temporary workspace and a scriptable engine.
    /// </summary>
    /// <param name="workspace">The workspace root every path is bounded by.</param>
    /// <param name="engine">The engine, or a fresh <see cref="FakeRoslynEngine"/>.</param>
    /// <param name="timeProvider">The edit cache's clock.</param>
    internal static (RoslynToolContext Context, FakeRoslynEngine Engine) CreateContext(
        TempWorkspace workspace,
        FakeRoslynEngine? engine = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var guard = new WorkspacePathGuard(workspace.Root);
        var actual = engine ?? new FakeRoslynEngine();

        var context = new RoslynToolContext(
            actual,
            ClaudeRoslynLspOptions.FromEnvironment(static _ => null),
            guard,
            new WorkspaceEditApplier(guard),
            new EditCache(timeProvider));

        return (context, actual);
    }

    /// <summary>Discovers the tool methods of an arbitrary class set, the way the SDK would.</summary>
    /// <param name="types">The tool classes.</param>
    internal static List<MethodInfo> DiscoverToolMethods(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        var methods = new List<MethodInfo>();

        foreach (var type in types)
        {
            // The same binding flags McpServerBuilderExtensions.WithTools<T> uses, so this test's
            // idea of "the tools" cannot be narrower than the server's.
            foreach (var method in type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is not null)
                {
                    methods.Add(method);
                }
            }
        }

        // Reflection order is not contractual; sort so a failure message always names the same tool.
        methods.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return methods;
    }

    /// <summary>Builds the tools an arbitrary class set would advertise.</summary>
    /// <param name="types">The tool classes.</param>
    internal static List<McpServerTool> BuildTools(IEnumerable<Type> types)
    {
        var services = new ServiceCollection();
        var options = ClaudeRoslynLspOptions.FromEnvironment(static _ => null);

        services.AddSingleton(options);
        McpServerSetup.RegisterToolServices(services, options, static _ => new FakeRoslynEngine());

        var provider = services.BuildServiceProvider();
        var tools = new List<McpServerTool>();

        lock (BuildGate)
        {
            foreach (var method in DiscoverToolMethods(types))
            {
                tools.Add(McpServerTool.Create(
                    method,
                    target: null,
                    new McpServerToolCreateOptions
                    {
                        Services = provider,
                        SerializerOptions = SerializerOptions,
                    }));
            }
        }

        tools.Sort(static (left, right) => string.CompareOrdinal(left.ProtocolTool.Name, right.ProtocolTool.Name));
        return tools;
    }
}
