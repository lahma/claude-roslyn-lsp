using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Protocol;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// The correlation table. Its whole job is that two peers counting from one cannot collide, and that
/// an answer comes back under the id its asker used.
/// </summary>
public class IdMapTests
{
    /// <summary>
    /// Forwarded requests and the adapter's own share one counter, so an id can never be handed out
    /// twice. Without that, Claude's <c>initialize</c> (id 1) and the adapter's <c>initialize</c> to
    /// Roslyn (also id 1) would be the same outstanding request — and one would be answered with the
    /// other's result, which is a wrong answer rather than an error.
    /// </summary>
    [Fact]
    public void EveryOutboundIdIsUniqueAcrossBothKindsOfRequest()
    {
        var map = new IdMap();
        var issued = new HashSet<int>();

        for (var index = 0; index < 500; index++)
        {
            var forwarded = map.Forward(JsonRpcId.FromNumber(1), "1"u8, "textDocument/definition");
            var own = map.Register("initialize", new TaskCompletionSource<JsonElement>());

            Assert.True(issued.Add(forwarded), $"Outbound id {forwarded} was issued twice.");
            Assert.True(issued.Add(own), $"Outbound id {own} was issued twice.");
        }

        Assert.Equal(1000, issued.Count);
    }

    [Fact]
    public void CompletingAForwardedRequestReturnsThePeersOwnIdToken()
    {
        var map = new IdMap();
        var outbound = map.Forward(JsonRpcId.FromText("init-1"), "\"init-1\""u8, "textDocument/hover");

        Assert.True(map.TryComplete(outbound, out var pending));
        Assert.Equal("textDocument/hover", pending.Method);
        Assert.Equal("\"init-1\"", Encoding.UTF8.GetString(pending.OriginalIdToken!));
        Assert.Null(pending.Completion);

        // And it is gone: an answer arriving twice must not be delivered twice.
        Assert.False(map.TryComplete(outbound, out _));
    }

    [Fact]
    public void CompletingAnAdapterRequestHandsBackItsCompletionSource()
    {
        var map = new IdMap();
        var completion = new TaskCompletionSource<JsonElement>();
        var outbound = map.Register("shutdown", completion);

        Assert.True(map.TryComplete(outbound, out var pending));
        Assert.Same(completion, pending.Completion);
        Assert.Null(pending.OriginalIdToken);
    }

    /// <summary>
    /// What <c>$/cancelRequest</c> needs: the id the <em>other</em> peer knows the request by, with
    /// the request still outstanding, because a cancellation is not an answer.
    /// </summary>
    [Fact]
    public void ACancellationResolvesThePeersIdWithoutRemovingTheRequest()
    {
        var map = new IdMap();
        var outbound = map.Forward(JsonRpcId.FromNumber(9), "9"u8, "textDocument/references");

        Assert.True(map.TryResolveOutboundId(JsonRpcId.FromNumber(9), out var resolved));
        Assert.Equal(outbound, resolved);
        Assert.Equal(1, map.PendingCount);

        Assert.True(map.TryComplete(outbound, out _));
        Assert.False(map.TryResolveOutboundId(JsonRpcId.FromNumber(9), out _));
    }

    /// <summary>
    /// The backend going away must leave no request unanswered — a peer waits on an outstanding
    /// request indefinitely, so every entry has to become an error rather than silence.
    /// </summary>
    [Fact]
    public void DrainingReturnsEverythingOutstandingAndEmptiesTheTable()
    {
        var map = new IdMap();

        map.Forward(JsonRpcId.FromNumber(1), "1"u8, "a");
        map.Forward(JsonRpcId.FromNumber(2), "2"u8, "b");
        map.Register("initialize", new TaskCompletionSource<JsonElement>());

        var drained = map.DrainAll();

        Assert.Equal(3, drained.Count);
        Assert.Equal(0, map.PendingCount);
        Assert.False(map.TryResolveOutboundId(JsonRpcId.FromNumber(1), out _));
    }

    [Fact]
    public void AnOutboundIdRendersAsABareJsonNumberToken()
    {
        Assert.Equal("17", Encoding.UTF8.GetString(IdMap.TokenFor(17)));
    }
}
