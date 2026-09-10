using System.Text.Json;

using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>One <c>textDocument/diagnostic</c> source Roslyn registered.</summary>
/// <param name="RegistrationId">The id it was registered under, for unregistration.</param>
/// <param name="Identifier">
/// The <c>registerOptions.identifier</c> that names the source in a pull — <c>DocumentCompilerSemantic</c>,
/// <c>DocumentAnalyzerSemantic</c> and eight others. Null for the Razor registration, which has none (C11).
/// </param>
/// <param name="WorkspaceDiagnostics">Whether this source also answers <c>workspace/diagnostic</c>.</param>
/// <param name="InterFileDependencies">Whether a change elsewhere can change this source's answer.</param>
internal sealed record DiagnosticSourceRegistration(
    string RegistrationId,
    string? Identifier,
    bool WorkspaceDiagnostics,
    bool InterFileDependencies);

/// <summary>A set of watch patterns sharing one base directory.</summary>
/// <param name="BaseUri">
/// The directory the patterns are relative to, or the empty string for an absolute glob.
/// </param>
/// <param name="Patterns">The distinct glob patterns registered against it.</param>
internal sealed record CollapsedWatcher(string BaseUri, IReadOnlyList<string> Patterns);

/// <summary>
/// What Roslyn has dynamically registered for, kept so the adapter can act on it — because the
/// client this adapter fronts refuses to.
/// </summary>
/// <remarks>
/// <para>
/// Claude Code answers <c>client/registerCapability</c> with <c>-32601</c>. Roslyn sends about a
/// hundred and forty of them (C32), and two of the things they carry are load-bearing: the ten
/// diagnostic source identifiers, without which a pull returns the union of every source and the
/// bridge cannot ask the compiler and the analysers separately (C9, C10); and the file-watch globs,
/// without which a file created by Bash never joins its project (C33). So the adapter answers them
/// itself, with <c>null</c>, and writes down what it was told.
/// </para>
/// <para>
/// <b>Collapse by base URI.</b> 113 of those watchers are one per reference assembly under the NuGet
/// cache, and standing up a filesystem watcher for each would be absurd. Grouping by
/// <c>globPattern.baseUri</c> reduces a two-project solution to a handful of real directories.
/// Patterns are compared as a <em>set</em>, not as strings: Roslyn emits both
/// <c>**/*{.cs,.razor,.cshtml}</c> and <c>**/*{.cs,.cshtml,.razor}</c> for the same intent (C32).
/// </para>
/// <para>
/// This work package stores; WP4's <c>FileWatchBridge</c> and diagnostics bridge consume. The
/// storage is the seam, and it is deliberately complete now so that WP4 adds behaviour rather than
/// re-deriving facts from the wire.
/// </para>
/// </remarks>
internal sealed partial class RegistrationTracker
{
    private readonly Lock _lock = new();

    /// <summary>Every registration, by its id, so an unregistration can find it.</summary>
    private readonly Dictionary<string, TrackedRegistration> _registrations = new(StringComparer.Ordinal);

    private readonly ILogger _logger;

    /// <summary>Creates an empty tracker.</summary>
    /// <param name="logger">The stderr log.</param>
    internal RegistrationTracker(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>How many registrations are live.</summary>
    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _registrations.Count;
            }
        }
    }

    /// <summary>
    /// The diagnostic sources, in registration-id order.
    /// </summary>
    /// <remarks>
    /// Sorted rather than left in arrival order because the ten registrations arrive in a different
    /// sequence on every run (C11) — a consumer that indexed into them would be right most of the
    /// time, which is the worst kind of right.
    /// </remarks>
    internal IReadOnlyList<DiagnosticSourceRegistration> DiagnosticSources
    {
        get
        {
            lock (_lock)
            {
                return _registrations.Values
                    .Select(x => x.Diagnostic)
                    .OfType<DiagnosticSourceRegistration>()
                    .OrderBy(x => x.Identifier ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(x => x.RegistrationId, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    /// <summary>The file-watch globs, collapsed to one entry per base directory.</summary>
    internal IReadOnlyList<CollapsedWatcher> Watchers
    {
        get
        {
            lock (_lock)
            {
                return _registrations.Values
                    .SelectMany(x => x.Watchers)
                    .GroupBy(x => x.BaseUri, StringComparer.Ordinal)
                    .Select(group => new CollapsedWatcher(
                        group.Key,
                        group.Select(x => x.Pattern)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(x => x, StringComparer.Ordinal)
                            .ToArray()))
                    .OrderBy(x => x.BaseUri, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    /// <summary>The methods that currently have at least one registration.</summary>
    internal IReadOnlyList<string> RegisteredMethods
    {
        get
        {
            lock (_lock)
            {
                return _registrations.Values
                    .Select(x => x.Method)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    /// <summary>Records a <c>client/registerCapability</c>.</summary>
    /// <param name="parameters">The registrations.</param>
    internal void Register(RegistrationParams? parameters)
    {
        if (parameters?.Registrations is not { Count: > 0 } registrations)
        {
            return;
        }

        foreach (var registration in registrations)
        {
            if (registration.Id is not { Length: > 0 } id || registration.Method is not { Length: > 0 } method)
            {
                continue;
            }

            var tracked = new TrackedRegistration(
                id,
                method,
                ReadDiagnosticSource(id, method, registration.RegisterOptions),
                ReadWatchers(method, registration.RegisterOptions));

            lock (_lock)
            {
                _registrations[id] = tracked;
            }
        }

        Log.Registered(_logger, registrations.Count, Count);
    }

    /// <summary>Records a <c>client/unregisterCapability</c>.</summary>
    /// <param name="parameters">The registrations being withdrawn.</param>
    internal void Unregister(UnregistrationParams? parameters)
    {
        if (parameters?.Unregisterations is not { Count: > 0 } unregisterations)
        {
            return;
        }

        var removed = 0;

        lock (_lock)
        {
            foreach (var unregistration in unregisterations)
            {
                if (unregistration.Id is { Length: > 0 } id && _registrations.Remove(id))
                {
                    removed++;
                }
            }
        }

        Log.Unregistered(_logger, removed, Count);
    }

    /// <summary>Reads a diagnostic registration's identifier and flags, when this is one.</summary>
    private static DiagnosticSourceRegistration? ReadDiagnosticSource(string id, string method, JsonElement options)
    {
        if (!string.Equals(method, "textDocument/diagnostic", StringComparison.Ordinal))
        {
            return null;
        }

        string? identifier = null;
        var workspaceDiagnostics = false;
        var interFileDependencies = false;

        if (options.ValueKind == JsonValueKind.Object)
        {
            if (options.TryGetProperty("identifier", out var value) && value.ValueKind == JsonValueKind.String)
            {
                identifier = value.GetString();
            }

            workspaceDiagnostics = options.TryGetProperty("workspaceDiagnostics", out var workspace)
                                   && workspace.ValueKind == JsonValueKind.True;

            interFileDependencies = options.TryGetProperty("interFileDependencies", out var interFile)
                                    && interFile.ValueKind == JsonValueKind.True;
        }

        return new DiagnosticSourceRegistration(id, identifier, workspaceDiagnostics, interFileDependencies);
    }

    /// <summary>Reads the watcher list, accepting both glob forms the specification allows.</summary>
    private static List<WatcherPattern> ReadWatchers(string method, JsonElement options)
    {
        if (!string.Equals(method, "workspace/didChangeWatchedFiles", StringComparison.Ordinal)
            || options.ValueKind != JsonValueKind.Object
            || !options.TryGetProperty("watchers", out var watchers)
            || watchers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parsed = new List<WatcherPattern>();

        foreach (var watcher in watchers.EnumerateArray())
        {
            if (watcher.ValueKind != JsonValueKind.Object
                || !watcher.TryGetProperty("globPattern", out var glob))
            {
                continue;
            }

            switch (glob.ValueKind)
            {
                // The plain form: one absolute or workspace-relative glob.
                case JsonValueKind.String when glob.GetString() is { Length: > 0 } pattern:
                    parsed.Add(new WatcherPattern(string.Empty, pattern));
                    break;

                // The relative form, which is what Roslyn actually sends (C32): a base directory
                // plus a pattern under it. Collapsing on the base is what turns 135 registrations
                // into a handful of directories worth watching.
                case JsonValueKind.Object:
                    var baseUri = glob.TryGetProperty("baseUri", out var b) && b.ValueKind == JsonValueKind.String
                        ? b.GetString() ?? string.Empty
                        : string.Empty;

                    if (glob.TryGetProperty("pattern", out var p)
                        && p.ValueKind == JsonValueKind.String
                        && p.GetString() is { Length: > 0 } relative)
                    {
                        parsed.Add(new WatcherPattern(baseUri, relative));
                    }

                    break;

                default:
                    break;
            }
        }

        return parsed;
    }

    /// <summary>One registration, reduced to the parts anything downstream reads.</summary>
    private sealed record TrackedRegistration(
        string Id,
        string Method,
        DiagnosticSourceRegistration? Diagnostic,
        IReadOnlyList<WatcherPattern> Watchers);

    /// <summary>One glob, with the base it is relative to.</summary>
    private sealed record WatcherPattern(string BaseUri, string Pattern);

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 600,
            Level = LogLevel.Debug,
            Message = "Recorded {Added} capability registration(s); {Total} live.")]
        internal static partial void Registered(ILogger logger, int added, int total);

        [LoggerMessage(
            EventId = 601,
            Level = LogLevel.Debug,
            Message = "Removed {Removed} capability registration(s); {Total} live.")]
        internal static partial void Unregistered(ILogger logger, int removed, int total);
    }
}
