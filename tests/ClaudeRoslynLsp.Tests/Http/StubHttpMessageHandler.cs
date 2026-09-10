using System.Collections.Concurrent;
using System.Net;

namespace ClaudeRoslynLsp.Tests.Http;

/// <summary>
/// Hand-rolled <see cref="HttpMessageHandler"/> stub (AGENTS.md: no mocking libraries), serving a
/// FIFO queue of responders and recording every request it saw.
/// </summary>
/// <remarks>
/// Byte content rather than string content is the default here, because everything this repository
/// downloads is a <c>.nupkg</c> — and the property under test is that the bytes that arrive are the
/// bytes that hash. A stub that served text would be testing a different thing.
/// </remarks>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();
    private readonly List<Uri?> _requests = [];
    private readonly Lock _gate = new();

    /// <summary>Used when the queue is empty; null means an unexpected request throws.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? Fallback { get; set; }

    /// <summary>Every request URI, in order.</summary>
    public IReadOnlyList<Uri?> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Every request's headers, flattened, in order.</summary>
    public List<Dictionary<string, string>> Headers { get; } = [];

    /// <summary>Queues one responder.</summary>
    /// <param name="responder">Builds the response for the next request.</param>
    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responders.Enqueue(responder);

    /// <summary>Queues a 200 carrying <paramref name="content"/> as <c>application/octet-stream</c>.</summary>
    /// <param name="content">The body.</param>
    public void EnqueueBytes(byte[] content) => Enqueue(_ =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });

    /// <summary>Queues a bare status code with no body.</summary>
    /// <param name="statusCode">The status to answer with.</param>
    public void EnqueueStatus(HttpStatusCode statusCode) =>
        Enqueue(_ => new HttpResponseMessage(statusCode));

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        lock (_gate)
        {
            _requests.Add(request.RequestUri);
            Headers.Add(headers);
        }

        if (_responders.TryDequeue(out var responder))
        {
            return Task.FromResult(responder(request));
        }

        return Task.FromResult(
            Fallback?.Invoke(request)
            ?? throw new InvalidOperationException($"No stubbed response for {request.Method} {request.RequestUri}."));
    }
}
