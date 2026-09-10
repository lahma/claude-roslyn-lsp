using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Adapter.Sharing;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

using ModelContextProtocol;

namespace ClaudeRoslynLsp.Mcp.Engine;

/// <summary>
/// The MCP process's own Roslyn: it launches one, opens the workspace, keeps it alive, and answers
/// <see cref="IRoslynEngine"/> out of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composed from the mediation's components, not from its session (D75).</b> Everything here that
/// carries a decision is a class the <c>lsp</c> verb already uses and this one reuses unchanged:
/// <see cref="ServerEndpoint"/> and the authored <c>initialize</c> in it (D14),
/// <see cref="ReadinessGate"/> (D46, C27), <see cref="ConfigurationResponder"/> (D48),
/// <see cref="RegistrationTracker"/>, <see cref="ProgressTracker"/>,
/// <see cref="ServerRequestHandler"/>, <see cref="WorkspaceOpener"/>,
/// <see cref="RoslynSupervisor"/> (D57), <see cref="FileWatchBridge"/> (D55, D56),
/// <see cref="DocumentMirror"/> and <see cref="IdMap"/> (D47). What is written here is the lifecycle
/// glue that joins them, and it is deliberately <em>not</em> shared with
/// <see cref="AdapterSession"/>: that class's glue exists to serve a peer — forwarding bytes,
/// rewriting ids in two directions, holding a queue of somebody else's requests, de-duplicating a
/// call hierarchy on the way back — and none of that exists here, where the caller is a method on
/// this object. Extracting a common session would mean inventing an abstraction for "the peer" whose
/// only second implementation is "there isn't one", and it would put a refactor with no test of its
/// own underneath the mediation that WP4 spent a live session getting right. The seam that was worth
/// cutting turned out to be the narrow one WP9 cut instead: <see cref="ISharedEngineHost"/>, which
/// this class implements without being restructured, and which lets another process attach to this
/// one's Roslyn rather than launching a second (D23, D87).
/// </para>
/// <para>
/// <b>File watching is mandatory here, not optional.</b> C48: without <c>project.assets.json</c>
/// arriving as a watched-file event, a repository that needs a restore never finishes loading at all
/// — the MCP half would sit at <c>status: "loading"</c> until the readiness budget ran out, on a
/// freshly cloned repository, which is the commonest first run there is. And C33: an edit made
/// outside this process only reaches Roslyn when the owning project file is reported changed. So
/// <c>CLAUDE_ROSLYN_LSP_FILE_WATCHER=off</c> is honoured in <c>lsp</c> mode, where the client is an
/// editor that tells the server what it did, and ignored here, where nobody does.
/// </para>
/// <para>
/// <b>Nothing is pushed anywhere.</b> The <c>lsp</c> verb's diagnostics bridge exists to turn
/// Roslyn's pull model into the push a client renders (D52); an MCP client has no such channel and
/// asks instead, so <c>getDiagnostics</c> pulls per call and the bridge is absent. Roslyn's log and
/// progress streams go to stderr.
/// </para>
/// </remarks>
internal sealed partial class OwnedRoslynEngine : IRoslynEngine, IAdapterChannel, ISharedEngineHost, IAsyncDisposable
{
    /// <summary>How long <c>shutdown</c> waits for Roslyn to acknowledge before the process is closed.</summary>
    internal static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the <em>first</em> workspace diagnostic pull of a session may take, when the
    /// readiness budget does not say.
    /// </summary>
    /// <remarks>
    /// The first pull is the one that waits for a full-solution compilation — C17's first
    /// <em>document</em> pull already costs 1.3-1.7 s, and 30 projects took about a minute (C60). It
    /// therefore runs on <c>CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS</c>, which is the user's own
    /// statement of how long they are willing to wait for this solution, and falls back to this when
    /// that is unset.
    /// </remarks>
    internal static readonly TimeSpan DefaultFirstWorkspacePull = TimeSpan.FromSeconds(120);

    /// <summary>How long a later pull waits before it is treated as "Roslyn has nothing new" (C58).</summary>
    /// <remarks>
    /// Ten seconds. The pull is a long poll: with nothing to report Roslyn holds the request rather
    /// than answering, so this is not a deadline on an answer, it is how long "there is nothing new"
    /// takes to establish. It only costs anything on a repeat call that changed nothing.
    /// </remarks>
    internal static readonly TimeSpan HeldWorkspacePull = TimeSpan.FromSeconds(10);

    /// <summary>How long a diagnostic scope change is given to reach Roslyn's analysis (C57).</summary>
    /// <remarks>
    /// Two seconds, measured: a pull issued straight after the configuration round trip still answers
    /// from the old scope, and one issued about two seconds later carries the new set. A request
    /// already in flight when the change lands is not woken by it, so this is a delay before asking
    /// rather than a longer wait for an answer.
    /// </remarks>
    internal static readonly TimeSpan ScopeSettle = TimeSpan.FromSeconds(1.5);

    /// <summary>How long one confirming pull after a scope change waits, given a fallback exists.</summary>
    internal static readonly TimeSpan ScopeConfirmPull = TimeSpan.FromSeconds(3);

    /// <summary>How long a confirming attempt waits for a <c>workspace/diagnostic/refresh</c>.</summary>
    /// <remarks>
    /// Roslyn sends one on an idle machine and does not always send one on a loaded machine, so this
    /// is a shortcut rather than a dependency: when it arrives the next sample happens at once, and
    /// when it does not the loop falls back to sampling on <see cref="ScopeSettle"/>.
    /// </remarks>
    internal static readonly TimeSpan RefreshWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a scope change is given to show up in a pull before the first answer stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A budget rather than a count of attempts, because what varies is how long Roslyn takes to run
    /// every analyzer over every file, and that is a property of the machine and the solution rather
    /// than of a number chosen here: the same three-project fixture confirmed on the first sample
    /// idle and had not confirmed after six on a box running the rest of the test suite.
    /// </para>
    /// <para>
    /// Eight seconds, deliberately short of what a contended machine needs. Sampling for longer does
    /// not rescue that case — a box running a whole test suite beside Roslyn had not produced the
    /// analyzer set after thirty — and it does make the common case, where the first pull was already
    /// right, eight seconds slower for nothing. The answer to "a solution-wide pull may not carry
    /// analyzer diagnostics for closed files on a busy machine" is `scope: "file"`, which always
    /// does, and that is where `fixDiagnostics` looks for its site when it is given a path.
    /// </para>
    /// </remarks>
    internal static readonly TimeSpan ScopeConfirmBudget = TimeSpan.FromSeconds(8);

    private readonly Lock _stateLock = new();
    private readonly IRoslynConnectionFactory _factory;
    private readonly ClaudeRoslynLspOptions _options;
    private readonly Func<WorkspaceSelection> _selectWorkspace;
    private readonly string _workspaceRoot;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<RoslynBackendIdentity>? _identity;

    private readonly IdMap _serverBound = new();
    private readonly DocumentMirror _mirror;
    private readonly RegistrationTracker _registrations;
    private readonly ConfigurationResponder _configuration;
    private readonly ProgressTracker _progress;
    private readonly ReadinessGate _gate;
    private readonly ServerRequestHandler _serverHandler;
    private readonly WorkspaceOpener _opener;
    private readonly RoslynSupervisor _supervisor;
    private readonly FileWatchBridge _watching;
    private readonly WorkspaceLoadTracker _load = new();

    private ServerEndpoint? _server;
    private Task? _startTask;
    private Task? _serverPump;
    private TaskCompletionSource _opened = Completion();
    private WorkspaceSelection? _selection;
    private IReadOnlyList<WorkspaceProject>? _projects;
    private CompilerDiagnosticsScope? _compilerScope;
    private CompilerDiagnosticsScope? _analyzerScope;
    private WorkspaceDiagnosticReport? _lastWorkspaceReport;
    private TaskCompletionSource _diagnosticsRefreshed = Completion();
    private TaskCompletionSource<byte[]> _handshake = Completion<byte[]>();
    private bool _scopeChangedSincePull;
    private bool? _organizeImports;
    private int? _backendProcessId;
    private bool _attached;
    private int? _hostProcessId;
    private bool _stopping;
    private int _generation;

    /// <summary>Creates the engine. Nothing is launched until <see cref="Start"/> runs.</summary>
    /// <param name="factory">What connects to Roslyn: the launcher, or a scripted fake under <c>--smoke</c>.</param>
    /// <param name="options">The process's configuration.</param>
    /// <param name="selectWorkspace">What to open. Called once per backend start, so a relaunch re-opens the same thing.</param>
    /// <param name="workspaceRoot">The directory the process was launched in (D68).</param>
    /// <param name="time">The clock, so the readiness budget is testable.</param>
    /// <param name="logger">The stderr log. stdout is the MCP channel.</param>
    /// <param name="identity">
    /// What to report about the backend when the wire does not say: the pinned version, and the
    /// launched process id as a fallback for the one <c>initialize</c> names (C45).
    /// </param>
    internal OwnedRoslynEngine(
        IRoslynConnectionFactory factory,
        ClaudeRoslynLspOptions options,
        Func<WorkspaceSelection> selectWorkspace,
        string workspaceRoot,
        TimeProvider time,
        ILogger logger,
        Func<RoslynBackendIdentity>? identity = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(selectWorkspace);
        ArgumentNullException.ThrowIfNull(workspaceRoot);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _factory = factory;
        _options = options;
        _selectWorkspace = selectWorkspace;
        _workspaceRoot = workspaceRoot;
        _time = time;
        _logger = logger;
        _identity = identity;

        _mirror = new DocumentMirror(logger);
        _registrations = new RegistrationTracker(logger);
        _progress = new ProgressTracker(logger);
        _supervisor = new RoslynSupervisor(time, logger);
        _configuration = new ConfigurationResponder(options.RoslynOptionsJson, logger);

        _gate = new ReadinessGate(
            time,
            TimeSpan.FromSeconds(options.ReadyTimeoutSeconds),
            logger,
            notice => Log.Notice(logger, notice));

        _serverHandler = new ServerRequestHandler(_registrations, _configuration, _progress, _gate, logger)
        {
            WorkspaceFolders = [new WorkspaceFolder { Uri = RootUri, Name = Path.GetFileName(workspaceRoot) }],
        };

        _opener = new WorkspaceOpener(_selectWorkspace, logger);

        // Mandatory, unlike in `lsp` mode: see the class remarks (C48, C33).
        _watching = new FileWatchBridge(() => _workspaceRoot, _mirror, this, time, logger);
        _registrations.Changed += () => _watching.Schedule(_registrations.Watchers);

        // Roslyn's own "your diagnostics are stale, ask again" signal, which is what a scope change
        // eventually produces (D83). Waiting for it beats guessing at a settle delay, and the guess
        // stays as the fallback for a build that stops sending it.
        _serverHandler.RefreshRequested += method =>
        {
            if (!string.Equals(method, "workspace/diagnostic/refresh", StringComparison.Ordinal))
            {
                return;
            }

            TaskCompletionSource refreshed;

            lock (_stateLock)
            {
                refreshed = _diagnosticsRefreshed;
            }

            refreshed.TrySetResult();
        };

        _gate.Opened += OnGateOpened;
        _gate.WorkspaceDescription = "the workspace";
    }

    /// <summary>The readiness gate, exposed for the live tests.</summary>
    internal ReadinessGate Gate => _gate;

    /// <summary>
    /// What <c>getWorkspaceStatus</c> reports for <c>engine</c> when this process launched its own.
    /// </summary>
    internal const string Engine = "owned";

    /// <summary>
    /// What it reports when this process is using another one's Roslyn over the shared pipe (D23).
    /// </summary>
    /// <remarks>
    /// The distinction is worth an answer rather than only a log line: it is how a user finds out
    /// that the <c>lsp</c> and <c>mcp</c> servers in one session are one Roslyn instead of two, and
    /// <c>hostProcessId</c> beside it names which process to look at when the memory question comes
    /// up.
    /// </remarks>
    internal const string AttachedEngine = "attached";

    /// <summary>The workspace root as a <c>file:</c> URI.</summary>
    private string RootUri => new Uri(_workspaceRoot).AbsoluteUri;

    /// <summary>
    /// Starts the backend in the background, once.
    /// </summary>
    /// <remarks>
    /// <b>Called from the MCP handshake, not from the first tool call (D76).</b> Every MCP client
    /// starts its servers when the session starts and calls a tool minutes later, if at all — so
    /// launching Roslyn at the handshake spends the 3-45 s load (C31, C53) during time the user is
    /// already spending, and a first tool call then answers immediately instead of being the thing
    /// that pays. A session that never asks a C# question pays for a child process it did not need,
    /// which is the trade this makes deliberately: the alternative makes the *first* question the
    /// slow one, and the first question is the one that decides whether the model uses these tools
    /// again.
    /// </remarks>
    internal void Start()
    {
        int generation;

        lock (_stateLock)
        {
            if (_startTask is not null || _stopping)
            {
                return;
            }

            generation = _generation;
            _gate.Start();
            _startTask = Task.Run(() => ConnectAsync(generation, CancellationToken.None), CancellationToken.None);
        }

        Log.Starting(_logger, _factory.Description, _workspaceRoot);
    }

    // ---------------------------------------------------------------------------------------------
    // IRoslynEngine
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<WorkspaceState> EnsureReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Start();

        Task opened;

        lock (_stateLock)
        {
            opened = _opened.Task;
        }

        try
        {
            await opened.WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Not a failure: a workspace that is still loading is the normal state of a session that
            // has just started, and the caller answers with a status rather than an error (D66).
        }

        return Snapshot();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SymbolInformation>> WorkspaceSymbolAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var result = await AskAsync(
            "workspace/symbol",
            new WorkspaceSymbolRequest { Query = query },
            RoslynEngineJsonContext.Default.WorkspaceSymbolRequest,
            cancellationToken).ConfigureAwait(false);

        return Read(result, RoslynEngineJsonContext.Default.SymbolInformationArray) ?? [];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LspLocation>> ReferencesAsync(
        string uri,
        LspPosition position,
        bool includeDeclaration,
        CancellationToken cancellationToken)
    {
        var result = await AskAsync(
            "textDocument/references",
            new ReferencesRequest
            {
                TextDocument = new TextDocumentRef { Uri = uri },
                Position = position,
                Context = new ReferenceContextShape { IncludeDeclaration = includeDeclaration },
            },
            RoslynEngineJsonContext.Default.ReferencesRequest,
            cancellationToken).ConfigureAwait(false);

        return Read(result, RoslynEngineJsonContext.Default.LspLocationArray) ?? [];
    }

    /// <inheritdoc />
    public async Task<LspRange?> PrepareRenameAsync(
        string uri,
        LspPosition position,
        CancellationToken cancellationToken)
    {
        var result = await AskAsync(
            "textDocument/prepareRename",
            Position(uri, position),
            RoslynEngineJsonContext.Default.TextDocumentPositionRequest,
            cancellationToken).ConfigureAwait(false);

        if (result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // C23 says a bare Range, and that is what this build sends; the { range, placeholder } shape
        // the specification also allows is read rather than silently deserialised into zeros.
        return result.TryGetProperty("range", out _)
            ? Read(result, RoslynEngineJsonContext.Default.PrepareRenameResult)?.Range
            : Read(result, RoslynEngineJsonContext.Default.LspRange);
    }

    /// <inheritdoc />
    public async Task<WorkspaceEdit?> RenameAsync(
        string uri,
        LspPosition position,
        string newName,
        CancellationToken cancellationToken)
    {
        var result = await AskAsync(
            "textDocument/rename",
            new RenameRequest
            {
                TextDocument = new TextDocumentRef { Uri = uri },
                Position = position,
                NewName = newName,
            },
            RoslynEngineJsonContext.Default.RenameRequest,
            cancellationToken).ConfigureAwait(false);

        return Read(result, RoslynEngineJsonContext.Default.WorkspaceEdit);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RawCodeAction>> CodeActionsAsync(
        string uri,
        LspRange range,
        IReadOnlyList<RawDiagnostic>? diagnostics,
        CancellationToken cancellationToken)
    {
        var result = await AskAsync(
            "textDocument/codeAction",
            new CodeActionRequest
            {
                TextDocument = new TextDocumentRef { Uri = uri },
                Range = range,
                Context = new CodeActionContextShape { Diagnostics = diagnostics?.ToArray() ?? [] },
            },
            RoslynEngineJsonContext.Default.CodeActionRequest,
            cancellationToken).ConfigureAwait(false);

        return Read(result, RoslynEngineJsonContext.Default.RawCodeActionArray) ?? [];
    }

    /// <inheritdoc />
    public async Task<RawCodeAction> ResolveCodeActionAsync(
        RawCodeAction action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        var result = await AskAsync(
            "codeAction/resolve",
            action,
            RoslynEngineJsonContext.Default.RawCodeAction,
            cancellationToken).ConfigureAwait(false);

        return Read(result, RoslynEngineJsonContext.Default.RawCodeAction) ?? action;
    }

    /// <inheritdoc />
    public async Task<WorkspaceEdit?> ResolveFixAllAsync(
        string title,
        JsonElement data,
        FixAllScope scope,
        CancellationToken cancellationToken)
    {
        var result = await AskAsync(
            "codeAction/resolveFixAll",
            new ResolveFixAllRequest { Title = title, Data = data, Scope = scope.ToString() },
            RoslynEngineJsonContext.Default.ResolveFixAllRequest,
            cancellationToken).ConfigureAwait(false);

        if (result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // The answer is a resolved code action carrying the edit. Read as a workspace edit too,
        // because a build that answered with the edit directly would otherwise resolve to nothing
        // and read as "there was no fix", which is the wrong thing to tell a model.
        return Read(result, RoslynEngineJsonContext.Default.RawCodeAction)?.Edit
            ?? (result.TryGetProperty("documentChanges", out _)
                ? Read(result, RoslynEngineJsonContext.Default.WorkspaceEdit)
                : null);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LspTextEdit>> FormattingAsync(
        string uri,
        LspFormattingOptions options,
        CancellationToken cancellationToken)
    {
        var result = await AskAsync(
            "textDocument/formatting",
            new FormattingRequest
            {
                TextDocument = new TextDocumentRef { Uri = uri },
                Options = new FormattingOptionsShape
                {
                    // Clamped, and not defensively-in-general: C59 is that `tabSize: 0` makes
                    // Roslyn's formatter fail a Contract assertion deep inside the trivia engine
                    // and answer -32000 with a stack trace, rather than refusing the argument. This
                    // is the last gate before the wire, so it is where the impossible value stops.
                    TabSize = Math.Max(1, options.TabSize),
                    InsertSpaces = options.InsertSpaces,
                },
            },
            RoslynEngineJsonContext.Default.FormattingRequest,
            cancellationToken).ConfigureAwait(false);

        return Read(result, RoslynEngineJsonContext.Default.LspTextEditArray) ?? [];
    }

    /// <inheritdoc />
    public async Task<DocumentDiagnosticReport> DocumentDiagnosticAsync(
        string uri,
        CancellationToken cancellationToken)
    {
        var result = await AskAsync(
            "textDocument/diagnostic",
            new DocumentDiagnosticRequest { TextDocument = new TextDocumentRef { Uri = uri } },
            RoslynEngineJsonContext.Default.DocumentDiagnosticRequest,
            cancellationToken).ConfigureAwait(false);

        return Read(result, RoslynEngineJsonContext.Default.DocumentDiagnosticReport) ?? new DocumentDiagnosticReport();
    }

    /// <summary>
    /// <c>workspace/diagnostic</c>, bounded and pulled twice after a scope change (D83).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two facts a live run found, and neither is optional.</b> C58: the workspace pull is a
    /// <em>long poll</em> — once Roslyn has nothing new to say it simply holds the request, for
    /// minutes, forever. An unbounded pull therefore turns a second
    /// <c>getDiagnostics scope: "solution"</c> into a tool call that never returns. So the wait is
    /// bounded, and a pull that is still held when the budget runs out is answered from the previous
    /// report: Roslyn holding the request <em>means</em> "nothing has changed since I last told you",
    /// which makes the cached report the current truth rather than a guess.
    /// </para>
    /// <para>
    /// C57: raising a diagnostic scope does not reach the pull that immediately follows it. The pull
    /// after a scope change returns the old set and the one after <em>that</em> carries the new one,
    /// so a scope change arms a second pull. Without it, <c>fixDiagnostics IDE0005 scope:
    /// "solution"</c> reports "no occurrence" on a solution that has one — a confidently wrong
    /// answer, which is the failure this product exists to remove.
    /// </para>
    /// </remarks>
    /// <param name="previousResultIds">Per-document result ids from a previous pull (C12).</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    public async Task<WorkspaceDiagnosticReport> WorkspaceDiagnosticAsync(
        IReadOnlyList<PreviousResultId> previousResultIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previousResultIds);

        var report = await PullWorkspaceAsync(previousResultIds, HeldWorkspacePull, cancellationToken)
            .ConfigureAwait(false);

        bool again;

        lock (_stateLock)
        {
            // Remembered before the confirming samples, not after them. It is what makes those
            // samples "warm" — that is, entitled to the short budget and to answering `null` when
            // Roslyn holds them — and without it the very first call of a session would spend the
            // whole first-pull budget on each of them and then refuse.
            if (report is not null)
            {
                _lastWorkspaceReport = report;
            }

            again = _scopeChangedSincePull;
            _scopeChangedSincePull = false;
        }

        if (again)
        {
            report = await ConfirmScopeChangeAsync(previousResultIds, report, cancellationToken)
                .ConfigureAwait(false);
        }

        lock (_stateLock)
        {
            if (report is not null)
            {
                _lastWorkspaceReport = report;
            }

            return report ?? _lastWorkspaceReport ?? new WorkspaceDiagnosticReport();
        }
    }

    /// <summary>
    /// Keeps asking, after a scope change, until the answer actually changes or the budget runs out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// C57 is why a single extra pull is not enough, and a loaded machine is why a fixed number of
    /// them is not either. Raising the analyzer scope makes Roslyn run every analyzer over every
    /// file, which takes seconds on a three-project fixture and much longer on a contended box; a
    /// pull issued before that finishes answers with the set it had, and — the part that costs the
    /// retries — a pull *waiting* when the new set lands is not woken by it. So the loop samples:
    /// wait for Roslyn's own <c>workspace/diagnostic/refresh</c> where it sends one, settle, ask
    /// again, and stop as soon as the report is no longer the one we started with.
    /// </para>
    /// <para>
    /// "No longer the same" is measured as the number of diagnostics across the whole report. That
    /// is a heuristic and it is the honest one available: the engine does not know which id the
    /// caller is looking for, and a scope is only ever raised here, so more is the shape the change
    /// takes. When the budget expires the first answer stands — a smaller true answer beats a call
    /// that never returns.
    /// </para>
    /// </remarks>
    private async Task<WorkspaceDiagnosticReport?> ConfirmScopeChangeAsync(
        IReadOnlyList<PreviousResultId> previousResultIds,
        WorkspaceDiagnosticReport? first,
        CancellationToken cancellationToken)
    {
        var baseline = CountOf(first);
        var started = _time.GetTimestamp();
        var attempts = 0;

        while (_time.GetElapsedTime(started) < ScopeConfirmBudget)
        {
            attempts++;

            await WaitForRefreshAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(ScopeSettle, _time, cancellationToken).ConfigureAwait(false);

            var confirmed = await PullWorkspaceAsync(previousResultIds, ScopeConfirmPull, cancellationToken)
                .ConfigureAwait(false);

            if (confirmed is null)
            {
                continue;
            }

            if (CountOf(confirmed) != baseline)
            {
                var elapsed = _time.GetElapsedTime(started).TotalSeconds;
                Log.ScopeConfirmed(_logger, attempts, elapsed);

                return confirmed;
            }

            first ??= confirmed;
        }

        Log.ScopeUnconfirmed(_logger, attempts, ScopeConfirmBudget.TotalSeconds);

        return first;
    }

    /// <summary>How many diagnostics a report carries, across every document in it.</summary>
    private static int CountOf(WorkspaceDiagnosticReport? report)
    {
        if (report is null)
        {
            return -1;
        }

        var total = 0;

        foreach (var document in report.Items)
        {
            total += document.Items.Length;
        }

        return total;
    }

    /// <summary>
    /// Waits, briefly, for Roslyn to say the client's diagnostics have gone stale.
    /// </summary>
    /// <remarks>
    /// Consumes the signal and arms the next one, so two confirming attempts wait for two refreshes
    /// rather than both returning on the first. A timeout is not a failure: it means Roslyn did not
    /// send one, and the settle delay beside this call is what covers that.
    /// </remarks>
    private async Task WaitForRefreshAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource refreshed;

        lock (_stateLock)
        {
            refreshed = _diagnosticsRefreshed;
        }

        try
        {
            await refreshed.Task.WaitAsync(RefreshWait, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return;
        }

        lock (_stateLock)
        {
            if (ReferenceEquals(_diagnosticsRefreshed, refreshed))
            {
                _diagnosticsRefreshed = Completion();
            }
        }
    }

    /// <summary>One bounded pull, or <see langword="null"/> when Roslyn was still holding it.</summary>
    /// <param name="previousResultIds">What the caller already has (C12).</param>
    /// <param name="warmBudget">How long to wait once a previous report exists to fall back on.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    private async Task<WorkspaceDiagnosticReport?> PullWorkspaceAsync(
        IReadOnlyList<PreviousResultId> previousResultIds,
        TimeSpan warmBudget,
        CancellationToken cancellationToken)
    {
        bool warm;

        lock (_stateLock)
        {
            warm = _lastWorkspaceReport is not null;
        }

        var budget = warm
            ? warmBudget
            : TimeSpan.FromSeconds(Math.Max(_options.ReadyTimeoutSeconds, DefaultFirstWorkspacePull.TotalSeconds));

        using var timeout = new CancellationTokenSource(budget, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        JsonElement result;

        try
        {
            result = await AskAsync(
                "workspace/diagnostic",
                new WorkspaceDiagnosticRequest
                {
                    PreviousResultIds = [.. previousResultIds.Select(previous =>
                        new PreviousResultIdShape { Uri = previous.Uri, Value = previous.Value })],
                },
                RoslynEngineJsonContext.Default.WorkspaceDiagnosticRequest,
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.WorkspacePullHeld(_logger, budget.TotalSeconds, warm);

            if (!warm)
            {
                // Nothing to fall back on, so answering with an empty report would tell a model that
                // a solution it has never looked at compiles cleanly. That is the confidently-wrong
                // answer this product exists to remove; a refusal that names a cheaper scope is not.
                throw new McpException(
                    $"Roslyn did not finish the first solution-wide diagnostic pass within "
                    + $"{budget.TotalSeconds:0} s, so there is no answer to give — an empty list here would "
                    + "mean 'nothing was found', not 'nothing was looked at'. Ask for scope: \"project\" or "
                    + "scope: \"file\", or raise CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS if this solution is "
                    + "simply large.");
            }

            return null;
        }

        return Read(result, RoslynEngineJsonContext.Default.WorkspaceDiagnosticReport);
    }

    /// <inheritdoc />
    public Task SetCompilerDiagnosticsScopeAsync(
        CompilerDiagnosticsScope scope,
        CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_compilerScope == scope)
            {
                // Already in effect. The round trip is not free and Roslyn keeps the setting for the
                // life of the session, so a second solution pull costs one request instead of three.
                return Task.CompletedTask;
            }

            _compilerScope = scope;

            // C57: the pull that immediately follows a scope change still answers from the old one.
            _scopeChangedSincePull = true;
        }

        return ApplyConfigurationAsync(
            ConfigurationResponder.CompilerDiagnosticsScopeSection,
            scope == CompilerDiagnosticsScope.FullSolution
                ? "\"fullSolution\""u8.ToArray()
                : "\"openFiles\""u8.ToArray(),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task SetAnalyzerDiagnosticsScopeAsync(
        CompilerDiagnosticsScope scope,
        CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_analyzerScope == scope)
            {
                return Task.CompletedTask;
            }

            _analyzerScope = scope;
            _scopeChangedSincePull = true;
        }

        return ApplyConfigurationAsync(
            ConfigurationResponder.AnalyzerDiagnosticsScopeSection,
            scope == CompilerDiagnosticsScope.FullSolution
                ? "\"fullSolution\""u8.ToArray()
                : "\"openFiles\""u8.ToArray(),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task SetOrganizeImportsOnFormatAsync(bool enabled, CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_organizeImports == enabled)
            {
                return Task.CompletedTask;
            }

            _organizeImports = enabled;
        }

        return ApplyConfigurationAsync(
            ConfigurationResponder.OrganizeImportsOnFormatSection,
            enabled ? "true"u8.ToArray() : "false"u8.ToArray(),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task OpenDocumentAsync(string uri, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(text);

        // Through the mirror, so a backend that dies gets every open document replayed at the text
        // it was opened with (D57) rather than at whatever is on disk by then.
        var body = DocumentMirror.BuildDidOpen(new MirroredDocument(uri, "csharp", 1, text));
        _mirror.Open(body);

        Notify(body);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CloseDocumentAsync(string uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var body = JsonRpcErrors.Notification(
            "textDocument/didClose",
            JsonSerializer.SerializeToUtf8Bytes(
                new DidCloseRequest { TextDocument = new TextDocumentRef { Uri = uri } },
                RoslynEngineJsonContext.Default.DidCloseRequest));

        _mirror.Close(body);
        Notify(body);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public bool IsDocumentOpen(string uri) => _mirror.Find(uri) is not null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentSymbolNode>> DocumentSymbolAsync(
        string uri,
        CancellationToken cancellationToken)
    {
        var result = await AskAsync(
            "textDocument/documentSymbol",
            new DocumentDiagnosticRequest { TextDocument = new TextDocumentRef { Uri = uri } },
            RoslynEngineJsonContext.Default.DocumentDiagnosticRequest,
            cancellationToken).ConfigureAwait(false);

        return Read(result, RoslynEngineJsonContext.Default.DocumentSymbolNodeArray) ?? [];
    }

    /// <inheritdoc />
    public Task NotifyFilesChangedAsync(IReadOnlyList<FileChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.Count == 0)
        {
            return Task.CompletedTask;
        }

        var events = new List<FileEventShape>(changes.Count + 1);
        var nudged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in changes)
        {
            events.Add(new FileEventShape { Uri = change.Uri, Type = (int) change.Type });

            // C33: a Created event for a new .cs file does nothing at all. What makes Roslyn
            // re-evaluate — and the new type appear — is a Changed event for the owning project file,
            // even with that file untouched on disk. `applyCodeAction` on "Move type to X.cs" is
            // exactly this case, so without the nudge the moved type would vanish from the workspace.
            if (!TryLocalPath(change.Uri, out var path)
                || !FileWatchBridge.NeedsProjectNudge(path, (int) change.Type)
                || FileWatchBridge.NearestProjectFile(path, _workspaceRoot) is not { } project
                || !nudged.Add(project))
            {
                continue;
            }

            events.Add(new FileEventShape
            {
                Uri = new Uri(project).AbsoluteUri,
                Type = FileWatchBridge.Changed,
            });
        }

        Notify(JsonRpcErrors.Notification(
            "workspace/didChangeWatchedFiles",
            JsonSerializer.SerializeToUtf8Bytes(
                new DidChangeWatchedFilesRequest { Changes = [.. events] },
                RoslynEngineJsonContext.Default.DidChangeWatchedFilesRequest)));

        if (nudged.Count > 0)
        {
            Log.Nudged(_logger, nudged.Count);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<string?> HoverAsync(string uri, LspPosition position, CancellationToken cancellationToken)
    {
        var result = await AskAsync(
            "textDocument/hover",
            Position(uri, position),
            RoslynEngineJsonContext.Default.TextDocumentPositionRequest,
            cancellationToken).ConfigureAwait(false);

        var hover = Read(result, RoslynEngineJsonContext.Default.HoverResult);

        return SignatureOf(hover?.Contents?.Value);
    }

    /// <summary>
    /// The signature line out of Roslyn's hover markdown: the first line of its opening
    /// <c>```csharp</c> fence (S5).
    /// </summary>
    /// <remarks>
    /// Everything after that fence is documentation — the summary, the parameter list, the remarks —
    /// which is worth reading in an editor and is noise in a list of symbol matches. If the fence is
    /// not there the first non-empty line is used, because a hover that changed shape should degrade
    /// to something rather than to nothing.
    /// </remarks>
    /// <param name="markdown">The hover's markdown, or null.</param>
    internal static string? SignatureOf(string? markdown)
    {
        if (markdown is not { Length: > 0 })
        {
            return null;
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inFence = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (inFence)
                {
                    break;
                }

                inFence = true;
                continue;
            }

            if (inFence && trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.Length > 0 && !trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------------------------------------
    // IAdapterChannel - what the file-watch bridge is allowed to do
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    bool IAdapterChannel.BackendConnected => Server is not null;

    /// <inheritdoc />
    ReadinessState IAdapterChannel.Readiness => _gate.State;

    /// <inheritdoc />
    string? IAdapterChannel.WorkspaceRoot => _workspaceRoot;

    /// <inheritdoc />
    void IAdapterChannel.NotifyServer(ReadOnlyMemory<byte> body) => Server?.Post(body);

    /// <inheritdoc />
    /// <remarks>There is no LSP client in this process; an MCP client asks rather than being told.</remarks>
    void IAdapterChannel.NotifyClient(ReadOnlyMemory<byte> body)
    {
    }

    /// <inheritdoc />
    void IAdapterChannel.LogToClient(int type, string message) => Log.Notice(_logger, message);

    /// <inheritdoc />
    Task<JsonElement> IAdapterChannel.AskAsync(
        string method,
        ReadOnlyMemory<byte> rawParams,
        CancellationToken cancellationToken) => RequestAsync(method, rawParams, cancellationToken);

    // ---------------------------------------------------------------------------------------------
    // ISharedEngineHost - what an attached claude-roslyn-lsp process is allowed to do (D87)
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    ReadinessGate ISharedEngineHost.Gate => _gate;

    /// <inheritdoc />
    DocumentMirror ISharedEngineHost.Documents => _mirror;

    /// <inheritdoc />
    public event Action<byte[]>? BackendMessage;

    /// <inheritdoc />
    async Task<byte[]> ISharedEngineHost.BackendInitializeResultAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<byte[]> handshake;

        lock (_stateLock)
        {
            handshake = _handshake;
        }

        return await handshake.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    async Task<byte[]> ISharedEngineHost.ForwardAsync(
        byte[] request,
        byte[] replyIdToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(replyIdToken);

        return await SharedForwarding
            .ForwardAsync(request, replyIdToken, RequestAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    void ISharedEngineHost.NotifyBackend(ReadOnlyMemory<byte> body) => Notify(body);

    /// <inheritdoc />
    Task ISharedEngineHost.SetOptionAsync(string section, byte[] rawValue, CancellationToken cancellationToken) =>
        ConfigurationRoundTrip.ApplyAsync(_configuration, section, rawValue, Notify, _time, _logger, cancellationToken);

    // ---------------------------------------------------------------------------------------------
    // Backend lifecycle
    // ---------------------------------------------------------------------------------------------

    /// <summary>Connect, initialize, replay the mirror, open the workspace.</summary>
    /// <param name="generation">Which attempt this is, so a superseded one stays quiet (D57).</param>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    private async Task ConnectAsync(int generation, CancellationToken cancellationToken)
    {
        ServerEndpoint server;

        try
        {
            var connection = await _factory.ConnectAsync(cancellationToken).ConfigureAwait(false);
            server = new ServerEndpoint(connection, _logger);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            FailIfCurrent(generation, $"the Roslyn backend could not be started ({exception.Message})", exception);
            return;
        }

        lock (_stateLock)
        {
            if (_stopping || _generation != generation)
            {
                _ = server.DisposeAsync().AsTask();
                return;
            }

            _server = server;
            _serverPump = Task.Run(() => PumpServerAsync(server), CancellationToken.None);

            // Which of the two backends this is decides how a configuration change is routed (D90)
            // and what getWorkspaceStatus reports for `engine` (D23).
            _attached = server.Connection.Attached;
            _hostProcessId = server.Connection.HostProcessId;
        }

        var budget = TimeSpan.FromSeconds(_options.ReadyTimeoutSeconds);

        try
        {
            var completion = Completion<JsonElement>();
            var initializeId = _serverBound.Register("initialize", completion);

            server.Post(ServerEndpoint.BuildInitialize(
                IdMap.TokenFor(initializeId),
                RootUri,
                workspaceFolders: null,
                watchFiles: true));

            var result = await completion.Task.WaitAsync(budget, _time, cancellationToken).ConfigureAwait(false);

            RecordBackendIdentity(result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                              || cancellationToken.IsCancellationRequested)
        {
            FailIfCurrent(
                generation,
                exception is TimeoutException
                    ? $"Roslyn did not answer initialize within {budget.TotalSeconds:0} s"
                    : $"the Roslyn handshake failed ({exception.Message})",
                exception);

            return;
        }

        server.Post(ServerEndpoint.BuildInitialized());
        _gate.MarkRoslynInitialized();

        // A relaunch re-opens whatever this engine had open, at the text it was opened with (D57).
        foreach (var replay in _mirror.BuildReplay())
        {
            server.Post(replay);
        }

        // Whatever a tool changed before the crash has to be re-applied too: Roslyn keeps these for
        // the life of a process and the new one has never heard of them.
        ReapplyConfiguration(server);

        var selection = _selectWorkspace();

        lock (_stateLock)
        {
            _selection = selection;
        }

        var open = _opener.Build();
        _gate.WorkspaceDescription = open.Description;

        if (open.Notification is { } notification)
        {
            server.Post(notification);
        }

        if (!open.OpensSomething)
        {
            // Nothing will ever report projectInitializationComplete, so waiting would be a hang.
            // Misc-files mode is degraded, not broken (C28), and the log says so.
            _gate.MarkProjectsLoaded();
        }
    }

    /// <summary>Reads Roslyn's messages until the connection ends.</summary>
    private async Task PumpServerAsync(ServerEndpoint server)
    {
        try
        {
            while (await server.ReadAsync(CancellationToken.None).ConfigureAwait(false) is { } body)
            {
                DispatchServer(server, body);
            }
        }
        catch (Exception exception) when (exception is LspProtocolException or IOException or ObjectDisposedException)
        {
            Log.StreamFailed(_logger, exception);
        }
        finally
        {
            OnBackendGone();
        }
    }

    /// <summary>Handles one message from Roslyn.</summary>
    private void DispatchServer(ServerEndpoint server, byte[] body)
    {
        var info = LspMessageScanner.Scan(body);

        switch (info.Kind)
        {
            case LspMessageKind.Invalid:
                Log.NotAMessage(_logger);
                return;

            case LspMessageKind.Response:
            case LspMessageKind.ErrorResponse:
                CompleteServerResponse(body, info);
                return;

            default:
                break;
        }

        Observe(body, info);

        // Everything the mediation answers on the client's behalf — registrations, configuration,
        // progress, projectInitializationComplete, the prompts a headless client cannot answer — is
        // answered here by the same table (D44's argument, minus the peer to forward to).
        _serverHandler.Handle(body, info, answer: message => server.Post(message), forward: static _ => { });

        // The shared host, when there is one, filters its fan-out out of this stream (D89). Raised
        // after the table has run, so a request Roslyn is waiting on has already been answered by the
        // one process that owes it an answer.
        BackendMessage?.Invoke(body);
    }

    /// <summary>Reads the two notifications that say how the load is going.</summary>
    /// <remarks>
    /// A pure observation, before the handler table runs and without changing what it does. The
    /// progress stream carries the project count at every log level; the log lines carry the
    /// per-project outcomes and only exist when somebody raised the child's level (D37).
    /// </remarks>
    private void Observe(ReadOnlySpan<byte> body, in LspMessageInfo info)
    {
        switch (info.Method)
        {
            case "$/progress":
                if (ParamsOf(body, LspJsonContext.Default.ProgressParams)?.Value is { } value)
                {
                    _load.OnProgressMessage(value.Message);
                    _load.OnProgressMessage(value.Title);
                }

                return;

            case "window/logMessage":
            case "window/showMessage":
                if (ParamsOf(body, LspJsonContext.Default.LogMessageParams) is { } log)
                {
                    _load.OnLogMessage(log.Message, log.Type);
                    Log.FromRoslyn(_logger, log.Message ?? string.Empty);
                }

                return;

            default:
                return;
        }
    }

    /// <summary>Routes an answer back to the call that asked for it.</summary>
    private void CompleteServerResponse(byte[] body, in LspMessageInfo info)
    {
        if (!info.Id.TryGetInt32(out var outboundId) || !_serverBound.TryComplete(outboundId, out var pending))
        {
            Log.Unmatched(_logger, info.Id);
            return;
        }

        if (pending.Completion is { } completion)
        {
            RoslynResponses.Complete(body, info, pending.Method, completion);
        }
    }

    /// <summary>The backend went away: fail what was waiting, and decide about a relaunch (D57).</summary>
    private void OnBackendGone()
    {
        bool stopping;
        int generation;

        lock (_stateLock)
        {
            stopping = _stopping;
            _server = null;
            generation = stopping ? _generation : ++_generation;
        }

        foreach (var request in _serverBound.DrainAll())
        {
            request.Completion?.TrySetException(new IOException("The Roslyn backend closed the connection."));
        }

        if (stopping)
        {
            return;
        }

        switch (_supervisor.Decide(stopping: false, out var attempt))
        {
            case RestartDecision.Restart:
                // Shut again first: a tool call that arrives in the gap waits for the reloaded
                // workspace rather than being answered emptily by a backend that has not loaded it
                // (C27).
                _gate.MarkRestarting();
                _load.Reset();

                lock (_stateLock)
                {
                    _opened = Completion();

                    // A relaunched Roslyn is a different process with a different _roslyn_processId
                    // (C45), so a client attaching from now on must be told about the new one rather
                    // than about the corpse.
                    _handshake = Completion<byte[]>();
                }

                Log.Relaunching(_logger, attempt, RoslynSupervisor.MaxRestarts);
                _ = Task.Run(() => ConnectAsync(generation, CancellationToken.None), CancellationToken.None);
                break;

            case RestartDecision.GiveUp:
            default:
                FailIfCurrent(generation, RoslynSupervisor.GiveUpReason, exception: null);
                break;
        }
    }

    /// <summary>The gate opened, or gave up. Either way every waiting call stops waiting.</summary>
    private void OnGateOpened(ReadinessState state)
    {
        if (state is ReadinessState.ProjectsLoaded or ReadinessState.LoadTimedOut)
        {
            // The registrations arrived while the solution was loading; this is the first moment they
            // are complete enough to be worth standing watchers up for.
            _watching.Schedule(_registrations.Watchers);
        }

        TaskCompletionSource opened;

        lock (_stateLock)
        {
            opened = _opened;
        }

        opened.TrySetResult();
    }

    /// <summary>Records a startup failure, unless a newer attempt has already superseded this one.</summary>
    private void FailIfCurrent(int generation, string reason, Exception? exception)
    {
        lock (_stateLock)
        {
            if (_generation != generation)
            {
                Log.Superseded(_logger, reason);
                return;
            }
        }

        Log.Failed(_logger, reason, exception);
        _gate.MarkFailed(reason);
    }

    // ---------------------------------------------------------------------------------------------
    // Requests
    // ---------------------------------------------------------------------------------------------

    /// <summary>Serialises a request's parameters and asks Roslyn.</summary>
    private Task<JsonElement> AskAsync<T>(
        string method,
        T parameters,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken) =>
        RequestAsync(method, JsonSerializer.SerializeToUtf8Bytes(parameters, typeInfo), cancellationToken);

    /// <summary>Sends one request and waits for its answer.</summary>
    /// <remarks>
    /// Cancelling both forgets the pending entry and tells Roslyn to stop, because a request the
    /// caller abandoned is a compilation Roslyn would otherwise finish for nobody.
    /// </remarks>
    private async Task<JsonElement> RequestAsync(
        string method,
        ReadOnlyMemory<byte> rawParams,
        CancellationToken cancellationToken)
    {
        if (Server is not { } server)
        {
            throw new IOException(
                $"There is no Roslyn backend to ask '{method}'. "
                + (_gate.FailureReason is { } reason
                    ? reason + "; run `" + ServerVersion.Name + " doctor`."
                    : "It is being relaunched; try again in a moment."));
        }

        var completion = Completion<JsonElement>();
        var outboundId = _serverBound.Register(method, completion);

        server.Post(JsonRpcErrors.Request(IdMap.TokenFor(outboundId), method, rawParams.Span));

        await using var registration = cancellationToken.Register(() =>
        {
            if (_serverBound.TryComplete(outboundId, out _))
            {
                Server?.Post(RoslynResponses.BuildCancelRequest(outboundId));
                completion.TrySetCanceled(cancellationToken);
            }
        }).ConfigureAwait(false);

        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>Sends a notification, dropping it when there is no backend to send it to.</summary>
    private void Notify(ReadOnlyMemory<byte> body)
    {
        if (Server is { } server && _gate.NotificationsAllowed)
        {
            server.Post(body);
        }
    }

    /// <summary>
    /// Changes one Roslyn setting and waits for the pull that makes it real (D77, D90).
    /// </summary>
    /// <remarks>
    /// Two routes, because there are two things this engine can be talking to. Over its own Roslyn it
    /// sets the override in its own responder and tells Roslyn to re-read. Attached to another
    /// process's Roslyn it can do neither: Roslyn asks the <em>host</em> for configuration, so an
    /// override set here would be a value nobody ever reads and the call that needed it —
    /// <c>getDiagnostics scope: "solution"</c> without a <c>fullSolution</c> compiler scope (C14) —
    /// would answer emptily and successfully. So it asks the host to do it, and waits for the host's
    /// answer, which arrives only once the host's own round trip has come back.
    /// </remarks>
    private async Task ApplyConfigurationAsync(
        string section,
        byte[] rawValue,
        CancellationToken cancellationToken)
    {
        bool attached;

        lock (_stateLock)
        {
            attached = _attached;
        }

        if (!attached)
        {
            await ConfigurationRoundTrip
                .ApplyAsync(_configuration, section, rawValue, Notify, _time, _logger, cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        try
        {
            await RequestAsync(
                    SharedRoslynHost.SetOptionMethod,
                    BuildSetOption(section, rawValue),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The same trade the direct route makes: a setting that did not take is a slower or a
            // narrower answer, not a failed call, and the tool above still has something to say.
            Log.SharedOptionFailed(_logger, section, exception.Message);
        }
    }

    /// <summary>Renders the shared host's <c>setOption</c> parameters.</summary>
    /// <param name="section">The exact section name.</param>
    /// <param name="rawValue">Its raw JSON value.</param>
    private static byte[] BuildSetOption(string section, byte[] rawValue)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(section.Length + rawValue.Length + 32);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("section"u8, section);
            writer.WritePropertyName("value"u8);
            writer.WriteRawValue(rawValue, skipInputValidation: false);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Re-pushes the settings a tool changed, for a backend that has never heard of them.</summary>
    private void ReapplyConfiguration(ServerEndpoint server)
    {
        bool needed;

        lock (_stateLock)
        {
            // An attached engine holds no overrides of its own — the host does, and the host's
            // Roslyn is the one that was relaunched, so the host re-applies them.
            needed = !_attached
                     && (_compilerScope is not null || _analyzerScope is not null || _organizeImports is not null);
        }

        if (!needed)
        {
            return;
        }

        server.Post(ConfigurationRoundTrip.Notification);
    }

    // ---------------------------------------------------------------------------------------------
    // State
    // ---------------------------------------------------------------------------------------------

    /// <summary>The connected backend, or null when there is not one right now.</summary>
    private ServerEndpoint? Server
    {
        get
        {
            lock (_stateLock)
            {
                return _server;
            }
        }
    }

    /// <summary>Builds the state every tool gates on and <c>getWorkspaceStatus</c> reports.</summary>
    private WorkspaceState Snapshot()
    {
        var state = _gate.State;

        var status = state switch
        {
            ReadinessState.Failed => WorkspaceLoadStatus.Failed,
            ReadinessState.ProjectsLoaded or ReadinessState.LoadTimedOut => WorkspaceLoadStatus.Ready,
            _ => WorkspaceLoadStatus.Loading,
        };

        var projects = ResolveProjects();
        var total = Math.Max(_load.ReportedTotal, projects.Count);
        var loaded = status == WorkspaceLoadStatus.Ready
            ? total
            : Math.Min(_load.LoadedProjects.Count, total);

        var errors = new List<string>(_load.Errors);

        if (state == ReadinessState.LoadTimedOut)
        {
            errors.Insert(
                0,
                $"The workspace did not finish loading within {_options.ReadyTimeoutSeconds} s, so answers may be "
                + "incomplete. Raise CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS if this solution is simply large.");
        }

        var identity = _identity?.Invoke() ?? default;

        int? processId;
        bool attached;
        int? hostProcessId;

        lock (_stateLock)
        {
            processId = _backendProcessId ?? identity.ProcessId;
            attached = _attached;
            hostProcessId = _hostProcessId;
        }

        return new WorkspaceState(
            status,
            SolutionPathOf(),
            loaded,
            total,
            projects,
            errors,
            identity.Version,
            processId,
            WorkingSetOf(processId),
            status switch
            {
                WorkspaceLoadStatus.Failed => _gate.FailureMessage,
                WorkspaceLoadStatus.Loading =>
                    $"The workspace is still loading ({loaded} of {total} project(s)). "
                    + "Call getWorkspaceStatus again, or retry the tool in a few seconds.",
                _ => null,
            },
            attached ? AttachedEngine : Engine,
            hostProcessId);
    }

    /// <summary>The solution or project this session opened, once discovery has run.</summary>
    private string? SolutionPathOf()
    {
        lock (_stateLock)
        {
            return _selection?.SolutionPath
                   ?? (_selection?.ProjectPaths is { Count: 1 } single ? single[0] : null);
        }
    }

    /// <summary>The project list, read once from the solution and remembered (D78).</summary>
    private IReadOnlyList<WorkspaceProject> ResolveProjects()
    {
        WorkspaceSelection? selection;

        lock (_stateLock)
        {
            if (_projects is { Count: > 0 } cached)
            {
                return cached;
            }

            selection = _selection;
        }

        if (selection is null)
        {
            return [];
        }

        var projects = WorkspaceProjects.Read(selection.SolutionPath, selection.ProjectPaths);

        lock (_stateLock)
        {
            _projects = projects;
        }

        return projects;
    }

    /// <summary>The backend's working set, or null when the process cannot be looked at.</summary>
    /// <remarks>
    /// C38: a loaded solution is 350 MB to 2 GB depending on its size and on the GC mode (C54, D79),
    /// and it is by far the largest thing this product puts on a machine. Reporting it is what makes
    /// "why is my laptop swapping" answerable without a task manager.
    /// </remarks>
    private static long? WorkingSetOf(int? processId)
    {
        if (processId is not { } id)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(id);
            process.Refresh();

            return process.WorkingSet64;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                              or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Remembers what the backend said it was, including C45's real process id.</summary>
    private void RecordBackendIdentity(JsonElement result)
    {
        // Kept whole, because a process that goes on to host the shared engine answers an attached
        // client's initialize out of exactly these bytes (D88) rather than authoring a second
        // capability document that would drift from this one.
        TaskCompletionSource<byte[]> handshake;

        lock (_stateLock)
        {
            handshake = _handshake;
        }

        handshake.TrySetResult(
            result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? "{}"u8.ToArray()
                : System.Text.Encoding.UTF8.GetBytes(result.GetRawText()));

        if (result.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // C45: the initialize result carries a non-standard _roslyn_processId naming the process that
        // actually holds the workspace, which is not always the one that was launched.
        if (result.TryGetProperty("_roslyn_processId", out var pid)
            && pid.ValueKind == JsonValueKind.Number
            && pid.TryGetInt32(out var parsed))
        {
            lock (_stateLock)
            {
                _backendProcessId = parsed;
            }
        }

        var name = result.TryGetProperty("serverInfo", out var serverInfo)
                   && serverInfo.ValueKind == JsonValueKind.Object
                   && serverInfo.TryGetProperty("name", out var nameValue)
            ? nameValue.GetString() ?? "(unnamed)"
            : "(unnamed)";

        Log.BackendReady(_logger, name, _backendProcessId ?? 0);
    }

    // ---------------------------------------------------------------------------------------------
    // Shutdown
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        ServerEndpoint? server;

        lock (_stateLock)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            server = _server;
            _server = null;
        }

        _watching.Dispose();

        if (server is not null)
        {
            try
            {
                var completion = Completion<JsonElement>();
                var shutdownId = _serverBound.Register("shutdown", completion);

                server.Post(ServerEndpoint.BuildShutdown(IdMap.TokenFor(shutdownId)));
                await completion.Task.WaitAsync(ShutdownWait, _time, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.ShutdownNotAcknowledged(_logger, exception);
            }

            server.Post(ServerEndpoint.BuildExit());

            try
            {
                await server.DrainAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
            }

            await server.DisposeAsync().ConfigureAwait(false);
        }

        if (_serverPump is { } pump)
        {
            try
            {
                await pump.WaitAsync(ShutdownWait, _time, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
            }
        }

        _gate.Dispose();
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TextDocumentPositionRequest Position(string uri, LspPosition position) =>
        new() { TextDocument = new TextDocumentRef { Uri = uri }, Position = position };

    /// <summary>Deserialises a result, treating <c>null</c> and an unreadable shape alike.</summary>
    private static T? Read<T>(JsonElement result, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        try
        {
            return result.Deserialize(typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads a notification's <c>params</c>, treating anything unreadable as absent.</summary>
    private static T? ParamsOf<T>(ReadOnlySpan<byte> body, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            var envelope = JsonSerializer.Deserialize(body, LspJsonContext.Default.RawParamsNotification);

            return envelope?.Params.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? envelope.Params.Deserialize(typeInfo)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Turns a <c>file:</c> URI into a local path, treating anything else as absent.</summary>
    private static bool TryLocalPath(string uri, out string path)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
        {
            try
            {
                path = Path.GetFullPath(parsed.LocalPath);
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException or PathTooLongException
                                                  or NotSupportedException)
            {
            }
        }

        path = string.Empty;
        return false;
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 5100,
            Level = LogLevel.Information,
            Message = "Starting the owned Roslyn backend ({Backend}) for {Root}.")]
        internal static partial void Starting(ILogger logger, string backend, string root);

        [LoggerMessage(
            EventId = 5101,
            Level = LogLevel.Information,
            Message = "Roslyn backend ready: {Name}, workspace held by process {ProcessId}.")]
        internal static partial void BackendReady(ILogger logger, string name, int processId);

        [LoggerMessage(EventId = 5102, Level = LogLevel.Error, Message = "The Roslyn backend is unusable: {Reason}.")]
        internal static partial void Failed(ILogger logger, string reason, Exception? exception);

        [LoggerMessage(EventId = 5103, Level = LogLevel.Debug, Message = "Reading from Roslyn stopped.")]
        internal static partial void StreamFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 5104,
            Level = LogLevel.Warning,
            Message = "Discarding a Roslyn message that is not a JSON-RPC message.")]
        internal static partial void NotAMessage(ILogger logger);

        [LoggerMessage(EventId = 5105, Level = LogLevel.Debug, Message = "Roslyn answered id {Id}, which nothing was waiting for.")]
        internal static partial void Unmatched(ILogger logger, JsonRpcId id);

        [LoggerMessage(
            EventId = 5106,
            Level = LogLevel.Warning,
            Message = "The Roslyn backend exited; relaunching it (attempt {Attempt} of {Limit}). Tool calls " +
                      "report the workspace as loading until it is back.")]
        internal static partial void Relaunching(ILogger logger, int attempt, int limit);

        [LoggerMessage(
            EventId = 5107,
            Level = LogLevel.Debug,
            Message = "A backend attempt that has already been replaced reported '{Reason}'.")]
        internal static partial void Superseded(ILogger logger, string reason);

        [LoggerMessage(
            EventId = 5108,
            Level = LogLevel.Warning,
            Message = "The shared host would not set {Section} ({Reason}); the answer stands but it may " +
                      "be narrower than the caller asked for.")]
        internal static partial void SharedOptionFailed(ILogger logger, string section, string reason);

        [LoggerMessage(
            EventId = 5109,
            Level = LogLevel.Debug,
            Message = "Reported {Count} project file(s) as changed alongside the files that were written, " +
                      "because a created or deleted .cs file alone tells Roslyn nothing (C33).")]
        internal static partial void Nudged(ILogger logger, int count);

        [LoggerMessage(
            EventId = 5110,
            Level = LogLevel.Warning,
            Message = "Roslyn did not acknowledge shutdown; the transport is being closed under it.")]
        internal static partial void ShutdownNotAcknowledged(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 5111, Level = LogLevel.Information, Message = "[roslyn] {Message}")]
        internal static partial void FromRoslyn(ILogger logger, string message);

        [LoggerMessage(EventId = 5112, Level = LogLevel.Information, Message = "{Message}")]
        internal static partial void Notice(ILogger logger, string message);

        [LoggerMessage(
            EventId = 5114,
            Level = LogLevel.Debug,
            Message = "The diagnostic scope change showed up on sample {Attempts} after {Seconds:0.0} s (C57).")]
        internal static partial void ScopeConfirmed(ILogger logger, int attempts, double seconds);

        [LoggerMessage(
            EventId = 5115,
            Level = LogLevel.Warning,
            Message = "A diagnostic scope change had not changed what a pull reports after {Attempts} " +
                      "sample(s) over {Seconds:0} s; answering from what Roslyn has now, which may not " +
                      "include analyzer diagnostics for files nobody has open (C14, C57).")]
        internal static partial void ScopeUnconfirmed(ILogger logger, int attempts, double seconds);

        [LoggerMessage(
            EventId = 5113,
            Level = LogLevel.Debug,
            Message = "Roslyn was still holding the workspace diagnostic pull after {Seconds:0} s, which " +
                      "means it has nothing new to report (C58); answering from the previous report " +
                      "(had one: {Warm}).")]
        internal static partial void WorkspacePullHeld(ILogger logger, double seconds, bool warm);
    }
}

/// <summary>What the launcher knows about the backend that the wire does not say.</summary>
/// <param name="Version">The Roslyn version that was resolved, pinned or overridden (D25).</param>
/// <param name="ProcessId">The launched process id, as a fallback for C45's own.</param>
internal readonly record struct RoslynBackendIdentity(string? Version = null, int? ProcessId = null);
