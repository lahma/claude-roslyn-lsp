using System.Text.Json.Serialization;

namespace ClaudeRoslynLsp.Adapter.Sharing;

/// <summary>
/// The contents of one session file: which process is hosting a solution's Roslyn, and how to reach
/// it.
/// </summary>
/// <remarks>
/// <para>
/// <b>D85 — the registry is a file per solution, not a broadcast or a well-known port.</b> Two
/// processes have to find each other with no coordinator and no protocol between them, and the only
/// thing they are known to agree on is the solution path and the adapter's home directory (D27). A
/// file named after the first, kept under the second, is therefore the whole rendezvous: it is
/// readable by a human, survives nothing at all when the machine reboots (the pid check discards
/// it), and needs no daemon to maintain.
/// </para>
/// <para>
/// <see cref="Version"/> is this binary's own version and is checked on every read. A 0.1.0 host and
/// a 0.2.0 attacher would speak the same LSP over the same pipe right up to the moment one of them
/// changed what it fans out or how it counts documents, and the failure would be a wrong answer
/// rather than a refused connection. Refusing to attach across versions costs one extra Roslyn on
/// the day of an upgrade and costs nothing afterwards.
/// </para>
/// </remarks>
/// <param name="ProcessId">The hosting process. Checked for liveness before anything is attempted.</param>
/// <param name="Version">The host's adapter version; an attacher refuses anything but its own.</param>
/// <param name="Transport">How to reach the host. Only <c>pipe</c> exists; the field is here so a second one can be added without a format break.</param>
/// <param name="PipeName">The named pipe the host is accepting on.</param>
/// <param name="Solution">The solution the host has open, for <c>doctor</c> and for a human reading the file.</param>
/// <param name="StartedAt">When the host published itself, for <c>doctor</c>'s age column.</param>
internal sealed record SharedSession(
    [property: JsonPropertyName("pid")] int ProcessId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("transport")] string Transport,
    [property: JsonPropertyName("pipeName")] string PipeName,
    [property: JsonPropertyName("solution")] string Solution,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt)
{
    /// <summary>The only transport this version knows how to attach over.</summary>
    internal const string PipeTransport = "pipe";
}

/// <summary>One session file as <c>doctor</c> reports it.</summary>
/// <param name="Key">The file's name without its extension — the hashed solution path.</param>
/// <param name="Path">The file itself, so a stale one can be deleted by hand.</param>
/// <param name="Session">What it says, or null when it could not be read at all.</param>
/// <param name="Alive">Whether the process it names still exists.</param>
/// <param name="VersionMatches">Whether it was written by a binary of this version.</param>
internal sealed record SharedSessionReport(
    string Key,
    string Path,
    SharedSession? Session,
    bool Alive,
    bool VersionMatches)
{
    /// <summary>Whether this file would be used, rather than deleted, by a process starting now.</summary>
    internal bool Usable => Session is not null && Alive && VersionMatches;

    /// <summary>Why it would not be used, in the words <c>doctor</c> prints.</summary>
    internal string Verdict => Session is null
        ? "unreadable"
        : !Alive
            ? $"stale: process {Session.ProcessId} is gone"
            : !VersionMatches
                ? $"stale: written by version {Session.Version}, this is {ServerVersion.Value}"
                : "live";
}

/// <summary>
/// The serializer contract for the session file.
/// </summary>
/// <remarks>
/// A fourth source-generated context, and D7's rule is satisfied because it is chained with
/// nothing: this file is neither an LSP wire shape nor part of the tool vocabulary, it is this
/// product's own on-disk format, and giving it its own contract keeps it out of both of the
/// vocabularies that already have a reason not to meet.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SharedSession))]
internal sealed partial class SharedSessionJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
