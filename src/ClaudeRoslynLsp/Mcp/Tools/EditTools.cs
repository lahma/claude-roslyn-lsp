using System.ComponentModel;

using ClaudeRoslynLsp.Edits;
using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Mcp.Models;

using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ClaudeRoslynLsp.Mcp.Tools;

/// <summary>
/// The two mutating tools that are not code actions: a solution-wide rename, and formatting.
/// </summary>
[McpServerToolType]
internal sealed class EditTools
{
    /// <summary>How many files one <c>formatCode</c> call will format.</summary>
    private const int MaximumFormattedFiles = 500;

    private EditTools()
    {
    }

    /// <summary>Renames a symbol everywhere it is used.</summary>
    /// <param name="context">The injected tool context.</param>
    /// <param name="symbol">The symbol to rename.</param>
    /// <param name="newName">Its new name.</param>
    /// <param name="preview">Whether to write anything.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    [McpServerTool(
        Name = "renameSymbol",
        Title = "Rename symbol",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Renames a C# symbol and every use of it across the whole solution, semantically. Use this instead of sed, "
        + "Edit or a search-and-replace: it follows overrides and interface implementations, crosses projects, "
        + "updates the name inside nameof and string interpolation where the compiler tracks it, and never touches "
        + "an identically named symbol in another type or a word inside a comment. It does not rename the file the "
        + "type lives in — use getCodeActions for \"Move type to X.cs\" if you want that too. Pass preview: true "
        + "first to see the diff; the apply that follows reuses exactly the edit you previewed.")]
    public static async Task<EditResult> RenameSymbolAsync(
        RoslynToolContext context,
        [Description("The symbol to rename: a simple or partially qualified name (IScheduler.Start), or a position as path:line:col with 1-based numbers.")]
        string symbol,
        [Description("The new name, without any namespace or type qualification.")]
        string newName,
        [Description("Report the diff without writing anything. Default false.")]
        bool preview = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await ToolErrors.ExecuteAsync("renameSymbol", async () =>
        {
            var address = SymbolAddress.Parse(symbol);

            if (string.IsNullOrWhiteSpace(newName))
            {
                throw new ArgumentException("a new name is required", nameof(newName));
            }

            var state = await context.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

            if (!state.IsReady)
            {
                return NotReadyResults.Edit(state);
            }

            var key = EditCache.KeyFor("renameSymbol", address.Text, newName.Trim());

            if (!preview && context.Cache.TryTake(key) is { } cached)
            {
                return await context
                    .ApplyEditAsync("renameSymbol", key, cached, preview: false, cancellationToken)
                    .ConfigureAwait(false);
            }

            var resolved = await ToolLookup
                .FindOneAsync(context, "renameSymbol", address, kind: null, cancellationToken)
                .ConfigureAwait(false);

            // prepareRename first, because Roslyn's answer to "can this be renamed" is a range and its
            // answer to "rename it" is an edit — and an unrenamable position produces an empty edit
            // rather than an error, which would look like a rename that changed nothing (C23).
            _ = await context.Engine
                    .PrepareRenameAsync(resolved.Uri, resolved.Position, cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new McpException(
                    $"renameSymbol cannot rename '{address.Text}' at "
                    + $"{resolved.Match.Path}:{resolved.Match.Line}:{resolved.Match.Column}. Roslyn renames "
                    + "declarations it owns: a symbol from a referenced assembly, a keyword, a literal or a position "
                    + "that is not an identifier cannot be renamed here.");

            var edit = await context.Engine
                .RenameAsync(resolved.Uri, resolved.Position, newName.Trim(), cancellationToken)
                .ConfigureAwait(false);

            return await context.ApplyEditAsync("renameSymbol", key, edit, preview, cancellationToken)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Formats files, optionally organising their using directives.</summary>
    /// <param name="context">The injected tool context.</param>
    /// <param name="paths">The files to format.</param>
    /// <param name="project">A project whose C# files should all be formatted.</param>
    /// <param name="organizeUsings">Whether to sort and remove using directives as well.</param>
    /// <param name="preview">Whether to write anything.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    [McpServerTool(
        Name = "formatCode",
        Title = "Format code",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Formats C# files with Roslyn, honouring the repository's .editorconfig, and optionally sorts and removes "
        + "their using directives. Use this instead of running `dotnet format` in Bash: it is the same engine without "
        + "the MSBuild round trip, it needs no restore, and it reports exactly which files changed. Name the files "
        + "with paths, or a project to format all of its C# files. Formatting the same code twice changes nothing the "
        + "second time. Pass preview: true first to see the diff.")]
    public static async Task<EditResult> FormatCodeAsync(
        RoslynToolContext context,
        [Description("The files to format, workspace-relative with forward slashes.")]
        string[]? paths = null,
        [Description("A project, by name or by workspace-relative .csproj path, whose C# files should all be formatted. Ignored when paths is given.")]
        string? project = null,
        [Description("Also sort using directives and remove unnecessary ones. Default false, because it changes more lines than formatting alone.")]
        bool organizeUsings = false,
        [Description("Report the diff without writing anything. Default false.")]
        bool preview = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await ToolErrors.ExecuteAsync("formatCode", async () =>
        {
            var state = await context.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

            if (!state.IsReady)
            {
                return NotReadyResults.Edit(state);
            }

            var files = ResolveFiles(context, state, paths, project);

            var key = EditCache.KeyFor(
                "formatCode",
                string.Join('|', files.Select(context.Guard.ToRelative)),
                organizeUsings ? "organize" : "format");

            if (!preview && context.Cache.TryTake(key) is { } cached)
            {
                return await context
                    .ApplyEditAsync("formatCode", key, cached, preview: false, cancellationToken)
                    .ConfigureAwait(false);
            }

            // The setting is a configuration pull, not a request parameter: Roslyn reads
            // csharp|formatting.dotnet_organize_imports_on_format when it formats, and never takes it
            // as an argument (D48 is the standing answer this call overrides).
            await context.Engine
                .SetOrganizeImportsOnFormatAsync(organizeUsings, cancellationToken)
                .ConfigureAwait(false);

            var changes = new List<DocumentChange>(files.Count);

            foreach (var file in files)
            {
                await using var session = await DocumentSession
                    .OpenAsync(context, "formatCode", file, cancellationToken)
                    .ConfigureAwait(false);

                var edits = await context.Engine
                    .FormattingAsync(session.Uri, new LspFormattingOptions(), cancellationToken)
                    .ConfigureAwait(false);

                if (edits.Count == 0)
                {
                    continue;
                }

                changes.Add(new DocumentChange
                {
                    TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = session.Uri },
                    Edits = [.. edits],
                });
            }

            var edit = new WorkspaceEdit { DocumentChanges = [.. changes] };

            return await context.ApplyEditAsync("formatCode", key, edit, preview, cancellationToken)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static List<string> ResolveFiles(
        RoslynToolContext context,
        WorkspaceState state,
        string[]? paths,
        string? project)
    {
        if (paths is { Length: > 0 })
        {
            var resolved = new List<string>(paths.Length);

            foreach (var path in paths)
            {
                var full = context.Guard.FromModelPath(path);

                if (!File.Exists(full))
                {
                    throw ToolErrors.NotFound("formatCode", $"file at '{path}'");
                }

                resolved.Add(full);
            }

            return resolved;
        }

        if (string.IsNullOrWhiteSpace(project))
        {
            throw new ArgumentException(
                "formatCode needs either paths or a project; it will not format the whole solution by accident",
                nameof(paths));
        }

        var wanted = project.Trim();
        var projects = state.Projects ?? [];

        var match = projects.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? projects.FirstOrDefault(candidate =>
                context.RelativeFromPath(candidate.Path)
                    .EndsWith(wanted.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            ?? throw ToolErrors.Ambiguous(
                "formatCode",
                $"project '{wanted}'",
                projects.Select(candidate => candidate.Name));

        var directory = Path.GetDirectoryName(Path.GetFullPath(match.Path))
            ?? throw ToolErrors.NotFound("formatCode", $"directory of project '{wanted}'");

        var files = Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => DiagnosticQuery.IsSourceFile(context.Guard.ToRelative(file)))
            .Take(MaximumFormattedFiles + 1)
            .ToList();

        if (files.Count > MaximumFormattedFiles)
        {
            throw new McpException(
                $"formatCode will not format more than {MaximumFormattedFiles} files in one call; "
                + $"'{wanted}' has more than that. Name the files with paths instead.");
        }

        return files;
    }
}
