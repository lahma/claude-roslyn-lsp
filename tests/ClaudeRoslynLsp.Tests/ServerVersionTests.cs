using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests;

/// <summary>
/// The name and version the two handshakes report.
/// </summary>
/// <remarks>
/// One string is the binary name, the NuGet package id, the MCP <c>serverInfo.name</c> and the
/// LSP <c>serverInfo.name</c> — which is what lets <c>SmokeTest</c>
/// assert both protocols against the same constant. If it ever drifts, the smoke test fails on a
/// release runner rather than here, which is a slower and much less obvious place to find out.
/// </remarks>
public class ServerVersionTests
{
    [Test]
    public void NameIsTheProductName()
    {
        Assert.That(ServerVersion.Name, Is.EqualTo("claude-roslyn-lsp"));
    }

    [Test]
    public void VersionIsResolvedFromTheAssembly()
    {
        // The build passes -p:Version from CHANGELOG.md; a plain `dotnet test` falls back to
        // Directory.Build.props's VersionPrefix. Either way it must be a real version, never the
        // "0.0.0" that means the informational-version attribute was missing.
        Assert.That(ServerVersion.Value, Is.Not.EqualTo("0.0.0"));
        Assert.That(ServerVersion.Value, Does.Not.Contain('+'));
    }
}
