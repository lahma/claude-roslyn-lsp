namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// An established, framed connection to something that speaks Roslyn's LSP dialect.
/// </summary>
/// <remarks>
/// <para>
/// A pair of one-way streams rather than a single duplex one, because the two transports this ends
/// up over are shaped differently: a child process's stdio is genuinely two handles, while a named
/// pipe is one. Expressing the pair is the shape that fits both without either having to pretend.
/// </para>
/// <para>
/// <see cref="Exited"/> is what separates "the peer closed the stream" from "the process died".
/// The adapter waits on it during shutdown, and WP4's supervisor waits on it to decide that a crash
/// happened at all — a distinction that cannot be made from a stream that simply stops.
/// </para>
/// </remarks>
internal sealed class RoslynConnection : IAsyncDisposable
{
    private readonly Func<ValueTask> _dispose;
    private int _disposed;

    /// <summary>Creates a connection over an already-established stream pair.</summary>
    /// <param name="input">Where Roslyn's output arrives. The adapter reads this.</param>
    /// <param name="output">Where Roslyn's input goes. The adapter writes this.</param>
    /// <param name="exited">
    /// Completes when the backend is gone. For a process this is process exit; for an in-process
    /// fake it is the fake's own loop finishing.
    /// </param>
    /// <param name="description">
    /// A short phrase naming the backend, for log lines. It reaches the user through <c>doctor</c>
    /// and through the stderr log, so it says what was launched, not merely that something was.
    /// </param>
    /// <param name="dispose">Releases the transport.</param>
    internal RoslynConnection(
        Stream input,
        Stream output,
        Task exited,
        string description,
        Func<ValueTask> dispose)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(exited);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(dispose);

        Input = input;
        Output = output;
        Exited = exited;
        Description = description;
        _dispose = dispose;
    }

    /// <summary>The stream Roslyn's messages arrive on.</summary>
    internal Stream Input { get; }

    /// <summary>The stream the adapter's messages go out on.</summary>
    internal Stream Output { get; }

    /// <summary>Completes when the backend has gone away, however it went.</summary>
    internal Task Exited { get; }

    /// <summary>A short phrase naming this backend, for logs.</summary>
    internal string Description { get; }

    /// <summary>
    /// Whether the thing on the other end is another <c>claude-roslyn-lsp</c> process's shared Roslyn
    /// rather than a Roslyn this process launched (D23).
    /// </summary>
    /// <remarks>
    /// Two things read it. <c>getWorkspaceStatus</c> reports <c>engine: "attached"</c>, which is what
    /// makes "there is one Roslyn, not two" observable in an answer rather than only in a log. And
    /// the MCP engine routes a configuration change through
    /// <c>claude-roslyn-lsp/setOption</c> instead of <c>workspace/didChangeConfiguration</c>, because
    /// Roslyn asks the <em>host</em> for configuration and an override set on this side would never
    /// be seen (D90).
    /// </remarks>
    internal bool Attached { get; init; }

    /// <summary>The process hosting the shared Roslyn, when this connection is attached to one.</summary>
    internal int? HostProcessId { get; init; }

    /// <summary>
    /// The process this connection started, when it started one — null for an attached pipe or an
    /// in-process fake.
    /// </summary>
    /// <remarks>
    /// What the idle policy measures and compacts is checked against this (D100, D101). C45's
    /// <c>_roslyn_processId</c> is what the server <em>says</em> holds the workspace, and with
    /// <c>CLAUDE_ROSLYN_LSP_ROSLYN_PATH</c> pointing at a wrapper or at this binary's own
    /// <c>fake-roslyn</c> verb (D34) — which reports 4242 — it names a process this adapter never
    /// started. Forcing a blocking collection in whatever .NET process happens to have that id would be
    /// a side effect on somebody else's program.
    /// </remarks>
    internal int? LaunchedProcessId { get; init; }

    /// <summary>
    /// Ends the backend process and nothing else, or null when there is no process to end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Separate from <see cref="DisposeAsync"/> on purpose (D97).</b> An idle recycle stops Roslyn
    /// by protocol — <c>shutdown</c>, <c>exit</c>, wait — and only a Roslyn that ignored both has to
    /// be killed. Disposing the connection would kill it too, but for a shared host disposing the
    /// current connection is also what withdraws the session file and closes every attached client's
    /// pipe (D93): a recycle that disposed would turn "the backend went quiet for a reload" into "the
    /// host went away" for every other process using it. This member ends the process and leaves the
    /// rest of the connection — and the sharing built on it — exactly where it was.
    /// </para>
    /// <para>
    /// Null for a backend that is not a process this connection owns — an attached pipe, an
    /// in-process fake — where the stream ending is the whole of what "stopped" means.
    /// </para>
    /// </remarks>
    internal Func<ValueTask>? Terminate { get; init; }

    /// <summary>Runs <see cref="Terminate"/>, when there is one.</summary>
    internal ValueTask TerminateAsync() => Terminate?.Invoke() ?? ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _disposed, 1) == 0 ? _dispose() : ValueTask.CompletedTask;
}

/// <summary>
/// Whatever the adapter is talking to on the Roslyn side, reduced to "give me a connection".
/// </summary>
/// <remarks>
/// <para>
/// The seam exists because there are four things that can be on the other end and only one of them
/// is Microsoft's server: the scripted fake running in this process (what <c>lsp --smoke</c> and the
/// mediation tests drive), the same fake in a child process (what proves the process plumbing on
/// every release RID), a real Roslyn over stdio or a pipe (WP3's launcher, wired in by WP4), and
/// nothing at all. Everything above this interface is written once and exercised by all of them,
/// which is the only reason an end-to-end test of the mediation is affordable.
/// </para>
/// <para>
/// <see cref="ConnectAsync"/> may fail, and failing is a normal outcome rather than a crash: a
/// missing Roslyn or a launch that will not start puts the session into
/// <see cref="ReadinessState.Failed"/>, where every request is answered with a code and a
/// <c>doctor</c> hint. A client whose server exits during startup has no channel left to be told
/// anything on.
/// </para>
/// </remarks>
internal interface IRoslynConnectionFactory
{
    /// <summary>A short phrase naming what this factory would connect to, for log lines.</summary>
    string Description { get; }

    /// <summary>Establishes the connection.</summary>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    Task<RoslynConnection> ConnectAsync(CancellationToken cancellationToken);
}
