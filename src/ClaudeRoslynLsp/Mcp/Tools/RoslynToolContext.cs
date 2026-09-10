using ClaudeRoslynLsp.Configuration;
using ClaudeRoslynLsp.Edits;
using ClaudeRoslynLsp.Mcp.Engine;
using ClaudeRoslynLsp.Mcp.Models;

namespace ClaudeRoslynLsp.Mcp.Tools;

/// <summary>
/// Everything a tool method needs, as one injected collaborator.
/// </summary>
/// <remarks>
/// <para>
/// One parameter rather than five because every parameter a tool method declares is a parameter the
/// SDK has to be told <em>not</em> to put in the generated schema — it decides that by asking the
/// container whether the type is a service, so an unregistered collaborator silently becomes a
/// required argument the model then has to invent. One registration, one exclusion, and the schema
/// tests only have one name to watch for.
/// </para>
/// <para>
/// It also owns the two operations every tool shares: the readiness gate, and turning a resolved
/// <see cref="WorkspaceEdit"/> into an <see cref="EditResult"/>. Both are places where getting it
/// slightly different in five tools would be five slightly different products.
/// </para>
/// </remarks>
internal sealed class RoslynToolContext
{
    /// <summary>Creates the context.</summary>
    /// <param name="engine">The Roslyn session.</param>
    /// <param name="options">The process's configuration.</param>
    /// <param name="guard">The workspace root every path is bounded by.</param>
    /// <param name="applier">The edit applier.</param>
    /// <param name="cache">The preview-to-apply cache.</param>
    internal RoslynToolContext(
        IRoslynEngine engine,
        ClaudeRoslynLspOptions options,
        WorkspacePathGuard guard,
        WorkspaceEditApplier applier,
        EditCache cache)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(cache);

        Engine = engine;
        Options = options;
        Guard = guard;
        Applier = applier;
        Cache = cache;
    }

    /// <summary>The Roslyn session.</summary>
    internal IRoslynEngine Engine { get; }

    /// <summary>The process's configuration.</summary>
    internal ClaudeRoslynLspOptions Options { get; }

    /// <summary>The workspace root every path is bounded by.</summary>
    internal WorkspacePathGuard Guard { get; }

    /// <summary>The edit applier.</summary>
    internal WorkspaceEditApplier Applier { get; }

    /// <summary>The preview-to-apply cache.</summary>
    internal EditCache Cache { get; }

    /// <summary>
    /// Waits for the workspace, for as long as <c>CLAUDE_ROSLYN_LSP_READY_TIMEOUT_SECONDS</c> allows.
    /// </summary>
    /// <param name="cancellationToken">The client's cancellation.</param>
    internal Task<WorkspaceState> EnsureReadyAsync(CancellationToken cancellationToken) =>
        Engine.EnsureReadyAsync(TimeSpan.FromSeconds(Options.ReadyTimeoutSeconds), cancellationToken);

    /// <summary>The <c>file:</c> URI for a path a model supplied.</summary>
    /// <param name="path">A workspace-relative or absolute path.</param>
    internal string UriFor(string? path) => WorkspacePathGuard.ToUri(Guard.FromModelPath(path));

    /// <summary>The workspace-relative path for a URI Roslyn reported, or <see langword="null"/> when it is outside the workspace.</summary>
    /// <param name="uri">The URI.</param>
    internal string? RelativeOrNull(string? uri) =>
        Guard.TryResolve(uri, out var path, out _) ? Guard.ToRelative(path) : null;

    /// <summary>
    /// The workspace-relative spelling of a local path, or the path unchanged when it sits outside
    /// the workspace.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="RelativeOrNull"/> this never returns <see langword="null"/>: the solution
    /// file and the project files it names are worth reporting even when a repository keeps them
    /// somewhere the guard would refuse to write to.
    /// </remarks>
    /// <param name="path">A local path, as configuration or MSBuild spelled it.</param>
    internal string RelativeFromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            var full = Path.GetFullPath(path);
            return Guard.IsInsideRoot(full) ? Guard.ToRelative(full) : path;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>
    /// Turns a resolved edit into the answer every mutating tool gives, applying it unless this is a
    /// preview.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plan is computed identically either way (D62), so the diff a preview shows is the diff the
    /// apply writes. A preview stores the resolved edit under <paramref name="cacheKey"/>; the apply
    /// that follows reuses it, which is what stops the two calls from being about two different
    /// versions of the file.
    /// </para>
    /// <para>
    /// After a real write, Roslyn is told what changed. Without that the workspace it is answering
    /// from is the one from before the edit, and every later answer is confidently stale — which is
    /// the worst failure mode this product has (C33 for why a creation needs more than a create
    /// event; the engine owns that rule).
    /// </para>
    /// </remarks>
    /// <param name="tool">The tool's MCP name, for error text.</param>
    /// <param name="cacheKey">The preview-to-apply key from <see cref="EditCache.KeyFor"/>.</param>
    /// <param name="edit">The resolved edit.</param>
    /// <param name="preview">Whether to write anything.</param>
    /// <param name="cancellationToken">The client's cancellation.</param>
    internal async Task<EditResult> ApplyEditAsync(
        string tool,
        string cacheKey,
        WorkspaceEdit? edit,
        bool preview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(cacheKey);

        var plan = Applier.Plan(edit);

        if (plan.IsEmpty)
        {
            return new EditResult(ToolStatus.Ok, Applied: false, Note: EditResult.NothingToDoNote);
        }

        var changed = plan.ChangedFiles;

        var files = changed
            .Select(file => new EditedFile(
                file.RelativePath,
                file.EditCount,
                file.Created ? true : null,
                file is { Deleted: true, MovedToPath: null } ? true : null,
                file.MovedToRelativePath))
            .ToArray();

        var diff = UnifiedDiff.Render(plan);

        if (preview)
        {
            Cache.Store(cacheKey, edit!, plan);

            return new EditResult(
                ToolStatus.Ok,
                Applied: false,
                changed.Count,
                plan.TotalEditCount,
                files,
                diff,
                EditResult.PreviewNote);
        }

        WorkspaceEditApplier.Apply(plan);

        // Everything the cache remembered was computed against files this write has just changed.
        Cache.Clear();

        await Engine.NotifyFilesChangedAsync(FileChangesOf(changed), cancellationToken).ConfigureAwait(false);

        return new EditResult(
            ToolStatus.Ok,
            Applied: true,
            changed.Count,
            plan.TotalEditCount,
            files,
            diff,
            EditResult.AppliedNote);
    }

    private static List<FileChange> FileChangesOf(IReadOnlyList<PlannedFile> files)
    {
        var changes = new List<FileChange>(files.Count + 1);

        foreach (var file in files)
        {
            if (file.MovedToPath is { } moved)
            {
                changes.Add(new FileChange(WorkspacePathGuard.ToUri(file.Path), FileChangeType.Deleted));
                changes.Add(new FileChange(WorkspacePathGuard.ToUri(moved), FileChangeType.Created));
                continue;
            }

            var type = file switch
            {
                { Deleted: true } => FileChangeType.Deleted,
                { Created: true } => FileChangeType.Created,
                _ => FileChangeType.Changed,
            };

            changes.Add(new FileChange(WorkspacePathGuard.ToUri(file.Path), type));
        }

        return changes;
    }
}
