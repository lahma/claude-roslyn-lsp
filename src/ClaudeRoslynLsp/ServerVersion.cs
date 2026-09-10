using System.Reflection;

namespace ClaudeRoslynLsp;

/// <summary>
/// The version this binary reports — to <c>--version</c>, to MCP clients in <c>serverInfo</c>, and
/// to LSP clients in <c>InitializeResult.serverInfo</c>. Read once from the assembly's
/// informational version, which the build derives from <c>CHANGELOG.md</c>.
/// </summary>
internal static class ServerVersion
{
    /// <summary>
    /// The product name. It is the binary name, the NuGet package id, the plugin name, the MCP
    /// <c>serverInfo.name</c> and the LSP <c>serverInfo.name</c> — one string for all five, which
    /// is what lets <c>SmokeTest</c> assert both handshakes against the same constant.
    /// </summary>
    internal const string Name = "claude-roslyn-lsp";

    /// <summary>
    /// The informational version with any source-revision suffix (<c>+sha</c>) removed.
    /// </summary>
    internal static string Value { get; } = Resolve();

    private static string Resolve()
    {
        var informational = typeof(ServerVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrEmpty(informational))
        {
            return "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
