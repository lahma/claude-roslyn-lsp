using System.Globalization;
using System.Text;

using ClaudeRoslynLsp.Mcp.Engine;

namespace ClaudeRoslynLsp.Edits;

/// <summary>
/// Writes a resolved <see cref="WorkspaceEdit"/> to disk.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists (D21, D61).</b> The MCP server is the LSP <em>client</em> in this direction, so
/// nothing else is going to apply the edit Roslyn just resolved. The adapter deliberately does not
/// declare <c>workspace.applyEdit</c> (D14) and the v1 LSP half writes no files at all; this is the
/// only place in the product that changes a source file.
/// </para>
/// <para>
/// <b>The rules, each of which is a bug that would otherwise be silent.</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>documentChanges</c> only. Roslyn always answers in that form (C21), and the ordered array is
/// load-bearing: a <c>Move type to X.cs</c> resolves to a <c>create</c> operation followed by edits
/// <em>into the file that does not exist yet</em>. Applying the array out of order, or collapsing it
/// into LSP's unordered <c>changes</c> map, turns that into a write to a missing file.
/// </description></item>
/// <item><description>
/// Every URI goes through <see cref="WorkspacePathGuard"/> before it becomes a path. An edit is an
/// instruction from another process, and Roslyn really does hand out URIs outside the workspace —
/// navigation into a BCL symbol lands in <c>%TEMP%\MetadataAsSource\...</c> (C25).
/// </description></item>
/// <item><description>
/// Edits within a document are applied in reverse range order, so each one's offsets are still the
/// offsets Roslyn computed them against. Overlapping edits are refused rather than merged: LSP
/// forbids them, and "merged" would mean guessing.
/// </description></item>
/// <item><description>
/// The file's byte order mark and dominant line ending survive, and the incoming <c>newText</c> is
/// normalised to that line ending — a single replacement string can mix both (C22).
/// </description></item>
/// <item><description>
/// The write is a temporary file beside the target followed by <see cref="File.Move(string, string, bool)"/>,
/// so an interrupted write leaves the original rather than half of the new one. Beside the target
/// rather than in the system temp directory, because a move across volumes is a copy and stops being
/// atomic.
/// </description></item>
/// </list>
/// </remarks>
internal sealed class WorkspaceEditApplier
{
    private readonly WorkspacePathGuard _guard;

    /// <summary>Creates an applier bounded by one workspace root.</summary>
    /// <param name="guard">The path guard.</param>
    internal WorkspaceEditApplier(WorkspacePathGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        _guard = guard;
    }

    /// <summary>The guard this applier is bounded by, so callers report the same relative paths.</summary>
    internal WorkspacePathGuard Guard => _guard;

    /// <summary>
    /// Reads every file the edit touches and works out what it would say afterwards, without writing
    /// anything.
    /// </summary>
    /// <param name="edit">The resolved edit.</param>
    internal WorkspaceEditPlan Plan(WorkspaceEdit? edit)
    {
        if (edit?.DocumentChanges is not { Length: > 0 } changes)
        {
            return new WorkspaceEditPlan([]);
        }

        var files = new List<PlannedFile>();
        var byPath = new Dictionary<string, PlannedFile>(_guard.PathComparer);

        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case null or "":
                    ApplyTextDocumentEdit(change, files, byPath);
                    break;

                case "create":
                    ApplyCreate(change, files, byPath);
                    break;

                case "rename":
                    ApplyRename(change, files, byPath);
                    break;

                case "delete":
                    ApplyDelete(change, files, byPath);
                    break;

                default:
                    throw new WorkspaceEditRefusedException(
                        $"the edit contains a '{change.Kind}' resource operation, which this server does not know how to apply");
            }
        }

        return new WorkspaceEditPlan(files);
    }

    /// <summary>
    /// Writes a plan to disk: renames first, then deletions, then contents.
    /// </summary>
    /// <remarks>
    /// The order is not cosmetic. A <c>rename A to B</c> followed by edits to <c>B</c> plans as "move
    /// the bytes, then write the new text" — doing the write first would put the edited text at
    /// <c>B</c> and then have the move overwrite it with the unedited original.
    /// </remarks>
    /// <param name="plan">The plan to write.</param>
    internal static void Apply(WorkspaceEditPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var file in plan.Files.Where(candidate => candidate.MovedToPath is not null))
        {
            EnsureDirectory(file.MovedToPath!);
            File.Move(file.Path, file.MovedToPath!, overwrite: true);
        }

        foreach (var file in plan.Files.Where(candidate => candidate is { Deleted: true, MovedToPath: null }))
        {
            File.Delete(file.Path);
        }

        foreach (var file in plan.Files)
        {
            if (file.Deleted || file.MovedToPath is not null || file.NewText is null)
            {
                continue;
            }

            if (!file.Created && !file.ContentChanged)
            {
                continue;
            }

            WriteAtomically(file.Path, TextFileCodec.Encode(file.NewText, file.Format));
        }
    }

    private void ApplyTextDocumentEdit(
        DocumentChange change,
        List<PlannedFile> files,
        Dictionary<string, PlannedFile> byPath)
    {
        if (change.TextDocument?.Uri is not { } uri)
        {
            throw new WorkspaceEditRefusedException("the edit contains a document change with no textDocument.uri");
        }

        var file = Load(uri, files, byPath);

        if (file.Deleted)
        {
            throw new WorkspaceEditRefusedException(
                $"the edit both deletes and edits '{file.RelativePath}'");
        }

        if (change.Edits is not { Length: > 0 } edits)
        {
            return;
        }

        file.NewText = ApplyEdits(file.NewText ?? string.Empty, edits, file.Format.NewLine, file.RelativePath);
        file.EditCount += edits.Length;
    }

    private void ApplyCreate(DocumentChange change, List<PlannedFile> files, Dictionary<string, PlannedFile> byPath)
    {
        var path = _guard.Resolve(change.Uri);
        var existed = File.Exists(path);

        if (existed && change.Options?.IgnoreIfExists == true && change.Options.Overwrite != true)
        {
            // The operation says so explicitly: leave what is there. Loading it keeps later edits in
            // the same array addressing the real contents rather than an empty string.
            _ = Load(change.Uri, files, byPath);
            return;
        }

        var file = GetOrAdd(path, existed: false, files, byPath);
        file.Created = !existed;
        file.Deleted = false;
        file.OriginalText = existed ? file.OriginalText : null;
        file.NewText = string.Empty;
        file.Format = existed ? file.Format : TextFileFormat.CreatedFileDefault;
    }

    private void ApplyRename(DocumentChange change, List<PlannedFile> files, Dictionary<string, PlannedFile> byPath)
    {
        var sourcePath = _guard.Resolve(change.OldUri);
        var targetPath = _guard.Resolve(change.NewUri);

        var source = Load(change.OldUri, files, byPath);
        var target = GetOrAdd(targetPath, existed: File.Exists(targetPath), files, byPath);

        target.NewText = source.NewText;
        target.Format = source.Format;
        target.MovedFromRelativePath = source.RelativePath;
        target.Deleted = false;

        source.MovedToPath = targetPath;
        source.MovedToRelativePath = _guard.ToRelative(targetPath);
        source.NewText = null;

        _ = sourcePath;
    }

    private void ApplyDelete(DocumentChange change, List<PlannedFile> files, Dictionary<string, PlannedFile> byPath)
    {
        var path = _guard.Resolve(change.Uri);

        if (!File.Exists(path) && change.Options?.IgnoreIfNotExists == true)
        {
            return;
        }

        var file = Load(change.Uri, files, byPath);
        file.Deleted = true;
        file.NewText = null;
    }

    private PlannedFile Load(string? uri, List<PlannedFile> files, Dictionary<string, PlannedFile> byPath)
    {
        var path = _guard.Resolve(uri);

        if (byPath.TryGetValue(path, out var existing))
        {
            return existing;
        }

        var file = GetOrAdd(path, File.Exists(path), files, byPath);

        if (file.ExistedOnDisk)
        {
            var (text, format) = TextFileCodec.Read(path);
            file.OriginalText = text;
            file.NewText = text;
            file.Format = format;
        }

        return file;
    }

    private PlannedFile GetOrAdd(
        string path,
        bool existed,
        List<PlannedFile> files,
        Dictionary<string, PlannedFile> byPath)
    {
        if (byPath.TryGetValue(path, out var existing))
        {
            return existing;
        }

        var file = new PlannedFile
        {
            Path = path,
            RelativePath = _guard.ToRelative(path),
            ExistedOnDisk = existed,
            NewText = existed ? null : string.Empty,
        };

        files.Add(file);
        byPath[path] = file;
        return file;
    }

    /// <summary>
    /// Applies one document's edits to its text, last edit first.
    /// </summary>
    /// <remarks>
    /// The ordering is by offset descending, and among edits at the same offset by array position
    /// descending, so a sequence of insertions at one point ends up in the order Roslyn wrote them.
    /// After sorting, each edit's end must not reach past the previous edit's start; anything else is
    /// an overlap, which LSP forbids and which this refuses rather than resolving by guesswork.
    /// </remarks>
    /// <param name="text">The document's current text.</param>
    /// <param name="edits">The edits.</param>
    /// <param name="newLine">The document's line ending, which incoming text is normalised to (C22).</param>
    /// <param name="relativePath">The path, for the refusal message.</param>
    internal static string ApplyEdits(string text, IReadOnlyList<LspTextEdit> edits, string newLine, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(edits);

        var offsets = new TextOffsets(text);

        var ordered = edits
            .Select((edit, index) => (Edit: edit, Index: index,
                Start: offsets.OffsetOf(edit.Range.Start),
                End: offsets.OffsetOf(edit.Range.End)))
            .OrderByDescending(candidate => candidate.Start)
            .ThenByDescending(candidate => candidate.Index)
            .ToArray();

        var builder = new StringBuilder(text);
        var lowestTouched = int.MaxValue;

        foreach (var (edit, _, start, end) in ordered)
        {
            var from = Math.Min(start, end);
            var to = Math.Max(start, end);

            if (to > lowestTouched)
            {
                throw new WorkspaceEditRefusedException(
                    $"Roslyn returned overlapping edits for '{relativePath}' "
                    + $"(offset {from.ToString(CultureInfo.InvariantCulture)}..{to.ToString(CultureInfo.InvariantCulture)}); "
                    + "applying them would produce text neither edit describes");
            }

            builder.Remove(from, to - from);
            builder.Insert(from, TextFileCodec.NormalizeNewLines(edit.NewText, newLine));
            lowestTouched = from;
        }

        return builder.ToString();
    }

    private static void EnsureDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        EnsureDirectory(path);

        // Beside the target, never in the system temp directory: File.Move across volumes degrades
        // to copy-then-delete, which is exactly the non-atomic write this is here to avoid.
        var temporary = path + ".claude-roslyn-lsp-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";

        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // The original is intact either way; a stranded temporary file is not worth masking
                // the real failure for.
            }

            throw;
        }
    }
}
