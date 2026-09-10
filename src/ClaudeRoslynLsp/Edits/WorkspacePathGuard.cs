using System.Diagnostics.CodeAnalysis;

namespace ClaudeRoslynLsp.Edits;

/// <summary>
/// The one place a URI from Roslyn becomes a path this process is willing to write to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a guard and not just a conversion (D61).</b> Everything the applier writes originates
/// outside this process: a code action's resolved edit names the files it wants changed, and this
/// server writes them without asking. That is the design (D21) and it is the only way a refactoring
/// reaches disk — but it means a URI is an instruction, and an instruction is exactly the thing that
/// has to be bounded. Two rules do the bounding: the scheme must be <c>file</c>, and the resolved
/// full path must sit under the workspace root. A <c>%2e%2e</c> segment, a UNC path, a
/// <c>MetadataAsSource</c> temp file (C25 — Roslyn really does return those from navigation) and a
/// symlink escape all fail the second rule, because it compares resolved paths rather than the text
/// it was given.
/// </para>
/// <para>
/// The comparison is case-insensitive on Windows and macOS and case-sensitive elsewhere, which is
/// what the file systems themselves do. Getting that backwards would either refuse a legitimate edit
/// on Windows or accept an escape on Linux.
/// </para>
/// </remarks>
internal sealed class WorkspacePathGuard
{
    private readonly string _root;
    private readonly string _rootWithSeparator;
    private readonly StringComparison _comparison;

    /// <summary>Creates a guard for one workspace root.</summary>
    /// <param name="root">The directory every writable file must sit under.</param>
    internal WorkspacePathGuard(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _rootWithSeparator = _root + Path.DirectorySeparatorChar;

        // Path comparison follows the file system, not the host language: NTFS and APFS fold case,
        // ext4 does not. A guard that folded case on Linux would accept /Src/x.cs as /src/x.cs.
        _comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    /// <summary>The workspace root, resolved and without a trailing separator.</summary>
    internal string Root => _root;

    /// <summary>How paths under this root are compared, so callers key dictionaries the same way.</summary>
    internal StringComparer PathComparer =>
        _comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Converts a URI to a local path, or explains why it will not.
    /// </summary>
    /// <param name="uri">The URI, as Roslyn spelled it.</param>
    /// <param name="path">The resolved local path.</param>
    /// <param name="reason">Why the URI was refused, when it was.</param>
    internal bool TryResolve(string? uri, [NotNullWhen(true)] out string? path, [NotNullWhen(false)] out string? reason)
    {
        path = null;
        reason = null;

        if (string.IsNullOrWhiteSpace(uri))
        {
            reason = "the edit named a document with no URI";
            return false;
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            reason = $"'{uri}' is not an absolute URI";
            return false;
        }

        if (!parsed.IsFile)
        {
            reason = $"'{uri}' is not a file: URI, and this server only writes local files";
            return false;
        }

        string full;

        try
        {
            full = Path.GetFullPath(parsed.LocalPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = $"'{uri}' does not resolve to a usable local path";
            return false;
        }

        if (!IsInsideRoot(full))
        {
            reason = $"'{full}' is outside the workspace root '{_root}'";
            return false;
        }

        path = full;
        return true;
    }

    /// <summary>Converts a URI to a local path, throwing the guard's own reason when it will not.</summary>
    /// <param name="uri">The URI.</param>
    internal string Resolve(string? uri) =>
        TryResolve(uri, out var path, out var reason)
            ? path
            : throw new WorkspaceEditRefusedException(reason);

    /// <summary>Whether a resolved absolute path sits under the workspace root.</summary>
    /// <param name="fullPath">An already-resolved absolute path.</param>
    internal bool IsInsideRoot(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);

        return string.Equals(fullPath, _root, _comparison)
            || fullPath.StartsWith(_rootWithSeparator, _comparison);
    }

    /// <summary>
    /// The workspace-relative, forward-slashed spelling of a path — the only form any tool reports,
    /// so that an answer does not depend on where the checkout happens to live.
    /// </summary>
    /// <param name="fullPath">An absolute path under the root.</param>
    internal string ToRelative(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);

        var relative = Path.GetRelativePath(_root, fullPath);
        return relative.Replace('\\', '/');
    }

    /// <summary>
    /// Turns a path a model supplied — relative to the workspace, or absolute — into a full path
    /// under the root.
    /// </summary>
    /// <param name="path">The path as the model wrote it.</param>
    internal string FromModelPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new WorkspaceEditRefusedException("no path was given");
        }

        var candidate = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)));

        return IsInsideRoot(candidate)
            ? candidate
            : throw new WorkspaceEditRefusedException($"'{path}' is outside the workspace root '{_root}'");
    }

    /// <summary>The <c>file:</c> URI for a local path, spelled the way Roslyn spells them.</summary>
    /// <param name="fullPath">An absolute path.</param>
    internal static string ToUri(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        return new Uri(fullPath).AbsoluteUri;
    }
}

/// <summary>
/// Thrown when an edit names something this server will not write.
/// </summary>
/// <remarks>
/// Its own type rather than an <see cref="InvalidOperationException"/> so the tool layer's error
/// funnel can turn it into a message that names the refused path instead of a generic failure.
/// </remarks>
internal sealed class WorkspaceEditRefusedException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why the edit was refused.</param>
    internal WorkspaceEditRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why the edit was refused.</param>
    /// <param name="innerException">The underlying failure.</param>
    internal WorkspaceEditRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
