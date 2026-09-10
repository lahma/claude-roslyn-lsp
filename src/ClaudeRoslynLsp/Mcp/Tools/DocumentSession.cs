using ClaudeRoslynLsp.Edits;

namespace ClaudeRoslynLsp.Mcp.Tools;

/// <summary>
/// Holds a document open for the length of one tool call, and closes it again only if this call is
/// what opened it.
/// </summary>
/// <remarks>
/// <para>
/// C13 is why this exists: Roslyn answers <c>textDocument/diagnostic</c> — and, in practice, offers
/// code actions and formatting — only for documents the client has opened. The MCP half has no
/// editor and therefore no open documents, so it has to open one, ask, and put it back.
/// </para>
/// <para>
/// The "only if this call opened it" rule is the part that is easy to get wrong and expensive to get
/// wrong: when the LSP half is sharing the session (D23), a document the editor has open is a
/// document with unsaved changes in Roslyn's mirror, and closing it would silently revert Roslyn's
/// view to the bytes on disk. So an already-open document is used as it is and left alone, and the
/// text this opens with is always the bytes on disk — never a guess, because Roslyn answers about
/// the text it was given.
/// </para>
/// </remarks>
internal sealed class DocumentSession : IAsyncDisposable
{
    private readonly RoslynToolContext _context;
    private readonly bool _openedHere;

    private DocumentSession(RoslynToolContext context, string path, string uri, bool openedHere)
    {
        _context = context;
        _openedHere = openedHere;
        Path = path;
        Uri = uri;
    }

    /// <summary>The absolute path.</summary>
    internal string Path { get; }

    /// <summary>The document's <c>file:</c> URI.</summary>
    internal string Uri { get; }

    /// <summary>Opens a document if it is not open already.</summary>
    /// <param name="context">The tool context.</param>
    /// <param name="tool">The calling tool's MCP name, for the not-found message.</param>
    /// <param name="path">The absolute path.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    internal static async Task<DocumentSession> OpenAsync(
        RoslynToolContext context,
        string tool,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
        {
            throw ToolErrors.NotFound(tool, $"file at '{context.Guard.ToRelative(path)}'");
        }

        var uri = WorkspacePathGuard.ToUri(path);
        var wasOpen = context.Engine.IsDocumentOpen(uri);

        if (!wasOpen)
        {
            var (text, _) = TextFileCodec.Read(path);
            await context.Engine.OpenDocumentAsync(uri, text, cancellationToken).ConfigureAwait(false);
        }

        return new DocumentSession(context, path, uri, !wasOpen);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_openedHere)
        {
            // CancellationToken.None deliberately: a cancelled call must still put the document back,
            // or the next call sees a document this one opened and never closed.
            await _context.Engine.CloseDocumentAsync(Uri, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
