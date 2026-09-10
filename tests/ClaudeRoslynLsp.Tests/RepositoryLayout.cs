namespace ClaudeRoslynLsp.Tests;

/// <summary>
/// Finds the repository root from the test assembly's location, for the tests that assert something
/// about a checked-in file rather than about a type.
/// </summary>
/// <remarks>
/// The walk stops at the solution file rather than at <c>.git</c>, so it works in a worktree, in a
/// CI checkout without history, and in an unpacked source archive — all of which are places these
/// tests are expected to pass.
/// </remarks>
internal static class RepositoryLayout
{
    /// <summary>The file that identifies the root.</summary>
    private const string RootMarker = "claude-roslyn-lsp.slnx";

    /// <summary>The repository root.</summary>
    internal static string Root { get; } = Find();

    /// <summary>A path relative to the repository root.</summary>
    /// <param name="parts">Path segments.</param>
    internal static string Path_(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RootMarker)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find '{RootMarker}' above '{AppContext.BaseDirectory}'.");
    }
}
