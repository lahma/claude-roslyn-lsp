using System.Buffers;
using System.Text.Json;

using ClaudeRoslynLsp.Configuration;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// Pulls diagnostics from Roslyn and pushes them at the client — the single feature without which
/// the adapter would be navigation-only.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two halves speak different protocols and neither will change.</b> Roslyn implements
/// diagnostics as a <em>pull</em>: the client asks <c>textDocument/diagnostic</c> and gets a report,
/// and the server volunteers nothing. Claude Code consumes only <em>push</em>: it renders
/// <c>textDocument/publishDiagnostics</c> as a text attachment on the next turn and has no code path
/// that issues a pull. Point them at each other unmediated and the result is a language server that
/// never reports a single error — which is exactly what raw Roslyn under Claude Code does today, and
/// it is silent about it. So this class is the translation, and it is the reason the product exists
/// as much as the readiness gate is.
/// </para>
/// <para>
/// <b>One pull, no identifier.</b> C9: <c>textDocument/diagnostic</c> with no <c>identifier</c>
/// returns the union of every registered source, which is what a client wants and is one round trip
/// instead of ten. The ten identifiers are recorded anyway (C11, <see cref="RegistrationTracker"/>)
/// because the MCP half asks the compiler and the analysers separately, but the bridge never uses
/// them: a per-source pull would multiply the traffic by ten to produce the same union.
/// <c>previousResultId</c> is kept per URI (C12) and an <c>unchanged</c> report publishes nothing at
/// all, which is what makes an idle session quiet.
/// </para>
/// <para>
/// <b>One pull in flight per URI, with a dirty flag.</b> Roslyn takes 1.3-1.7 s for the first pull
/// of a session and about 90 ms afterwards (C17), and a fast typist outruns that. Queueing pulls
/// would spend the whole budget computing answers about text that no longer exists; a dirty flag
/// coalesces them into exactly one more pull at the end, which is the only one whose answer is true.
/// </para>
/// <para>
/// <b>Every published set carries the mirror version the pull started at.</b> That is the client's
/// own mechanism for discarding a set an edit has already overtaken, and without it a slow pull
/// racing a fast edit paints diagnostics at positions that have moved.
/// </para>
/// <para>
/// <b>Gated on readiness (C28).</b> A file that is open before its project has loaded is served in
/// misc-files mode, where Roslyn compiles it alone and reports IDE0005 on the very usings the real
/// project needs. Publishing that would be worse than publishing nothing, so a <c>didOpen</c> before
/// the workspace is loaded is remembered and pulled the moment it is.
/// </para>
/// </remarks>
internal sealed partial class DiagnosticsBridge : IDisposable
{
    /// <summary>
    /// How long after the last <c>didChange</c> a pull is issued.
    /// </summary>
    /// <remarks>
    /// 400 ms, trailing. Roslyn answers a clean pull about 90 ms after a full-text change (C17), so
    /// this is not about Roslyn's speed — it is about not asking a question whose answer is stale
    /// before it arrives. Short enough that a pause in typing produces diagnostics that feel
    /// immediate; long enough that a burst of keystrokes is one pull rather than twenty.
    /// </remarks>
    internal static readonly TimeSpan ChangeDebounce = TimeSpan.FromMilliseconds(400);

    /// <summary>How long after <c>workspace/diagnostic/refresh</c> every open document is re-pulled.</summary>
    /// <remarks>
    /// Roslyn sends a refresh when the world changed underneath it — a build finished, a project
    /// reloaded — and it usually sends several in a row. Half a second collapses the burst.
    /// </remarks>
    internal static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(500);

    /// <summary>How long after a project file changed on disk every open document is re-pulled.</summary>
    /// <remarks>
    /// A second, because the event is only the <em>start</em> of Roslyn's work: a <c>.csproj</c>
    /// change makes it re-evaluate, and the symbol appears 1.6-2.1 s later (C33). Pulling sooner
    /// would report the world as it was.
    /// </remarks>
    internal static readonly TimeSpan ProjectFileDebounce = TimeSpan.FromSeconds(1);

    /// <summary>How long after a save the opt-in workspace pull runs.</summary>
    internal static readonly TimeSpan WorkspaceSaveDebounce = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long a retryable refusal waits before the one retry.</summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>How long one pull may take before it is abandoned and cancelled at Roslyn.</summary>
    internal static readonly TimeSpan PullTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long the whole-solution pull may take.</summary>
    internal static readonly TimeSpan WorkspacePullTimeout = TimeSpan.FromMinutes(3);

    /// <summary>The most closed files the opt-in workspace mode reports on in one round.</summary>
    internal const int MaxWorkspaceFiles = 10;

    /// <summary>The most diagnostics per closed file the opt-in workspace mode publishes.</summary>
    internal const int MaxWorkspaceDiagnosticsPerFile = 5;

    /// <summary>The value of <c>CLAUDE_ROSLYN_LSP_WORKSPACE_DIAGNOSTICS</c> that turns the mode on.</summary>
    internal const string WorkspaceModeErrors = "errors";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, DocumentDiagnosticState> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _workspaceResultIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _publishedClosedUris = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deferred = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();

    private readonly DocumentMirror _mirror;
    private readonly IAdapterChannel _channel;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly int _severityFloor;

    private ITimer? _refreshTimer;
    private ITimer? _projectFileTimer;
    private ITimer? _workspaceTimer;
    private string? _lastSavedUri;
    private bool _workspaceInFlight;

    /// <summary>Creates the bridge.</summary>
    /// <param name="mirror">The open documents and their versions.</param>
    /// <param name="channel">How to ask Roslyn and how to tell the client.</param>
    /// <param name="options">The environment configuration.</param>
    /// <param name="time">The clock, so every debounce is testable without waiting.</param>
    /// <param name="logger">The stderr log.</param>
    internal DiagnosticsBridge(
        DocumentMirror mirror,
        IAdapterChannel channel,
        ClaudeRoslynLspOptions options,
        TimeProvider time,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _mirror = mirror;
        _channel = channel;
        _time = time;
        _logger = logger;
        _severityFloor = DiagnosticTranslation.ParseSeverityFloor(options.DiagnosticMinSeverity, logger);

        WorkspaceMode = IsWorkspaceModeRequested(options.WorkspaceDiagnostics, logger);

        Log.Started(_logger, _severityFloor, WorkspaceMode);
    }

    /// <summary>Whether the opt-in whole-solution mode is on.</summary>
    internal bool WorkspaceMode { get; }

    /// <summary>How many pulls have completed, for the tests and for a log line.</summary>
    internal int CompletedPulls { get; private set; }

    /// <summary>
    /// Whether <c>CLAUDE_ROSLYN_LSP_WORKSPACE_DIAGNOSTICS</c> asks for the whole-solution mode.
    /// </summary>
    /// <remarks>
    /// One accepted value, and it names what it does. <c>errors</c> is not a severity setting that
    /// happens to be spelled out — it is the whole contract of the mode: closed files are reported
    /// on for compile errors and nothing else, because that scope needs <c>fullSolution</c>
    /// background analysis (C14) and reporting analyser output at that scope is what makes a large
    /// solution unusable.
    /// </remarks>
    /// <param name="value">The configured value, or null.</param>
    /// <param name="logger">Where an unrecognised value is reported.</param>
    internal static bool IsWorkspaceModeRequested(string? value, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (value is null)
        {
            return false;
        }

        if (string.Equals(value, WorkspaceModeErrors, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.ToUpperInvariant() is "0" or "OFF" or "FALSE" or "NO" or "NONE")
        {
            return false;
        }

        Log.UnknownWorkspaceMode(logger, value);
        return false;
    }

    /// <summary>A document was opened: pull now, or as soon as the workspace is loaded (C28).</summary>
    /// <param name="uri">The document URI.</param>
    internal void OnDidOpen(string uri)
    {
        if (uri is not { Length: > 0 })
        {
            return;
        }

        if (!IsReady)
        {
            lock (_lock)
            {
                _deferred.Add(uri);
            }

            Log.Deferred(_logger, uri);
            return;
        }

        RequestPull(uri);
    }

    /// <summary>A document was edited: pull after the trailing debounce.</summary>
    /// <param name="uri">The document URI.</param>
    internal void OnDidChange(string uri)
    {
        if (uri is not { Length: > 0 } || !IsReady)
        {
            return;
        }

        lock (_lock)
        {
            var state = GetOrAdd(uri);

            state.Debounce ??= _time.CreateTimer(
                static x => ((DocumentDiagnosticState) x!).Owner.RequestPull(((DocumentDiagnosticState) x).Uri),
                state,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);

            state.Debounce.Change(ChangeDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>A document was saved: pull at once, and start the workspace round if that is on.</summary>
    /// <param name="uri">The document URI.</param>
    internal void OnDidSave(string uri)
    {
        if (uri is not { Length: > 0 } || !IsReady)
        {
            return;
        }

        RequestPull(uri);

        if (!WorkspaceMode)
        {
            return;
        }

        lock (_lock)
        {
            // Remembered so the round can put the saved file's own project first: an agent that just
            // edited a file cares about what it broke nearby before what was already broken far away.
            _lastSavedUri = uri;

            _workspaceTimer ??= _time.CreateTimer(
                static x => ((DiagnosticsBridge) x!).RunWorkspaceRound(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);

            _workspaceTimer.Change(WorkspaceSaveDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>A document was closed: clear its diagnostics and forget it.</summary>
    /// <remarks>
    /// The empty publish is not optional. A client keeps the last set it was given for a URI
    /// forever; a closed file whose diagnostics were never cleared keeps reporting an error the user
    /// fixed and then closed, which is a wrong answer with no way to make it go away.
    /// </remarks>
    /// <param name="uri">The document URI.</param>
    internal void OnDidClose(string uri)
    {
        if (uri is not { Length: > 0 })
        {
            return;
        }

        lock (_lock)
        {
            _documents.Remove(uri, out var state);
            state?.Debounce?.Dispose();
            _deferred.Remove(uri);
        }

        PublishEmpty(uri);
    }

    /// <summary>The workspace finished loading: pull everything that is open.</summary>
    internal void OnProjectsLoaded()
    {
        List<string> deferred;

        lock (_lock)
        {
            deferred = [.. _deferred];
            _deferred.Clear();
        }

        foreach (var document in _mirror.Snapshot())
        {
            RequestPull(document.Uri);
        }

        foreach (var uri in deferred)
        {
            RequestPull(uri);
        }

        if (WorkspaceMode)
        {
            lock (_lock)
            {
                _workspaceTimer ??= _time.CreateTimer(
                    static x => ((DiagnosticsBridge) x!).RunWorkspaceRound(),
                    this,
                    Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan);

                _workspaceTimer.Change(WorkspaceSaveDebounce, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>Roslyn asked for a category to be recomputed.</summary>
    /// <param name="method">Which refresh it was, for the log line.</param>
    internal void OnRefreshRequested(string method)
    {
        if (!string.Equals(method, "workspace/diagnostic/refresh", StringComparison.Ordinal))
        {
            // codeLens, inlayHint and semanticTokens refreshes are for capabilities the adapter does
            // not advertise (D45), so re-pulling diagnostics for them would be work nobody asked for.
            return;
        }

        Log.Refresh(_logger);
        Schedule(ref _refreshTimer, RefreshDebounce);
    }

    /// <summary>A project file changed on disk, so every open document's answer may have moved.</summary>
    internal void OnProjectFilesChanged()
    {
        Schedule(ref _projectFileTimer, ProjectFileDebounce);
    }

    /// <summary>
    /// Forgets every <c>previousResultId</c>, because the server that issued them is gone.
    /// </summary>
    /// <remarks>
    /// Called by the supervisor after a relaunch (D57). A result id is a token in one server's
    /// memory; presenting it to a fresh one would at best be ignored and at worst answered
    /// <c>unchanged</c> — which would publish nothing and leave the client holding the diagnostics
    /// of a session that ended.
    /// </remarks>
    internal void Reset()
    {
        lock (_lock)
        {
            foreach (var state in _documents.Values)
            {
                state.PreviousResultId = null;
            }

            _workspaceResultIds.Clear();
        }

        Log.Reset(_logger);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Cancel();

        lock (_lock)
        {
            foreach (var state in _documents.Values)
            {
                state.Debounce?.Dispose();
            }

            _documents.Clear();
            _refreshTimer?.Dispose();
            _projectFileTimer?.Dispose();
            _workspaceTimer?.Dispose();
        }

        _stopping.Dispose();
    }

    /// <summary>Whether the workspace has loaded far enough for a pull to mean anything (C28).</summary>
    private bool IsReady =>
        _channel.Readiness is ReadinessState.ProjectsLoaded or ReadinessState.LoadTimedOut;

    /// <summary>Arms one of the shared debounce timers.</summary>
    private void Schedule(ref ITimer? timer, TimeSpan delay)
    {
        lock (_lock)
        {
            timer ??= _time.CreateTimer(
                static x => ((DiagnosticsBridge) x!).PullEveryOpenDocument(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);

            timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Re-pulls every open document.</summary>
    private void PullEveryOpenDocument()
    {
        if (!IsReady)
        {
            return;
        }

        foreach (var document in _mirror.Snapshot())
        {
            RequestPull(document.Uri);
        }
    }

    /// <summary>Starts a pull, or marks the one already running as needing to run again.</summary>
    private void RequestPull(string uri)
    {
        if (!IsReady || !_channel.BackendConnected)
        {
            return;
        }

        lock (_lock)
        {
            var state = GetOrAdd(uri);

            if (state.InFlight)
            {
                state.Dirty = true;
                return;
            }

            state.InFlight = true;
        }

        _ = Task.Run(() => PullLoopAsync(uri), CancellationToken.None);
    }

    /// <summary>Pulls until nothing has changed since the last one started.</summary>
    private async Task PullLoopAsync(string uri)
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                await PullOnceAsync(uri).ConfigureAwait(false);

                lock (_lock)
                {
                    if (!_documents.TryGetValue(uri, out var state))
                    {
                        return;
                    }

                    if (!state.Dirty)
                    {
                        state.InFlight = false;
                        return;
                    }

                    state.Dirty = false;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.PullFailed(_logger, uri, exception);
        }
        finally
        {
            lock (_lock)
            {
                if (_documents.TryGetValue(uri, out var state))
                {
                    state.InFlight = false;
                }
            }
        }
    }

    /// <summary>One <c>textDocument/diagnostic</c>, with the one retry a moved world earns.</summary>
    private async Task PullOnceAsync(string uri)
    {
        var version = _mirror.Find(uri)?.Version;
        string? previousResultId;

        lock (_lock)
        {
            previousResultId = _documents.TryGetValue(uri, out var state) ? state.PreviousResultId : null;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                // The budget runs on the injected clock, so a test can expire it without waiting a
                // minute; the linked source is what makes disposal cancel a pull that is in flight.
                using var budget = new CancellationTokenSource(PullTimeout, _time);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, _stopping.Token);

                var result = await _channel
                    .AskAsync("textDocument/diagnostic", BuildPullParams(uri, previousResultId), deadline.Token)
                    .ConfigureAwait(false);

                Apply(uri, version, result);
                return;
            }
            catch (RoslynRequestException exception) when (exception.IsRetryable && attempt == 0)
            {
                // ContentModified means the document changed under the pull. One retry, because the
                // world settles or it does not — and a retry loop against a fast typist would never
                // return at all.
                Log.Retrying(_logger, uri, exception.Code);
                await Task.Delay(RetryDelay, _time, _stopping.Token).ConfigureAwait(false);
            }
            catch (RoslynRequestException exception)
            {
                Log.Refused(_logger, uri, exception.Code, exception.Message);
                return;
            }
            catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
            {
                Log.PullTimedOut(_logger, uri, PullTimeout.TotalSeconds);
                return;
            }
            catch (IOException exception)
            {
                // The backend went away mid-pull. The supervisor is already dealing with it.
                Log.PullFailed(_logger, uri, exception);
                return;
            }
        }
    }

    /// <summary>Publishes what a report said, or nothing when it said "unchanged".</summary>
    private void Apply(string uri, int? version, JsonElement report)
    {
        if (report.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var kind = report.TryGetProperty("kind", out var kindValue) && kindValue.ValueKind == JsonValueKind.String
            ? kindValue.GetString()
            : "full";

        if (report.TryGetProperty("resultId", out var resultId) && resultId.ValueKind == JsonValueKind.String)
        {
            lock (_lock)
            {
                if (_documents.TryGetValue(uri, out var state))
                {
                    state.PreviousResultId = resultId.GetString();
                }
            }
        }

        CompletedPulls++;

        if (string.Equals(kind, "unchanged", StringComparison.Ordinal))
        {
            // C12: the whole point of previousResultId. Publishing the previous set again would be
            // correct and would still cost the client a rendered attachment on every keystroke.
            Log.Unchanged(_logger, uri);
            return;
        }

        var items = report.TryGetProperty("items", out var itemsValue) ? itemsValue : default;
        var (body, count) = DiagnosticTranslation.BuildPublish(uri, version, items, _severityFloor);

        _channel.NotifyClient(body);
        Log.Published(_logger, count, uri, version ?? -1);
    }

    /// <summary>Publishes an empty set, which is how a client is told there is nothing left.</summary>
    private void PublishEmpty(string uri)
    {
        var (body, _) = DiagnosticTranslation.BuildPublish(uri, null, default, _severityFloor);
        _channel.NotifyClient(body);
    }

    /// <summary>Builds the pull's <c>params</c>: the document, and the last result id if there is one.</summary>
    private static byte[] BuildPullParams(string uri, string? previousResultId)
    {
        var buffer = new ArrayBufferWriter<byte>(128);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("textDocument"u8);
            writer.WriteStartObject();
            writer.WriteString("uri"u8, uri);
            writer.WriteEndObject();

            // Deliberately no "identifier": C9 says the union of every source comes back without
            // one, which is one round trip instead of ten for exactly the same set.
            if (previousResultId is { Length: > 0 })
            {
                writer.WriteString("previousResultId"u8, previousResultId);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The per-URI state, created on demand.</summary>
    private DocumentDiagnosticState GetOrAdd(string uri)
    {
        if (!_documents.TryGetValue(uri, out var state))
        {
            state = new DocumentDiagnosticState(this, uri);
            _documents[uri] = state;
        }

        return state;
    }

    /// <summary>Everything the bridge remembers about one document.</summary>
    private sealed class DocumentDiagnosticState
    {
        internal DocumentDiagnosticState(DiagnosticsBridge owner, string uri)
        {
            Owner = owner;
            Uri = uri;
        }

        /// <summary>The bridge, so a timer callback can find its way home without a closure.</summary>
        internal DiagnosticsBridge Owner { get; }

        /// <summary>The document this is about.</summary>
        internal string Uri { get; }

        /// <summary>The last <c>resultId</c> Roslyn gave for this document (C12).</summary>
        internal string? PreviousResultId { get; set; }

        /// <summary>Whether a pull is running right now.</summary>
        internal bool InFlight { get; set; }

        /// <summary>Whether the document changed while that pull was running.</summary>
        internal bool Dirty { get; set; }

        /// <summary>The trailing debounce for <c>didChange</c>.</summary>
        internal ITimer? Debounce { get; set; }
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1310,
            Level = LogLevel.Information,
            Message = "The diagnostics bridge is on: severity floor {Floor}, workspace mode {WorkspaceMode}.")]
        internal static partial void Started(ILogger logger, int floor, bool workspaceMode);

        [LoggerMessage(
            EventId = 1311,
            Level = LogLevel.Debug,
            Message = "Published {Count} diagnostic(s) for {Uri} at version {Version}.")]
        internal static partial void Published(ILogger logger, int count, string uri, int version);

        [LoggerMessage(EventId = 1312, Level = LogLevel.Debug, Message = "{Uri} is unchanged; nothing published.")]
        internal static partial void Unchanged(ILogger logger, string uri);

        [LoggerMessage(
            EventId = 1313,
            Level = LogLevel.Debug,
            Message = "{Uri} was opened before the workspace was loaded; its diagnostics are deferred so " +
                      "misc-files answers are never published (C28).")]
        internal static partial void Deferred(ILogger logger, string uri);

        [LoggerMessage(
            EventId = 1314,
            Level = LogLevel.Debug,
            Message = "Roslyn answered the pull for {Uri} with {Code}; retrying once.")]
        internal static partial void Retrying(ILogger logger, string uri, int code);

        [LoggerMessage(
            EventId = 1315,
            Level = LogLevel.Warning,
            Message = "Roslyn refused the diagnostic pull for {Uri} with {Code}: {Reason}")]
        internal static partial void Refused(ILogger logger, string uri, int code, string reason);

        [LoggerMessage(EventId = 1316, Level = LogLevel.Debug, Message = "The diagnostic pull for {Uri} failed.")]
        internal static partial void PullFailed(ILogger logger, string uri, Exception exception);

        [LoggerMessage(
            EventId = 1317,
            Level = LogLevel.Warning,
            Message = "The diagnostic pull for {Uri} did not answer within {Seconds:0} s; it was cancelled.")]
        internal static partial void PullTimedOut(ILogger logger, string uri, double seconds);

        [LoggerMessage(
            EventId = 1318,
            Level = LogLevel.Debug,
            Message = "Roslyn asked for diagnostics to be refreshed; every open document will be re-pulled.")]
        internal static partial void Refresh(ILogger logger);

        [LoggerMessage(
            EventId = 1319,
            Level = LogLevel.Information,
            Message = "Forgot every diagnostic result id; the server that issued them is gone.")]
        internal static partial void Reset(ILogger logger);

        [LoggerMessage(
            EventId = 1320,
            Level = LogLevel.Warning,
            Message = "CLAUDE_ROSLYN_LSP_WORKSPACE_DIAGNOSTICS='{Value}' is not 'errors'; the whole-solution " +
                      "mode stays off.")]
        internal static partial void UnknownWorkspaceMode(ILogger logger, string value);

        [LoggerMessage(
            EventId = 1321,
            Level = LogLevel.Information,
            Message = "Published whole-solution errors for {Count} closed file(s).")]
        internal static partial void WorkspacePublished(ILogger logger, int count);

        [LoggerMessage(
            EventId = 1322,
            Level = LogLevel.Warning,
            Message = "Roslyn refused workspace/diagnostic with {Code}: {Reason}")]
        internal static partial void WorkspaceRefused(ILogger logger, int code, string reason);

        [LoggerMessage(
            EventId = 1323,
            Level = LogLevel.Warning,
            Message = "workspace/diagnostic did not answer within {Seconds:0} s; it was cancelled. " +
                      "A solution this large is what CLAUDE_ROSLYN_LSP_WORKSPACE_DIAGNOSTICS being " +
                      "opt-in is for.")]
        internal static partial void WorkspaceTimedOut(ILogger logger, double seconds);

        [LoggerMessage(EventId = 1324, Level = LogLevel.Debug, Message = "The whole-solution pull failed.")]
        internal static partial void WorkspaceFailed(ILogger logger, Exception exception);
    }
}
