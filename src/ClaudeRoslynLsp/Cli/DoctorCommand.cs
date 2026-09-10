using System.Globalization;
using System.Text;
using System.Text.Json;

using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Roslyn;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Cli;

/// <summary>
/// <c>claude-roslyn-lsp doctor</c> and <c>claude-roslyn-lsp install</c> — the whole support surface.
/// </summary>
/// <remarks>
/// <para>
/// The report is deliberately in the order a failure is diagnosed in, not the order the code runs in:
/// which adapter, on what platform; which <c>dotnet</c> and which runtimes; where Roslyn was looked
/// for and what won; where the cache is and whether there is room; which solution would be opened and
/// why that one; whether anything else in Claude Code is already claiming C#. Then it launches the
/// thing and talks to it, because every static check above can pass on a machine where Roslyn still
/// does not start.
/// </para>
/// <para>
/// <b>D43 — <c>doctor</c> never downloads; <c>install</c> is the same command with the download
/// allowed.</b> A diagnostic that quietly spends 70 MB of somebody's tethered connection is a
/// diagnostic they will not run again. So the two verbs are one implementation with one flag, and
/// <c>doctor</c> on a machine with nothing installed exits 1 and says which command to type — which
/// is also exactly what the exit code should mean: 0 iff Roslyn is runnable right now.
/// </para>
/// <para>
/// This is a <c>Cli/</c> file, so stdout is legitimate here — and only here (hard rule 5). Everything
/// the report prints goes to stdout so it can be piped and pasted into an issue; the logger, which
/// carries the acquisition records, goes to stderr as everywhere else.
/// </para>
/// </remarks>
internal static class DoctorCommand
{
    /// <summary>How long the live handshake is given, end to end.</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Runs <c>doctor</c> or <c>install</c> and returns the process exit code.</summary>
    /// <param name="arguments">The arguments after the verb.</param>
    /// <param name="fix">
    /// Whether a missing Roslyn may be downloaded. <see langword="true"/> for <c>install</c> and for
    /// <c>doctor --fix</c>.
    /// </param>
    internal static async Task<int> RunAsync(string[] arguments, bool fix)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var json = false;

        foreach (var argument in arguments)
        {
            switch (argument)
            {
                case "--json":
                    json = true;
                    break;

                case "--fix":
                    fix = true;
                    break;

                default:
                    CliRuntime.WriteError($"{ServerVersion.Name}: unknown option '{argument}' for this verb.");
                    return CliDispatcher.ExitUsage;
            }
        }

        var options = ClaudeRoslynLspOptions.FromEnvironment();

        // The report's own progress goes to stderr through the logger, so --json keeps stdout to one
        // JSON document even while a 70 MB download is running underneath it.
        using var loggerFactory = CliRuntime.CreateLoggerFactory(
            json ? LogLevel.Warning : options.LogLevel);

        var logger = loggerFactory.CreateLogger("doctor");
        var report = await GatherAsync(options, logger, fix, json).ConfigureAwait(false);

        CliRuntime.WriteOut(json ? RenderJson(report) : RenderText(report));

        return report.Ok ? CliDispatcher.ExitSuccess : CliDispatcher.ExitFailure;
    }

    private static async Task<DoctorReport> GatherAsync(
        ClaudeRoslynLspOptions options,
        ILogger logger,
        bool fix,
        bool json)
    {
        var paths = AdapterPaths.Resolve(options);
        var host = DotnetHostLocator.Resolve();

        var locator = new RoslynServerLocator(
            options,
            paths,
            logger,
            downloaderFactory: () => new NuGetPayloadDownloader(
                NuGetPayloadDownloader.CreateHttpClient(), paths, logger));

        var progress = json
            ? null
            : new Progress<RoslynDownloadProgress>(static report => CliRuntime.WriteError("  " + report.Describe()));

        var resolution = await locator.ResolveAsync(fix, progress).ConfigureAwait(false);

        var solution = SolutionDiscovery.Discover(
            Environment.CurrentDirectory,
            options.Solution,
            options.SolutionVariable);

        RoslynHandshakeResult? handshake = null;

        if (resolution.IsResolved && (host.IsUsable || resolution.LaunchKind == RoslynLaunchKind.Native))
        {
            using var guard = ChildProcessGuard.Create(logger);
            var launcher = new RoslynProcessLauncher(logger, guard);

            handshake = await RoslynHandshakeProbe
                .RunAsync(
                    launcher,
                    RoslynLaunchRequest.FromOptions(options, resolution, host, paths, logger),
                    HandshakeTimeout)
                .ConfigureAwait(false);
        }

        return new DoctorReport
        {
            Options = options,
            Paths = paths,
            FreeBytes = TryGetFreeSpace(paths.RoslynCacheRoot),
            Host = host,
            Locator = locator,
            Resolution = resolution,
            Solution = solution,
            Integration = IntegrationState.Read(),
            Handshake = handshake,
            Fix = fix,
        };
    }

    private static string RenderText(DoctorReport report)
    {
        var text = new StringBuilder();

        text.Append(ServerVersion.Name).Append(' ').Append(ServerVersion.Value).AppendLine(" doctor");
        text.AppendLine();

        Section(text, "Adapter");
        Item(text, "version", ServerVersion.Value);
        Item(text, "runtime identifier", report.Rid + (report.RidSupported ? "" : "  (no Roslyn payload is published for it)"));
        Item(text, "operating system", Environment.OSVersion.VersionString);
        Item(text, "home", report.Paths.Home + "  (from " + report.Paths.HomeSource + ")");
        text.AppendLine();

        Section(text, ".NET host");

        if (report.Host.Path is null)
        {
            Item(text, "dotnet", "not found");
        }
        else
        {
            Item(text, "dotnet", report.Host.Path + "  (from " + report.Host.Source + ")");
        }

        foreach (var runtime in report.Host.Runtimes)
        {
            Item(text, runtime.IsNet10 ? "runtime *" : "runtime", runtime.Name + " " + runtime.Version);
        }

        if (report.Host.Runtimes.Count > 0)
        {
            Item(text, "selected", report.Host.SelectedRuntime?.ToString() ?? "none - no 10.x runtime");
        }

        if (report.Host.Failure is { } hostFailure)
        {
            Item(text, "problem", hostFailure);

            foreach (var step in report.Host.Chain)
            {
                Item(text, "looked at", step);
            }
        }

        text.AppendLine();

        Section(text, "Roslyn");
        Item(text, "pinned version", RoslynServerManifest.Version + "  (pin generated " + RoslynServerManifest.PinGenerated + ")");

        if (!report.Locator.IsPinnedVersion)
        {
            Item(text, "requested version", report.Locator.Version + "  (CLAUDE_ROSLYN_LSP_ROSLYN_VERSION overrides the pin)");
        }

        foreach (var step in report.Resolution.Chain)
        {
            Item(text, Mark(step.Outcome) + " " + step.Source, step.Detail);
        }

        if (report.Resolution.IsResolved)
        {
            Item(text, "using", report.Resolution.LaunchTarget!);
            Item(text, "hash-verified", report.Resolution.Verified ? "yes" : "no - see the chain above");
        }
        else if (report.Resolution.Failure is { } failure)
        {
            Item(text, "problem", failure);
        }

        Item(text, "cache", report.Paths.RoslynCacheRoot + FormatFreeSpace(report.FreeBytes));
        text.AppendLine();

        Section(text, "Solution");
        Item(text, "working directory", report.Solution.Root);
        Item(text, "decision", report.Solution.Explanation);

        if (report.Solution.Failure is { } solutionFailure)
        {
            Item(text, "problem", solutionFailure);
        }

        foreach (var candidate in report.Solution.Candidates)
        {
            Item(
                text,
                candidate.Path == report.Solution.SolutionPath ? "candidate *" : "candidate",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{candidate.Path}  score {candidate.Score} ({candidate.Reason})"));
        }

        foreach (var project in report.Solution.ProjectPaths)
        {
            Item(text, "project", project);
        }

        text.AppendLine();

        Section(text, "Claude Code integration");
        Item(text, "settings", report.Integration.SettingsPath);
        Item(text, "ENABLE_LSP_TOOL", report.Integration.DescribeLspTool());
        Item(text, "official csharp-lsp plugin", report.Integration.DescribeCsharpLsp());

        if (report.Integration.CsharpLspEnabled == true)
        {
            Item(
                text,
                "warning",
                "conflict: the first plugin registered for .cs wins, so whichever of the two Claude Code loads " +
                "first is the one that answers. Disable csharp-lsp to make this adapter's registration decisive.");
        }

        text.AppendLine();

        Section(text, "Handshake");

        if (report.Handshake is null)
        {
            Item(text, "skipped", report.Resolution.IsResolved
                ? "no usable .NET host"
                : "no Roslyn server resolved");
        }
        else
        {
            var handshake = report.Handshake;

            Item(text, "transport", handshake.Transport.ToString().ToLowerInvariant());

            if (handshake.Succeeded)
            {
                Item(text, "server", (handshake.ServerName ?? "(no serverInfo.name)")
                                     + (handshake.ServerVersion is { } v ? " " + v : " (no version reported)"));
                Item(text, "connect", Milliseconds(handshake.ConnectElapsed));
                Item(text, "initialize", Milliseconds(handshake.InitializeRoundTrip));
                Item(text, "shutdown", Milliseconds(handshake.ShutdownElapsed)
                                       + (handshake.ExitCode is { } code
                                           ? $", exit code {code}"
                                           : ", did not exit in time"));

                if (handshake.PeakWorkingSet is { } peak)
                {
                    Item(text, "peak working set", (peak / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB");
                }

                Item(text, "capabilities", handshake.Capabilities.Count == 0
                    ? "(none advertised)"
                    : string.Join(", ", handshake.Capabilities));
            }

            if (handshake.Failure is { } handshakeFailure)
            {
                Item(text, "problem", handshakeFailure);
            }

            foreach (var line in handshake.LogMessages)
            {
                Item(text, "[roslyn]", line);
            }
        }

        text.AppendLine();
        text.AppendLine(report.Ok
            ? "OK: Roslyn is installed, starts, and answers `initialize`."
            : "FAILED: " + report.Verdict);

        return text.ToString().TrimEnd();
    }

    private static string RenderJson(DoctorReport report)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", report.Ok);
            writer.WriteString("adapter", ServerVersion.Name);
            writer.WriteString("version", ServerVersion.Value);
            writer.WriteString("runtimeIdentifier", report.Rid);
            writer.WriteBoolean("runtimeIdentifierSupported", report.RidSupported);
            writer.WriteString("home", report.Paths.Home);
            writer.WriteString("homeSource", report.Paths.HomeSource);
            writer.WriteString("cacheDirectory", report.Paths.RoslynCacheRoot);

            if (report.FreeBytes is { } free)
            {
                writer.WriteNumber("cacheFreeBytes", free);
            }

            writer.WriteStartObject("dotnet");
            WriteNullableString(writer, "path", report.Host.Path);
            WriteNullableString(writer, "source", report.Host.Source);
            WriteNullableString(writer, "selectedRuntime", report.Host.SelectedRuntime?.Version);
            WriteNullableString(writer, "failure", report.Host.Failure);
            writer.WriteStartArray("runtimes");

            foreach (var runtime in report.Host.Runtimes)
            {
                writer.WriteStartObject();
                writer.WriteString("name", runtime.Name);
                writer.WriteString("version", runtime.Version);
                writer.WriteString("location", runtime.Location);
                writer.WriteBoolean("isNet10", runtime.IsNet10);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteStartObject("roslyn");
            writer.WriteString("pinnedVersion", RoslynServerManifest.Version);
            writer.WriteString("pinGenerated", RoslynServerManifest.PinGenerated);
            writer.WriteString("requestedVersion", report.Locator.Version);
            writer.WriteString("source", report.Resolution.Kind.ToString());
            WriteNullableString(writer, "directory", report.Resolution.Directory);
            WriteNullableString(writer, "launchTarget", report.Resolution.LaunchTarget);
            writer.WriteBoolean("verified", report.Resolution.Verified);
            WriteNullableString(writer, "failure", report.Resolution.Failure);
            writer.WriteStartArray("chain");

            foreach (var step in report.Resolution.Chain)
            {
                writer.WriteStartObject();
                writer.WriteString("source", step.Source);
                writer.WriteString("detail", step.Detail);
                writer.WriteString("outcome", step.Outcome.ToString());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteStartObject("solution");
            writer.WriteString("root", report.Solution.Root);
            writer.WriteString("outcome", report.Solution.Outcome.ToString());
            WriteNullableString(writer, "path", report.Solution.SolutionPath);
            WriteNullableString(writer, "failure", report.Solution.Failure);
            writer.WriteString("explanation", report.Solution.Explanation);
            writer.WriteStartArray("candidates");

            foreach (var candidate in report.Solution.Candidates)
            {
                writer.WriteStartObject();
                writer.WriteString("path", candidate.Path);
                writer.WriteNumber("score", candidate.Score);
                writer.WriteNumber("projectCount", candidate.ProjectCount);
                writer.WriteNumber("depth", candidate.Depth);
                writer.WriteString("reason", candidate.Reason);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("projects");

            foreach (var project in report.Solution.ProjectPaths)
            {
                writer.WriteStringValue(project);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteStartObject("claudeCode");
            writer.WriteString("settingsPath", report.Integration.SettingsPath);
            WriteNullableBoolean(writer, "enableLspTool", report.Integration.EnableLspTool);
            WriteNullableBoolean(writer, "csharpLspPluginEnabled", report.Integration.CsharpLspEnabled);
            writer.WriteEndObject();

            if (report.Handshake is { } handshake)
            {
                writer.WriteStartObject("handshake");
                writer.WriteBoolean("succeeded", handshake.Succeeded);
                writer.WriteString("transport", handshake.Transport.ToString());
                WriteNullableString(writer, "serverName", handshake.ServerName);
                WriteNullableString(writer, "serverVersion", handshake.ServerVersion);
                writer.WriteNumber("connectMilliseconds", (long)handshake.ConnectElapsed.TotalMilliseconds);
                writer.WriteNumber("initializeMilliseconds", (long)handshake.InitializeRoundTrip.TotalMilliseconds);
                writer.WriteNumber("shutdownMilliseconds", (long)handshake.ShutdownElapsed.TotalMilliseconds);

                if (handshake.ExitCode is { } code)
                {
                    writer.WriteNumber("exitCode", code);
                }

                if (handshake.PeakWorkingSet is { } peak)
                {
                    writer.WriteNumber("peakWorkingSetBytes", peak);
                }

                WriteNullableString(writer, "failure", handshake.Failure);
                writer.WriteStartArray("capabilities");

                foreach (var capability in handshake.Capabilities)
                {
                    writer.WriteStringValue(capability);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteNullableBoolean(Utf8JsonWriter writer, string name, bool? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteBoolean(name, value.Value);
        }
    }

    private static void Section(StringBuilder text, string title) =>
        text.Append(title).AppendLine(":");

    private static void Item(StringBuilder text, string label, string value) =>
        text.Append("  ").Append(label.PadRight(28)).Append(' ').AppendLine(value);

    private static string Mark(RoslynResolutionStepOutcome outcome) => outcome switch
    {
        RoslynResolutionStepOutcome.Used => "*",
        RoslynResolutionStepOutcome.Rejected => "!",
        _ => "-",
    };

    private static string Milliseconds(TimeSpan value) =>
        value.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms";

    private static string FormatFreeSpace(long? free) =>
        free is { } bytes
            ? string.Create(CultureInfo.InvariantCulture, $"  ({bytes / (1024 * 1024 * 1024)} GB free)")
            : string.Empty;

    private static long? TryGetFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));

            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or IOException
                                              or UnauthorizedAccessException
                                              or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Everything the report knows, gathered before anything is printed.</summary>
    /// <remarks>
    /// Split from rendering so that the text and the <c>--json</c> forms cannot drift: two renderers
    /// over one record is the only arrangement in which "the JSON says something the text does not"
    /// is impossible rather than merely unlikely.
    /// </remarks>
    private sealed record DoctorReport
    {
        internal required ClaudeRoslynLspOptions Options { get; init; }

        internal required AdapterPaths Paths { get; init; }

        internal required long? FreeBytes { get; init; }

        internal required DotnetHostResult Host { get; init; }

        internal required RoslynServerLocator Locator { get; init; }

        internal required RoslynResolution Resolution { get; init; }

        internal required SolutionDiscoveryResult Solution { get; init; }

        internal required IntegrationState Integration { get; init; }

        internal required RoslynHandshakeResult? Handshake { get; init; }

        internal required bool Fix { get; init; }

        internal string Rid => Locator.RuntimeIdentifier;

        internal bool RidSupported => RuntimeIdentifier.IsSupported(Rid);

        /// <summary>Green only when a real Roslyn started and answered.</summary>
        internal bool Ok => Handshake?.Succeeded == true;

        /// <summary>The one line that says what to do next.</summary>
        internal string Verdict
        {
            get
            {
                // Each failure already carries its own next step; appending a generic one here would
                // print the same sentence twice, which reads as a report that is not sure.
                if (Resolution.Failure is { } failure)
                {
                    return failure;
                }

                if (Handshake?.Failure is { } handshakeFailure)
                {
                    return handshakeFailure;
                }

                return Host.Failure ?? "Roslyn could not be started; see the sections above.";
            }
        }
    }

    /// <summary>
    /// What Claude Code's own configuration says about language servers on this machine.
    /// </summary>
    /// <remarks>
    /// Read tolerantly and reported, never changed. Both files belong to Claude Code, both are
    /// undocumented as file formats, and both can be absent on a perfectly working machine — so every
    /// question here has three answers (yes, no, could not tell), and "could not tell" is printed as
    /// such instead of being rounded to "no".
    /// </remarks>
    private sealed record IntegrationState
    {
        internal required string SettingsPath { get; init; }

        internal bool? EnableLspTool { get; init; }

        internal bool? CsharpLspEnabled { get; init; }

        internal static IntegrationState Read()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var settings = Path.Combine(home, ".claude", "settings.json");
            var plugins = Path.Combine(home, ".claude", "plugins", "installed_plugins.json");

            return new IntegrationState
            {
                SettingsPath = settings,
                EnableLspTool = ReadEnableLspTool(settings),
                CsharpLspEnabled = ReadCsharpLsp(plugins),
            };
        }

        internal string DescribeLspTool() => EnableLspTool switch
        {
            true => "set - Claude Code's language-server tool is enabled",
            false => "set to a false value - the language-server tool is off, so the `lsp` verb has no client",
            _ => "not set (Claude Code's default applies)",
        };

        internal string DescribeCsharpLsp() => CsharpLspEnabled switch
        {
            true => "enabled",
            false => "installed but disabled",
            _ => "not installed, or the plugin list could not be read",
        };

        private static bool? ReadEnableLspTool(string path)
        {
            using var document = TryParse(path);

            if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Claude Code carries process-level switches under `env`, but a top-level key is a shape
            // people write by hand and it costs nothing to honour.
            foreach (var container in new[] { document.RootElement, Child(document.RootElement, "env") })
            {
                if (container.ValueKind == JsonValueKind.Object
                    && container.TryGetProperty("ENABLE_LSP_TOOL", out var value))
                {
                    return value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.String => value.GetString() is not ("0" or "false" or "off" or ""),
                        _ => true,
                    };
                }
            }

            return null;
        }

        private static bool? ReadCsharpLsp(string path)
        {
            using var document = TryParse(path);

            return document is null ? null : FindPlugin(document.RootElement, depth: 0);
        }

        /// <summary>
        /// Walks the plugin list looking for anything named after the official C# plugin.
        /// </summary>
        /// <remarks>
        /// A shape-tolerant walk rather than a model of the file, because the file is Claude Code's and
        /// its layout has moved between releases. What is stable is that the plugin is identified by a
        /// name containing <c>csharp-lsp</c> and that its state is either a boolean or an object with an
        /// <c>enabled</c> member; anything else answers "could not tell", which is honest.
        /// </remarks>
        private static bool? FindPlugin(JsonElement element, int depth)
        {
            if (depth > 6)
            {
                return null;
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Name.Contains("csharp-lsp", StringComparison.OrdinalIgnoreCase))
                        {
                            return property.Value.ValueKind switch
                            {
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                JsonValueKind.Object when property.Value.TryGetProperty("enabled", out var enabled)
                                    => enabled.ValueKind == JsonValueKind.True,
                                JsonValueKind.Object => true,
                                _ => null,
                            };
                        }

                        if (FindPlugin(property.Value, depth + 1) is { } fromValue)
                        {
                            return fromValue;
                        }
                    }

                    return null;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String
                            && item.GetString()?.Contains("csharp-lsp", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            return true;
                        }

                        if (FindPlugin(item, depth + 1) is { } fromItem)
                        {
                            return fromItem;
                        }
                    }

                    return null;

                default:
                    return null;
            }
        }

        private static JsonElement Child(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value : default;

        private static JsonDocument? TryParse(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return JsonDocument.Parse(
                    File.ReadAllText(path),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
