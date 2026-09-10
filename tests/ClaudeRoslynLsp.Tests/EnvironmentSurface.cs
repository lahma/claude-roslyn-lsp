using System.Text.RegularExpressions;

using Xunit;

namespace ClaudeRoslynLsp.Tests;

/// <summary>
/// The set of environment variables this adapter actually reads, recovered from
/// <c>ClaudeRoslynLspOptions.FromEnvironment</c>'s own source rather than restated.
/// </summary>
/// <remarks>
/// <para>
/// Three documents outside the compiler's reach name these variables: <c>.mcp/server.json</c>
/// (which is what nuget.org and every MCP client read before running anything),
/// <c>.claude-plugin/plugin.json</c> (which is what the plugin launcher writes into the child's
/// environment) and the client configuration packs under <c>docs/clients/</c>. A name misspelled in
/// any of them produces no error at all — the server starts, ignores the value, and answers
/// confidently about the wrong solution.
/// </para>
/// <para>
/// <b>Why a source scan rather than a list.</b> A list here would be a fourth place to type the same
/// sixteen strings, and the first one to fall behind would make the other tests agree with a stale
/// copy of the surface instead of with the code. The scan reads the literals out of the reader calls
/// themselves, so a variable added to <c>FromEnvironment</c> is in this set the moment it is read,
/// and one deleted from it disappears here too. <see cref="ConfigurationTests"/> is the other half:
/// it proves each of these actually reaches a property.
/// </para>
/// </remarks>
internal static class EnvironmentSurface
{
    private const string OptionsSource = "src/ClaudeRoslynLsp/Configuration/ClaudeRoslynLspOptions.cs";

    /// <summary>
    /// One reader call: <c>ReadConfigured(read, "NAME")</c>, and optionally the second, aliased
    /// spelling that <c>ReadAliasedWithSource</c> takes.
    /// </summary>
    private static readonly Regex ReaderCall = new(
        """Read\w+\(\s*read,\s*"(CLAUDE_ROSLYN_LSP_[A-Z0-9_]+)"(?:\s*,\s*"(CLAUDE_ROSLYN_LSP_[A-Z0-9_]+)")?""",
        RegexOptions.Compiled);

    private static readonly (IReadOnlySet<string> Canonical, IReadOnlySet<string> Accepted) Scanned = Scan();

    /// <summary>
    /// The canonical name of every variable, which is what documentation and manifests must use.
    /// </summary>
    internal static IReadOnlySet<string> Canonical => Scanned.Canonical;

    /// <summary>The canonical names plus the accepted older spellings.</summary>
    /// <remarks>
    /// An alias is read rather than ignored — a documented variable that silently does nothing is
    /// the one configuration bug a user cannot diagnose — so a configuration snippet that uses one
    /// is wrong only stylistically, and this set is what stops that being reported as a typo.
    /// </remarks>
    internal static IReadOnlySet<string> Accepted => Scanned.Accepted;

    private static (IReadOnlySet<string> Canonical, IReadOnlySet<string> Accepted) Scan()
    {
        var source = File.ReadAllText(RepositoryLayout.Path_(OptionsSource.Split('/')));

        var canonical = new HashSet<string>(StringComparer.Ordinal);
        var accepted = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match call in ReaderCall.Matches(source))
        {
            canonical.Add(call.Groups[1].Value);
            accepted.Add(call.Groups[1].Value);

            if (call.Groups[2].Success)
            {
                accepted.Add(call.Groups[2].Value);
            }
        }

        // Self-check: a scan that matched nothing would make every test built on it pass for the
        // wrong reason. The count is deliberately a floor rather than an equality — this set is
        // supposed to grow with the product, and a test that had to be edited to add a knob would
        // be edited by deleting the assertion.
        Assert.True(
            canonical.Count >= 10,
            $"Only {canonical.Count} environment variables were recovered from {OptionsSource}. The reader "
            + "calls must have changed shape; fix the pattern rather than the documents it checks.");

        return (canonical, accepted);
    }
}
