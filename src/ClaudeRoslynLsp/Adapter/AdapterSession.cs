using System.Buffers;
using System.Text.Json;

using ClaudeRoslynLsp.Cli;
using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// One <c>lsp</c> session: a client on one side, a Roslyn backend on the other, and the mediation
/// between them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The adapter terminates both sessions</b> rather than relaying one. It is a server to Claude
/// and a client to Roslyn, and the two handshakes are independent: Claude's <c>initialize</c> is
/// answered from a capability document written here, immediately, while the backend is still being
/// launched; Roslyn's is sent later with a document authored for Roslyn. A byte relay with patches
/// applied in flight cannot do that — it has to wait for one side before it can answer the other,
/// and it has no state of its own to answer registrations, configuration or progress out of, all of
/// which the client refuses.
/// </para>
/// <para>
/// <b>Everything else crosses as bytes.</b> A navigation request and its answer are forwarded with
/// exactly one change: the JSON-RPC id token, replaced by a three-segment copy
/// (<see cref="LspMessageScanner"/>). That is what keeps this repository out of the business of
/// modelling Roslyn's result shapes — the opaque <c>data</c> on a call-hierarchy item or a code
/// action (C18, C24) round-trips because nothing here ever looks inside it.
/// </para>
/// <para>
/// <b>Two read loops, two single writers.</b> Each side has one reader and one
/// <see cref="OutboundQueue"/>; shared state lives in the small components this class composes
/// (<see cref="IdMap"/>, <see cref="ReadinessGate"/>, <see cref="DocumentMirror"/>,
/// <see cref="RegistrationTracker"/>), each of which owns its own <see cref="Lock"/>. The session's
/// own two mutable fields — the connected endpoint and whether shutdown was requested — are guarded
/// here.
/// </para>
/// </remarks>
internal sealed partial class AdapterSession : IAdapterChannel, IAsyncDisposable
{
    /// <summary>How long <c>shutdown</c> waits for Roslyn to answer before answering the client anyway.</summary>
    /// <remarks>
    /// Five seconds, and then the client is answered regardless. A client that asked to shut down is
    /// entitled to an answer; a backend that will not acknowledge one is going to be killed in a
    /// moment either way, and holding the client's shutdown open is how an editor comes to hang on
    /// quit.
    /// </remarks>
    internal static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);

    /// <summary>How long <c>exit</c> waits for the backend process to actually go.</summary>
    internal static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(3);

    private readonly Lock _stateLock = new();
    private readonly ClientEndpoint _client;
    private readonly IRoslynConnectionFactory _factory;
    private readonly ClaudeRoslynLspOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private readonly IdMap _serverBound = new();
    private readonly IdMap _clientBound = new();
    private readonly DocumentMirror _mirror;
    private readonly RegistrationTracker _registrations;
    private readonly ConfigurationResponder _configuration;
    private readonly ProgressTracker _progress;
    private readonly ReadinessGate _gate;
    private readonly ServerRequestHandler _serverHandler;
    private readonly WorkspaceOpener _opener;
    private readonly RoslynSupervisor _supervisor;
    private readonly DiagnosticsBridge? _diagnostics;
    private readonly FileWatchBridge? _watching;
    private readonly bool _watchFiles;

    private ServerEndpoint? _server;
    private Task? _backendTask;
    private Task? _serverPump;
    private bool _shutdownRequested;
    private bool _stopping;
    private int _failureShown;
    private int _generation;

    /// <summary>Creates a session over the client's streams.</summary>
    /// <param name="clientInput">The client's messages arrive here.</param>
    /// <param name="clientOutput">The adapter's messages leave here.</param>
    /// <param name="factory">What connects to the backend.</param>
    /// <param name="options">The environment configuration.</param>
    /// <param name="resolveSolution">
    /// Where the solution path comes from. The v1 implementation reads
    /// <c>CLAUDE_ROSLYN_LSP_SOLUTION</c>; WP3's discovery replaces it without touching this class.
    /// </param>
    /// <param name="time">The clock, so the readiness timeout is testable.</param>
    /// <param name="logger">The stderr log. Never the client's stream.</param>
    /// <param name="selectWorkspace">
    /// What to open, when discovery has already decided. Null falls back to
    /// <paramref name="resolveSolution"/>'s single path, which is what the mediation's own tests
    /// drive the session with.
    /// </param>
    internal AdapterSession(
        Stream clientInput,
        Stream clientOutput,
        IRoslynConnectionFactory factory,
        ClaudeRoslynLspOptions options,
        Func<string?> resolveSolution,
        TimeProvider time,
        ILogger logger,
        Func<WorkspaceSelection>? selectWorkspace = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolveSolution);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _factory = factory;
        _options = options;
        _time = time;
        _logger = logger;
        _watchFiles = options.FileWatcher;

        _client = new ClientEndpoint(clientInput, clientOutput, logger);
        _mirror = new DocumentMirror(logger);
        _registrations = new RegistrationTracker(logger);
        _progress = new ProgressTracker(logger);
        _supervisor = new RoslynSupervisor(time, logger);

        if (options.Diagnostics)
        {
            _diagnostics = new DiagnosticsBridge(_mirror, this, options, time, logger);
        }
        else
        {
            Log.DiagnosticsOff(logger);
        }

        // The scope is raised only when the opt-in mode is on, and only for the compiler (C14).
        // Deciding it here rather than inside the responder keeps the one expensive setting tied to
        // the one feature that needs it.
        _configuration = new ConfigurationResponder(
            options.RoslynOptionsJson,
            logger,
            fullSolutionCompilerScope: _diagnostics?.WorkspaceMode ?? false);

        _gate = new ReadinessGate(
            time,
            TimeSpan.FromSeconds(options.ReadyTimeoutSeconds),
            logger,
            notice => _client.Log(LogMessageType.Info, $"{ServerVersion.Name}: {notice}"));

        _serverHandler = new ServerRequestHandler(_registrations, _configuration, _progress, _gate, logger);

        _opener = new WorkspaceOpener(
            selectWorkspace ?? (() => WorkspaceSelection.FromPath(resolveSolution())),
            logger,
            explanation => _client.Log(LogMessageType.Info, $"{ServerVersion.Name}: {explanation}"));

        if (_watchFiles)
        {
            _watching = new FileWatchBridge(WorkspaceRootPath, _mirror, this, time, logger);
            _watching.ProjectFilesChanged += () => _diagnostics?.OnProjectFilesChanged();
            _registrations.Changed += () => _watching.Schedule(_registrations.Watchers);
        }
        else
        {
            Log.WatcherOff(logger);
        }

        _gate.Opened += OnGateOpened;
        _serverHandler.RefreshRequested += method => _diagnostics?.OnRefreshRequested(method);
    }

    /// <summary>The readiness gate, exposed for tests and for WP4's bridges.</summary>
    internal ReadinessGate Gate => _gate;

    /// <summary>The document mirror, exposed for WP4's crash recovery.</summary>
    internal DocumentMirror Documents => _mirror;

    /// <summary>What Roslyn has registered for, exposed for WP4's diagnostics and watch bridges.</summary>
    internal RegistrationTracker Registrations => _registrations;

    /// <summary>The table that answers Roslyn's requests, exposed for WP4's refresh subscription.</summary>
    internal ServerRequestHandler ServerRequests => _serverHandler;

    /// <summary>The pull-to-push diagnostics bridge, or null when it is switched off.</summary>
    internal DiagnosticsBridge? Diagnostics => _diagnostics;

    /// <summary>The filesystem watch bridge, or null when it is switched off.</summary>
    internal FileWatchBridge? Watching => _watching;

    /// <summary>What decides whether a dead backend gets another go.</summary>
    internal RoslynSupervisor Supervisor => _supervisor;

    /// <inheritdoc />
    bool IAdapterChannel.BackendConnected => Server is not null;

    /// <inheritdoc />
    ReadinessState IAdapterChannel.Readiness => _gate.State;

    /// <inheritdoc />
    string? IAdapterChannel.WorkspaceRoot => WorkspaceRootPath;

    /// <summary>The client's workspace root as a local path, or null when it sent none usable.</summary>
    private string? WorkspaceRootPath
    {
        get
        {
            if (_client.RootUri is not { Length: > 0 } uri
                || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                || !parsed.IsFile)
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(parsed.LocalPath);
            }
            catch (Exception exception) when (exception is ArgumentException or PathTooLongException
                                                  or NotSupportedException)
            {
                return null;
            }
        }
    }

    /// <summary>Runs the session until <c>exit</c>, end of stream, or a framing failure.</summary>
    /// <param name="cancellationToken">Cancels the read loop.</param>
    /// <returns>The process exit code.</returns>
    internal async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (true)
            {
                var body = await _client.ReadAsync(cancellationToken).ConfigureAwait(false);

                if (body is null)
                {
                    // The client closed the stream without saying goodbye — an editor that was
                    // killed is the ordinary cause. Treated exactly like `exit`, and the
                    // specification's rule about whether `shutdown` came first is what decides
                    // whether that was orderly.
                    Log.ClientClosed(_logger);
                    return ExitCode;
                }

                if (await DispatchClientAsync(body, cancellationToken).ConfigureAwait(false) is { } exitCode)
                {
                    return exitCode;
                }
            }
        }
        catch (LspProtocolException exception)
        {
            // Unrecoverable by construction: once framing is lost there is no way to find the start
            // of the next message, so continuing would answer garbage rather than nothing.
            Log.ClientNotFramed(_logger, exception);
            return CliDispatcher.ExitFailure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExitCode;
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        _diagnostics?.Dispose();
        _watching?.Dispose();
        _gate.Dispose();

        if (_server is { } server)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }

        await _client.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The exit code the specification prescribes for wherever the loop happens to end.</summary>
    private int ExitCode
    {
        get
        {
            lock (_stateLock)
            {
                return _shutdownRequested ? CliDispatcher.ExitSuccess : CliDispatcher.ExitFailure;
            }
        }
    }

    /// <summary>The connected backend, or null when there is not one.</summary>
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

    // -----------------------------------------------------------------------------------------
    // Client -> adapter
    // -----------------------------------------------------------------------------------------

    /// <summary>Handles one client message, returning an exit code when it was the last one.</summary>
    /// <param name="body">The raw message.</param>
    /// <param name="cancellationToken">Cancels the awaited shutdown handshake.</param>
    private async Task<int?> DispatchClientAsync(byte[] body, CancellationToken cancellationToken)
    {
        var info = LspMessageScanner.Scan(body);

        if (info.Kind == LspMessageKind.Invalid)
        {
            // Well framed, badly written. The frame boundary is still known, so this is answerable
            // and the connection survives it — under a null id, because there was no readable one.
            Log.ClientSentGarbage(_logger);
            _client.Post(JsonRpcErrors.Error(default, JsonRpcErrors.ParseError, "The message body is not a JSON-RPC message."));
            return null;
        }

        if (info.Kind is LspMessageKind.Response or LspMessageKind.ErrorResponse)
        {
            CompleteClientResponse(body, info);
            return null;
        }

        var method = info.Method!;

        switch (RequestRouter.Route(method, info.Kind == LspMessageKind.Request))
        {
            case ClientRoute.AnswerLocally:
                return await AnswerLocallyAsync(body, info, method, cancellationToken).ConfigureAwait(false);

            case ClientRoute.Cancel:
                CancelClientRequest(body);
                return null;

            case ClientRoute.Document:
                HandleDocumentNotification(body, method);
                return null;

            case ClientRoute.Gated:
                HoldOrForward(body, info);
                return null;

            case ClientRoute.Forward:
                if (string.Equals(method, "workspace/didChangeConfiguration", StringComparison.Ordinal))
                {
                    UpdateConfigurationFrom(body);
                }

                ForwardNotificationToServer(body, method);
                return null;

            case ClientRoute.Drop:
            default:
                Log.DroppedClientMessage(_logger, method);
                return null;
        }
    }

    /// <summary>The four methods the adapter answers out of its own state.</summary>
    private async Task<int?> AnswerLocallyAsync(
        byte[] body,
        LspMessageInfo info,
        string method,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                _client.Post(_client.AcceptInitialize(body, info));
                _configuration.UpdateClientSettings(_client.InitializationOptions);
                _serverHandler.ClientAppliesEdits = _client.SupportsApplyEdit;
                _serverHandler.WorkspaceFolders = ResolveWorkspaceFolders();
                Log.Initialized(_logger, _client.RootUri ?? "(no root)", _client.SupportsApplyEdit);
                return null;

            case "initialized":
                StartBackend(cancellationToken);
                return null;

            case "shutdown":
                await ShutdownBackendAsync(cancellationToken).ConfigureAwait(false);

                lock (_stateLock)
                {
                    _shutdownRequested = true;
                }

                _client.Post(JsonRpcErrors.NullResult(info.IdToken(body)));
                return null;

            case "exit":
            default:
                var exitCode = ExitCode;
                await ExitBackendAsync(cancellationToken).ConfigureAwait(false);

                Log.Exiting(
                    _logger,
                    exitCode,
                    exitCode == CliDispatcher.ExitSuccess
                        ? "shutdown was requested first"
                        : "no shutdown was requested");

                return exitCode;
        }
    }

    /// <summary>Queues a request behind the readiness gate, or forwards it at once.</summary>
    private void HoldOrForward(byte[] body, LspMessageInfo info)
    {
        _gate.TryHold(info.Id, info.Method!, outcome =>
        {
            switch (outcome)
            {
                case GateOutcome.Pass:
                    ForwardRequestToServer(body, info);
                    break;

                case GateOutcome.Cancelled:
                    _client.Post(JsonRpcErrors.Error(
                        info.IdToken(body),
                        JsonRpcErrors.RequestCancelled,
                        "The request was cancelled while it was waiting for the workspace to load."));
                    break;

                case GateOutcome.Failed:
                default:
                    _client.Post(JsonRpcErrors.Error(
                        info.IdToken(body),
                        JsonRpcErrors.InternalError,
                        _gate.FailureMessage));
                    break;
            }
        });
    }

    /// <summary>Forwards one request under a fresh id, or refuses it when there is no backend.</summary>
    private void ForwardRequestToServer(byte[] body, LspMessageInfo info)
    {
        if (Server is not { } server)
        {
            _client.Post(JsonRpcErrors.Error(
                info.IdToken(body),
                JsonRpcErrors.InternalError,
                _gate.State == ReadinessState.Failed
                    ? _gate.FailureMessage
                    : $"{ServerVersion.Name} lost its Roslyn backend while this request was in flight."));

            return;
        }

        var outboundId = _serverBound.Forward(info.Id, info.IdToken(body), info.Method!);
        server.Post(LspMessageScanner.RewriteId(body, info, IdMap.TokenFor(outboundId)));
    }

    /// <summary>Forwards a notification once the backend can accept one.</summary>
    private void ForwardNotificationToServer(byte[] body, string method)
    {
        if (Server is { } server && _gate.NotificationsAllowed)
        {
            server.Post(body);
            return;
        }

        // Roslyn refuses everything before its own initialize is answered, so a notification sent
        // now would be lost with no error. Document notifications are the exception and are handled
        // by the mirror, which replays them; the rest are one-shot facts the client will resend.
        Log.NotificationTooEarly(_logger, method);
    }

    /// <summary>Records a document notification in the mirror, then forwards it if it can.</summary>
    /// <remarks>
    /// The order is deliberate: mirror, then forward, then tell the diagnostics bridge. The bridge
    /// posts its pull onto the same outbound queue, which is FIFO with a single writer, so Roslyn
    /// always has the document before it is asked about it.
    /// </remarks>
    private void HandleDocumentNotification(byte[] body, string method)
    {
        string? uri = null;

        switch (method)
        {
            case "textDocument/didOpen":
                uri = _mirror.Open(body)?.Uri;
                break;

            case "textDocument/didChange":
                uri = _mirror.Change(body)?.Uri;
                break;

            case "textDocument/didClose":
                uri = _mirror.Close(body);
                break;

            case "textDocument/didSave":
                // Not a mirror event — a save changes the disk, not the buffer — but it is the
                // strongest signal an agent gives that it wants to know whether the file compiles.
                uri = UriOf(body);
                break;

            default:
                break;
        }

        if (Server is { } server && _gate.NotificationsAllowed)
        {
            server.Post(body);
            NotifyDiagnostics(method, uri);
            return;
        }

        NotifyDiagnostics(method, uri);

        // Not queued as raw bytes: the mirror already holds the latest text, and replaying one
        // didOpen per document is both smaller and correct in the case a queue gets wrong — three
        // edits before the backend was up become one didOpen at the third text.
        Log.DocumentBuffered(_logger, method, _mirror.Count);
    }

    /// <summary>Tells the diagnostics bridge what the client just did to a document.</summary>
    private void NotifyDiagnostics(string method, string? uri)
    {
        if (_diagnostics is null || uri is not { Length: > 0 })
        {
            return;
        }

        switch (method)
        {
            case "textDocument/didOpen":
                _diagnostics.OnDidOpen(uri);
                break;

            case "textDocument/didChange":
                _diagnostics.OnDidChange(uri);
                break;

            case "textDocument/didSave":
                _diagnostics.OnDidSave(uri);
                break;

            case "textDocument/didClose":
                _diagnostics.OnDidClose(uri);
                break;

            default:
                break;
        }
    }

    /// <summary>Reads a document notification's URI, treating anything unreadable as absent.</summary>
    private static string? UriOf(byte[] body)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize(body, LspJsonContext.Default.RawParamsNotification);

            return envelope?.Params.ValueKind == JsonValueKind.Object
                ? envelope.Params.Deserialize(LspJsonContext.Default.TextDocumentParams)?.TextDocument?.Uri
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Stores a <c>didChangeConfiguration</c>'s settings for the configuration responder.</summary>
    private void UpdateConfigurationFrom(byte[] body)
    {
        try
        {
            var notification = JsonSerializer.Deserialize(body, LspJsonContext.Default.RawParamsNotification);

            if (notification?.Params.ValueKind == JsonValueKind.Object)
            {
                _configuration.UpdateClientSettings(notification.Params);
            }
        }
        catch (JsonException)
        {
            Log.ConfigurationUnreadable(_logger);
        }
    }

    /// <summary>Cancels a held request, or maps the cancellation through to Roslyn.</summary>
    private void CancelClientRequest(byte[] body)
    {
        if (!LspMessageScanner.TryReadCancelRequestId(body, out var id))
        {
            return;
        }

        // The gate first: a held request has never reached Roslyn, so cancelling it there would
        // name an id Roslyn has never seen. Answering it -32800 here is what the client is waiting
        // for, and it is the only way the queue does not keep growing.
        if (_gate.TryCancel(id))
        {
            return;
        }

        if (_serverBound.TryResolveOutboundId(id, out var outboundId) && Server is { } server)
        {
            server.Post(BuildCancelRequest(outboundId));
        }
    }

    /// <summary>Routes the client's answer to a request Roslyn asked it.</summary>
    private void CompleteClientResponse(byte[] body, LspMessageInfo info)
    {
        if (!info.Id.TryGetInt32(out var outboundId)
            || !_clientBound.TryComplete(outboundId, out var pending)
            || pending.OriginalIdToken is null)
        {
            Log.UnmatchedResponse(_logger, "the client", info.Id);
            return;
        }

        Server?.Post(LspMessageScanner.RewriteId(body, info, pending.OriginalIdToken));
    }

    // -----------------------------------------------------------------------------------------
    // Backend lifecycle
    // -----------------------------------------------------------------------------------------

    /// <summary>Connects to the backend and drives its handshake, on its own task.</summary>
    private void StartBackend(CancellationToken cancellationToken)
    {
        int generation;

        lock (_stateLock)
        {
            if (_backendTask is not null)
            {
                return;
            }

            generation = _generation;
            _gate.Start();
            _backendTask = Task.Run(() => ConnectAsync(generation, cancellationToken), CancellationToken.None);
        }
    }

    /// <summary>Whether a connect attempt is still the one this session is waiting on.</summary>
    /// <remarks>
    /// The whole reason the generation exists. A backend that dies <em>during</em> its own handshake
    /// produces two things at once: the pump's <see cref="OnBackendGone"/>, which decides to
    /// relaunch, and the abandoned <c>initialize</c> wait, which throws. Without this check the
    /// second one races the first and fails a session the supervisor had just decided to save — and
    /// it wins often enough on a loaded machine to be a real failure, not a theoretical one.
    /// </remarks>
    /// <param name="generation">The generation the attempt was started under.</param>
    private bool IsCurrentAttempt(int generation)
    {
        lock (_stateLock)
        {
            return _generation == generation;
        }
    }

    /// <summary>The whole backend startup: connect, initialize, replay, open the workspace.</summary>
    /// <param name="generation">Which connect attempt this is, so a superseded one stays quiet.</param>
    /// <param name="cancellationToken">Cancels the connection and the handshake.</param>
    private async Task ConnectAsync(int generation, CancellationToken cancellationToken)
    {
        ServerEndpoint server;

        try
        {
            Log.Connecting(_logger, _factory.Description);
            var connection = await _factory.ConnectAsync(cancellationToken).ConfigureAwait(false);
            server = new ServerEndpoint(connection, _logger);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            FailIfCurrent(generation, $"the backend could not be started ({exception.Message})", exception);
            return;
        }

        lock (_stateLock)
        {
            if (_stopping || _generation != generation)
            {
                // A relaunch that raced the session's own teardown, or an attempt a newer one has
                // already replaced. Nothing here may adopt it, or the child outlives the adapter
                // holding a whole solution in memory.
                _ = server.DisposeAsync().AsTask();
                return;
            }

            _server = server;
            _serverPump = Task.Run(() => PumpServerAsync(server), CancellationToken.None);
        }

        var budget = TimeSpan.FromSeconds(_options.ReadyTimeoutSeconds);

        try
        {
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var initializeId = _serverBound.Register("initialize", completion);

            server.Post(ServerEndpoint.BuildInitialize(
                IdMap.TokenFor(initializeId),
                _client.RootUri,
                _client.WorkspaceFolders,
                _watchFiles));

            // The same budget the readiness gate spends, spent earlier: a backend that has not
            // answered initialize inside it is not going to load a solution inside it either.
            var result = await completion.Task.WaitAsync(budget, _time, cancellationToken).ConfigureAwait(false);

            LogBackendIdentity(result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            // The message names the exception rather than only the budget: the wait also ends when
            // the backend went away underneath it, and "did not answer within 120 s" said 40 ms
            // after a launch is a sentence that sends the reader looking in the wrong place.
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

        // Anything the client opened while the backend was starting. One didOpen per document at
        // its latest text — the same primitive WP4's crash recovery uses.
        foreach (var replay in _mirror.BuildReplay())
        {
            server.Post(replay);
        }

        var open = _opener.Build();
        _gate.WorkspaceDescription = open.Description;

        if (open.Notification is { } notification)
        {
            server.Post(notification);
        }

        if (!open.OpensSomething)
        {
            // Nothing will ever send projectInitializationComplete, so holding requests would be a
            // hang. Misc-files mode is degraded, not broken (C28), and the log said so.
            _gate.MarkProjectsLoaded();
        }
    }

    /// <summary>Reads Roslyn's messages until the connection ends.</summary>
    private async Task PumpServerAsync(ServerEndpoint server)
    {
        try
        {
            while (true)
            {
                var body = await server.ReadAsync(CancellationToken.None).ConfigureAwait(false);

                if (body is null)
                {
                    break;
                }

                DispatchServer(server, body);
            }
        }
        catch (Exception exception) when (exception is LspProtocolException or IOException or ObjectDisposedException)
        {
            Log.BackendStreamFailed(_logger, exception);
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

        if (info.Kind == LspMessageKind.Invalid)
        {
            Log.BackendSentGarbage(_logger);
            return;
        }

        if (info.Kind is LspMessageKind.Response or LspMessageKind.ErrorResponse)
        {
            CompleteServerResponse(body, info);
            return;
        }

        _serverHandler.Handle(
            body,
            info,
            answer: message => server.Post(message),
            forward: outbound => ForwardServerMessageToClient(outbound, info.Kind == LspMessageKind.Request));
    }

    /// <summary>Routes an answer from Roslyn back to whoever asked the question.</summary>
    private void CompleteServerResponse(byte[] body, LspMessageInfo info)
    {
        if (!info.Id.TryGetInt32(out var outboundId)
            || !_serverBound.TryComplete(outboundId, out var pending))
        {
            Log.UnmatchedResponse(_logger, "Roslyn", info.Id);
            return;
        }

        if (pending.Completion is { } completion)
        {
            CompleteAdapterRequest(body, info, pending, completion);
            return;
        }

        if (info.Kind == LspMessageKind.Response && CallHierarchyDeduplicator.Applies(pending.Method))
        {
            _client.Post(DeduplicateCallHierarchy(body, pending));
            return;
        }

        _client.Post(LspMessageScanner.RewriteId(body, info, pending.OriginalIdToken!));
    }

    /// <summary>
    /// Rewrites a call-hierarchy answer without its per-target-framework duplicates (C24).
    /// </summary>
    /// <remarks>
    /// The only place the adapter rewrites a <em>result</em> rather than an id, and it is worth the
    /// exception. A multi-targeted project reports every call once per framework, which an agent
    /// reads as several distinct call sites: it reports the wrong number of callers, and a walk down
    /// the tree does every branch twice. Reserialisation is confined to the array itself — each
    /// surviving entry is copied verbatim, opaque <c>data</c> and all — and an answer with nothing to
    /// remove keeps its original bytes.
    /// </remarks>
    private byte[] DeduplicateCallHierarchy(byte[] body, PendingRequest pending)
    {
        var info = LspMessageScanner.Scan(body);

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("result", out var result)
                && CallHierarchyDeduplicator.Deduplicate(pending.Method, result) is { } reduced)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    var before = result.GetArrayLength();
                    Log.Deduplicated(_logger, pending.Method, before);
                }

                return JsonRpcErrors.RawResult(pending.OriginalIdToken!, reduced);
            }
        }
        catch (JsonException exception)
        {
            Log.DeduplicationFailed(_logger, pending.Method, exception);
        }

        return LspMessageScanner.RewriteId(body, info, pending.OriginalIdToken!);
    }

    /// <summary>Delivers an answer to a question the adapter asked itself.</summary>
    private static void CompleteAdapterRequest(
        byte[] body,
        LspMessageInfo info,
        PendingRequest pending,
        TaskCompletionSource<JsonElement> completion)
    {
        if (info.Kind == LspMessageKind.ErrorResponse)
        {
            // The code, not only the sentence: -32801 means "the document moved, ask again" and the
            // diagnostics bridge acts on that differently from every other refusal.
            var (code, message) = ErrorOf(body);
            completion.TrySetException(new RoslynRequestException(pending.Method, code, message));

            return;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            completion.TrySetResult(document.RootElement.TryGetProperty("result", out var result)
                ? result.Clone()
                : JsonRpc.Null);
        }
        catch (JsonException exception)
        {
            completion.TrySetException(exception);
        }
    }

    /// <summary>Forwards a Roslyn-originated message to the client, minting an id for a request.</summary>
    private void ForwardServerMessageToClient(byte[] body, bool isRequest)
    {
        if (!isRequest)
        {
            _client.Post(body);
            return;
        }

        // Roslyn's id would collide with the client's own counter exactly as the client's would
        // collide with Roslyn's, so the same treatment applies mirrored.
        var info = LspMessageScanner.Scan(body);

        if (!info.HasIdToken)
        {
            _client.Post(body);
            return;
        }

        var outboundId = _clientBound.Forward(info.Id, info.IdToken(body), info.Method ?? string.Empty);
        _client.Post(LspMessageScanner.RewriteId(body, info, IdMap.TokenFor(outboundId)));
    }

    /// <summary>The backend is gone: fail everything that was waiting on it.</summary>
    private void OnBackendGone()
    {
        bool stopping;

        lock (_stateLock)
        {
            stopping = _stopping;
            _server = null;
        }

        var pending = _serverBound.DrainAll();

        foreach (var request in pending)
        {
            if (request.Completion is { } completion)
            {
                completion.TrySetException(new IOException("The Roslyn backend closed the connection."));
            }
            else if (request.OriginalIdToken is { } token && !stopping)
            {
                // A request the client is still waiting on. Silence would leave it outstanding
                // forever; an answer, even a refusal, lets the client move on.
                _client.Post(JsonRpcErrors.Error(
                    token,
                    JsonRpcErrors.InternalError,
                    $"The Roslyn backend exited while '{request.Method}' was in flight."));
            }
        }

        if (stopping)
        {
            return;
        }

        Log.BackendGone(_logger, pending.Count);

        switch (_supervisor.Decide(stopping: false, out var attempt))
        {
            case RestartDecision.Restart:
                // Held first, relaunched second. A request that arrives in the gap must not reach a
                // backend that has not loaded the solution, because that answer would be empty and
                // successful (C27) — the exact failure the gate exists to prevent, arriving late.
                _gate.MarkRestarting();
                _diagnostics?.Reset();

                _client.Log(
                    LogMessageType.Info,
                    $"{ServerVersion.Name}: the Roslyn backend exited; relaunching it "
                    + $"(attempt {attempt} of {RoslynSupervisor.MaxRestarts}). Requests are held "
                    + "until the workspace has loaded again.");

                int generation;

                lock (_stateLock)
                {
                    generation = ++_generation;
                }

                _ = Task.Run(() => ConnectAsync(generation, CancellationToken.None), CancellationToken.None);
                break;

            case RestartDecision.GiveUp:
            default:
                Fail(RoslynSupervisor.GiveUpReason, exception: null);
                break;
        }
    }

    /// <summary>The gate opened, or gave up. Either way the bridges stop waiting.</summary>
    private void OnGateOpened(ReadinessState state)
    {
        if (state is ReadinessState.ProjectsLoaded or ReadinessState.LoadTimedOut)
        {
            _diagnostics?.OnProjectsLoaded();

            // The registrations arrived while the solution was loading; this is the first moment
            // they are complete enough to be worth standing watchers up for.
            _watching?.Schedule(_registrations.Watchers);
        }
    }

    /// <summary>
    /// Records a startup failure and refuses everything that was waiting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The adapter stays alive.</b> Every reason this is reached is ordinary — an offline laptop,
    /// a machine with no .NET 10 runtime, a proxy serving HTML where a nupkg should be, a hash that
    /// does not match the pin — and a process that exits over one leaves its client with a dead pipe
    /// and no channel to be told why, because stdout <em>is</em> the channel. So the session keeps
    /// answering: the handshake stands, and every request gets <c>-32603</c> with a sentence naming
    /// <c>doctor</c>.
    /// </para>
    /// <para>
    /// Both message kinds, and each exactly once. <c>window/showMessage</c> is what a client
    /// surfaces; <c>window/logMessage</c> is what it files where somebody looking for the reason will
    /// find it. Sending them on every subsequent failure would train the reader to dismiss them.
    /// </para>
    /// </remarks>
    /// <param name="generation">Which connect attempt is reporting; a superseded one is ignored.</param>
    /// <param name="reason">What went wrong, in a sentence the user can act on.</param>
    /// <param name="exception">The failure, when there was one.</param>
    private void FailIfCurrent(int generation, string reason, Exception? exception)
    {
        if (!IsCurrentAttempt(generation))
        {
            Log.AttemptSuperseded(_logger, reason);
            return;
        }

        Fail(reason, exception);
    }

    /// <inheritdoc cref="FailIfCurrent"/>
    private void Fail(string reason, Exception? exception)
    {
        Log.BackendFailed(_logger, reason, exception);
        _gate.MarkFailed(reason);

        var message =
            $"{ServerVersion.Name}: {reason}. Run `{ServerVersion.Name} doctor` for the resolution chain "
            + "and what to do about it. C# navigation and diagnostics are unavailable until it is fixed; "
            + "the adapter itself is still running.";

        _client.Log(LogMessageType.Error, message);

        if (Interlocked.Exchange(ref _failureShown, 1) == 0)
        {
            _client.ShowMessage(LogMessageType.Error, message);
        }
    }

    /// <summary>Sends <c>shutdown</c> to Roslyn and waits, briefly, for its answer.</summary>
    private async Task ShutdownBackendAsync(CancellationToken cancellationToken)
    {
        if (Server is not { } server)
        {
            return;
        }

        try
        {
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var shutdownId = _serverBound.Register("shutdown", completion);

            server.Post(ServerEndpoint.BuildShutdown(IdMap.TokenFor(shutdownId)));
            await completion.Task.WaitAsync(ShutdownWait, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            Log.ShutdownNotAcknowledged(_logger, ShutdownWait.TotalSeconds, exception);
        }
    }

    /// <summary>Sends <c>exit</c> to Roslyn and waits, briefly, for the process to go.</summary>
    private async Task ExitBackendAsync(CancellationToken cancellationToken)
    {
        if (Server is not { } server)
        {
            return;
        }

        lock (_stateLock)
        {
            _stopping = true;
        }

        server.Post(ServerEndpoint.BuildExit());

        try
        {
            await server.DrainAsync().ConfigureAwait(false);
            await server.Connection.Exited.WaitAsync(ExitWait, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            Log.BackendDidNotExit(_logger, ExitWait.TotalSeconds, exception);
        }
    }

    /// <summary>Tears everything down, idempotently.</summary>
    private async Task StopAsync()
    {
        ServerEndpoint? server;

        lock (_stateLock)
        {
            if (_stopping && _server is null)
            {
                server = null;
            }
            else
            {
                _stopping = true;
                server = _server;
                _server = null;
            }
        }

        if (server is not null)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }

        if (_serverPump is { } pump)
        {
            try
            {
                await pump.WaitAsync(ExitWait, _time, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log.PumpDidNotStop(_logger);
            }
        }

        await _client.DrainAsync().ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    /// <summary>The folders <c>workspace/workspaceFolders</c> answers with.</summary>
    private IReadOnlyList<WorkspaceFolder> ResolveWorkspaceFolders()
    {
        if (_client.WorkspaceFolders is { Count: > 0 } folders)
        {
            return folders;
        }

        return _client.RootUri is { Length: > 0 } root
            ? [new WorkspaceFolder { Uri = root, Name = "workspace" }]
            : [];
    }

    /// <summary>Builds a <c>$/cancelRequest</c> naming the id Roslyn knows the request by.</summary>
    private static byte[] BuildCancelRequest(int outboundId)
    {
        var buffer = new ArrayBufferWriter<byte>(32);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id"u8, outboundId);
            writer.WriteEndObject();
        }

        return JsonRpcErrors.Notification("$/cancelRequest", buffer.WrittenSpan);
    }

    /// <summary>Reports what the backend said it was, which is the first thing a bug report needs.</summary>
    private void LogBackendIdentity(JsonElement result)
    {
        var name = "(unnamed)";
        var version = string.Empty;

        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("serverInfo", out var serverInfo)
            && serverInfo.ValueKind == JsonValueKind.Object)
        {
            if (serverInfo.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String)
            {
                name = nameValue.GetString() ?? name;
            }

            if (serverInfo.TryGetProperty("version", out var versionValue)
                && versionValue.ValueKind == JsonValueKind.String)
            {
                version = versionValue.GetString() ?? string.Empty;
            }
        }

        // Roslyn's own document is deliberately not adopted: the adapter already answered its client
        // with an authored one, and Roslyn advertises four providers it would then be promising on
        // Roslyn's behalf (C26).
        Log.BackendReady(_logger, name, version.Length == 0 ? "(no version)" : version);
    }

    /// <summary>Pulls the code and the message out of an error response.</summary>
    private static (int Code, string Message) ErrorOf(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return (JsonRpcErrors.InternalError, "(no error object)");
            }

            var code = error.TryGetProperty("code", out var codeValue)
                       && codeValue.ValueKind == JsonValueKind.Number
                       && codeValue.TryGetInt32(out var parsed)
                ? parsed
                : JsonRpcErrors.InternalError;

            var message = error.TryGetProperty("message", out var messageValue)
                          && messageValue.ValueKind == JsonValueKind.String
                ? messageValue.GetString() ?? "(no message)"
                : "(no message)";

            return (code, message);
        }
        catch (JsonException)
        {
            return (JsonRpcErrors.InternalError, "(unreadable)");
        }
    }

    // -----------------------------------------------------------------------------------------
    // IAdapterChannel - what the bridges are allowed to do
    // -----------------------------------------------------------------------------------------

    /// <inheritdoc />
    void IAdapterChannel.NotifyServer(ReadOnlyMemory<byte> body) => Server?.Post(body);

    /// <inheritdoc />
    void IAdapterChannel.NotifyClient(ReadOnlyMemory<byte> body) => _client.Post(body);

    /// <inheritdoc />
    void IAdapterChannel.LogToClient(int type, string message) => _client.Log(type, message);

    /// <summary>Sends a <c>window/logMessage</c> from outside the session.</summary>
    /// <remarks>
    /// The one thing the <c>lsp</c> verb needs that the channel interface does not give it: the
    /// factory reports download progress, and it exists before the session does.
    /// </remarks>
    /// <param name="type">The severity: 1 error, 2 warning, 3 info, 4 log.</param>
    /// <param name="message">The text.</param>
    internal void TellClient(int type, string message) => _client.Log(type, message);

    /// <inheritdoc />
    async Task<JsonElement> IAdapterChannel.AskAsync(
        string method,
        ReadOnlyMemory<byte> rawParams,
        CancellationToken cancellationToken)
    {
        if (Server is not { } server)
        {
            throw new IOException($"there is no Roslyn backend to ask '{method}'.");
        }

        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outboundId = _serverBound.Register(method, completion);

        server.Post(JsonRpcErrors.Request(IdMap.TokenFor(outboundId), method, rawParams.Span));

        // Cancelling both forgets the pending entry and tells Roslyn to stop: a diagnostic pull the
        // bridge abandoned is a compilation Roslyn would otherwise finish for nobody.
        await using var registration = cancellationToken.Register(() =>
        {
            if (_serverBound.TryComplete(outboundId, out _))
            {
                Server?.Post(BuildCancelRequest(outboundId));
                completion.TrySetCanceled(cancellationToken);
            }
        }).ConfigureAwait(false);

        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>The <c>window/logMessage</c> severities, so the numbers are not bare.</summary>
    private static class LogMessageType
    {
        internal const int Error = 1;
        internal const int Info = 3;
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "The client closed the connection.")]
        internal static partial void ClientClosed(ILogger logger);

        [LoggerMessage(
            EventId = 101,
            Level = LogLevel.Error,
            Message = "The inbound stream is not well-framed LSP; stopping.")]
        internal static partial void ClientNotFramed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 102,
            Level = LogLevel.Warning,
            Message = "Discarding a client message that is not a JSON-RPC message.")]
        internal static partial void ClientSentGarbage(ILogger logger);

        [LoggerMessage(
            EventId = 103,
            Level = LogLevel.Information,
            Message = "Client handshake complete: root {RootUri}, applies edits: {AppliesEdits}.")]
        internal static partial void Initialized(ILogger logger, string rootUri, bool appliesEdits);

        [LoggerMessage(EventId = 104, Level = LogLevel.Information, Message = "Connecting to {Backend}.")]
        internal static partial void Connecting(ILogger logger, string backend);

        [LoggerMessage(
            EventId = 105,
            Level = LogLevel.Information,
            Message = "Roslyn backend ready: {Name} {Version}.")]
        internal static partial void BackendReady(ILogger logger, string name, string version);

        [LoggerMessage(EventId = 106, Level = LogLevel.Error, Message = "The Roslyn backend is unusable: {Reason}.")]
        internal static partial void BackendFailed(ILogger logger, string reason, Exception? exception);

        [LoggerMessage(
            EventId = 107,
            Level = LogLevel.Warning,
            Message = "The Roslyn backend went away; {Count} in-flight request(s) were answered with an error.")]
        internal static partial void BackendGone(ILogger logger, int count);

        [LoggerMessage(EventId = 108, Level = LogLevel.Debug, Message = "Reading from Roslyn stopped.")]
        internal static partial void BackendStreamFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 109,
            Level = LogLevel.Warning,
            Message = "Discarding a Roslyn message that is not a JSON-RPC message.")]
        internal static partial void BackendSentGarbage(ILogger logger);

        [LoggerMessage(
            EventId = 110,
            Level = LogLevel.Debug,
            Message = "{Peer} answered id {Id}, which nothing was waiting for.")]
        internal static partial void UnmatchedResponse(ILogger logger, string peer, JsonRpcId id);

        [LoggerMessage(
            EventId = 111,
            Level = LogLevel.Debug,
            Message = "Buffered {Method} in the document mirror; {Count} document(s) will be replayed once " +
                      "Roslyn is initialised.")]
        internal static partial void DocumentBuffered(ILogger logger, string method, int count);

        [LoggerMessage(
            EventId = 112,
            Level = LogLevel.Debug,
            Message = "Dropping the {Method} notification: Roslyn is not initialised yet and would refuse it.")]
        internal static partial void NotificationTooEarly(ILogger logger, string method);

        [LoggerMessage(EventId = 113, Level = LogLevel.Debug, Message = "Consumed the {Method} message here.")]
        internal static partial void DroppedClientMessage(ILogger logger, string method);

        [LoggerMessage(
            EventId = 114,
            Level = LogLevel.Warning,
            Message = "A workspace/didChangeConfiguration could not be read; Roslyn keeps the settings it has.")]
        internal static partial void ConfigurationUnreadable(ILogger logger);

        [LoggerMessage(
            EventId = 115,
            Level = LogLevel.Warning,
            Message = "Roslyn did not acknowledge shutdown within {Seconds:0} s; answering the client anyway.")]
        internal static partial void ShutdownNotAcknowledged(ILogger logger, double seconds, Exception exception);

        [LoggerMessage(
            EventId = 116,
            Level = LogLevel.Warning,
            Message = "Roslyn had not exited {Seconds:0} s after being told to; the transport is being " +
                      "closed under it.")]
        internal static partial void BackendDidNotExit(ILogger logger, double seconds, Exception exception);

        [LoggerMessage(EventId = 117, Level = LogLevel.Debug, Message = "The Roslyn read loop did not stop in time.")]
        internal static partial void PumpDidNotStop(ILogger logger);

        [LoggerMessage(EventId = 118, Level = LogLevel.Information, Message = "Exiting with code {ExitCode} ({Reason}).")]
        internal static partial void Exiting(ILogger logger, int exitCode, string reason);

        [LoggerMessage(
            EventId = 119,
            Level = LogLevel.Warning,
            Message = "CLAUDE_ROSLYN_LSP_DIAGNOSTICS is off; this session is navigation-only and will " +
                      "never publish a diagnostic.")]
        internal static partial void DiagnosticsOff(ILogger logger);

        [LoggerMessage(
            EventId = 120,
            Level = LogLevel.Warning,
            Message = "CLAUDE_ROSLYN_LSP_FILE_WATCHER is off, so the didChangeWatchedFiles capability is " +
                      "not declared and Roslyn will register no watchers at all (C34). A file created " +
                      "outside the editor will not join its project until the session restarts.")]
        internal static partial void WatcherOff(ILogger logger);

        [LoggerMessage(
            EventId = 121,
            Level = LogLevel.Debug,
            Message = "De-duplicated the {Method} answer, which carried {Count} entries before the " +
                      "per-target-framework copies were removed (C24).")]
        internal static partial void Deduplicated(ILogger logger, string method, int count);

        [LoggerMessage(
            EventId = 122,
            Level = LogLevel.Debug,
            Message = "The {Method} answer could not be read for de-duplication; it crosses unchanged.")]
        internal static partial void DeduplicationFailed(ILogger logger, string method, Exception exception);

        [LoggerMessage(
            EventId = 123,
            Level = LogLevel.Debug,
            Message = "A backend attempt that has already been replaced reported '{Reason}'; the " +
                      "relaunch under way owns the session now.")]
        internal static partial void AttemptSuperseded(ILogger logger, string reason);
    }
}
