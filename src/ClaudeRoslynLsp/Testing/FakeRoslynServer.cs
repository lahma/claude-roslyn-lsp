using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Protocol;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Testing;

/// <summary>
/// A scripted stand-in for <c>roslyn-language-server</c>, speaking the same framed dialect over any
/// pair of streams.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the mediation cannot be tested against nothing and should not have to be tested
/// against seventy megabytes of Microsoft's server on five release runners. What the adapter does is
/// almost entirely a response to what Roslyn <em>says</em> — a hundred and forty registrations, two
/// configuration requests, a progress stream, a readiness notification arriving seconds after the
/// solution was opened — so a fake that reproduces those, from the wire logs rather than from
/// imagination, exercises every decision the adapter makes.
/// </para>
/// <para>
/// The same class serves three callers: the in-process factory behind <c>lsp --smoke</c>, the hidden
/// <c>fake-roslyn</c> verb that the smoke test launches as a child process, and the mediation tests.
/// One implementation means the thing the tests prove and the thing the smoke test proves are the
/// same thing.
/// </para>
/// <para>
/// Deliberately <b>not</b> a Roslyn simulator. It answers a fixed set of navigation requests with
/// fixtures and everything else with <c>null</c>; it has no semantic model and no opinion about the
/// files it is told about. Anything that needs a real answer needs real Roslyn, which is what WP7's
/// live tests are for.
/// </para>
/// </remarks>
internal sealed partial class FakeRoslynServer
{
    private readonly FakeRoslynScript _script;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly List<string> _received = [];
    private readonly List<string> _rawReceived = [];

    private OutboundQueue? _outbound;
    private bool _projectInitializationSent;

    /// <summary>Creates a server that will run the given script.</summary>
    /// <param name="script">What it answers and what it volunteers.</param>
    /// <param name="logger">Where it reports what it is doing. Never its own output stream.</param>
    /// <param name="time">The clock its scripted delays are measured on.</param>
    internal FakeRoslynServer(FakeRoslynScript script, ILogger logger, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(logger);

        _script = script;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Whether <c>shutdown</c> has been received, which decides what <c>exit</c> means.</summary>
    internal bool ShutdownRequested { get; private set; }

    /// <summary>
    /// Whether this server has reported <c>workspace/projectInitializationComplete</c> yet.
    /// </summary>
    /// <remarks>
    /// The canned answers key on it, because that is what the real server does: navigation issued
    /// before this point comes back empty and successful (C27). A fake that answered correctly from
    /// the first millisecond would let a broken readiness gate pass every test there is.
    /// </remarks>
    internal bool ProjectsLoaded
    {
        get
        {
            lock (_lock)
            {
                return _projectInitializationSent;
            }
        }
    }

    /// <summary>The methods this server has been sent, in order. What the tests assert against.</summary>
    internal IReadOnlyList<string> ReceivedMethods
    {
        get
        {
            lock (_lock)
            {
                return _received.ToArray();
            }
        }
    }

    /// <summary>
    /// Every message this server has been sent, verbatim, including responses.
    /// </summary>
    /// <remarks>
    /// Responses are the interesting half: the adapter answering Roslyn's registrations, its
    /// configuration requests and its unknown methods is behaviour only visible from this side of the
    /// connection, and a test that could not see it would have to take the table's word for it.
    /// </remarks>
    internal IReadOnlyList<string> ReceivedMessages
    {
        get
        {
            lock (_lock)
            {
                return _rawReceived.ToArray();
            }
        }
    }

    /// <summary>
    /// Sends <c>workspace/projectInitializationComplete</c> now.
    /// </summary>
    /// <remarks>
    /// The manual half of the two load modes. A test that asserts "this request was held, and then
    /// answered" has to control the moment in between; a scripted delay would make it a race.
    /// </remarks>
    internal void CompleteProjectInitialization()
    {
        lock (_lock)
        {
            if (_projectInitializationSent)
            {
                return;
            }

            _projectInitializationSent = true;
        }

        Post(JsonRpcErrors.Notification("workspace/projectInitializationComplete", default));
    }

    /// <summary>Runs the server until <c>exit</c> or end of stream.</summary>
    /// <param name="input">Where the adapter's messages arrive.</param>
    /// <param name="output">Where this server's messages go.</param>
    /// <param name="cancellationToken">Stops the loop.</param>
    internal async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        var reader = new LspFrameReader(input);
        var outbound = new OutboundQueue(output, "the adapter", _logger);

        lock (_lock)
        {
            _outbound = outbound;
        }

        try
        {
            while (true)
            {
                var body = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false);

                if (body is null)
                {
                    Log.StreamEnded(_logger);
                    return;
                }

                if (Handle(body))
                {
                    return;
                }
            }
        }
        catch (LspProtocolException exception)
        {
            Log.NotFramed(_logger, exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Asked to stop; that is not a failure.
        }
        finally
        {
            await outbound.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Handles one message; returns true when it was <c>exit</c>.</summary>
    private bool Handle(byte[] body)
    {
        var info = LspMessageScanner.Scan(body);

        lock (_lock)
        {
            _rawReceived.Add(System.Text.Encoding.UTF8.GetString(body));
        }

        if (info.Method is not { } method)
        {
            // A response to something this server asked. Nothing here is waiting on one; the fake's
            // requests exist to be answered, not to be acted on.
            return false;
        }

        lock (_lock)
        {
            _received.Add(method);
        }

        var isRequest = info.Kind == LspMessageKind.Request;
        var idToken = info.IdToken(body).ToArray();

        switch (method)
        {
            case "initialize":
                Post(FakeRoslynScript.Response(idToken, _script.InitializeResult.Span));
                return false;

            case "shutdown":
                ShutdownRequested = true;
                Post(JsonRpcErrors.NullResult(idToken));
                return false;

            case "exit":
                Log.Exiting(_logger, ShutdownRequested);
                return true;

            default:
                if (isRequest)
                {
                    var result = _script.Responders.TryGetValue(method, out var responder)
                        ? responder(this, body)
                        : "null"u8.ToArray();

                    Post(FakeRoslynScript.Response(idToken, result.Span));
                }

                RunSteps(method);
                return false;
        }
    }

    /// <summary>Fires everything the script schedules off one method, on its own task.</summary>
    private void RunSteps(string trigger)
    {
        var steps = _script.Steps.Where(x => string.Equals(x.Trigger, trigger, StringComparison.Ordinal)).ToArray();

        if (steps.Length == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var elapsed = TimeSpan.Zero;

            foreach (var step in steps)
            {
                if (step.Delay > elapsed)
                {
                    await Task.Delay(step.Delay - elapsed, _time).ConfigureAwait(false);
                    elapsed = step.Delay;
                }

                // Recorded before the message goes out, so a request racing it can never see the
                // notification without the state that comes with it.
                if (step.Body.AsSpan().IndexOf("projectInitializationComplete"u8) >= 0)
                {
                    lock (_lock)
                    {
                        _projectInitializationSent = true;
                    }
                }

                Post(step.Body);
            }
        });
    }

    /// <summary>Queues one message for the adapter.</summary>
    private void Post(ReadOnlyMemory<byte> body)
    {
        OutboundQueue? outbound;

        lock (_lock)
        {
            outbound = _outbound;
        }

        outbound?.Post(body);
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 1100, Level = LogLevel.Debug, Message = "The adapter closed the connection.")]
        internal static partial void StreamEnded(ILogger logger);

        [LoggerMessage(
            EventId = 1101,
            Level = LogLevel.Error,
            Message = "The adapter's stream is not well-framed LSP; the fake backend is stopping.")]
        internal static partial void NotFramed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 1102,
            Level = LogLevel.Debug,
            Message = "Fake Roslyn exiting (shutdown was requested first: {Orderly}).")]
        internal static partial void Exiting(ILogger logger, bool orderly);
    }
}
