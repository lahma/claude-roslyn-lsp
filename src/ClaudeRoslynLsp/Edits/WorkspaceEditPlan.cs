namespace ClaudeRoslynLsp.Edits;

/// <summary>
/// One file as a workspace edit leaves it: what it used to say, what it will say, and what happened
/// to the file itself.
/// </summary>
/// <remarks>
/// Mutable, and only inside <see cref="WorkspaceEditApplier"/>'s planning pass, because planning is
/// a simulation: a <c>create</c> followed by edits into the not-yet-existing file (C21) has to see
/// its own earlier result, and so does a rename followed by edits to the new name.
/// </remarks>
internal sealed class PlannedFile
{
    /// <summary>The absolute path, guarded to be inside the workspace root.</summary>
    internal required string Path { get; init; }

    /// <summary>The workspace-relative, forward-slashed path every tool reports.</summary>
    internal required string RelativePath { get; init; }

    /// <summary>Whether the file was on disk when planning started.</summary>
    internal required bool ExistedOnDisk { get; init; }

    /// <summary>The text as it was on disk, or <see langword="null"/> for a file being created.</summary>
    internal string? OriginalText { get; set; }

    /// <summary>The text as the edit leaves it, or <see langword="null"/> for a file being deleted.</summary>
    internal string? NewText { get; set; }

    /// <summary>The encoding and line ending to write it back with.</summary>
    internal TextFileFormat Format { get; set; } = TextFileFormat.CreatedFileDefault;

    /// <summary>How many text edits landed in this file.</summary>
    internal int EditCount { get; set; }

    /// <summary>The edit created this file.</summary>
    internal bool Created { get; set; }

    /// <summary>The edit deletes this file.</summary>
    internal bool Deleted { get; set; }

    /// <summary>The absolute path this file is renamed to, when it is.</summary>
    internal string? MovedToPath { get; set; }

    /// <summary>The workspace-relative path this file is renamed to, for reporting.</summary>
    internal string? MovedToRelativePath { get; set; }

    /// <summary>The workspace-relative path this file was renamed from, when it is a rename target.</summary>
    internal string? MovedFromRelativePath { get; set; }

    /// <summary>Whether the contents differ from what is on disk.</summary>
    internal bool ContentChanged =>
        !string.Equals(OriginalText ?? string.Empty, NewText ?? string.Empty, StringComparison.Ordinal);

    /// <summary>Whether this file is worth reporting at all.</summary>
    internal bool IsInteresting => Created || Deleted || MovedToPath is not null || EditCount > 0 || ContentChanged;
}

/// <summary>
/// A workspace edit resolved against the files on disk, but not yet written.
/// </summary>
/// <remarks>
/// The split between planning and applying is what makes <c>preview</c> honest (D62): a preview runs
/// the identical planning pass — the same guard, the same offsets, the same encoding round trip —
/// and simply never calls <see cref="WorkspaceEditApplier.Apply"/>. A preview that computed a diff
/// some other way would be a description of a different operation.
/// </remarks>
/// <param name="Files">Every file the edit touches, in the order it first touched them.</param>
internal sealed record WorkspaceEditPlan(IReadOnlyList<PlannedFile> Files)
{
    /// <summary>The files worth reporting: everything that is created, deleted, renamed or changed.</summary>
    internal IReadOnlyList<PlannedFile> ChangedFiles => [.. Files.Where(file => file.IsInteresting)];

    /// <summary>How many text edits the whole plan carries.</summary>
    internal int TotalEditCount => Files.Sum(file => file.EditCount);

    /// <summary>Whether the plan would change anything at all.</summary>
    internal bool IsEmpty => ChangedFiles.Count == 0;
}
