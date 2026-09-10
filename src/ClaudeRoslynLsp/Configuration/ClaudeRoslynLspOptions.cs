using System.Globalization;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Configuration;

/// <summary>
/// Every knob the adapter has, read once from environment variables. There are deliberately no
/// configuration files and no <c>Microsoft.Extensions.Configuration</c> providers: an LSP or MCP
/// client launches the binary with an environment block and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FromEnvironment()"/> never throws. A malformed number or log level falls back to the
/// documented default, because failing startup over a typo would leave the client with a dead
/// process and no way to see why — stdout is the protocol channel in both server modes, so there is
/// nowhere to complain. Anything genuinely unusable (a solution path that does not exist, a Roslyn
/// directory with no server in it) surfaces later as a live, non-crashing failure state with a
/// <c>doctor</c> hint.
/// </para>
/// <para>
/// Values that only later work packages consume are kept as trimmed strings here rather than parsed
/// into types this work package would have to invent: <see cref="Transport"/>,
/// <see cref="DiagnosticMinSeverity"/>, <see cref="WorkspaceDiagnostics"/> and
/// <see cref="RoslynOptionsJson"/> are validated where they are used (WP3/WP4), because the
/// vocabulary each one accepts is decided there. What this type guarantees for all of them is the
/// part that is easy to get wrong and impossible to see: the plugin-option precedence, and that a
/// blank value means <em>absent</em>.
/// </para>
/// </remarks>
internal sealed record ClaudeRoslynLspOptions
{
    /// <summary>Default minimum log level for this adapter's own stderr logger.</summary>
    internal const LogLevel DefaultLogLevel = LogLevel.Information;

    /// <summary>
    /// Default number of seconds a request may be held while the Roslyn workspace loads, before it
    /// is passed through unheld. 120 s matches the plugin manifest's <c>startupTimeout</c>, so a
    /// client that gives up and a server that stops waiting do so at the same moment.
    /// </summary>
    internal const int DefaultReadyTimeoutSeconds = 120;

    /// <summary>Lower and upper bounds on <see cref="ReadyTimeoutSeconds"/>.</summary>
    internal const int MinReadyTimeoutSeconds = 1;

    /// <inheritdoc cref="MinReadyTimeoutSeconds"/>
    internal const int MaxReadyTimeoutSeconds = 3600;

    /// <summary>
    /// The prefix the Claude Code plugin launcher's values arrive under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plugin manifest maps each declared option into the server's environment through a
    /// <c>${user_config.KEY}</c> placeholder, and an option the user never filled in substitutes as
    /// the <b>empty string</b> rather than being omitted. Mapping such an option straight onto
    /// <c>CLAUDE_ROSLYN_LSP_SOLUTION</c> would therefore set that variable to <c>""</c> in the child
    /// process and <em>shadow</em> a perfectly good value the user already had in their environment.
    /// </para>
    /// <para>
    /// So the manifest writes to <c>CLAUDE_PLUGIN_OPTION_*</c> instead, and these are read
    /// <b>first, in preference to</b> the plain names, with blank treated as absent. A user who
    /// fills the option in gets their value; a user who leaves it blank falls through to whatever
    /// their environment already said. Nobody sets these by hand — they are the plugin launcher's
    /// half of the contract, and the prefix is Claude Code's own convention for the same values.
    /// This is a sibling repository's hard-won lesson (sonarqube-mcp issue #1), adopted before it
    /// could be re-learned here; <c>PluginManifestTests</c> fails if the manifest is "simplified"
    /// back to the plain names.
    /// </para>
    /// </remarks>
    internal const string PluginOptionPrefix = "CLAUDE_PLUGIN_OPTION_";

    /// <summary>
    /// <c>CLAUDE_ROSLYN_LSP_SOLUTION</c> — the <c>.slnx</c>, <c>.sln</c> or <c>.csproj</c> to open.
    /// Absent means discovery decides (WP3), which is right in a single-solution repository and
    /// usually wrong in a monorepo.
    /// </summary>
    internal string? Solution { get; init; }

    /// <summary>Which variable <see cref="Solution"/> came from, or <see langword="null"/>.</summary>
    /// <remarks>Reported by <c>doctor</c> so a shadowed value is visible rather than merely wrong.</remarks>
    internal string? SolutionVariable { get; init; }

    /// <summary>
    /// <c>CLAUDE_ROSLYN_LSP_ROSLYN_PATH</c> — a directory that already holds
    /// <c>Microsoft.CodeAnalysis.LanguageServer.dll</c>, used instead of downloading one. First in
    /// the resolution chain (D2), and a value that does not resolve fails loudly rather than
    /// silently falling through to a download.
    /// </summary>
    /// <remarks>
    /// <c>CLAUDE_ROSLYN_LSP_SERVER_PATH</c> is accepted as an alias. The design document names the
    /// knob both ways; the plugin manifest and the smoke test use the <c>ROSLYN_</c> spelling, so
    /// that one is canonical and the other is read as a fallback rather than left to be a silent
    /// no-op for whoever followed the other half of the document.
    /// </remarks>
    internal string? RoslynPath { get; init; }

    /// <summary>Which variable <see cref="RoslynPath"/> came from, or <see langword="null"/>.</summary>
    internal string? RoslynPathVariable { get; init; }

    /// <summary>
    /// <c>CLAUDE_ROSLYN_LSP_ROSLYN_VERSION</c> (alias <c>CLAUDE_ROSLYN_LSP_SERVER_VERSION</c>) —
    /// overrides the pinned <c>roslyn-language-server</c> version. The pin is the only version a
    /// release is tested against, so an override is downloaded into its own cache directory, has no
    /// hash to check against and is reported by <c>doctor</c> as unverified (D25); the launcher also
    /// drops the flags only the pinned build is known to accept (D26).
    /// </summary>
    internal string? RoslynVersion { get; init; }

    /// <summary>
    /// <c>CLAUDE_ROSLYN_LSP_ROSLYN_ARGS</c> — extra arguments for the launched Roslyn process,
    /// space-separated. Exists for the smoke test, which points the launcher at this same binary's
    /// <c>fake-roslyn</c> verb so a release RID can be proven without a 70 MB download.
    /// </summary>
    internal string? RoslynArguments { get; init; }

    /// <summary>
    /// <c>CLAUDE_ROSLYN_LSP_CACHE_DIR</c> — where downloaded Roslyn servers are extracted. Moves the
    /// payloads only; logs and staged downloads stay under <see cref="Home"/> (D27).
    /// </summary>
    internal string? CacheDirectory { get; init; }

    /// <summary>
    /// <c>CLAUDE_ROSLYN_LSP_HOME</c> — the adapter's per-user state directory. The plugin manifest
    /// sets it to <c>${CLAUDE_PLUGIN_DATA}</c> so an installed plugin keeps its downloads inside its
    /// own data directory instead of a second copy under the home directory; unset, the chain is
    /// <c>CLAUDE_PLUGIN_DATA</c> and then the platform's cache location — <c>%LOCALAPPDATA%</c>,
    /// <c>~/Library/Caches</c>, <c>$XDG_CACHE_HOME</c> or <c>~/.cache</c> (D27,
    /// <see cref="Roslyn.AdapterPaths"/>).
    /// </summary>
    internal string? Home { get; init; }

    /// <summary>
    /// <c>CLAUDE_ROSLYN_LSP_OFFLINE</c> — refuse to download a Roslyn server. Turns a missing cache
    /// entry into an immediate, explained failure instead of a 70 MB fetch nobody asked for.
    /// </summary>
    internal bool Offline { get; init; }

    /// <summary>
    /// <c>CLAUDE_ROSLYN_LSP_TRANSPORT</c> — how the adapter talks to Roslyn (<c>pipe</c>, the
    /// default, or <c>stdio</c>). Parsed by
    /// <see cref="Roslyn.RoslynProcessLauncher.ParseTransport"/> (D37); an unrecognised value warns
    /// and uses the pipe rather than refusing to start.
    /// </summary>
    internal string? Transport { get; init; }

    /// <summary>
    /// <c>CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS</c> — how long requests are held while the
    /// workspace loads before they are passed through anyway.
    /// </summary>
    internal int ReadyTimeoutSeconds { get; init; } = DefaultReadyTimeoutSeconds;

    /// <summary>
    /// <c>CLAUDE_ROSLYN_LSP_DIAGNOSTICS</c> — the pull-to-push diagnostics bridge. On by default:
    /// Claude Code consumes push diagnostics only, so with this off the adapter is navigation-only.
    /// </summary>
    internal bool Diagnostics { get; init; } = true;

    /// <summary>
    /// <c>CLAUDE_ROSLYN_LSP_DIAGNOSTIC_MIN_SEVERITY</c> — the severity floor applied before
    /// diagnostics are published. Parsed in WP4; the default floor is Warning.
    /// </summary>
    internal string? DiagnosticMinSeverity { get; init; }

    /// <summary>
    /// <c>CLAUDE_ROSLYN_LSP_WORKSPACE_DIAGNOSTICS</c> — opt-in workspace-wide diagnostics for files
    /// that are not open. Off unless set, because it needs a full-solution compiler scope. Parsed in
    /// WP4.
    /// </summary>
    internal string? WorkspaceDiagnostics { get; init; }

    /// <summary>
    /// <c>CLAUDE_ROSLYN_LSP_FILE_WATCHER</c> — the file-watch bridge. On by default: without it a
    /// file created by Bash or by git never joins its project and every later answer is silently
    /// stale.
    /// </summary>
    internal bool FileWatcher { get; init; } = true;

    /// <summary><c>CLAUDE_ROSLYN_LSP_LOG_LEVEL</c> — minimum level for this adapter's stderr logger.</summary>
    internal LogLevel LogLevel { get; init; } = DefaultLogLevel;

    /// <summary>
    /// <c>CLAUDE_ROSLYN_LSP_ROSLYN_LOG_LEVEL</c> — the level handed to the Roslyn child process.
    /// <see langword="null"/> means "not configured": the default belongs to the launcher
    /// (<see cref="Roslyn.RoslynLaunchRequest.DefaultRoslynLogLevel"/>, which is <c>Warning</c>),
    /// because that is where the cost of Roslyn's vocabulary in stderr volume is known — and
    /// inventing one here would make an unset variable indistinguishable from a deliberate choice.
    /// Roslyn's own level names are the same words as this enum's, so the value crosses unchanged.
    /// </summary>
    internal LogLevel? RoslynLogLevel { get; init; }

    /// <summary>
    /// <c>CLAUDE_ROSLYN_LSP_OPTIONS</c> — a JSON object merged into the answers the adapter gives
    /// Roslyn's <c>workspace/configuration</c> requests. Kept as raw text here: it is parsed where it
    /// is consumed (WP4), and a syntax error there is a logged warning, never a startup failure.
    /// </summary>
    internal string? RoslynOptionsJson { get; init; }

    /// <summary>Reads the options from the process environment.</summary>
    internal static ClaudeRoslynLspOptions FromEnvironment() =>
        FromEnvironment(static name => Environment.GetEnvironmentVariable(name));

    /// <summary>
    /// Reads the options from an arbitrary variable source. Tests use this overload so they never
    /// have to mutate the (process-wide, test-parallelism-hostile) real environment.
    /// </summary>
    /// <param name="read">Returns the raw value of a variable, or <see langword="null"/> if unset.</param>
    internal static ClaudeRoslynLspOptions FromEnvironment(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var solution = ReadConfiguredWithSource(read, "CLAUDE_ROSLYN_LSP_SOLUTION");
        var roslynPath = ReadAliasedWithSource(read, "CLAUDE_ROSLYN_LSP_ROSLYN_PATH", "CLAUDE_ROSLYN_LSP_SERVER_PATH");

        return new ClaudeRoslynLspOptions
        {
            Solution = solution.Value,
            SolutionVariable = solution.Source,
            RoslynPath = roslynPath.Value,
            RoslynPathVariable = roslynPath.Source,
            RoslynVersion = ReadAliasedWithSource(
                read, "CLAUDE_ROSLYN_LSP_ROSLYN_VERSION", "CLAUDE_ROSLYN_LSP_SERVER_VERSION").Value,
            RoslynArguments = ReadConfigured(read, "CLAUDE_ROSLYN_LSP_ROSLYN_ARGS"),
            CacheDirectory = ReadConfigured(read, "CLAUDE_ROSLYN_LSP_CACHE_DIR"),
            Home = ReadConfigured(read, "CLAUDE_ROSLYN_LSP_HOME"),
            Offline = ReadBoolean(read, "CLAUDE_ROSLYN_LSP_OFFLINE", defaultValue: false),
            Transport = ReadConfigured(read, "CLAUDE_ROSLYN_LSP_TRANSPORT"),
            ReadyTimeoutSeconds = ReadInt32(
                read,
                "CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS",
                DefaultReadyTimeoutSeconds,
                MinReadyTimeoutSeconds,
                MaxReadyTimeoutSeconds),
            Diagnostics = ReadBoolean(read, "CLAUDE_ROSLYN_LSP_DIAGNOSTICS", defaultValue: true),
            DiagnosticMinSeverity = ReadConfigured(read, "CLAUDE_ROSLYN_LSP_DIAGNOSTIC_MIN_SEVERITY"),
            WorkspaceDiagnostics = ReadConfigured(read, "CLAUDE_ROSLYN_LSP_WORKSPACE_DIAGNOSTICS"),
            FileWatcher = ReadBoolean(read, "CLAUDE_ROSLYN_LSP_FILE_WATCHER", defaultValue: true),
            LogLevel = ReadLogLevel(read, "CLAUDE_ROSLYN_LSP_LOG_LEVEL") ?? DefaultLogLevel,
            RoslynLogLevel = ReadLogLevel(read, "CLAUDE_ROSLYN_LSP_ROSLYN_LOG_LEVEL"),

            // Deliberately not trimmed of anything but surrounding whitespace: it is a JSON document
            // and its own parser is the only thing entitled to an opinion about its contents.
            RoslynOptionsJson = ReadConfigured(read, "CLAUDE_ROSLYN_LSP_OPTIONS"),
        };
    }

    /// <summary>
    /// Reads a variable the Claude Code plugin manifest also writes, preferring the plugin's value
    /// and falling back to the plain name when the plugin left it blank.
    /// </summary>
    /// <remarks>
    /// Blank means <em>absent</em> here, not "configured as empty" — see
    /// <see cref="PluginOptionPrefix"/> for why that distinction is the whole point.
    /// </remarks>
    /// <param name="read">The environment reader.</param>
    /// <param name="name">The plain variable name, which is also the option's suffix.</param>
    private static string? ReadConfigured(Func<string, string?> read, string name) =>
        ReadConfiguredWithSource(read, name).Value;

    /// <summary>The same read, reporting <em>which</em> variable supplied the value.</summary>
    /// <remarks>
    /// Only the two values <c>doctor</c> has to explain need this. "Set" without "set from where" is
    /// exactly the report that makes a shadowed plugin option take an afternoon to find.
    /// </remarks>
    /// <param name="read">The environment reader.</param>
    /// <param name="name">The plain variable name.</param>
    private static (string? Value, string? Source) ReadConfiguredWithSource(Func<string, string?> read, string name)
    {
        if (ReadString(read, PluginOptionPrefix + name) is { } fromPlugin)
        {
            return (fromPlugin, PluginOptionPrefix + name);
        }

        return ReadString(read, name) is { } plain ? (plain, name) : (null, null);
    }

    /// <summary>
    /// Reads a value that has two accepted spellings, canonical first, each through the plugin-option
    /// layer.
    /// </summary>
    /// <param name="read">The environment reader.</param>
    /// <param name="canonical">The name the manifest and the documentation use.</param>
    /// <param name="alias">The older spelling, still honoured so it is not a silent no-op.</param>
    private static (string? Value, string? Source) ReadAliasedWithSource(
        Func<string, string?> read,
        string canonical,
        string alias)
    {
        var primary = ReadConfiguredWithSource(read, canonical);
        return primary.Value is not null ? primary : ReadConfiguredWithSource(read, alias);
    }

    /// <summary>Trims, and normalises an unset or all-whitespace variable to <see langword="null"/>.</summary>
    private static string? ReadString(Func<string, string?> read, string name)
    {
        var raw = read(name);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    /// <summary>Parses an integer, falling back to <paramref name="defaultValue"/> when unparsable or out of range.</summary>
    private static int ReadInt32(Func<string, string?> read, string name, int defaultValue, int min, int max)
    {
        var raw = ReadConfigured(read, name);

        if (raw is null || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return defaultValue;
        }

        return value < min || value > max ? defaultValue : value;
    }

    /// <summary>
    /// Parses a boolean flag. <c>1/true/yes/on</c> and <c>0/false/no/off</c> are accepted (either
    /// case); anything else falls back to <paramref name="defaultValue"/>.
    /// </summary>
    private static bool ReadBoolean(Func<string, string?> read, string name, bool defaultValue)
    {
        var raw = ReadConfigured(read, name);

        return raw?.ToUpperInvariant() switch
        {
            "1" or "TRUE" or "YES" or "ON" => true,
            "0" or "FALSE" or "NO" or "OFF" => false,
            _ => defaultValue,
        };
    }

    /// <summary>
    /// Parses a <see cref="Microsoft.Extensions.Logging.LogLevel"/> name, or
    /// <see langword="null"/> when the variable is unset or unrecognised.
    /// </summary>
    private static LogLevel? ReadLogLevel(Func<string, string?> read, string name)
    {
        var raw = ReadConfigured(read, name);

        // Enum.TryParse also accepts raw numbers, so IsDefined is what actually rejects "42" —
        // which would otherwise become a LogLevel of 42 and silently suppress every log line.
        if (raw is null || !Enum.TryParse<LogLevel>(raw, ignoreCase: true, out var level) || !Enum.IsDefined(level))
        {
            return null;
        }

        return level;
    }
}
