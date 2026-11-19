using System.Text.Json;

using ClaudeRoslynLsp.Adapter;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// C24: a multi-targeted project reports every call once per target framework.
/// </summary>
/// <remarks>
/// The duplicates are not cosmetic for an agent. A model asked "who calls this" reports twice the
/// number, and a model walking the tree visits every branch twice — which on a three-level hierarchy
/// over two frameworks is eight times the work for the same answer.
/// </remarks>
public class CallHierarchyDeduplicatorTests
{
    /// <summary>One call-hierarchy item, parameterised by the project GUID that is all that differs.</summary>
    private static string Item(string projectGuid, int line = 16, string uri = "file:///w/Calculator.cs") => $$$"""
        {"name":"Compute","kind":6,"uri":"{{{uri}}}",
         "range":{"start":{"line":{{{line}}},"character":4},"end":{"line":{{{line + 3}}},"character":5}},
         "selectionRange":{"start":{"line":{{{line}}},"character":15},"end":{"line":{{{line}}},"character":22}},
         "data":{"SymbolKeyData":"opaque","ProjectGuid":"{{{projectGuid}}}"}}
        """;

    /// <summary>Only the three call-hierarchy methods are touched.</summary>
    [TestCase("textDocument/prepareCallHierarchy", true)]
    [TestCase("callHierarchy/incomingCalls", true)]
    [TestCase("callHierarchy/outgoingCalls", true)]
    [TestCase("textDocument/references", false)]
    [TestCase("workspace/symbol", false)]
    public void OnlyCallHierarchyAnswersAreTouched(string method, bool expected) =>
        Assert.That(CallHierarchyDeduplicator.Applies(method), Is.EqualTo(expected));

    /// <summary>
    /// Two items that differ only in an opaque project GUID are the same symbol, and the key is
    /// (uri, selectionRange) precisely because <c>data</c> is what differs.
    /// </summary>
    [Test]
    public void PrepareLosesThePerFrameworkCopy()
    {
        var result = Array(Item("aaaa"), Item("bbbb"));
        var reduced = CallHierarchyDeduplicator.Deduplicate("textDocument/prepareCallHierarchy", result);

        Assert.That(reduced, Is.Not.Null);

        var kept = JsonDocument.Parse(reduced).RootElement;

        Assert.That(kept.GetArrayLength(), Is.EqualTo(1));

        // The survivor is the first one, verbatim: the opaque payload the next request has to
        // round-trip is still there and still exactly what Roslyn sent.
        Assert.That(kept[0].GetProperty("data").GetProperty("SymbolKeyData").GetString(), Is.EqualTo("opaque"));
        Assert.That(kept[0].GetProperty("data").GetProperty("ProjectGuid").GetString(), Is.EqualTo("aaaa"));
    }

    /// <summary>Two genuinely different symbols both survive.</summary>
    [Test]
    public void DistinctSymbolsAreBothKept()
    {
        var result = Array(Item("aaaa", line: 16), Item("aaaa", line: 40));

        Assert.That(CallHierarchyDeduplicator.Deduplicate("textDocument/prepareCallHierarchy", result), Is.Null);
    }

    /// <summary>The same identifier range in two different files is two symbols.</summary>
    [Test]
    public void TheSameRangeInADifferentFileIsADifferentSymbol()
    {
        var result = Array(
            Item("aaaa", uri: "file:///w/A.cs"),
            Item("aaaa", uri: "file:///w/B.cs"));

        Assert.That(CallHierarchyDeduplicator.Deduplicate("textDocument/prepareCallHierarchy", result), Is.Null);
    }

    /// <summary>An incoming call is keyed on its <c>from</c> item, not on the envelope.</summary>
    [Test]
    public void IncomingCallsAreKeyedOnTheCaller()
    {
        var result = Array(
            $$$"""{"from":{{{Item("aaaa")}}},"fromRanges":[{"start":{"line":9,"character":8},"end":{"line":9,"character":15}}]}""",
            $$$"""{"from":{{{Item("bbbb")}}},"fromRanges":[{"start":{"line":9,"character":8},"end":{"line":9,"character":15}}]}""");

        var reduced = CallHierarchyDeduplicator.Deduplicate("callHierarchy/incomingCalls", result);

        Assert.That(reduced, Is.Not.Null);
        Assert.That(JsonDocument.Parse(reduced).RootElement.GetArrayLength(), Is.EqualTo(1));
    }

    /// <summary>An outgoing call is keyed on its <c>to</c> item.</summary>
    [Test]
    public void OutgoingCallsAreKeyedOnTheCallee()
    {
        var result = Array(
            $$$"""{"to":{{{Item("aaaa")}}},"fromRanges":[]}""",
            $$$"""{"to":{{{Item("bbbb")}}},"fromRanges":[]}""");

        var reduced = CallHierarchyDeduplicator.Deduplicate("callHierarchy/outgoingCalls", result);

        Assert.That(reduced, Is.Not.Null);
        Assert.That(JsonDocument.Parse(reduced).RootElement.GetArrayLength(), Is.EqualTo(1));
    }

    /// <summary>
    /// An entry whose key cannot be read is kept. Dropping something this code did not understand
    /// would turn a shape change in Roslyn into a silently shorter answer.
    /// </summary>
    [Test]
    public void AnUnreadableEntryIsKept()
    {
        var result = Array("""{"name":"Mystery"}""", """{"name":"Mystery"}""");

        Assert.That(CallHierarchyDeduplicator.Deduplicate("textDocument/prepareCallHierarchy", result), Is.Null);
    }

    /// <summary>A null answer — Roslyn's "no hierarchy here" — is left alone.</summary>
    [Test]
    public void ANullResultIsLeftAlone()
    {
        var result = JsonDocument.Parse("null").RootElement;

        Assert.That(CallHierarchyDeduplicator.Deduplicate("textDocument/prepareCallHierarchy", result), Is.Null);
    }

    private static JsonElement Array(params string[] entries) =>
        JsonDocument.Parse("[" + string.Join(",", entries) + "]").RootElement.Clone();
}
