namespace ClaudeRoslynLsp.Cli;

/// <summary>
/// <c>claude-roslyn-lsp doctor</c> — the whole support surface once WP3 lands: adapter version and
/// RID, the .NET host and the runtimes it lists, the Roslyn resolution chain with the winner named,
/// the cache directory, the solution candidates with their scores, watcher status, and whether the
/// official <c>csharp-lsp</c> plugin is enabled (which is a conflict, not a coincidence).
/// </summary>
/// <remarks>
/// It exists as a stub from the first commit for one reason: <see cref="CliDispatcher"/> is a file
/// two work packages have to touch, and a verb added late is a merge conflict in the one file every
/// parallel branch edits. The verb, its exit code and its place in the switch are settled here so
/// WP3 only has to fill in <see cref="Run"/>.
/// </remarks>
internal static class DoctorCommand
{
    /// <summary>Reports that this build cannot answer yet, on stderr, and exits 3.</summary>
    internal static int Run()
    {
        CliRuntime.WriteError(
            $"{ServerVersion.Name}: `doctor` is not implemented in this version. It reports the Roslyn " +
            "resolution chain, the .NET host and the solution candidates, and arrives with the acquisition " +
            "work package.");

        return CliDispatcher.ExitNotImplemented;
    }
}
