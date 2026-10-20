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
/// <para>
/// <b>D102 — everything after <see cref="StartedAt"/> is optional in both directions.</b> A reader
/// from before those fields ignores members it does not know, which System.Text.Json does by
/// default; a reader from after them tolerates their absence, because a constructor parameter with a
/// default is filled from the default when the member is missing. So a file written by either can be
/// read by either, which is what keeps one build from deleting another's live session as unreadable.
/// They are rewritten while the host runs, which is why the write is atomic (D102).
/// </para>
/// </remarks>
/// <param name="ProcessId">The hosting process. Checked for liveness before anything is attempted.</param>
/// <param name="Version">The host's adapter version; an attacher refuses anything but its own.</param>
/// <param name="Transport">How to reach the host. Only <c>pipe</c> exists; the field is here so a second one can be added without a format break.</param>
/// <param name="PipeName">The named pipe the host is accepting on.</param>
/// <param name="Solution">The solution the host has open, for <c>doctor</c> and for a human reading the file.</param>
/// <param name="StartedAt">When the host published itself, for <c>doctor</c>'s age column.</param>
/// <param name="RoslynProcessId">Roslyn's own process id (C45), while there is one (D102).</param>
/// <param name="State"><c>starting</c>, <c>running</c> or <c>dormant</c> (<see cref="BackendStatus.StateName"/>).</param>
/// <param name="LastActivityAt">When any client last asked the shared Roslyn for anything (D95).</param>
/// <param name="BaselineMegabytes">Its memory once the workspace had settled (D100).</param>
/// <param name="Recycles">How many times the host has recycled it.</param>
/// <param name="LastRecycleAt">When it last did.</param>
/// <param name="AttachedClients">How many other processes are attached right now.</param>
/// <param name="PidNamespace">
/// The host's pid namespace on Linux (<c>/proc/self/ns/pid</c>'s link target), so that a sweep in
/// another container sharing this home does not judge it by a pid that means nothing there (D103).
/// Null everywhere else, and in a file written before the field existed.
/// </param>
internal sealed record SharedSession(
    [property: JsonPropertyName("pid")] int ProcessId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("transport")] string Transport,
    [property: JsonPropertyName("pipeName")] string PipeName,
    [property: JsonPropertyName("solution")] string Solution,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("roslynProcessId")] int? RoslynProcessId = null,
    [property: JsonPropertyName("state")] string? State = null,
    [property: JsonPropertyName("lastActivityAt")] DateTimeOffset? LastActivityAt = null,
    [property: JsonPropertyName("baselineMegabytes")] int? BaselineMegabytes = null,
    [property: JsonPropertyName("recycles")] int Recycles = 0,
    [property: JsonPropertyName("lastRecycleAt")] DateTimeOffset? LastRecycleAt = null,
    [property: JsonPropertyName("attachedClients")] int AttachedClients = 0,
    [property: JsonPropertyName("pidNamespace")] string? PidNamespace = null)
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
/// <param name="ForeignNamespace">
/// Whether it was written in another pid namespace — another container sharing this home — so that
/// <paramref name="Alive"/> says nothing about it and the sweep leaves it alone (D103).
/// </param>
internal sealed record SharedSessionReport(
    string Key,
    string Path,
    SharedSession? Session,
    bool Alive,
    bool VersionMatches,
    bool ForeignNamespace = false)
{
    /// <summary>Whether this file would be used, rather than deleted, by a process starting now.</summary>
    internal bool Usable => Session is not null && Alive && VersionMatches;

    /// <summary>Why it would not be used, in the words <c>doctor</c> prints.</summary>
    internal string Verdict => Session is null
        ? "unreadable"
        : !Alive
            ? ForeignNamespace
                ? $"written in another pid namespace ({Session.PidNamespace}); process {Session.ProcessId} cannot "
                  + "be checked from here, so it is left alone"
                : $"stale: process {Session.ProcessId} is gone"
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
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SharedSession))]
internal sealed partial class SharedSessionJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
