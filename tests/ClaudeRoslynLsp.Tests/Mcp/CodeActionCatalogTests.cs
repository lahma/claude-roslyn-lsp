using ClaudeRoslynLsp.Mcp;
using ClaudeRoslynLsp.Mcp.Engine;

using Xunit;

namespace ClaudeRoslynLsp.Tests.Mcp;

/// <summary>
/// The catalogue, against the code action payloads the WP0 spikes captured (D64).
/// </summary>
public class CodeActionCatalogTests
{
    /// <summary>
    /// The fold. Roslyn offers three rows for two choices; a caller should read two, with a flag.
    /// </summary>
    [Fact]
    public void FixAllEntriesBecomeScopesOnThePlainActionAndDisappear()
    {
        var catalogue = CodeActionCatalog.Build([
            RoslynPayloads.Action(RoslynPayloads.RemoveUnnecessaryUsings),
            RoslynPayloads.Action(RoslynPayloads.FixAllRemoveUnnecessaryUsings),
            RoslynPayloads.Action(RoslynPayloads.SuppressOrConfigureIssues),
        ]);

        Assert.Equal(
            ["Remove unnecessary usings", "Suppress or configure issues"],
            catalogue.Select(action => action.Title));

        var fix = catalogue[0];

        Assert.Equal([FixAllScope.Document, FixAllScope.Project, FixAllScope.Solution], fix.FixAllScopes);
    }

    /// <summary>
    /// The defensive half of the fold: an orphan <c>Fix All:</c> entry — one whose plain action is not
    /// in the list — is kept, because dropping it would remove a capability with no trace.
    /// </summary>
    [Fact]
    public void AnOrphanFixAllEntryIsKept()
    {
        var catalogue = CodeActionCatalog.Build([
            RoslynPayloads.Action(RoslynPayloads.FixAllRemoveUnnecessaryUsings),
        ]);

        Assert.Equal("Fix All: Remove unnecessary usings", Assert.Single(catalogue).Title);
    }

    /// <summary>
    /// C18: the nested group's real choices are inside <c>command.arguments[0]</c>, not inside
    /// <c>data</c>, so a catalogue that only looked at <c>data</c> would report an empty group.
    /// </summary>
    [Fact]
    public void NestedActionsAreReadOutOfTheMarkerCommandsArgument()
    {
        var group = Assert.Single(CodeActionCatalog.Build([
            RoslynPayloads.Action(RoslynPayloads.SuppressOrConfigureIssues),
        ]));

        Assert.Equal(["None", "Silent"], group.Nested.Select(child => child.Title));
        Assert.All(group.Nested, child => Assert.Equal(8, child.Id.Length));
    }

    /// <summary>Every id in one list is distinct, or the id is not an address.</summary>
    [Fact]
    public void IdsAreDistinctAcrossTheWholeFlattenedList()
    {
        var catalogue = CodeActionCatalog.Build([
            RoslynPayloads.Action(RoslynPayloads.RemoveUnnecessaryUsings),
            RoslynPayloads.Action(RoslynPayloads.MakeStatic),
            RoslynPayloads.Action(RoslynPayloads.SuppressOrConfigureIssues),
        ]);

        var ids = CodeActionCatalog.Flatten(catalogue).Select(action => action.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The id has to survive the list being regenerated, which is what makes
    /// <c>getCodeActions</c> and <c>applyCodeAction</c> two calls about the same thing.
    /// </summary>
    [Fact]
    public void TheIdIsStableAcrossTwoIdenticalListings()
    {
        var first = CodeActionCatalog.Build([RoslynPayloads.Action(RoslynPayloads.MakeStatic)]);
        var second = CodeActionCatalog.Build([RoslynPayloads.Action(RoslynPayloads.MakeStatic)]);

        Assert.Equal(first[0].Id, second[0].Id);
    }

    /// <summary>
    /// And it has to differ between two actions that are the same fix at different depths — the
    /// nested children all have the same title shape and differ only in <c>CodeActionPath</c>.
    /// </summary>
    [Fact]
    public void TheIdDistinguishesActionsThatDifferOnlyInTheirPath()
    {
        var group = Assert.Single(CodeActionCatalog.Build([
            RoslynPayloads.Action(RoslynPayloads.SuppressOrConfigureIssues),
        ]));

        Assert.NotEqual(group.Nested[0].Id, group.Nested[1].Id);
        Assert.NotEqual(group.Id, group.Nested[0].Id);
    }

    [Fact]
    public void DiagnosticIdsComeOffTheAttachedDiagnostics()
    {
        var action = Assert.Single(CodeActionCatalog.Build([RoslynPayloads.Action(RoslynPayloads.MakeStatic)]));

        Assert.Equal(["CA1822"], action.DiagnosticIds);
        Assert.Equal("quickfix", action.Kind);
    }

    /// <summary>Lookup is by id first, then by exact title, then case-insensitively.</summary>
    [Fact]
    public void LookupPrefersTheIdAndFallsBackToTheTitle()
    {
        var catalogue = CodeActionCatalog.Build([
            RoslynPayloads.Action(RoslynPayloads.RemoveUnnecessaryUsings),
            RoslynPayloads.Action(RoslynPayloads.MakeStatic),
        ]);

        Assert.True(CodeActionCatalog.TryFind(catalogue, catalogue[1].Id, title: null, out var byId));
        Assert.Equal("Make static", byId.Title);

        Assert.True(CodeActionCatalog.TryFind(catalogue, id: null, "Make static", out var byTitle));
        Assert.Equal("Make static", byTitle.Title);

        Assert.True(CodeActionCatalog.TryFind(catalogue, id: null, "make STATIC", out var byLooseTitle));
        Assert.Equal("Make static", byLooseTitle.Title);

        Assert.False(CodeActionCatalog.TryFind(catalogue, "deadbeef", "No such action", out _));
    }

    /// <summary>A nested child is addressable directly, without a second listing call.</summary>
    [Fact]
    public void LookupReachesNestedChildren()
    {
        var catalogue = CodeActionCatalog.Build([
            RoslynPayloads.Action(RoslynPayloads.SuppressOrConfigureIssues),
        ]);

        Assert.True(CodeActionCatalog.TryFind(catalogue, id: null, "Silent", out var child));
        Assert.Equal("Silent", child.Title);
    }

    /// <summary>
    /// C18 again: <c>data</c> is absent on a nested group, which carries its resolve data in the
    /// marker command instead. Reading only <c>data</c> would make the group unresolvable.
    /// </summary>
    [Fact]
    public void ResolveDataFallsBackToTheMarkerCommandsArgument()
    {
        var action = RoslynPayloads.Action(RoslynPayloads.SuppressOrConfigureIssues);

        Assert.Null(action.Data);
        Assert.NotNull(CodeActionCatalog.DataOf(action));
    }
}
