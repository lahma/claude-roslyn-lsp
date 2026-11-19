using ClaudeRoslynLsp.Mcp;
using ClaudeRoslynLsp.Mcp.Engine;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Mcp;

/// <summary>
/// The catalogue, against the code action payloads the WP0 spikes captured (D64).
/// </summary>
public class CodeActionCatalogTests
{
    /// <summary>
    /// The fold. Roslyn offers three rows for two choices; a caller should read two, with a flag.
    /// </summary>
    [Test]
    public void FixAllEntriesBecomeScopesOnThePlainActionAndDisappear()
    {
        var catalogue = CodeActionCatalog.Build([
            RoslynPayloads.Action(RoslynPayloads.RemoveUnnecessaryUsings),
            RoslynPayloads.Action(RoslynPayloads.FixAllRemoveUnnecessaryUsings),
            RoslynPayloads.Action(RoslynPayloads.SuppressOrConfigureIssues),
        ]);

        Assert.That(
            catalogue.Select(action => action.Title),
            Is.EqualTo(["Remove unnecessary usings", "Suppress or configure issues"]));

        var fix = catalogue[0];

        Assert.That(fix.FixAllScopes, Is.EqualTo([FixAllScope.Document, FixAllScope.Project, FixAllScope.Solution]));
    }

    /// <summary>
    /// The defensive half of the fold: an orphan <c>Fix All:</c> entry — one whose plain action is not
    /// in the list — is kept, because dropping it would remove a capability with no trace.
    /// </summary>
    [Test]
    public void AnOrphanFixAllEntryIsKept()
    {
        var catalogue = CodeActionCatalog.Build([
            RoslynPayloads.Action(RoslynPayloads.FixAllRemoveUnnecessaryUsings),
        ]);

        Assert.That(catalogue, Has.Exactly(1).Items);
        Assert.That(catalogue.Single().Title, Is.EqualTo("Fix All: Remove unnecessary usings"));
    }

    /// <summary>
    /// C18: the nested group's real choices are inside <c>command.arguments[0]</c>, not inside
    /// <c>data</c>, so a catalogue that only looked at <c>data</c> would report an empty group.
    /// </summary>
    [Test]
    public void NestedActionsAreReadOutOfTheMarkerCommandsArgument()
    {
        var catalogue = CodeActionCatalog.Build([
            RoslynPayloads.Action(RoslynPayloads.SuppressOrConfigureIssues),
        ]);

        Assert.That(catalogue, Has.Exactly(1).Items);
        var group = catalogue.Single();

        Assert.That(group.Nested.Select(child => child.Title), Is.EqualTo(["None", "Silent"]));
        Assert.That(group.Nested.Select(child => child.Id.Length), Has.All.EqualTo(8));
    }

    /// <summary>Every id in one list is distinct, or the id is not an address.</summary>
    [Test]
    public void IdsAreDistinctAcrossTheWholeFlattenedList()
    {
        var catalogue = CodeActionCatalog.Build([
            RoslynPayloads.Action(RoslynPayloads.RemoveUnnecessaryUsings),
            RoslynPayloads.Action(RoslynPayloads.MakeStatic),
            RoslynPayloads.Action(RoslynPayloads.SuppressOrConfigureIssues),
        ]);

        var ids = CodeActionCatalog.Flatten(catalogue).Select(action => action.Id).ToArray();

        Assert.That(ids.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(ids.Length));
    }

    /// <summary>
    /// The id has to survive the list being regenerated, which is what makes
    /// <c>getCodeActions</c> and <c>applyCodeAction</c> two calls about the same thing.
    /// </summary>
    [Test]
    public void TheIdIsStableAcrossTwoIdenticalListings()
    {
        var first = CodeActionCatalog.Build([RoslynPayloads.Action(RoslynPayloads.MakeStatic)]);
        var second = CodeActionCatalog.Build([RoslynPayloads.Action(RoslynPayloads.MakeStatic)]);

        Assert.That(second[0].Id, Is.EqualTo(first[0].Id));
    }

    /// <summary>
    /// And it has to differ between two actions that are the same fix at different depths — the
    /// nested children all have the same title shape and differ only in <c>CodeActionPath</c>.
    /// </summary>
    [Test]
    public void TheIdDistinguishesActionsThatDifferOnlyInTheirPath()
    {
        var catalogue = CodeActionCatalog.Build([
            RoslynPayloads.Action(RoslynPayloads.SuppressOrConfigureIssues),
        ]);

        Assert.That(catalogue, Has.Exactly(1).Items);
        var group = catalogue.Single();

        Assert.That(group.Nested[1].Id, Is.Not.EqualTo(group.Nested[0].Id));
        Assert.That(group.Nested[0].Id, Is.Not.EqualTo(group.Id));
    }

    [Test]
    public void DiagnosticIdsComeOffTheAttachedDiagnostics()
    {
        var catalogue = CodeActionCatalog.Build([RoslynPayloads.Action(RoslynPayloads.MakeStatic)]);

        Assert.That(catalogue, Has.Exactly(1).Items);
        var action = catalogue.Single();

        Assert.That(action.DiagnosticIds, Is.EqualTo(["CA1822"]));
        Assert.That(action.Kind, Is.EqualTo("quickfix"));
    }

    /// <summary>Lookup is by id first, then by exact title, then case-insensitively.</summary>
    [Test]
    public void LookupPrefersTheIdAndFallsBackToTheTitle()
    {
        var catalogue = CodeActionCatalog.Build([
            RoslynPayloads.Action(RoslynPayloads.RemoveUnnecessaryUsings),
            RoslynPayloads.Action(RoslynPayloads.MakeStatic),
        ]);

        Assert.That(CodeActionCatalog.TryFind(catalogue, catalogue[1].Id, title: null, out var byId), Is.True);
        Assert.That(byId!.Title, Is.EqualTo("Make static"));

        Assert.That(CodeActionCatalog.TryFind(catalogue, id: null, "Make static", out var byTitle), Is.True);
        Assert.That(byTitle!.Title, Is.EqualTo("Make static"));

        Assert.That(CodeActionCatalog.TryFind(catalogue, id: null, "make STATIC", out var byLooseTitle), Is.True);
        Assert.That(byLooseTitle!.Title, Is.EqualTo("Make static"));

        Assert.That(CodeActionCatalog.TryFind(catalogue, "deadbeef", "No such action", out _), Is.False);
    }

    /// <summary>A nested child is addressable directly, without a second listing call.</summary>
    [Test]
    public void LookupReachesNestedChildren()
    {
        var catalogue = CodeActionCatalog.Build([
            RoslynPayloads.Action(RoslynPayloads.SuppressOrConfigureIssues),
        ]);

        Assert.That(CodeActionCatalog.TryFind(catalogue, id: null, "Silent", out var child), Is.True);
        Assert.That(child!.Title, Is.EqualTo("Silent"));
    }

    /// <summary>
    /// C18 again: <c>data</c> is absent on a nested group, which carries its resolve data in the
    /// marker command instead. Reading only <c>data</c> would make the group unresolvable.
    /// </summary>
    [Test]
    public void ResolveDataFallsBackToTheMarkerCommandsArgument()
    {
        var action = RoslynPayloads.Action(RoslynPayloads.SuppressOrConfigureIssues);

        Assert.That(action.Data, Is.Null);
        Assert.That(CodeActionCatalog.DataOf(action), Is.Not.Null);
    }
}
