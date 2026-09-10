using System.Text.Json;

using ClaudeRoslynLsp.Adapter;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// A hand-written <see cref="IAdapterChannel"/>: records what a bridge said, answers what it asked.
/// </summary>
/// <remarks>
/// Hand-rolled because the package budget has no mocking library (D9), and better for it — the
/// answers a diagnostics test needs are whole JSON reports, which read far better as raw wire text
/// than as a fluent setup chain. <see cref="Answer"/> is a queue rather than a single value so a
/// test can script "refused, then answered", which is the retry rule.
/// </remarks>
internal sealed class StubAdapterChannel : IAdapterChannel
{
    private readonly Lock _gate = new();
    private readonly List<JsonElement> _toClient = [];
    private readonly List<JsonElement> _toServer = [];
    private readonly List<(string Method, JsonElement Params)> _asked = [];
    private readonly Queue<Func<string, JsonElement>> _answers = new();

    /// <inheritdoc />
    public bool BackendConnected { get; set; } = true;

    /// <inheritdoc />
    public ReadinessState Readiness { get; set; } = ReadinessState.ProjectsLoaded;

    /// <inheritdoc />
    public string? WorkspaceRoot { get; set; }

    /// <summary>Every notification the bridge sent the client, in order.</summary>
    public IReadOnlyList<JsonElement> ToClient
    {
        get
        {
            lock (_gate)
            {
                return _toClient.ToArray();
            }
        }
    }

    /// <summary>Every notification the bridge sent Roslyn, in order.</summary>
    public IReadOnlyList<JsonElement> ToServer
    {
        get
        {
            lock (_gate)
            {
                return _toServer.ToArray();
            }
        }
    }

    /// <summary>Every request the bridge made, in order.</summary>
    public IReadOnlyList<(string Method, JsonElement Params)> Asked
    {
        get
        {
            lock (_gate)
            {
                return _asked.ToArray();
            }
        }
    }

    /// <summary>Queues one answer, as raw JSON.</summary>
    /// <param name="json">The <c>result</c> the next request gets.</param>
    public void Answer(string json)
    {
        var element = JsonDocument.Parse(json).RootElement.Clone();

        lock (_gate)
        {
            _answers.Enqueue(_ => element);
        }
    }

    /// <summary>Queues one refusal.</summary>
    /// <param name="code">The JSON-RPC error code.</param>
    public void Refuse(int code)
    {
        lock (_gate)
        {
            _answers.Enqueue(method => throw new RoslynRequestException(method, code, "scripted refusal"));
        }
    }

    /// <inheritdoc />
    public void NotifyServer(ReadOnlyMemory<byte> body) => Record(_toServer, body);

    /// <inheritdoc />
    public void NotifyClient(ReadOnlyMemory<byte> body) => Record(_toClient, body);

    /// <inheritdoc />
    public void LogToClient(int type, string message)
    {
    }

    /// <inheritdoc />
    public Task<JsonElement> AskAsync(string method, ReadOnlyMemory<byte> rawParams, CancellationToken cancellationToken)
    {
        Func<string, JsonElement>? answer;

        lock (_gate)
        {
            _asked.Add((method, JsonDocument.Parse(rawParams).RootElement.Clone()));
            answer = _answers.Count > 0 ? _answers.Dequeue() : null;
        }

        if (answer is null)
        {
            return Task.FromResult(JsonDocument.Parse("""{"kind":"full","items":[]}""").RootElement.Clone());
        }

        try
        {
            return Task.FromResult(answer(method));
        }
        catch (RoslynRequestException exception)
        {
            return Task.FromException<JsonElement>(exception);
        }
    }

    /// <summary>Waits until at least <paramref name="count"/> notifications have reached the client.</summary>
    /// <remarks>
    /// A bridge posts from a task it started, so "the pull has finished" is not observable from the
    /// call that triggered it. Polling on a short budget is the honest way to express that without a
    /// synchronisation primitive the production code would have to grow just for a test.
    /// </remarks>
    /// <param name="count">How many to wait for.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task WaitForClientAsync(int count, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_toClient.Count >= count)
                {
                    return;
                }
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Only {ToClient.Count} of {count} client notification(s) arrived.");
    }

    /// <summary>Waits until at least <paramref name="count"/> requests have been made.</summary>
    /// <param name="count">How many to wait for.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task WaitForAskedAsync(int count, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_asked.Count >= count)
                {
                    return;
                }
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Only {Asked.Count} of {count} request(s) were made.");
    }

    private void Record(List<JsonElement> into, ReadOnlyMemory<byte> body)
    {
        var element = JsonDocument.Parse(body).RootElement.Clone();

        lock (_gate)
        {
            into.Add(element);
        }
    }
}
