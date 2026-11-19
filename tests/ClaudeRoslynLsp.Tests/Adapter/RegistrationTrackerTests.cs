using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// What the adapter writes down when it answers Roslyn's hundred and forty registrations on the
/// client's behalf.
/// </summary>
/// <remarks>
/// Nothing here acts on the registrations — that is WP4 — but everything here is a fact WP4 would
/// otherwise have to re-derive from the wire, and two of them are easy to get wrong in a way that
/// only shows up as missing diagnostics or stale answers: keying diagnostic sources on their
/// identifier rather than their arrival order (C11), and collapsing watchers by base URI with the
/// globs compared as a set (C32).
/// </remarks>
public class RegistrationTrackerTests
{
    /// <summary>
    /// The ten sources arrive in a different order every run, so the tracker keys them on
    /// <c>registerOptions.identifier</c>. Anything that indexed into the arrival order would be right
    /// most of the time, which is the worst kind of right.
    /// </summary>
    [Test]
    public void DiagnosticSourcesAreKeyedOnTheirIdentifier()
    {
        var tracker = new RegistrationTracker(NullLogger.Instance);

        tracker.Register(Parse("""
            {"registrations":[
              {"id":"a","method":"textDocument/diagnostic","registerOptions":{"identifier":"DocumentAnalyzerSemantic","interFileDependencies":true,"workspaceDiagnostics":false}},
              {"id":"b","method":"textDocument/diagnostic","registerOptions":{"identifier":"DocumentCompilerSemantic","interFileDependencies":true,"workspaceDiagnostics":false}},
              {"id":"c","method":"textDocument/diagnostic","registerOptions":{"workDoneProgress":true,"identifier":"WorkspaceDocumentsAndProject","interFileDependencies":true,"workspaceDiagnostics":true}}
            ]}
            """));

        var sources = tracker.DiagnosticSources;

        Assert.That(sources.Count, Is.EqualTo(3));

        Assert.That(sources, Has.Exactly(1).Matches<DiagnosticSourceRegistration>(x => x!.Identifier == "DocumentCompilerSemantic"));
        var compiler = sources.Single(x => x.Identifier == "DocumentCompilerSemantic");
        Assert.That(compiler.WorkspaceDiagnostics, Is.False);
        Assert.That(compiler.InterFileDependencies, Is.True);

        Assert.That(sources, Has.Exactly(1).Matches<DiagnosticSourceRegistration>(x => x!.Identifier == "WorkspaceDocumentsAndProject"));
        var workspace = sources.Single(x => x.Identifier == "WorkspaceDocumentsAndProject");
        Assert.That(workspace.WorkspaceDiagnostics, Is.True);
    }

    /// <summary>
    /// Razor's diagnostic registration has no <c>identifier</c> at all (C11). It has to survive being
    /// recorded, because a tracker that threw or skipped would lose the registration id and could
    /// never honour the matching unregistration.
    /// </summary>
    [Test]
    public void TheRazorRegistrationWithNoIdentifierIsStillRecorded()
    {
        var tracker = new RegistrationTracker(NullLogger.Instance);

        tracker.Register(Parse("""
            {"registrations":[
              {"id":"razor-1","method":"textDocument/diagnostic","registerOptions":{"documentSelector":[{"language":"aspnetcorerazor","pattern":"**/*.{razor,cshtml}"}],"interFileDependencies":false,"workspaceDiagnostics":false}}
            ]}
            """));

        Assert.That(tracker.DiagnosticSources, Has.Exactly(1).Items);
        var source = tracker.DiagnosticSources.Single();

        Assert.That(source.Identifier, Is.Null);
        Assert.That(source.RegistrationId, Is.EqualTo("razor-1"));
    }

    /// <summary>
    /// 113 of the real 135 watchers are one per reference assembly under the NuGet cache, and two of
    /// the rest name the same base with brace groups whose members are in a different order. Both
    /// collapse: one entry per base, patterns as a distinct set.
    /// </summary>
    [Test]
    public void WatchersCollapseByBaseUriWithTheirGlobsAsASet()
    {
        var tracker = new RegistrationTracker(NullLogger.Instance);

        tracker.Register(Parse("""
            {"registrations":[
              {"id":"w1","method":"workspace/didChangeWatchedFiles","registerOptions":{"watchers":[{"globPattern":{"baseUri":"file:///w/Core","pattern":"**/*{.cs,.razor,.cshtml}"}}]}},
              {"id":"w2","method":"workspace/didChangeWatchedFiles","registerOptions":{"watchers":[{"globPattern":{"baseUri":"file:///w/Core","pattern":"**/*{.cs,.razor,.cshtml}"}}]}},
              {"id":"w3","method":"workspace/didChangeWatchedFiles","registerOptions":{"watchers":[{"globPattern":{"baseUri":"file:///w/Core","pattern":"Core.csproj"}}]}},
              {"id":"w4","method":"workspace/didChangeWatchedFiles","registerOptions":{"watchers":[{"globPattern":{"baseUri":"file:///nuget/system.text.json/9.0.0","pattern":"**/*.dll"}}]}}
            ]}
            """));

        var watchers = tracker.Watchers;

        Assert.That(watchers.Count, Is.EqualTo(2));

        Assert.That(watchers, Has.Exactly(1).Matches<CollapsedWatcher>(x => x!.BaseUri == "file:///w/Core"));
        var core = watchers.Single(x => x.BaseUri == "file:///w/Core");
        Assert.That(core.Patterns, Is.EqualTo(["**/*{.cs,.razor,.cshtml}", "Core.csproj"]));

        Assert.That(watchers, Has.Exactly(1).Matches<CollapsedWatcher>(x => x!.BaseUri.Contains("nuget", StringComparison.Ordinal)));
        var cache = watchers.Single(x => x.BaseUri.Contains("nuget", StringComparison.Ordinal));
        Assert.That(cache.Patterns, Is.EqualTo(["**/*.dll"]));
    }

    /// <summary>The plain string form the specification also allows, which has no base at all.</summary>
    [Test]
    public void AGlobWithNoBaseUriIsRecordedUnderTheEmptyBase()
    {
        var tracker = new RegistrationTracker(NullLogger.Instance);

        tracker.Register(Parse("""
            {"registrations":[{"id":"w","method":"workspace/didChangeWatchedFiles","registerOptions":{"watchers":[{"globPattern":"**/*.cs"}]}}]}
            """));

        Assert.That(tracker.Watchers, Has.Exactly(1).Items);
        var watcher = tracker.Watchers.Single();

        Assert.That(watcher.BaseUri, Is.EqualTo(string.Empty));
        Assert.That(watcher.Patterns, Is.EqualTo(["**/*.cs"]));
    }

    /// <summary>
    /// The member is spelled <c>unregisterations</c> — a typo in the specification that every
    /// implementation reproduces, Roslyn included (C32). Spelling it correctly here would mean
    /// registrations are never removed and nothing would say so.
    /// </summary>
    [Test]
    public void UnregisterationsAreHonouredUnderTheSpecificationsMisspelling()
    {
        var tracker = new RegistrationTracker(NullLogger.Instance);

        tracker.Register(Parse("""
            {"registrations":[
              {"id":"a","method":"textDocument/diagnostic","registerOptions":{"identifier":"syntax"}},
              {"id":"b","method":"workspace/didChangeWatchedFiles","registerOptions":{"watchers":[{"globPattern":{"baseUri":"file:///w","pattern":"**/*.cs"}}]}}
            ]}
            """));

        Assert.That(tracker.Count, Is.EqualTo(2));

        var unregistration = JsonSerializer.Deserialize(
            Encoding.UTF8.GetBytes("""{"unregisterations":[{"id":"a","method":"textDocument/diagnostic"}]}"""),
            LspJsonContext.Default.UnregistrationParams);

        tracker.Unregister(unregistration);

        Assert.That(tracker.Count, Is.EqualTo(1));
        Assert.That(tracker.DiagnosticSources, Is.Empty);
        Assert.That(tracker.Watchers, Has.Exactly(1).Items);
        Assert.That(tracker.RegisteredMethods, Is.EqualTo(["workspace/didChangeWatchedFiles"]));
    }

    [Test]
    public void RegisteringTheSameIdTwiceReplacesRatherThanDuplicates()
    {
        var tracker = new RegistrationTracker(NullLogger.Instance);

        tracker.Register(Parse("""
            {"registrations":[{"id":"a","method":"textDocument/diagnostic","registerOptions":{"identifier":"syntax"}}]}
            """));

        tracker.Register(Parse("""
            {"registrations":[{"id":"a","method":"textDocument/diagnostic","registerOptions":{"identifier":"NonLocal"}}]}
            """));

        Assert.That(tracker.Count, Is.EqualTo(1));
        Assert.That(tracker.DiagnosticSources, Has.Exactly(1).Items);
        Assert.That(tracker.DiagnosticSources.Single().Identifier, Is.EqualTo("NonLocal"));
    }

    private static RegistrationParams? Parse(string json) =>
        JsonSerializer.Deserialize(Encoding.UTF8.GetBytes(json), LspJsonContext.Default.RegistrationParams);
}
