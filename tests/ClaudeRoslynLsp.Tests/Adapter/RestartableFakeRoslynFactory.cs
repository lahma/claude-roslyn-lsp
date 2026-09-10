using System.Text;

using ClaudeRoslynLsp.Adapter;
using ClaudeRoslynLsp.Testing;

using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeRoslynLsp.Tests.Adapter;

/// <summary>
/// A backend factory that can be connected to more than once, and whose current backend can be
/// killed the way a crashing process kills one.
/// </summary>
/// <remarks>
/// <para>
/// The production factories connect once and are done with. Recovery needs the other shape: a second
/// <see cref="ConnectAsync"/> that produces a <em>fresh</em> server with no memory of the first, so a
/// test can assert that the mirror was replayed and the solution re-opened rather than that some
/// state survived.
/// </para>
/// <para>
/// <see cref="Kill"/> completes the server's writer, which the adapter's frame reader sees as a clean
/// end of stream — exactly what it sees when a child process dies. It is deliberately not a
/// disposal of the connection: the adapter disposing its own connection is the <em>expected</em>
/// path, and the two must be told apart.
/// </para>
/// </remarks>
internal sealed class RestartableFakeRoslynFactory : IRoslynConnectionFactory
{
    private readonly Lock _gate = new();
    private readonly Func<FakeRoslynScript> _script;
    private readonly List<FakeRoslynServer> _servers = [];

    private DuplexEnd? _serverEnd;

    /// <summary>Creates a factory over a script source.</summary>
    /// <param name="script">Called once per connection, so each backend starts clean.</param>
    internal RestartableFakeRoslynFactory(Func<FakeRoslynScript>? script = null) =>
        _script = script ?? (() => FakeRoslynScript.Roslyn512Startup(projectLoadDelay: null));

    /// <inheritdoc />
    public string Description => "a restartable scripted fake Roslyn backend";

    /// <summary>How many times a backend has been connected.</summary>
    internal int ConnectCount
    {
        get
        {
            lock (_gate)
            {
                return _servers.Count;
            }
        }
    }

    /// <summary>The most recently connected backend.</summary>
    internal FakeRoslynServer? Latest
    {
        get
        {
            lock (_gate)
            {
                return _servers.Count == 0 ? null : _servers[^1];
            }
        }
    }

    /// <summary>Every backend that has been connected, oldest first.</summary>
    internal IReadOnlyList<FakeRoslynServer> Servers
    {
        get
        {
            lock (_gate)
            {
                return _servers.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public Task<RoslynConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var (adapterEnd, serverEnd) = DuplexStreamPair.Create();
        var server = new FakeRoslynServer(_script(), NullLogger.Instance);

        lock (_gate)
        {
            _servers.Add(server);
            _serverEnd = serverEnd;
        }

        var running = Task.Run(
            async () =>
            {
                try
                {
                    await server.RunAsync(serverEnd.Input, serverEnd.Output, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // The test killed it mid-read, which is what a dying process looks like.
                }
                finally
                {
                    serverEnd.CompleteOutput();
                }
            },
            CancellationToken.None);

        return Task.FromResult(new RoslynConnection(
            adapterEnd.Input,
            adapterEnd.Output,
            running,
            Description,
            () =>
            {
                adapterEnd.CompleteOutput();
                return ValueTask.CompletedTask;
            }));
    }

    /// <summary>Kills the current backend the way a crash would: the stream simply ends.</summary>
    internal void Kill()
    {
        DuplexEnd? end;

        lock (_gate)
        {
            end = _serverEnd;
            _serverEnd = null;
        }

        end?.CompleteOutput();
    }

    /// <summary>Every message one backend received, as text.</summary>
    /// <param name="server">Which backend.</param>
    internal static IReadOnlyList<string> MessagesOf(FakeRoslynServer server) => server.ReceivedMessages;

    /// <summary>Builds a script that also answers <c>textDocument/diagnostic</c>.</summary>
    /// <remarks>
    /// The real server answers a pull with a full report or an <c>unchanged</c> one; the default
    /// script answers every unknown request with <c>null</c>, which the bridge would read as "no
    /// report". Adding one responder is enough to drive the whole pull-to-push path end to end.
    /// </remarks>
    /// <param name="report">The report to answer every pull with.</param>
    /// <param name="projectLoadDelay">Passed through to the standard startup script.</param>
    internal static FakeRoslynScript ScriptWithDiagnostics(string report, TimeSpan? projectLoadDelay = null)
    {
        var script = FakeRoslynScript.Roslyn512Startup(projectLoadDelay: projectLoadDelay);

        return new FakeRoslynScript
        {
            InitializeResult = script.InitializeResult,
            Steps = script.Steps,
            ProjectLoadDelay = script.ProjectLoadDelay,
            Responders = new Dictionary<string, FakeRoslynResponder>(script.Responders, StringComparer.Ordinal)
            {
                ["textDocument/diagnostic"] = (_, _) => Encoding.UTF8.GetBytes(report),
            },
        };
    }
}
