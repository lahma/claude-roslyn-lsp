namespace ClaudeRoslynLsp.Cli;

/// <summary>
/// <c>claude-roslyn-lsp fake-roslyn</c> — a scripted stand-in for the real Roslyn language server,
/// speaking the same <c>Content-Length</c>-framed LSP dialect on stdio.
/// </summary>
/// <remarks>
/// <para>
/// Hidden from the usage text: it is a test double, not a user-facing verb. The smoke test points
/// the launcher at this binary (<c>CLAUDE_ROSLYN_LSP_ROSLYN_PATH</c> plus
/// <c>CLAUDE_ROSLYN_LSP_ROSLYN_ARGS=fake-roslyn</c>) so every release RID can prove framing,
/// readiness gating and shutdown on its own architecture without downloading 70 MB of real Roslyn
/// on five runners. The same scripted server is what the mediation tests drive in-process.
/// </para>
/// <para>
/// Stubbed from the first commit for the reason <see cref="DoctorCommand"/> gives: the dispatcher is
/// the one file every parallel work package would otherwise edit.
/// </para>
/// </remarks>
internal static class FakeRoslynCommand
{
    /// <summary>Reports that this build cannot answer yet, on stderr, and exits 3.</summary>
    internal static int Run()
    {
        CliRuntime.WriteError(
            $"{ServerVersion.Name}: `fake-roslyn` is not implemented in this version. It is the scripted " +
            "backend the smoke test drives, and arrives with the protocol work package.");

        return CliDispatcher.ExitNotImplemented;
    }
}
