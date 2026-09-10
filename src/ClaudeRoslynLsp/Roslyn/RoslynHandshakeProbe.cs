using System.Diagnostics;
using System.Text.Json;

using ClaudeRoslynLsp.Protocol;

namespace ClaudeRoslynLsp.Roslyn;

/// <summary>What a handshake against a real Roslyn established.</summary>
internal sealed record RoslynHandshakeResult
{
    /// <summary>Whether <c>initialize</c>, <c>shutdown</c> and <c>exit</c> all completed.</summary>
    internal bool Succeeded { get; init; }

    /// <summary>The transport that was used.</summary>
    internal RoslynTransport Transport { get; init; }

    /// <summary>The child's process id, for a report that has to be correlated with a task manager.</summary>
    internal int ProcessId { get; init; }

    /// <summary><c>serverInfo.name</c>, which the pinned build gives as its factory class name.</summary>
    internal string? ServerName { get; init; }

    /// <summary><c>serverInfo.version</c>, which the pinned build does not send at all.</summary>
    internal string? ServerVersion { get; init; }

    /// <summary>The capability keys the server advertised, sorted.</summary>
    internal IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>How long the <c>initialize</c> request took to answer.</summary>
    internal TimeSpan InitializeRoundTrip { get; init; }

    /// <summary>How long from process start to a connected channel.</summary>
    internal TimeSpan ConnectElapsed { get; init; }

    /// <summary>How long from <c>exit</c> to the process ending.</summary>
    internal TimeSpan ShutdownElapsed { get; init; }

    /// <summary>The child's exit code, when it exited within the budget.</summary>
    internal int? ExitCode { get; init; }

    /// <summary>The child's peak working set in bytes, when it could be sampled.</summary>
    internal long? PeakWorkingSet { get; init; }

    /// <summary>
    /// The <c>window/logMessage</c> lines the server sent during the handshake.
    /// </summary>
    /// <remarks>
    /// Collected because that is where Roslyn's own logging actually goes:
    /// <c>--extensionLogDirectory</c> wrote nothing at all in the pinned build (C6), so a report that
    /// only offered to point at a log directory would be pointing at an empty one.
    /// </remarks>
    internal IReadOnlyList<string> LogMessages { get; init; } = [];

    /// <summary>Why the handshake did not complete.</summary>
    internal string? Failure { get; init; }
}

/// <summary>
/// Starts Roslyn, completes an <c>initialize</c>/<c>shutdown</c>/<c>exit</c> exchange with it, and
/// reports what it said and how long it took.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes <c>doctor</c> a diagnosis rather than an inventory. Every static check it
/// performs — the RID, the host, the runtimes, the hash — can pass on a machine where Roslyn still
/// does not start, because the thing that stops it is usually none of those: a security product that
/// blocks the child, a pipe policy, a runtime that lists itself and then fails to load. One real
/// handshake settles all of them at once, and its round-trip time is the number that says whether a
/// slow session is slow here or slow upstream.
/// </para>
/// <para>
/// The exchange stops short of <c>initialized</c> on purpose. That notification is what makes Roslyn
/// start registering capabilities and loading the solution, and a probe has no business paying for a
/// solution load or leaving 135 file watchers behind (C32).
/// </para>
/// </remarks>
internal static class RoslynHandshakeProbe
{
    /// <summary>How long the child is given to exit after <c>exit</c>.</summary>
    internal static readonly TimeSpan ExitBudget = TimeSpan.FromSeconds(5);

    private const int InitializeId = 1;
    private const int ShutdownId = 2;

    /// <summary>Runs the whole exchange and disposes the connection.</summary>
    /// <param name="launcher">The launcher to start Roslyn with.</param>
    /// <param name="request">What to start.</param>
    /// <param name="timeout">How long the whole exchange may take.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    internal static async Task<RoslynHandshakeResult> RunAsync(
        IRoslynLauncher launcher,
        RoslynLaunchRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(request);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);

        var connectWatch = Stopwatch.StartNew();
        RoslynConnection connection;

        try
        {
            connection = await launcher.LaunchAsync(request, budget.Token).ConfigureAwait(false);
        }
        catch (RoslynAcquisitionException exception)
        {
            return new RoslynHandshakeResult { Transport = request.Transport, Failure = exception.Message };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RoslynHandshakeResult
            {
                Transport = request.Transport,
                Failure = $"Roslyn did not start within {timeout.TotalSeconds:F0} seconds.",
            };
        }

        connectWatch.Stop();

        await using (connection.ConfigureAwait(false))
        {
            try
            {
                return await ExchangeAsync(connection, connectWatch.Elapsed, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new RoslynHandshakeResult
                {
                    Transport = connection.Transport,
                    ProcessId = SafeProcessId(connection),
                    ConnectElapsed = connectWatch.Elapsed,
                    Failure = $"Roslyn did not complete the handshake within {timeout.TotalSeconds:F0} seconds.",
                };
            }
            catch (LspProtocolException exception)
            {
                return new RoslynHandshakeResult
                {
                    Transport = connection.Transport,
                    ProcessId = SafeProcessId(connection),
                    ConnectElapsed = connectWatch.Elapsed,
                    Failure = "Roslyn's output is not well-framed LSP: " + exception.Message,
                };
            }
            catch (IOException exception)
            {
                return new RoslynHandshakeResult
                {
                    Transport = connection.Transport,
                    ProcessId = SafeProcessId(connection),
                    ConnectElapsed = connectWatch.Elapsed,
                    Failure = "The channel to Roslyn closed during the handshake: " + exception.Message,
                };
            }
        }
    }

    /// <summary>
    /// Builds the smallest <c>initialize</c> that a probe can send.
    /// </summary>
    /// <remarks>
    /// Written with a <see cref="Utf8JsonWriter"/> rather than through a serialisable record because
    /// it is one document that is never read back: adding four nested types to
    /// <c>LspJsonContext</c> would put a probe's private shape into the contract WP4's authored
    /// capability document lives in.
    /// <para>
    /// <c>processId</c> is real, and load-bearing: Roslyn watches it and exits when it dies, which is
    /// what stops a probe that is killed mid-run from leaving a server behind.
    /// </para>
    /// <para>
    /// <c>capabilities</c> is empty on purpose. Declaring <c>didChangeWatchedFiles</c> would make
    /// Roslyn register 135-149 watchers (C32); declaring diagnostics would start a pull cycle. A probe
    /// asks whether the server starts and talks, and nothing else.
    /// </para>
    /// </remarks>
    /// <param name="rootPath">The workspace root to report, or <see langword="null"/>.</param>
    internal static byte[] BuildInitializeRequest(string? rootPath)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", JsonRpc.Version);
            writer.WriteNumber("id", InitializeId);
            writer.WriteString("method", "initialize");

            writer.WriteStartObject("params");
            writer.WriteNumber("processId", Environment.ProcessId);

            if (rootPath is not null)
            {
                writer.WriteString("rootUri", new Uri(rootPath).AbsoluteUri);
            }
            else
            {
                writer.WriteNull("rootUri");
            }

            writer.WriteStartObject("clientInfo");
            writer.WriteString("name", ServerVersion.Name + " doctor");
            writer.WriteString("version", ServerVersion.Value);
            writer.WriteEndObject();

            writer.WriteStartObject("capabilities");
            writer.WriteEndObject();

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static async Task<RoslynHandshakeResult> ExchangeAsync(
        RoslynConnection connection,
        TimeSpan connectElapsed,
        CancellationToken cancellationToken)
    {
        var reader = new LspFrameReader(connection.Stream);
        using var writer = new LspFrameWriter(connection.Stream);
        var logs = new List<string>();

        var initializeWatch = Stopwatch.StartNew();
        await writer.WriteFrameAsync(BuildInitializeRequest(rootPath: null), cancellationToken).ConfigureAwait(false);

        var initialize = await ReadResponseAsync(reader, InitializeId, logs, cancellationToken).ConfigureAwait(false);
        initializeWatch.Stop();

        if (initialize is null)
        {
            return new RoslynHandshakeResult
            {
                Transport = connection.Transport,
                ProcessId = SafeProcessId(connection),
                ConnectElapsed = connectElapsed,
                LogMessages = logs,
                Failure = "Roslyn closed the channel without answering `initialize`.",
            };
        }

        using (initialize)
        {
            var (name, version, capabilities) = ReadServerInfo(initialize.RootElement);

            // Sampled here, while the child is still alive: once it has exited, every counter on
            // Process throws rather than reporting the last value it had.
            var peak = SafePeakWorkingSet(connection);

            await writer.WriteFrameAsync(BuildRequest(ShutdownId, "shutdown"), cancellationToken).ConfigureAwait(false);
            using (await ReadResponseAsync(reader, ShutdownId, logs, cancellationToken).ConfigureAwait(false))
            {
            }

            var shutdownWatch = Stopwatch.StartNew();
            await writer.WriteFrameAsync(BuildNotification("exit"), cancellationToken).ConfigureAwait(false);

            var exited = await Task.WhenAny(connection.Exited, Task.Delay(ExitBudget, cancellationToken))
                .ConfigureAwait(false) == connection.Exited;

            shutdownWatch.Stop();

            return new RoslynHandshakeResult
            {
                Succeeded = true,
                Transport = connection.Transport,
                ProcessId = SafeProcessId(connection),
                ServerName = name,
                ServerVersion = version,
                Capabilities = capabilities,
                InitializeRoundTrip = initializeWatch.Elapsed,
                ConnectElapsed = connectElapsed,
                ShutdownElapsed = shutdownWatch.Elapsed,
                ExitCode = exited ? await connection.Exited.ConfigureAwait(false) : null,
                PeakWorkingSet = peak,
                LogMessages = logs,
                Failure = exited
                    ? null
                    : $"Roslyn did not exit within {ExitBudget.TotalSeconds:F0} seconds of `exit`; it was killed.",
            };
        }
    }

    /// <summary>
    /// Reads frames until the response to <paramref name="id"/> arrives, collecting log messages and
    /// discarding everything else.
    /// </summary>
    private static async Task<JsonDocument?> ReadResponseAsync(
        LspFrameReader reader,
        int id,
        List<string> logs,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false) is { } body)
        {
            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                continue;
            }

            var root = document.RootElement;

            if (root.TryGetProperty("id", out var element)
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetInt32(out var value)
                && value == id)
            {
                return document;
            }

            if (root.TryGetProperty("method", out var method)
                && method.ValueKind == JsonValueKind.String
                && method.GetString() is "window/logMessage" or "window/showMessage"
                && root.TryGetProperty("params", out var parameters)
                && parameters.TryGetProperty("message", out var message)
                && message.GetString() is { } text)
            {
                logs.Add(text);
            }

            document.Dispose();
        }

        return null;
    }

    private static (string? Name, string? Version, IReadOnlyList<string> Capabilities) ReadServerInfo(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            return (null, null, []);
        }

        string? name = null;
        string? version = null;

        if (result.TryGetProperty("serverInfo", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            name = info.TryGetProperty("name", out var n) ? n.GetString() : null;
            version = info.TryGetProperty("version", out var v) ? v.GetString() : null;
        }

        var capabilities = new List<string>();

        if (result.TryGetProperty("capabilities", out var advertised) && advertised.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in advertised.EnumerateObject())
            {
                // `false` is how a server declines a capability it still lists; reporting it as
                // present would make the summary say the opposite of what the server said.
                if (property.Value.ValueKind != JsonValueKind.False)
                {
                    capabilities.Add(property.Name);
                }
            }

            capabilities.Sort(StringComparer.Ordinal);
        }

        return (name, version, capabilities);
    }

    private static byte[] BuildRequest(int id, string method)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", JsonRpc.Version);
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static byte[] BuildNotification(string method)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", JsonRpc.Version);
            writer.WriteString("method", method);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static int SafeProcessId(RoslynConnection connection)
    {
        try
        {
            return connection.Process.Id;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static long? SafePeakWorkingSet(RoslynConnection connection)
    {
        try
        {
            connection.Process.Refresh();
            return connection.Process.PeakWorkingSet64;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or PlatformNotSupportedException
                                              or NotSupportedException)
        {
            return null;
        }
    }
}
