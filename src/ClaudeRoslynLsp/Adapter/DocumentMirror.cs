using System.Text.Json;

using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>One open document, as the adapter last saw it.</summary>
/// <param name="Uri">The document URI.</param>
/// <param name="LanguageId">The language id the client opened it with.</param>
/// <param name="Version">The version the text belongs to.</param>
/// <param name="Text">The whole document.</param>
internal sealed record MirroredDocument(string Uri, string LanguageId, int Version, string Text);

/// <summary>
/// Every document the client has open, at its latest text — the state a restarted Roslyn has to be
/// told about before it can answer anything.
/// </summary>
/// <remarks>
/// <para>
/// The client sends <c>didOpen</c> once and then edits. Roslyn holds the edited text in memory and
/// nowhere else, so a Roslyn that has just been relaunched knows only what is on disk — which is not
/// what the user is looking at, and answering from it produces positions that are off by however
/// many lines the unsaved edits added. Replaying one <c>didOpen</c> per document at its latest text
/// is the whole recovery primitive; WP4's supervisor calls <see cref="BuildReplay"/> and nothing
/// else.
/// </para>
/// <para>
/// This is also the argument for full document synchronisation (D13). A mirror maintained from
/// range edits is only correct if every edit was applied, in order, with none dropped — and the one
/// moment it matters is precisely the moment something went wrong. Full text costs bytes on a
/// channel that is a local pipe.
/// </para>
/// <para>
/// The mirror is updated <em>before</em> the notification is forwarded, and it is updated even while
/// the backend is still starting. That is what "buffered as mirror state, not as a raw queue" means:
/// a document opened and edited three times before Roslyn was ready replays as one <c>didOpen</c>
/// carrying the third text, not as four notifications Roslyn would have to apply in order.
/// </para>
/// </remarks>
internal sealed partial class DocumentMirror
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, MirroredDocument> _documents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnedAboutRanges = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    /// <summary>Creates an empty mirror.</summary>
    /// <param name="logger">Where an out-of-contract client is reported.</param>
    internal DocumentMirror(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>How many documents are open.</summary>
    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _documents.Count;
            }
        }
    }

    /// <summary>Records a <c>textDocument/didOpen</c>.</summary>
    /// <param name="body">The raw notification body.</param>
    /// <returns>The document, or null when the notification could not be read.</returns>
    internal MirroredDocument? Open(ReadOnlySpan<byte> body)
    {
        var item = ParamsOf(body, LspJsonContext.Default.DidOpenParams)?.TextDocument;

        if (item is null)
        {
            Log.Unreadable(_logger, "textDocument/didOpen");
            return null;
        }

        var document = new MirroredDocument(item.Uri, item.LanguageId, item.Version, item.Text);

        lock (_lock)
        {
            _documents[item.Uri] = document;
        }

        return document;
    }

    /// <summary>Records a full-text <c>textDocument/didChange</c>.</summary>
    /// <param name="body">The raw notification body.</param>
    /// <returns>The updated document, or null when the notification could not be applied.</returns>
    internal MirroredDocument? Change(ReadOnlySpan<byte> body)
    {
        var parameters = ParamsOf(body, LspJsonContext.Default.DidChangeParams);
        var uri = parameters?.TextDocument?.Uri;

        if (uri is null || parameters?.ContentChanges is not { Count: > 0 } changes)
        {
            Log.Unreadable(_logger, "textDocument/didChange");
            return null;
        }

        var last = changes[^1];

        if (last.Range.ValueKind is JsonValueKind.Object)
        {
            // The server advertised change: 1 (D13), so this client is out of contract. Said once
            // per document rather than per keystroke, and the mirror still stores what it was given
            // so a replay is wrong rather than absent — which is the failure that gets noticed.
            lock (_lock)
            {
                if (_warnedAboutRanges.Add(uri))
                {
                    Log.RangeChange(_logger, uri);
                }
            }
        }

        var text = last.Text ?? string.Empty;

        lock (_lock)
        {
            var languageId = _documents.TryGetValue(uri, out var existing) ? existing.LanguageId : "csharp";
            var document = new MirroredDocument(uri, languageId, parameters.TextDocument!.Version, text);
            _documents[uri] = document;
            return document;
        }
    }

    /// <summary>Records a <c>textDocument/didClose</c>.</summary>
    /// <param name="body">The raw notification body.</param>
    /// <returns>The URI that was closed, or null when the notification could not be read.</returns>
    internal string? Close(ReadOnlySpan<byte> body)
    {
        var uri = ParamsOf(body, LspJsonContext.Default.TextDocumentParams)?.TextDocument?.Uri;

        if (uri is null)
        {
            Log.Unreadable(_logger, "textDocument/didClose");
            return null;
        }

        lock (_lock)
        {
            _documents.Remove(uri);
            _warnedAboutRanges.Remove(uri);
        }

        return uri;
    }

    /// <summary>One open document, or null when the client does not have it open.</summary>
    /// <remarks>
    /// The diagnostics bridge reads the version here at the moment a pull starts, so the published
    /// set can be tagged with the text it is actually about; the watch bridge reads it to find out
    /// that a file on disk is one the client already owns, and must therefore not be reported as a
    /// watched-file change (which would make Roslyn re-read the saved copy over the live buffer).
    /// </remarks>
    /// <param name="uri">The document URI.</param>
    internal MirroredDocument? Find(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        lock (_lock)
        {
            return _documents.GetValueOrDefault(uri);
        }
    }

    /// <summary>The open documents, in the order they were opened.</summary>
    internal IReadOnlyList<MirroredDocument> Snapshot()
    {
        lock (_lock)
        {
            return _documents.Values.ToArray();
        }
    }

    /// <summary>
    /// The notifications that bring a fresh Roslyn up to the client's current state: one
    /// <c>didOpen</c> per document, at its latest text.
    /// </summary>
    internal IReadOnlyList<byte[]> BuildReplay()
    {
        var documents = Snapshot();
        var replay = new byte[documents.Count][];

        for (var index = 0; index < documents.Count; index++)
        {
            replay[index] = BuildDidOpen(documents[index]);
        }

        return replay;
    }

    /// <summary>Renders one document as a <c>textDocument/didOpen</c> notification.</summary>
    /// <param name="document">The document to open.</param>
    internal static byte[] BuildDidOpen(MirroredDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return JsonSerializer.SerializeToUtf8Bytes(
            new DidOpenNotification
            {
                Params = new DidOpenParams
                {
                    TextDocument = new TextDocumentItem
                    {
                        Uri = document.Uri,
                        LanguageId = document.LanguageId,
                        Version = document.Version,
                        Text = document.Text,
                    },
                },
            },
            LspJsonContext.Default.DidOpenNotification);
    }

    /// <summary>
    /// Reads a notification's <c>params</c>, treating anything unreadable as "nothing to record".
    /// </summary>
    /// <remarks>
    /// The <em>whole</em> read is inside the guard, the inner one included. A client that sends a
    /// <c>didOpen</c> with an empty <c>params</c> is out of contract, but a thrown exception here
    /// would escape into the read loop and take the session down with it — which turns a
    /// malformed notification into a dead language server.
    /// </remarks>
    /// <typeparam name="T">The parameter shape.</typeparam>
    /// <param name="body">The raw notification.</param>
    /// <param name="typeInfo">The source-generated contract for <typeparamref name="T"/>.</param>
    private static T? ParamsOf<T>(ReadOnlySpan<byte> body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            var envelope = JsonSerializer.Deserialize(body, LspJsonContext.Default.RawParamsNotification);

            return envelope?.Params.ValueKind == JsonValueKind.Object
                ? envelope.Params.Deserialize(typeInfo)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 500,
            Level = LogLevel.Warning,
            Message = "A {Method} notification could not be read; the document mirror is now behind the " +
                      "client and a Roslyn restart would replay stale text.")]
        internal static partial void Unreadable(ILogger logger, string method);

        [LoggerMessage(
            EventId = 501,
            Level = LogLevel.Warning,
            Message = "The client sent a ranged didChange for {Uri} although this server advertises full " +
                      "document synchronisation; the mirror cannot reconstruct the document from it.")]
        internal static partial void RangeChange(ILogger logger, string uri);
    }
}
