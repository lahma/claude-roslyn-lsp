namespace ClaudeRoslynLsp.Adapter;

/// <summary>What the adapter does with one message from its client.</summary>
internal enum ClientRoute
{
    /// <summary>The adapter answers it out of its own state; Roslyn never sees it.</summary>
    AnswerLocally,

    /// <summary>Held until the workspace has loaded, then forwarded with a rewritten id.</summary>
    Gated,

    /// <summary>Forwarded as soon as Roslyn is initialised; never held.</summary>
    Forward,

    /// <summary>Recorded in the document mirror and then forwarded.</summary>
    Document,

    /// <summary>Mapped onto Roslyn's id, or used to dequeue a held request.</summary>
    Cancel,

    /// <summary>Consumed here and not forwarded.</summary>
    Drop,
}

/// <summary>
/// The dispatch table for the Claude-facing side: which methods the adapter answers itself, which it
/// holds, and which cross immediately.
/// </summary>
/// <remarks>
/// <para>
/// The default for an unrecognised <em>request</em> is to gate it, not to refuse it. Roslyn
/// implements more than this adapter has ever heard of — a <c>textDocument/_vs_*</c> family, type
/// hierarchy, folding ranges, document highlight (C39, C26) — and a client that asks for one of
/// them is better served by an answer that arrives late than by <c>-32601</c> from a process whose
/// entire job is to carry the question somewhere else. The same default is what keeps this table
/// from being a maintenance liability on every Roslyn bump.
/// </para>
/// <para>
/// The default for an unrecognised <em>notification</em> is to forward it once Roslyn is
/// initialised. A notification cannot be answered, so holding one would only be a way to lose it.
/// </para>
/// </remarks>
internal static class RequestRouter
{
    /// <summary>
    /// The requests the readiness gate exists for: everything that reads the semantic model.
    /// </summary>
    /// <remarks>
    /// Enumerated even though the default is the same, because this list is what the tests assert
    /// against and what a reader checks their expectation against. Every one of these returns an
    /// empty successful result if it reaches Roslyn early (C27) — which is a wrong answer, not an
    /// error, and is the failure this whole adapter exists to prevent.
    /// </remarks>
    internal static readonly IReadOnlySet<string> GatedMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "textDocument/definition",
        "textDocument/typeDefinition",
        "textDocument/implementation",
        "textDocument/references",
        "textDocument/hover",
        "textDocument/documentSymbol",
        "textDocument/prepareCallHierarchy",
        "callHierarchy/incomingCalls",
        "callHierarchy/outgoingCalls",
        "textDocument/prepareTypeHierarchy",
        "typeHierarchy/supertypes",
        "typeHierarchy/subtypes",
        "workspace/symbol",
        "workspaceSymbol/resolve",
        "textDocument/prepareRename",
        "textDocument/rename",
        "textDocument/codeAction",
        "codeAction/resolve",
        "textDocument/formatting",
        "textDocument/rangeFormatting",
        "textDocument/onTypeFormatting",
        "textDocument/signatureHelp",
        "textDocument/completion",
        "completionItem/resolve",
        "textDocument/documentHighlight",
        "textDocument/foldingRange",
        "textDocument/selectionRange",
        "textDocument/diagnostic",
        "workspace/diagnostic",
    };

    /// <summary>Decides what happens to one client message.</summary>
    /// <param name="method">The method name.</param>
    /// <param name="isRequest">Whether it carries a correlatable id.</param>
    internal static ClientRoute Route(string method, bool isRequest)
    {
        ArgumentNullException.ThrowIfNull(method);

        switch (method)
        {
            // The handshake and the lifecycle are the adapter's own. Answering initialize without
            // waiting for Roslyn is the point: Claude Code holds it open forever otherwise, and the
            // capability document is authored here anyway (D45).
            case "initialize":
            case "shutdown":
            case "initialized":
            case "exit":
                return ClientRoute.AnswerLocally;

            case "$/cancelRequest":
                return ClientRoute.Cancel;

            // Trace control is a client-to-server convention this adapter has no use for, and
            // Roslyn does not implement it. Consumed rather than forwarded so it cannot produce a
            // -32601 in somebody's log every time a client turns tracing on.
            case "$/setTrace":
                return ClientRoute.Drop;

            case "textDocument/didOpen":
            case "textDocument/didChange":
            case "textDocument/didSave":
            case "textDocument/didClose":
                return ClientRoute.Document;

            case "workspace/didChangeConfiguration":
            case "workspace/didChangeWatchedFiles":
            case "workspace/didChangeWorkspaceFolders":
                return ClientRoute.Forward;

            default:
                return isRequest ? ClientRoute.Gated : ClientRoute.Forward;
        }
    }
}
