using System.Globalization;
using System.Text;

namespace ClaudeRoslynLsp.Edits;

/// <summary>
/// Renders a plan as a unified diff, inside a line budget.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why budgeted (D62).</b> The diff exists so a model can decide whether to apply a preview, and
/// a fix-all across a solution can rewrite two hundred files. An unbudgeted diff would spend tens of
/// thousands of tokens describing an edit whose shape was clear from the first three hunks, and the
/// caller would pay that on every preview. So the diff is capped, the cap is shared out across the
/// files, and what was cut is <em>named</em> — "… 14 more hunks" is information, silence is not.
/// </para>
/// <para>
/// The algorithm trims the common prefix and suffix first and only then compares, which is what
/// makes it cheap for the case that actually happens: a rename changes four characters in a
/// thousand-line file. When the differing middle is genuinely large the comparison is abandoned in
/// favour of one replace hunk rather than paying a quadratic cost to produce output the budget would
/// throw away anyway.
/// </para>
/// </remarks>
internal static class UnifiedDiff
{
    /// <summary>The default line budget for a whole diff.</summary>
    internal const int DefaultMaxDiffLines = 200;

    /// <summary>How many unchanged lines surround each hunk.</summary>
    private const int ContextLines = 3;

    /// <summary>The smallest useful share of the budget one file may get.</summary>
    private const int MinimumFileBudget = 8;

    /// <summary>Above this many differing lines on either side, the diff stops being line-accurate.</summary>
    private const int LargestComparableRegion = 2000;

    /// <summary>Renders a plan.</summary>
    /// <param name="plan">The planned edit.</param>
    /// <param name="maxDiffLines">The whole diff's line budget.</param>
    internal static string Render(WorkspaceEditPlan plan, int maxDiffLines = DefaultMaxDiffLines)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var files = plan.ChangedFiles;

        if (files.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var budget = Math.Max(MinimumFileBudget, maxDiffLines);
        var written = 0;

        for (var index = 0; index < files.Count; index++)
        {
            var remainingFiles = files.Count - index;
            var share = Math.Max(MinimumFileBudget, (budget - written) / remainingFiles);

            if (written >= budget)
            {
                builder.Append("… ")
                    .Append(remainingFiles.ToString(CultureInfo.InvariantCulture))
                    .Append(remainingFiles == 1 ? " more file changed" : " more files changed")
                    .Append('\n');
                break;
            }

            written += RenderFile(builder, files[index], share);
        }

        return builder.ToString();
    }

    private static int RenderFile(StringBuilder builder, PlannedFile file, int budget)
    {
        var written = 0;

        var oldLabel = file.MovedFromRelativePath ?? file.RelativePath;
        var newLabel = file.MovedToRelativePath ?? file.RelativePath;

        builder.Append("--- ").Append(file.Created ? "/dev/null" : "a/" + oldLabel).Append('\n');
        builder.Append("+++ ").Append(file.Deleted && file.MovedToPath is null ? "/dev/null" : "b/" + newLabel).Append('\n');
        written += 2;

        var hunks = Hunks(SplitLines(file.OriginalText), SplitLines(file.Deleted ? null : file.NewText));
        var emitted = 0;

        foreach (var hunk in hunks)
        {
            if (written + hunk.Lines.Count + 1 > budget && emitted > 0)
            {
                break;
            }

            builder.Append(hunk.Header).Append('\n');
            written++;

            foreach (var line in hunk.Lines)
            {
                builder.Append(line).Append('\n');
                written++;
            }

            emitted++;
        }

        if (emitted < hunks.Count)
        {
            var remaining = hunks.Count - emitted;
            builder.Append("… ")
                .Append(remaining.ToString(CultureInfo.InvariantCulture))
                .Append(remaining == 1 ? " more hunk" : " more hunks")
                .Append('\n');
            written++;
        }

        return written;
    }

    private static List<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var lines = new List<string>();
        var start = 0;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (character == '\r')
            {
                lines.Add(text[start..index]);

                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                start = index + 1;
            }
            else if (character == '\n')
            {
                lines.Add(text[start..index]);
                start = index + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    private sealed record Hunk(string Header, List<string> Lines);

    private static List<Hunk> Hunks(List<string> oldLines, List<string> newLines)
    {
        var prefix = 0;

        while (prefix < oldLines.Count
            && prefix < newLines.Count
            && string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;

        while (suffix < oldLines.Count - prefix
            && suffix < newLines.Count - prefix
            && string.Equals(
                oldLines[oldLines.Count - 1 - suffix],
                newLines[newLines.Count - 1 - suffix],
                StringComparison.Ordinal))
        {
            suffix++;
        }

        var oldMiddle = oldLines[prefix..(oldLines.Count - suffix)];
        var newMiddle = newLines[prefix..(newLines.Count - suffix)];

        if (oldMiddle.Count == 0 && newMiddle.Count == 0)
        {
            return [];
        }

        var operations = oldMiddle.Count > LargestComparableRegion || newMiddle.Count > LargestComparableRegion
            ? WholesaleReplacement(oldMiddle, newMiddle)
            : LongestCommonSubsequenceDiff(oldMiddle, newMiddle);

        return GroupIntoHunks(operations, prefix, oldLines, newLines, suffix);
    }

    private enum EditKind
    {
        Keep,
        Remove,
        Add,
    }

    private readonly record struct EditOperation(EditKind Kind, string Line);

    private static List<EditOperation> WholesaleReplacement(List<string> oldMiddle, List<string> newMiddle)
    {
        var operations = new List<EditOperation>(oldMiddle.Count + newMiddle.Count);
        operations.AddRange(oldMiddle.Select(line => new EditOperation(EditKind.Remove, line)));
        operations.AddRange(newMiddle.Select(line => new EditOperation(EditKind.Add, line)));
        return operations;
    }

    private static List<EditOperation> LongestCommonSubsequenceDiff(List<string> oldMiddle, List<string> newMiddle)
    {
        var rows = oldMiddle.Count + 1;
        var columns = newMiddle.Count + 1;
        var lengths = new int[rows, columns];

        for (var row = oldMiddle.Count - 1; row >= 0; row--)
        {
            for (var column = newMiddle.Count - 1; column >= 0; column--)
            {
                lengths[row, column] = string.Equals(oldMiddle[row], newMiddle[column], StringComparison.Ordinal)
                    ? lengths[row + 1, column + 1] + 1
                    : Math.Max(lengths[row + 1, column], lengths[row, column + 1]);
            }
        }

        var operations = new List<EditOperation>();
        var oldIndex = 0;
        var newIndex = 0;

        while (oldIndex < oldMiddle.Count && newIndex < newMiddle.Count)
        {
            if (string.Equals(oldMiddle[oldIndex], newMiddle[newIndex], StringComparison.Ordinal))
            {
                operations.Add(new EditOperation(EditKind.Keep, oldMiddle[oldIndex]));
                oldIndex++;
                newIndex++;
            }
            else if (lengths[oldIndex + 1, newIndex] >= lengths[oldIndex, newIndex + 1])
            {
                operations.Add(new EditOperation(EditKind.Remove, oldMiddle[oldIndex]));
                oldIndex++;
            }
            else
            {
                operations.Add(new EditOperation(EditKind.Add, newMiddle[newIndex]));
                newIndex++;
            }
        }

        while (oldIndex < oldMiddle.Count)
        {
            operations.Add(new EditOperation(EditKind.Remove, oldMiddle[oldIndex]));
            oldIndex++;
        }

        while (newIndex < newMiddle.Count)
        {
            operations.Add(new EditOperation(EditKind.Add, newMiddle[newIndex]));
            newIndex++;
        }

        return operations;
    }

    private static List<Hunk> GroupIntoHunks(
        List<EditOperation> operations,
        int prefix,
        List<string> oldLines,
        List<string> newLines,
        int suffix)
    {
        // Re-attach the trimmed prefix and suffix as context so the hunk headers carry real line
        // numbers, which is the only part of a unified diff a reader uses to find the place.
        var full = new List<EditOperation>(operations.Count + (2 * ContextLines));

        for (var index = Math.Max(0, prefix - ContextLines); index < prefix; index++)
        {
            full.Add(new EditOperation(EditKind.Keep, oldLines[index]));
        }

        var leadingContext = prefix - Math.Max(0, prefix - ContextLines);
        full.AddRange(operations);

        var suffixStart = oldLines.Count - suffix;

        for (var index = suffixStart; index < Math.Min(oldLines.Count, suffixStart + ContextLines); index++)
        {
            full.Add(new EditOperation(EditKind.Keep, oldLines[index]));
        }

        var hunks = new List<Hunk>();
        var oldLine = prefix - leadingContext + 1;
        var newLine = prefix - leadingContext + 1;

        var index2 = 0;

        while (index2 < full.Count)
        {
            if (full[index2].Kind == EditKind.Keep)
            {
                oldLine++;
                newLine++;
                index2++;
                continue;
            }

            // Walk back up to ContextLines unchanged lines, then forward until ContextLines
            // unchanged lines in a row have gone by with no change after them.
            var startIndex = index2;
            var back = 0;

            while (startIndex > 0 && back < ContextLines && full[startIndex - 1].Kind == EditKind.Keep)
            {
                startIndex--;
                back++;
                oldLine--;
                newLine--;
            }

            var endIndex = index2;
            var trailing = 0;

            while (endIndex < full.Count)
            {
                if (full[endIndex].Kind == EditKind.Keep)
                {
                    // Close the hunk once it has its trailing context and the next change — if there
                    // is one at all — is far enough away to deserve a hunk of its own. Looking ahead
                    // is what stops two changes three lines apart from being reported as two hunks
                    // whose context overlaps.
                    if (trailing >= ContextLines && !HasChangeWithin(full, endIndex, ContextLines + 1))
                    {
                        break;
                    }

                    trailing++;
                }
                else
                {
                    trailing = 0;
                }

                endIndex++;
            }

            var lines = new List<string>();
            var oldCount = 0;
            var newCount = 0;

            for (var cursor = startIndex; cursor < endIndex; cursor++)
            {
                var operation = full[cursor];

                switch (operation.Kind)
                {
                    case EditKind.Keep:
                        lines.Add(" " + operation.Line);
                        oldCount++;
                        newCount++;
                        break;

                    case EditKind.Remove:
                        lines.Add("-" + operation.Line);
                        oldCount++;
                        break;

                    default:
                        lines.Add("+" + operation.Line);
                        newCount++;
                        break;
                }
            }

            var header = string.Create(
                CultureInfo.InvariantCulture,
                $"@@ -{oldLine},{oldCount} +{newLine},{newCount} @@");

            hunks.Add(new Hunk(header, lines));

            oldLine += oldCount;
            newLine += newCount;
            index2 = endIndex;
        }

        return hunks;
    }

    private static bool HasChangeWithin(List<EditOperation> operations, int from, int count)
    {
        var limit = Math.Min(operations.Count, from + count);

        for (var index = from; index < limit; index++)
        {
            if (operations[index].Kind != EditKind.Keep)
            {
                return true;
            }
        }

        return false;
    }
}
