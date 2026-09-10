using ClaudeRoslynLsp.Adapter;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// The Claude-facing dispatch table: what the adapter answers itself, what it holds, and what
/// crosses immediately.
/// </summary>
public class RequestRouterTests
{
    /// <summary>
    /// Answering these four locally is what lets the handshake complete before a backend exists.
    /// Forwarding <c>initialize</c> would make the client's startup wait on Roslyn's, and Claude
    /// Code holds an unanswered <c>initialize</c> open indefinitely.
    /// </summary>
    [Theory]
    [InlineData("initialize", true)]
    [InlineData("shutdown", true)]
    [InlineData("initialized", false)]
    [InlineData("exit", false)]
    public void TheHandshakeAndTheLifecycleAreAnsweredHere(string method, bool isRequest)
    {
        Assert.Equal(ClientRoute.AnswerLocally, RequestRouter.Route(method, isRequest));
    }

    /// <summary>
    /// Every method the gate exists for routes to it. The enumeration and the routing rule agree
    /// here, which is what keeps the list from drifting into documentation nobody checks.
    /// </summary>
    [Fact]
    public void EveryGatedMethodIsActuallyGated()
    {
        Assert.NotEmpty(RequestRouter.GatedMethods);

        foreach (var method in RequestRouter.GatedMethods)
        {
            Assert.Equal(ClientRoute.Gated, RequestRouter.Route(method, isRequest: true));
        }
    }

    /// <summary>
    /// The default for an unknown <em>request</em> is to hold it, not to refuse it: Roslyn
    /// implements far more than this table names (C26, C39), and a client asking for one of those is
    /// better served by a late answer than by -32601 from a process whose job is to carry the
    /// question elsewhere.
    /// </summary>
    [Theory]
    [InlineData("textDocument/_vs_onAutoInsert")]
    [InlineData("textDocument/inlineValue")]
    [InlineData("something/nobodyHasHeardOf")]
    public void AnUnknownRequestIsHeldRatherThanRefused(string method)
    {
        Assert.Equal(ClientRoute.Gated, RequestRouter.Route(method, isRequest: true));
    }

    /// <summary>
    /// The default for an unknown <em>notification</em> is to forward it. A notification cannot be
    /// answered, so holding one would only be a way to lose it.
    /// </summary>
    [Fact]
    public void AnUnknownNotificationIsForwarded()
    {
        Assert.Equal(ClientRoute.Forward, RequestRouter.Route("$/somethingNew", isRequest: false));
    }

    [Theory]
    [InlineData("textDocument/didOpen")]
    [InlineData("textDocument/didChange")]
    [InlineData("textDocument/didSave")]
    [InlineData("textDocument/didClose")]
    public void TheDocumentLifecycleGoesThroughTheMirror(string method)
    {
        Assert.Equal(ClientRoute.Document, RequestRouter.Route(method, isRequest: false));
    }

    [Fact]
    public void CancellationHasItsOwnRoute()
    {
        Assert.Equal(ClientRoute.Cancel, RequestRouter.Route("$/cancelRequest", isRequest: false));
    }

    /// <summary>
    /// Roslyn does not implement <c>$/setTrace</c>, so forwarding it would put a -32601 in somebody's
    /// log every time a client turned tracing on.
    /// </summary>
    [Fact]
    public void TraceControlIsConsumedHere()
    {
        Assert.Equal(ClientRoute.Drop, RequestRouter.Route("$/setTrace", isRequest: false));
    }

    [Theory]
    [InlineData("workspace/didChangeConfiguration")]
    [InlineData("workspace/didChangeWatchedFiles")]
    [InlineData("workspace/didChangeWorkspaceFolders")]
    public void WorkspaceNotificationsCrossAsSoonAsRoslynCanTakeThem(string method)
    {
        Assert.Equal(ClientRoute.Forward, RequestRouter.Route(method, isRequest: false));
    }
}
