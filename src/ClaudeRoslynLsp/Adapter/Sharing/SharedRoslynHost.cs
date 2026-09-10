using System.Buffers;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter.Sharing;

/// <summary>
/// The pipe server in whichever process launched Roslyn: it accepts other <c>claude-roslyn-lsp</c>
/// processes and multiplexes them onto the one backend.
/// </summary>
/// <remarks>
/// <para>
/// <b>D88 — the multiplexing rules, in one list, because every one of them is a decision.</b>
/// </para>
/// <list type="number">
/// <item><description>
/// An attached client's <c>initialize</c> is answered <em>here</em>, out of the result Roslyn gave
/// the host, with <c>serverInfo</c> replaced by one naming this process. It is never forwarded:
/// Roslyn has exactly one client and a second handshake on the same connection is a protocol error.
/// Everything else in the document — including <c>_roslyn_processId</c> (C45) — crosses verbatim, so
/// an attached engine reports the same backend process the host does.
/// </description></item>
/// <item><description>
/// Requests are forwarded under the host's own <see cref="IdMap"/> and answered under the id the
/// attached client used. That is D47 applied a second time and for the same reason: two peers that
/// have never met both count from one.
/// </description></item>
/// <item><description>
/// Every forwarded request goes through the host's <see cref="ReadinessGate"/> first (D46). A
/// request that arrives over a pipe before the workspace has loaded would be answered empty and
/// successfully by Roslyn (C27), which is no less wrong for having come from another process.
/// </description></item>
/// <item><description>
/// Server-to-client <em>requests</em> — <c>client/registerCapability</c>,
/// <c>workspace/configuration</c>, <c>window/workDoneProgress/create</c>,
/// <c>workspace/applyEdit</c> — stay host-owned and are never sent to an attached client. There is
/// one correct answer to each and the host already gives it; a second answerer would be a second
/// opinion about the same question.
/// </description></item>
/// <item><description>
/// Four notifications <em>are</em> fanned out: <c>window/logMessage</c>, <c>window/showMessage</c>,
/// <c>workspace/projectInitializationComplete</c> and <c>workspace/diagnostic/refresh</c> — the last
/// converted from a request into a notification, so the attached client consumes it without owing
/// anybody an answer. A client that attaches after the load gets the readiness notification replayed,
/// because otherwise it would hold every request until its own budget ran out on a workspace that is
/// already loaded.
/// </description></item>
/// <item><description>
/// Documents are ref-counted per URI: <c>didOpen</c> reaches Roslyn on the first open from anyone,
/// <c>didClose</c> on the last, and <c>didChange</c> always. Roslyn keeps one buffer per document, so
/// a second <c>didOpen</c> is redundant and — much worse — a <c>didClose</c> from one client would
/// silently revert the other client's live buffer to what is on disk.
/// </description></item>
/// <item><description>
/// <c>solution/open</c> and <c>project/open</c> are swallowed. The workspace is already open, and
/// opening it again is how a second load gets started underneath the first.
/// </description></item>
/// <item><description>
/// <c>shutdown</c> and <c>exit</c> from an attached client detach that client and nothing else. The
/// host's own shutdown closes every pipe, which the attached clients see as a backend that went away
/// — the state their supervisors already know what to do with (D57).
/// </description></item>
/// </list>
/// <para>
/// The class is a component of its owner rather than a session of its own: everything that carries a
/// decision — the gate, the id map, the configuration responder, the document mirror — belongs to
/// <see cref="ISharedEngineHost"/>, and what is written here is the peer-serving glue D75 declined to
/// share between the two owners.
/// </para>
/// </remarks>
internal sealed partial class SharedRoslynHost : IAsyncDisposable
{
    /// <summary>
    /// The custom method an attached client changes a Roslyn setting with (D90).
    /// </summary>
    /// <remarks>
    /// A <em>request</em>, not a notification, because the caller has to know when the change is in
    /// effect: Roslyn only applies a setting once it has re-pulled it (D77), and an attached engine
    /// that fired and forgot would race its own <c>getDiagnostics scope: "solution"</c> against the
    /// scope change that call depends on. The host answers when the pull has come back.
    /// </remarks>
    internal const string SetOptionMethod = "claude-roslyn-lsp/setOption";

    /// <summary>Notifications from Roslyn that every attached client is given verbatim.</summary>
    private static readonly HashSet<string> FannedOut = new(StringComparer.Ordinal)
    {
        "window/logMessage",
        "window/showMessage",
        "workspace/projectInitializationComplete",
        "workspace/diagnostic/refresh",
    };

    /// <summary>The readiness notification, replayed to a client that attached after the load.</summary>
    private static readonly byte[] ProjectsLoadedNotification =
        JsonRpcErrors.Notification("workspace/projectInitializationComplete", default);

    private readonly Lock _lock = new();
    private readonly List<SharedClient> _clients = [];
    private readonly Dictionary<string, DocumentOwners> _documents = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ISharedEngineHost _owner;
    private readonly ILogger _logger;

    private Task? _accepting;
    private int _nextClientId;
    private bool _projectsLoaded;
    private int _disposed;

    /// <summary>Creates a host over an owner's backend. Nothing is accepted until <see cref="Listen"/>.</summary>
    /// <param name="owner">Whichever half of this product launched the Roslyn being shared.</param>
    /// <param name="pipeName">The pipe to accept on, which is what the session file publishes.</param>
    /// <param name="logger">The stderr log. stdout is the protocol channel in both verbs.</param>
    internal SharedRoslynHost(ISharedEngineHost owner, string pipeName, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(pipeName);
        ArgumentNullException.ThrowIfNull(logger);

        _owner = owner;
        _logger = logger;
        PipeName = pipeName;

        _owner.BackendMessage += OnBackendMessage;
    }

    /// <summary>The pipe this host accepts on.</summary>
    internal string PipeName { get; }

    /// <summary>How many clients are attached right now. Used by the tests and by <c>doctor</c>.</summary>
    internal int ClientCount
    {
        get
        {
            lock (_lock)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>
    /// A pipe name unique to this process and this host.
    /// </summary>
    /// <remarks>
    /// The random half is not decoration: a host that is torn down and started again inside one
    /// process — which is what a relaunch after a crash does — would otherwise re-use a name a client
    /// may still be holding, and the new host would inherit the old one's half-closed connections.
    /// </remarks>
    internal static string CreatePipeName() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{ServerVersion.Name}-share-{Environment.ProcessId}-{Random.Shared.Next():x8}");

    /// <summary>
    /// Starts accepting on the named pipe, with the first instance created before this returns.
    /// </summary>
    /// <remarks>
    /// Synchronous creation of the first instance is what makes the publish order safe: the session
    /// file is written only after this has returned, so a process that reads the file and connects
    /// cannot arrive before there is something listening.
    /// </remarks>
    internal void Listen()
    {
        var first = CreateServerStream();

        lock (_lock)
        {
            _accepting = Task.Run(() => AcceptLoopAsync(first), CancellationToken.None);
        }

        Log.Listening(_logger, PipeName);
    }

    /// <summary>
    /// Adds one already-connected client.
    /// </summary>
    /// <remarks>
    /// Separate from the accept loop so the tests can attach a client over an in-memory pipe pair,
    /// which is the only way the multiplexing rules can be exercised without two processes and a real
    /// Roslyn between them.
    /// </remarks>
    /// <param name="input">What the host reads from this client.</param>
    /// <param name="output">What the host writes to this client.</param>
    /// <param name="dispose">Releases the transport when the client goes.</param>
    internal void Accept(Stream input, Stream output, Func<ValueTask> dispose)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(dispose);

        SharedClient client;

        lock (_lock)
        {
            client = new SharedClient(++_nextClientId, input, output, dispose, _logger);
            _clients.Add(client);
        }

        Log.Attached(_logger, client.Id, _clients.Count);
        client.Pump = Task.Run(() => ServeAsync(client), CancellationToken.None);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _owner.BackendMessage -= OnBackendMessage;
        await _stopping.CancelAsync().ConfigureAwait(false);

        SharedClient[] clients;

        lock (_lock)
        {
            clients = [.. _clients];
            _clients.Clear();
        }

        foreach (var client in clients)
        {
            // Every attached client sees this as a backend that went away, which is a state its own
            // supervisor already handles: it relaunches, finds no session file or a stale one, and
            // becomes the host itself (D91).
            await client.DisposeAsync().ConfigureAwait(false);
        }

        if (_accepting is { } accepting)
        {
            try
            {
                await accepting.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException
                                                  or ObjectDisposedException)
            {
            }
        }

        _stopping.Dispose();
        Log.Stopped(_logger, PipeName);
    }

    // ---------------------------------------------------------------------------------------------
    // Accepting
    // ---------------------------------------------------------------------------------------------

    /// <summary>Creates one pipe instance with the same options the Roslyn launcher uses (C8).</summary>
    private NamedPipeServerStream CreateServerStream() =>
        new(
            PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    /// <summary>Accepts clients until the host is disposed.</summary>
    private async Task AcceptLoopAsync(NamedPipeServerStream first)
    {
        var server = first;

        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException
                                                  or ObjectDisposedException)
            {
                await server.DisposeAsync().ConfigureAwait(false);

                if (_stopping.IsCancellationRequested)
                {
                    return;
                }

                Log.AcceptFailed(_logger, exception);

                try
                {
                    server = CreateServerStream();
                    continue;
                }
                catch (IOException create)
                {
                    Log.ListenLost(_logger, PipeName, create.Message);
                    return;
                }
            }

            var accepted = server;
            Accept(accepted, accepted, () => accepted.DisposeAsync());

            try
            {
                server = CreateServerStream();
            }
            catch (IOException exception)
            {
                Log.ListenLost(_logger, PipeName, exception.Message);
                return;
            }
        }

        await server.DisposeAsync().ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------------
    // One attached client
    // ---------------------------------------------------------------------------------------------

    /// <summary>Reads one client's messages until it goes away.</summary>
    private async Task ServeAsync(SharedClient client)
    {
        try
        {
            while (await client.ReadAsync(_stopping.Token).ConfigureAwait(false) is { } body)
            {
                Dispatch(client, body);
            }
        }
        catch (Exception exception) when (exception is LspProtocolException or IOException
                                              or ObjectDisposedException or OperationCanceledException)
        {
            Log.ClientStreamEnded(_logger, client.Id, exception.Message);
        }
        finally
        {
            await DetachAsync(client).ConfigureAwait(false);
        }
    }

    /// <summary>Applies the multiplexing rules to one message from an attached client.</summary>
    private void Dispatch(SharedClient client, byte[] body)
    {
        var info = LspMessageScanner.Scan(body);

        if (info.Kind == LspMessageKind.Invalid)
        {
            Log.ClientSentGarbage(_logger, client.Id);
            return;
        }

        if (info.Kind is LspMessageKind.Response or LspMessageKind.ErrorResponse)
        {
            // The host never asks an attached client anything (rule 4), so nothing can be waiting
            // for this. Dropped rather than forwarded: forwarding it would deliver an answer to
            // Roslyn under an id Roslyn never issued.
            Log.UnexpectedResponse(_logger, client.Id);
            return;
        }

        var method = info.Method!;
        var isRequest = info.Kind == LspMessageKind.Request;
        var idToken = info.IdToken(body).ToArray();

        switch (method)
        {
            case "initialize":
                _ = AnswerInitializeAsync(client, idToken);
                return;

            case "initialized":
                return;

            case "shutdown":
                if (isRequest)
                {
                    client.Post(JsonRpcErrors.NullResult(idToken));
                }

                Log.ClientShuttingDown(_logger, client.Id);
                return;

            case "exit":
                _ = DetachAsync(client).AsTask();
                return;

            case "$/cancelRequest":
                if (LspMessageScanner.TryReadCancelRequestId(body, out var cancelled))
                {
                    client.Cancel(cancelled);
                }

                return;

            case "solution/open":
            case "project/open":
                // Rule 7: the workspace is already open. Answering with the readiness notification is
                // what stops a client that attached after the load from holding every request until
                // its own budget runs out.
                Log.OpenSwallowed(_logger, client.Id, method);
                ReplayReadiness(client);
                return;

            case "workspace/didChangeConfiguration":
                // Rule 4's other half: Roslyn asks the host for configuration, so an attached client's
                // idea of the settings can only be applied through setOption, which routes it into
                // the host's own responder.
                Log.ConfigurationDropped(_logger, client.Id);
                return;

            case SetOptionMethod:
                _ = SetOptionAsync(client, body, idToken, isRequest);
                return;

            case "textDocument/didOpen":
            case "textDocument/didChange":
            case "textDocument/didClose":
            case "textDocument/didSave":
                HandleDocument(client, body, method);
                return;

            default:
                if (isRequest)
                {
                    ForwardGated(client, body, info, idToken);
                }
                else
                {
                    _owner.NotifyBackend(body);
                }

                return;
        }
    }

    /// <summary>Answers an attached client's handshake out of the host's own (rule 1).</summary>
    private async Task AnswerInitializeAsync(SharedClient client, byte[] idToken)
    {
        try
        {
            var result = await _owner.BackendInitializeResultAsync(_stopping.Token).ConfigureAwait(false);

            client.Post(JsonRpcErrors.RawResult(idToken, RenameServer(result)));
            Log.Handshake(_logger, client.Id);

            ReplayReadiness(client);
        }
        catch (OperationCanceledException)
        {
            // The host is going away; the client's transport is about to close under it, which is the
            // signal its supervisor acts on.
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            client.Post(JsonRpcErrors.Error(
                idToken,
                JsonRpcErrors.InternalError,
                $"The shared {ServerVersion.Name} host could not report its Roslyn handshake "
                + $"({exception.Message})."));
        }
    }

    /// <summary>
    /// Copies Roslyn's <c>initialize</c> result with <c>serverInfo</c> replaced by this host's.
    /// </summary>
    /// <remarks>
    /// Everything else is written back with <see cref="JsonElement.WriteTo"/>, which reproduces the
    /// sub-tree it was parsed from — so the capability document an attached client sees is the one
    /// Roslyn actually sent, and <c>_roslyn_processId</c> still names the process that holds the
    /// workspace (C45). Only the name changes, and it changes because "which server am I talking to"
    /// is the first line of every log an attached session writes.
    /// </remarks>
    private static byte[] RenameServer(byte[] result)
    {
        var buffer = new ArrayBufferWriter<byte>(result.Length + 128);

        using (var document = JsonDocument.Parse(result))
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, "serverInfo", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    property.WriteTo(writer);
                }
            }

            writer.WriteStartObject("serverInfo"u8);
            writer.WriteString(
                "name"u8,
                $"{ServerVersion.Name} shared engine (host process {Environment.ProcessId})");
            writer.WriteString("version"u8, ServerVersion.Value);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Tells one client that the workspace is loaded, when it already is.</summary>
    private void ReplayReadiness(SharedClient client)
    {
        bool loaded;

        lock (_lock)
        {
            loaded = _projectsLoaded;
        }

        if (loaded || _owner.Gate.IsOpen)
        {
            client.Post(ProjectsLoadedNotification);
            Log.ReadinessReplayed(_logger, client.Id);
        }
    }

    /// <summary>Holds a request behind the host's gate, then forwards it (rules 2 and 3).</summary>
    private void ForwardGated(SharedClient client, byte[] body, LspMessageInfo info, byte[] idToken)
    {
        var method = info.Method!;
        var cancellation = client.Register(info.Id, _stopping.Token);

        _owner.Gate.TryHold(info.Id, method, outcome =>
        {
            switch (outcome)
            {
                case GateOutcome.Pass:
                    _ = ForwardAsync(client, body, idToken, info.Id, method, cancellation);
                    break;

                case GateOutcome.Cancelled:
                    client.Complete(info.Id);
                    client.Post(JsonRpcErrors.Error(
                        idToken,
                        JsonRpcErrors.RequestCancelled,
                        "The request was cancelled while it was waiting for the shared workspace to load."));
                    break;

                case GateOutcome.Failed:
                default:
                    client.Complete(info.Id);
                    client.Post(JsonRpcErrors.Error(idToken, JsonRpcErrors.InternalError, _owner.Gate.FailureMessage));
                    break;
            }
        });
    }

    /// <summary>
    /// Forwards one request, and asks again once when the backend died holding it (D74, shared).
    /// </summary>
    /// <remarks>
    /// The same argument as D74 makes over a direct connection: everything an attached client
    /// forwards is a read, the host is about to have a working backend again, and answering
    /// <c>-32603</c> in the gap would put a crash in front of the model as a failed tool call seconds
    /// before the relaunched server could have answered correctly. The retry waits on the gate, which
    /// the owner closes for the relaunch, so nothing is replayed into a corpse or into a server that
    /// has not loaded the solution (C27).
    /// </remarks>
    private async Task ForwardAsync(
        SharedClient client,
        byte[] body,
        byte[] idToken,
        JsonRpcId id,
        string method,
        CancellationTokenSource cancellation)
    {
        try
        {
            byte[] reply;

            try
            {
                reply = await _owner.ForwardAsync(body, idToken, cancellation.Token).ConfigureAwait(false);
            }
            catch (IOException) when (!cancellation.IsCancellationRequested)
            {
                Log.Replaying(_logger, client.Id, method);

                reply = await WaitForGateAsync(method, cancellation.Token).ConfigureAwait(false)
                    ? await _owner.ForwardAsync(body, idToken, cancellation.Token).ConfigureAwait(false)
                    : JsonRpcErrors.Error(idToken, JsonRpcErrors.InternalError, _owner.Gate.FailureMessage);
            }

            client.Post(reply);
        }
        catch (OperationCanceledException)
        {
            client.Post(JsonRpcErrors.Error(
                idToken,
                JsonRpcErrors.RequestCancelled,
                $"'{method}' was cancelled."));
        }
        catch (Exception exception)
        {
            client.Post(JsonRpcErrors.Error(
                idToken,
                exception is RoslynRequestException failure ? failure.Code : JsonRpcErrors.InternalError,
                exception.Message));
        }
        finally
        {
            client.Complete(id);
            cancellation.Dispose();
        }
    }

    /// <summary>Waits for the host's gate to reopen after a relaunch.</summary>
    private Task<bool> WaitForGateAsync(string method, CancellationToken cancellationToken)
    {
        var reopened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _owner.Gate.TryHold(JsonRpcId.Absent, method, outcome => reopened.TrySetResult(outcome == GateOutcome.Pass));

        return reopened.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Applies one attached client's configuration override in the host (rule 4, D90).</summary>
    private async Task SetOptionAsync(SharedClient client, byte[] body, byte[] idToken, bool isRequest)
    {
        string? section = null;
        byte[]? value = null;

        try
        {
            var envelope = JsonSerializer.Deserialize(body, LspJsonContext.Default.RawParamsRequest);

            if (envelope?.Params is { ValueKind: JsonValueKind.Object } parameters
                && parameters.TryGetProperty("section", out var name)
                && name.ValueKind == JsonValueKind.String
                && parameters.TryGetProperty("value", out var raw))
            {
                section = name.GetString();
                value = Encoding.UTF8.GetBytes(raw.GetRawText());
            }
        }
        catch (JsonException)
        {
            // Falls through to the invalid-params answer below.
        }

        if (section is not { Length: > 0 } || value is null)
        {
            Log.BadSetOption(_logger, client.Id);

            if (isRequest)
            {
                client.Post(JsonRpcErrors.Error(
                    idToken,
                    JsonRpcErrors.InvalidParams,
                    $"{SetOptionMethod} needs a 'section' string and a 'value'."));
            }

            return;
        }

        try
        {
            await _owner.SetOptionAsync(section, value, _stopping.Token).ConfigureAwait(false);
            Log.OptionSet(_logger, client.Id, section);

            if (isRequest)
            {
                client.Post(JsonRpcErrors.NullResult(idToken));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (isRequest)
            {
                client.Post(JsonRpcErrors.Error(idToken, JsonRpcErrors.InternalError, exception.Message));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Document ownership (rule 6)
    // ---------------------------------------------------------------------------------------------

    /// <summary>Applies the ref-counting rules to one document notification.</summary>
    private void HandleDocument(SharedClient client, byte[] body, string method)
    {
        var uri = UriOf(body);

        if (uri is not { Length: > 0 })
        {
            Log.DocumentUnreadable(_logger, client.Id, method);
            return;
        }

        switch (method)
        {
            case "textDocument/didOpen":
                OpenDocument(client, body, uri);
                return;

            case "textDocument/didChange":
                // Always forwarded, and always mirrored. The text on disk is shared between the two
                // processes and the last writer wins; mirroring is what makes the other client's next
                // pull see it, and what makes a Roslyn relaunch replay it (D57).
                _owner.Documents.Change(body);
                _owner.NotifyBackend(body);
                return;

            case "textDocument/didClose":
                CloseDocument(client, body, uri);
                return;

            case "textDocument/didSave":
            default:
                _owner.NotifyBackend(body);
                return;
        }
    }

    /// <summary>Forwards <c>didOpen</c> only when nobody had the document open already.</summary>
    private void OpenDocument(SharedClient client, byte[] body, string uri)
    {
        bool forward;

        lock (_lock)
        {
            if (!_documents.TryGetValue(uri, out var entry))
            {
                entry = new DocumentOwners();
                _documents[uri] = entry;
            }

            // "The host itself has it open" is asked before this open is mirrored, and it excludes
            // documents this ledger opened on an attached client's behalf — otherwise the mirror
            // entry we are about to write would make every later close look like the host's.
            var hostOwns = !entry.Shared && _owner.Documents.Find(uri) is not null;

            forward = entry.Clients.Count == 0 && !hostOwns;
            entry.Shared |= forward;
            entry.Clients.Add(client.Id);
            client.Documents.Add(uri);
        }

        if (forward)
        {
            _owner.Documents.Open(body);
            _owner.NotifyBackend(body);
        }

        Log.DocumentOpened(_logger, client.Id, uri, forward);
    }

    /// <summary>Forwards <c>didClose</c> only when the last owner let go.</summary>
    private void CloseDocument(SharedClient client, byte[] body, string uri)
    {
        bool forward;

        lock (_lock)
        {
            client.Documents.Remove(uri);

            if (!_documents.TryGetValue(uri, out var entry) || !entry.Clients.Remove(client.Id))
            {
                return;
            }

            forward = entry.Clients.Count == 0 && entry.Shared;

            if (entry.Clients.Count == 0)
            {
                _documents.Remove(uri);
            }
        }

        if (forward)
        {
            _owner.Documents.Close(body);
            _owner.NotifyBackend(body);
        }

        Log.DocumentClosed(_logger, client.Id, uri, forward);
    }

    /// <summary>Closes everything one client still had open, as if it had said so itself.</summary>
    private void ReleaseDocuments(SharedClient client)
    {
        string[] open;

        lock (_lock)
        {
            open = [.. client.Documents];
        }

        foreach (var uri in open)
        {
            CloseDocument(client, BuildDidClose(uri), uri);
        }
    }

    /// <summary>Renders a <c>didClose</c> for a client that went away without sending one.</summary>
    private static byte[] BuildDidClose(string uri)
    {
        var buffer = new ArrayBufferWriter<byte>(uri.Length + 48);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("textDocument"u8);
            writer.WriteString("uri"u8, uri);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return JsonRpcErrors.Notification("textDocument/didClose", buffer.WrittenSpan);
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

    // ---------------------------------------------------------------------------------------------
    // Fan-out (rule 5)
    // ---------------------------------------------------------------------------------------------

    /// <summary>Decides whether one Roslyn message reaches the attached clients, and in what form.</summary>
    private void OnBackendMessage(byte[] body)
    {
        var info = LspMessageScanner.Scan(body);

        if (info.Method is not { } method || !FannedOut.Contains(method))
        {
            return;
        }

        if (string.Equals(method, "workspace/projectInitializationComplete", StringComparison.Ordinal))
        {
            lock (_lock)
            {
                _projectsLoaded = true;
            }
        }

        // A request becomes a notification: the host owes Roslyn the answer and has already given it,
        // and an attached client that received the request form would answer a second time under an
        // id its own connection never issued.
        Broadcast(info.Kind == LspMessageKind.Request ? JsonRpcErrors.Notification(method, default) : body);
    }

    /// <summary>Sends one message to every attached client.</summary>
    private void Broadcast(byte[] body)
    {
        SharedClient[] clients;

        lock (_lock)
        {
            if (_clients.Count == 0)
            {
                return;
            }

            clients = [.. _clients];
        }

        foreach (var client in clients)
        {
            client.Post(body);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Detaching
    // ---------------------------------------------------------------------------------------------

    /// <summary>Removes one client and gives Roslyn back whatever only that client was holding.</summary>
    private async ValueTask DetachAsync(SharedClient client)
    {
        bool removed;

        lock (_lock)
        {
            removed = _clients.Remove(client);
        }

        if (!removed)
        {
            return;
        }

        ReleaseDocuments(client);
        client.CancelAll();

        await client.DisposeAsync().ConfigureAwait(false);

        Log.Detached(_logger, client.Id, ClientCount);
    }

    /// <summary>Who has one document open, and whether Roslyn has it because of them.</summary>
    private sealed class DocumentOwners
    {
        /// <summary>The attached clients holding it.</summary>
        internal HashSet<int> Clients { get; } = [];

        /// <summary>
        /// Whether the <c>didOpen</c> Roslyn is holding came from this ledger rather than from the
        /// host's own client — which is what decides whether a <c>didClose</c> may be forwarded.
        /// </summary>
        internal bool Shared { get; set; }
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 5400,
            Level = LogLevel.Information,
            Message = "Sharing this Roslyn: listening for attached clients on pipe {PipeName}.")]
        internal static partial void Listening(ILogger logger, string pipeName);

        [LoggerMessage(
            EventId = 5401,
            Level = LogLevel.Information,
            Message = "Client {ClientId} attached to the shared Roslyn; {Count} attached now.")]
        internal static partial void Attached(ILogger logger, int clientId, int count);

        [LoggerMessage(
            EventId = 5402,
            Level = LogLevel.Information,
            Message = "Client {ClientId} detached; {Count} attached now.")]
        internal static partial void Detached(ILogger logger, int clientId, int count);

        [LoggerMessage(
            EventId = 5403,
            Level = LogLevel.Debug,
            Message = "Answered client {ClientId}'s initialize from this host's own handshake.")]
        internal static partial void Handshake(ILogger logger, int clientId);

        [LoggerMessage(
            EventId = 5404,
            Level = LogLevel.Debug,
            Message = "Client {ClientId} attached after the workspace had loaded; replaying " +
                      "workspace/projectInitializationComplete so its readiness gate opens.")]
        internal static partial void ReadinessReplayed(ILogger logger, int clientId);

        [LoggerMessage(
            EventId = 5405,
            Level = LogLevel.Debug,
            Message = "Client {ClientId} sent {Method}; the shared workspace is already open, so it was " +
                      "answered rather than forwarded.")]
        internal static partial void OpenSwallowed(ILogger logger, int clientId, string method);

        [LoggerMessage(
            EventId = 5406,
            Level = LogLevel.Debug,
            Message = "Client {ClientId} opened {Uri} (forwarded to Roslyn: {Forwarded}).")]
        internal static partial void DocumentOpened(ILogger logger, int clientId, string uri, bool forwarded);

        [LoggerMessage(
            EventId = 5407,
            Level = LogLevel.Debug,
            Message = "Client {ClientId} closed {Uri} (forwarded to Roslyn: {Forwarded}).")]
        internal static partial void DocumentClosed(ILogger logger, int clientId, string uri, bool forwarded);

        [LoggerMessage(
            EventId = 5408,
            Level = LogLevel.Debug,
            Message = "Client {ClientId} set the Roslyn option {Section} through the shared host.")]
        internal static partial void OptionSet(ILogger logger, int clientId, string section);

        [LoggerMessage(
            EventId = 5409,
            Level = LogLevel.Warning,
            Message = "Client {ClientId} sent a claude-roslyn-lsp/setOption without a section and a value.")]
        internal static partial void BadSetOption(ILogger logger, int clientId);

        [LoggerMessage(
            EventId = 5410,
            Level = LogLevel.Debug,
            Message = "Client {ClientId} sent workspace/didChangeConfiguration; the host owns the " +
                      "configuration Roslyn asks for, so it was dropped (use claude-roslyn-lsp/setOption).")]
        internal static partial void ConfigurationDropped(ILogger logger, int clientId);

        [LoggerMessage(
            EventId = 5411,
            Level = LogLevel.Debug,
            Message = "Client {ClientId} is shutting down; it is detaching, and the shared Roslyn stays up.")]
        internal static partial void ClientShuttingDown(ILogger logger, int clientId);

        [LoggerMessage(
            EventId = 5412,
            Level = LogLevel.Debug,
            Message = "The connection to client {ClientId} ended ({Reason}).")]
        internal static partial void ClientStreamEnded(ILogger logger, int clientId, string reason);

        [LoggerMessage(
            EventId = 5413,
            Level = LogLevel.Warning,
            Message = "Discarding a message from client {ClientId} that is not a JSON-RPC message.")]
        internal static partial void ClientSentGarbage(ILogger logger, int clientId);

        [LoggerMessage(
            EventId = 5414,
            Level = LogLevel.Debug,
            Message = "Client {ClientId} answered a request the host never asked it; dropped.")]
        internal static partial void UnexpectedResponse(ILogger logger, int clientId);

        [LoggerMessage(
            EventId = 5415,
            Level = LogLevel.Warning,
            Message = "A document notification from client {ClientId} ({Method}) had no readable URI.")]
        internal static partial void DocumentUnreadable(ILogger logger, int clientId, string method);

        [LoggerMessage(
            EventId = 5416,
            Level = LogLevel.Information,
            Message = "The Roslyn backend went away while client {ClientId}'s '{Method}' was in flight; " +
                      "it will be asked again once the relaunched workspace has loaded (D74).")]
        internal static partial void Replaying(ILogger logger, int clientId, string method);

        [LoggerMessage(EventId = 5417, Level = LogLevel.Debug, Message = "Accepting a shared client failed.")]
        internal static partial void AcceptFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 5418,
            Level = LogLevel.Warning,
            Message = "The shared host can no longer accept on {PipeName} ({Reason}); processes that " +
                      "attach from now on will launch their own Roslyn instead.")]
        internal static partial void ListenLost(ILogger logger, string pipeName, string reason);

        [LoggerMessage(EventId = 5419, Level = LogLevel.Information, Message = "Stopped sharing on {PipeName}.")]
        internal static partial void Stopped(ILogger logger, string pipeName);
    }
}
