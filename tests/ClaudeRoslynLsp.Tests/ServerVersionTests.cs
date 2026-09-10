using Xunit;

namespace ClaudeRoslynLsp.Tests;

/// <summary>
/// The name and version the two handshakes report.
/// </summary>
/// <remarks>
/// One string is the binary name, the NuGet package id, the plugin name, the MCP
/// <c>serverInfo.name</c> and the LSP <c>serverInfo.name</c> — which is what lets <c>SmokeTest</c>
/// assert both protocols against the same constant. If it ever drifts, the smoke test fails on a
/// release runner rather than here, which is a slower and much less obvious place to find out.
/// </remarks>
public class ServerVersionTests
{
    [Fact]
    public void NameIsTheProductName()
    {
        Assert.Equal("claude-roslyn-lsp", ServerVersion.Name);
    }

    [Fact]
    public void VersionIsResolvedFromTheAssembly()
    {
        // The build passes -p:Version from CHANGELOG.md; a plain `dotnet test` falls back to
        // Directory.Build.props's VersionPrefix. Either way it must be a real version, never the
        // "0.0.0" that means the informational-version attribute was missing.
        Assert.NotEqual("0.0.0", ServerVersion.Value);
        Assert.DoesNotContain('+', ServerVersion.Value);
    }
}
