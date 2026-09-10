using System.Buffers;
using System.Text.Json;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// The opt-in half: compile errors in files nobody has open.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default, and the reason is not timidity.</b> A closed file is only reachable through
/// <c>workspace/diagnostic</c> (C13 — a document pull on a file that is not open always returns
/// zero items whatever the scope), and <c>workspace/diagnostic</c> only reports closed files when
/// <c>csharp|background_analysis.dotnet_compiler_diagnostics_scope</c> is <c>fullSolution</c>
/// (C14). That setting is the one that makes a large solution unusable: it puts every project's
/// compilation in memory, in a child process that is already the largest thing on the machine
/// during a Claude session. So turning it on is a decision somebody makes for a repository, not a
/// default somebody discovers when their laptop fans start.
/// </para>
/// <para>
/// <b>And when it is on, the output is cut hard.</b> C15: the report includes <c>obj/**/*.cs</c>,
/// <c>.csproj</c> entries, and one copy per target framework of every multi-targeted project.
/// Passing that through would fill the client's diagnostics attachment with generated files and
/// duplicates and push out the errors in the code somebody wrote. What survives is compile errors
/// only, ten files, five each, with the saved file's own project first — because an agent that just
/// edited something wants to know what it broke <em>there</em> before it hears about a project it
/// has never opened.
/// </para>
/// <para>
/// <b>Published URIs are remembered so they can be cleared.</b> A client keeps the last set it was
/// given for a URI forever. A closed file that was reported broken and then fixed has to be
/// published <c>[]</c> explicitly, or it stays broken in the transcript for the rest of the session.
/// </para>
/// </remarks>
internal sealed partial class DiagnosticsBridge
{
    /// <summary>Directory segments whose contents are build output rather than somebody's code.</summary>
    private static readonly string[] GeneratedDirectories = ["obj", "bin"];

    /// <summary>Runs one whole-solution round, unless one is already running.</summary>
    private void RunWorkspaceRound()
    {
        if (!WorkspaceMode || !IsReady || !_channel.BackendConnected)
        {
            return;
        }

        lock (_lock)
        {
            if (_workspaceInFlight)
            {
                return;
            }

            _workspaceInFlight = true;
        }

        _ = Task.Run(WorkspaceRoundAsync, CancellationToken.None);
    }

    /// <summary>Asks for the whole solution, then publishes the little of it that earns its place.</summary>
    private async Task WorkspaceRoundAsync()
    {
        try
        {
            string? savedUri;
            byte[] parameters;

            lock (_lock)
            {
                savedUri = _lastSavedUri;
                parameters = BuildWorkspaceParams(_workspaceResultIds);
            }

            using var budget = new CancellationTokenSource(WorkspacePullTimeout, _time);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, _stopping.Token);

            var result = await _channel
                .AskAsync("workspace/diagnostic", parameters, deadline.Token)
                .ConfigureAwait(false);

            PublishWorkspace(result, savedUri);
        }
        catch (RoslynRequestException exception)
        {
            Log.WorkspaceRefused(_logger, exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
        {
            Log.WorkspaceTimedOut(_logger, WorkspacePullTimeout.TotalSeconds);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            Log.WorkspaceFailed(_logger, exception);
        }
        finally
        {
            lock (_lock)
            {
                _workspaceInFlight = false;
            }
        }
    }

    /// <summary>Filters, prioritises, caps and publishes one workspace report.</summary>
    private void PublishWorkspace(JsonElement report, string? savedUri)
    {
        if (report.ValueKind != JsonValueKind.Object
            || !report.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var open = new HashSet<string>(StringComparer.Ordinal);

        foreach (var document in _mirror.Snapshot())
        {
            open.Add(document.Uri);
        }

        var priorityPrefix = ProjectDirectoryPrefix(savedUri);
        var candidates = new List<WorkspaceEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("uri", out var uriValue)
                || uriValue.ValueKind != JsonValueKind.String
                || uriValue.GetString() is not { Length: > 0 } uri)
            {
                continue;
            }

            RememberResultId(uri, entry);

            // C15's three noise sources, in the order they cost most: the same project once per
            // target framework, build output, and the project file itself.
            if (!seen.Add(uri) || open.Contains(uri) || IsNotSomebodysSource(uri))
            {
                continue;
            }

            if (!entry.TryGetProperty("items", out var entryItems) || entryItems.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var errors = DiagnosticTranslation.Select(
                entryItems,
                DiagnosticTranslation.SeverityError,
                MaxWorkspaceDiagnosticsPerFile);

            if (errors.Count == 0)
            {
                continue;
            }

            candidates.Add(new WorkspaceEntry(uri, errors, IsUnder(uri, priorityPrefix)));
        }

        candidates.Sort(static (left, right) =>
            left.Nearby == right.Nearby
                ? string.CompareOrdinal(left.Uri, right.Uri)
                : right.Nearby.CompareTo(left.Nearby));

        if (candidates.Count > MaxWorkspaceFiles)
        {
            candidates.RemoveRange(MaxWorkspaceFiles, candidates.Count - MaxWorkspaceFiles);
        }

        var published = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            _channel.NotifyClient(BuildWorkspacePublish(candidate));
            published.Add(candidate.Uri);
        }

        ClearStale(published);
        Log.WorkspacePublished(_logger, candidates.Count);
    }

    /// <summary>Publishes an empty set for everything that was reported last round and is now clean.</summary>
    private void ClearStale(HashSet<string> published)
    {
        List<string> stale;

        lock (_lock)
        {
            stale = _publishedClosedUris.Where(x => !published.Contains(x)).ToList();
            _publishedClosedUris.Clear();

            foreach (var uri in published)
            {
                _publishedClosedUris.Add(uri);
            }
        }

        foreach (var uri in stale)
        {
            PublishEmpty(uri);
        }
    }

    /// <summary>Stores the entry's <c>resultId</c> so the next round can ask for the delta (C14).</summary>
    private void RememberResultId(string uri, JsonElement entry)
    {
        if (!entry.TryGetProperty("resultId", out var resultId)
            || resultId.ValueKind != JsonValueKind.String
            || resultId.GetString() is not { Length: > 0 } value)
        {
            return;
        }

        lock (_lock)
        {
            _workspaceResultIds[uri] = value;
        }
    }

    /// <summary>Renders one closed file's errors as a <c>publishDiagnostics</c> notification.</summary>
    private static byte[] BuildWorkspacePublish(WorkspaceEntry entry)
    {
        var buffer = new ArrayBufferWriter<byte>(256);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("uri"u8, entry.Uri);

            // No version: the file is not open, so there is no mirror version to be stale against
            // and a fabricated one would make the client discard the set.
            writer.WritePropertyName("diagnostics"u8);
            writer.WriteStartArray();

            foreach (var diagnostic in entry.Diagnostics)
            {
                diagnostic.WriteTo(writer);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Protocol.JsonRpcErrors.Notification("textDocument/publishDiagnostics", buffer.WrittenSpan);
    }

    /// <summary>Builds <c>workspace/diagnostic</c>'s params: no identifier, plus every known result id.</summary>
    /// <remarks>
    /// No <c>identifier</c> for the same reason the document pull omits one (C9, C14): omitted or
    /// <c>WorkspaceDocumentsAndProject</c> are the only two that report closed files at all, and
    /// omitting it also gets the union rather than one category.
    /// </remarks>
    private static byte[] BuildWorkspaceParams(Dictionary<string, string> previousResultIds)
    {
        var buffer = new ArrayBufferWriter<byte>(128 + (previousResultIds.Count * 96));

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("previousResultIds"u8);
            writer.WriteStartArray();

            foreach (var pair in previousResultIds)
            {
                writer.WriteStartObject();
                writer.WriteString("uri"u8, pair.Key);
                writer.WriteString("value"u8, pair.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Whether a URI names build output or a project file rather than somebody's source.</summary>
    private static bool IsNotSomebodysSource(string uri)
    {
        if (uri.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || uri.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
            || uri.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || uri.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var directory in GeneratedDirectories)
        {
            if (uri.Contains("/" + directory + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The directory of the project that owns the saved file, as a URI prefix.</summary>
    /// <remarks>
    /// Approximated from the URI rather than resolved through Roslyn, deliberately. The exact
    /// project would cost a round trip on the save path to decide an <em>ordering</em>, and the
    /// approximation — the nearest ancestor directory holding a project file, or the file's own
    /// directory — puts the right things first in every layout anybody actually uses.
    /// </remarks>
    private string? ProjectDirectoryPrefix(string? savedUri)
    {
        if (savedUri is not { Length: > 0 } || _channel.WorkspaceRoot is not { Length: > 0 } root)
        {
            return null;
        }

        if (!Uri.TryCreate(savedUri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(parsed.LocalPath);
        var rootFull = Path.GetFullPath(root);

        while (directory is { Length: > 0 } && directory.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.EnumerateFiles(directory, "*.csproj").Any())
                {
                    return new Uri(directory + Path.DirectorySeparatorChar).AbsoluteUri;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /// <summary>Whether a URI sits under a prefix, tolerating a null prefix as "no preference".</summary>
    private static bool IsUnder(string uri, string? prefix) =>
        prefix is { Length: > 0 } && uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>One closed file that survived the filter.</summary>
    /// <param name="Uri">The file.</param>
    /// <param name="Diagnostics">Its errors, already capped.</param>
    /// <param name="Nearby">Whether it belongs to the project of the file that was just saved.</param>
    private sealed record WorkspaceEntry(string Uri, List<JsonElement> Diagnostics, bool Nearby);
}
