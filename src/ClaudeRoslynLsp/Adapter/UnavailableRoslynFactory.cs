using ClaudeRoslynLsp.Configuration;

using Microsoft.Extensions.Logging;

namespace ClaudeRoslynLsp.Adapter;

/// <summary>
/// The backend of a build that has no acquisition or launch chain yet: it refuses, with an
/// explanation.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a stub that answers things. A session behind this factory completes the
/// handshake, advertises its real capability document, and then refuses every request with
/// <c>-32603</c> and a <c>doctor</c> hint — which is a state a user can read and act on. The
/// alternative, an in-process fake standing in for a real Roslyn, would answer navigation questions
/// with fixture data and be indistinguishable from a working server until somebody trusted an
/// answer.
/// </para>
/// <para>
/// WP3 builds <c>Roslyn/IRoslynLauncher</c> (acquisition, the .NET host, the pipe or stdio
/// transport) and WP4 replaces this class with an adapter over it. Nothing above
/// <see cref="IRoslynConnectionFactory"/> changes when it does.
/// </para>
/// </remarks>
internal sealed partial class UnavailableRoslynFactory : IRoslynConnectionFactory
{
    private readonly ClaudeRoslynLspOptions _options;
    private readonly ILogger _logger;

    /// <summary>Creates the factory.</summary>
    /// <param name="options">The environment configuration, so the message can name what was set.</param>
    /// <param name="logger">The stderr log.</param>
    internal UnavailableRoslynFactory(ClaudeRoslynLspOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Description => "no Roslyn backend (this build has no launcher yet)";

    /// <inheritdoc />
    public Task<RoslynConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        Log.NoBackend(_logger, _options.RoslynPath ?? "(unset)");

        return Task.FromException<RoslynConnection>(new InvalidOperationException(
            "this build of " + ServerVersion.Name + " cannot acquire or launch roslyn-language-server yet; "
            + "the acquisition work package supplies it"));
    }

    /// <summary>Source-generated log methods (CA1873).</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 250,
            Level = LogLevel.Error,
            Message = "There is no Roslyn backend in this build (CLAUDE_ROSLYN_LSP_ROSLYN_PATH={Path}). " +
                      "Navigation and diagnostics will be refused with an explanation rather than answered " +
                      "wrongly.")]
        internal static partial void NoBackend(ILogger logger, string path);
    }
}
