using ClaudeRoslynLsp.Adapter;

using NUnit.Framework;

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
    [TestCase("initialize", true)]
    [TestCase("shutdown", true)]
    [TestCase("initialized", false)]
    [TestCase("exit", false)]
    public void TheHandshakeAndTheLifecycleAreAnsweredHere(string method, bool isRequest)
    {
        Assert.That(RequestRouter.Route(method, isRequest), Is.EqualTo(ClientRoute.AnswerLocally));
    }

    /// <summary>
    /// Every method the gate exists for routes to it. The enumeration and the routing rule agree
    /// here, which is what keeps the list from drifting into documentation nobody checks.
    /// </summary>
    [Test]
    public void EveryGatedMethodIsActuallyGated()
    {
        Assert.That(RequestRouter.GatedMethods, Is.Not.Empty);

        foreach (var method in RequestRouter.GatedMethods)
        {
            Assert.That(RequestRouter.Route(method, isRequest: true), Is.EqualTo(ClientRoute.Gated));
        }
    }

    /// <summary>
    /// The default for an unknown <em>request</em> is to hold it, not to refuse it: Roslyn
    /// implements far more than this table names (C26, C39), and a client asking for one of those is
    /// better served by a late answer than by -32601 from a process whose job is to carry the
    /// question elsewhere.
    /// </summary>
    [TestCase("textDocument/_vs_onAutoInsert")]
    [TestCase("textDocument/inlineValue")]
    [TestCase("something/nobodyHasHeardOf")]
    public void AnUnknownRequestIsHeldRatherThanRefused(string method)
    {
        Assert.That(RequestRouter.Route(method, isRequest: true), Is.EqualTo(ClientRoute.Gated));
    }

    /// <summary>
    /// The default for an unknown <em>notification</em> is to forward it. A notification cannot be
    /// answered, so holding one would only be a way to lose it.
    /// </summary>
    [Test]
    public void AnUnknownNotificationIsForwarded()
    {
        Assert.That(RequestRouter.Route("$/somethingNew", isRequest: false), Is.EqualTo(ClientRoute.Forward));
    }

    [TestCase("textDocument/didOpen")]
    [TestCase("textDocument/didChange")]
    [TestCase("textDocument/didSave")]
    [TestCase("textDocument/didClose")]
    public void TheDocumentLifecycleGoesThroughTheMirror(string method)
    {
        Assert.That(RequestRouter.Route(method, isRequest: false), Is.EqualTo(ClientRoute.Document));
    }

    [Test]
    public void CancellationHasItsOwnRoute()
    {
        Assert.That(RequestRouter.Route("$/cancelRequest", isRequest: false), Is.EqualTo(ClientRoute.Cancel));
    }

    /// <summary>
    /// Roslyn does not implement <c>$/setTrace</c>, so forwarding it would put a -32601 in somebody's
    /// log every time a client turned tracing on.
    /// </summary>
    [Test]
    public void TraceControlIsConsumedHere()
    {
        Assert.That(RequestRouter.Route("$/setTrace", isRequest: false), Is.EqualTo(ClientRoute.Drop));
    }

    [TestCase("workspace/didChangeConfiguration")]
    [TestCase("workspace/didChangeWatchedFiles")]
    [TestCase("workspace/didChangeWorkspaceFolders")]
    public void WorkspaceNotificationsCrossAsSoonAsRoslynCanTakeThem(string method)
    {
        Assert.That(RequestRouter.Route(method, isRequest: false), Is.EqualTo(ClientRoute.Forward));
    }
}
