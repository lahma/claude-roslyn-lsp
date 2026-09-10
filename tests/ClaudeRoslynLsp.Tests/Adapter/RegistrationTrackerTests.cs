using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

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
    [Fact]
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

        Assert.Equal(3, sources.Count);

        var compiler = Assert.Single(sources, x => x.Identifier == "DocumentCompilerSemantic");
        Assert.False(compiler.WorkspaceDiagnostics);
        Assert.True(compiler.InterFileDependencies);

        var workspace = Assert.Single(sources, x => x.Identifier == "WorkspaceDocumentsAndProject");
        Assert.True(workspace.WorkspaceDiagnostics);
    }

    /// <summary>
    /// Razor's diagnostic registration has no <c>identifier</c> at all (C11). It has to survive being
    /// recorded, because a tracker that threw or skipped would lose the registration id and could
    /// never honour the matching unregistration.
    /// </summary>
    [Fact]
    public void TheRazorRegistrationWithNoIdentifierIsStillRecorded()
    {
        var tracker = new RegistrationTracker(NullLogger.Instance);

        tracker.Register(Parse("""
            {"registrations":[
              {"id":"razor-1","method":"textDocument/diagnostic","registerOptions":{"documentSelector":[{"language":"aspnetcorerazor","pattern":"**/*.{razor,cshtml}"}],"interFileDependencies":false,"workspaceDiagnostics":false}}
            ]}
            """));

        var source = Assert.Single(tracker.DiagnosticSources);

        Assert.Null(source.Identifier);
        Assert.Equal("razor-1", source.RegistrationId);
    }

    /// <summary>
    /// 113 of the real 135 watchers are one per reference assembly under the NuGet cache, and two of
    /// the rest name the same base with brace groups whose members are in a different order. Both
    /// collapse: one entry per base, patterns as a distinct set.
    /// </summary>
    [Fact]
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

        Assert.Equal(2, watchers.Count);

        var core = Assert.Single(watchers, x => x.BaseUri == "file:///w/Core");
        Assert.Equal(["**/*{.cs,.razor,.cshtml}", "Core.csproj"], core.Patterns);

        var cache = Assert.Single(watchers, x => x.BaseUri.Contains("nuget", StringComparison.Ordinal));
        Assert.Equal(["**/*.dll"], cache.Patterns);
    }

    /// <summary>The plain string form the specification also allows, which has no base at all.</summary>
    [Fact]
    public void AGlobWithNoBaseUriIsRecordedUnderTheEmptyBase()
    {
        var tracker = new RegistrationTracker(NullLogger.Instance);

        tracker.Register(Parse("""
            {"registrations":[{"id":"w","method":"workspace/didChangeWatchedFiles","registerOptions":{"watchers":[{"globPattern":"**/*.cs"}]}}]}
            """));

        var watcher = Assert.Single(tracker.Watchers);

        Assert.Equal(string.Empty, watcher.BaseUri);
        Assert.Equal(["**/*.cs"], watcher.Patterns);
    }

    /// <summary>
    /// The member is spelled <c>unregisterations</c> — a typo in the specification that every
    /// implementation reproduces, Roslyn included (C32). Spelling it correctly here would mean
    /// registrations are never removed and nothing would say so.
    /// </summary>
    [Fact]
    public void UnregisterationsAreHonouredUnderTheSpecificationsMisspelling()
    {
        var tracker = new RegistrationTracker(NullLogger.Instance);

        tracker.Register(Parse("""
            {"registrations":[
              {"id":"a","method":"textDocument/diagnostic","registerOptions":{"identifier":"syntax"}},
              {"id":"b","method":"workspace/didChangeWatchedFiles","registerOptions":{"watchers":[{"globPattern":{"baseUri":"file:///w","pattern":"**/*.cs"}}]}}
            ]}
            """));

        Assert.Equal(2, tracker.Count);

        var unregistration = JsonSerializer.Deserialize(
            Encoding.UTF8.GetBytes("""{"unregisterations":[{"id":"a","method":"textDocument/diagnostic"}]}"""),
            LspJsonContext.Default.UnregistrationParams);

        tracker.Unregister(unregistration);

        Assert.Equal(1, tracker.Count);
        Assert.Empty(tracker.DiagnosticSources);
        Assert.Single(tracker.Watchers);
        Assert.Equal(["workspace/didChangeWatchedFiles"], tracker.RegisteredMethods);
    }

    [Fact]
    public void RegisteringTheSameIdTwiceReplacesRatherThanDuplicates()
    {
        var tracker = new RegistrationTracker(NullLogger.Instance);

        tracker.Register(Parse("""
            {"registrations":[{"id":"a","method":"textDocument/diagnostic","registerOptions":{"identifier":"syntax"}}]}
            """));

        tracker.Register(Parse("""
            {"registrations":[{"id":"a","method":"textDocument/diagnostic","registerOptions":{"identifier":"NonLocal"}}]}
            """));

        Assert.Equal(1, tracker.Count);
        Assert.Equal("NonLocal", Assert.Single(tracker.DiagnosticSources).Identifier);
    }

    private static RegistrationParams? Parse(string json) =>
        JsonSerializer.Deserialize(Encoding.UTF8.GetBytes(json), LspJsonContext.Default.RegistrationParams);
}
