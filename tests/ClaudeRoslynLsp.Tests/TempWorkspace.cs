namespace ClaudeRoslynLsp.Tests;

/// <summary>
/// A throwaway directory tree for one test, deleted when the test ends.
/// </summary>
/// <remarks>
/// Acquisition and discovery are both filesystem behaviour, and both are the kind of behaviour a
/// fake filesystem gets subtly wrong: path casing, directory-move semantics across a rename, a lock
/// file's <see cref="FileShare"/> flags. So these tests use the real one, in a directory nobody else
/// is in.
/// </remarks>
internal sealed class TempWorkspace : IDisposable
{
    private TempWorkspace(string root) => Root = root;

    /// <summary>The fully-qualified root of this workspace.</summary>
    public string Root { get; }

    /// <summary>Creates a new empty workspace under the system temp directory.</summary>
    /// <param name="name">A short label that ends up in the directory name, to make a leak diagnosable.</param>
    public static TempWorkspace Create(string name)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "claude-roslyn-lsp-tests",
            name + "-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);

        return new TempWorkspace(root);
    }

    /// <summary>Combines a relative path onto the root.</summary>
    /// <param name="parts">Path segments.</param>
    public string Path_(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>Creates a directory under the root and returns it.</summary>
    /// <param name="parts">Path segments.</param>
    public string Directory_(params string[] parts)
    {
        var path = Path_(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a file under the root, creating its directory, and returns its path.</summary>
    /// <param name="relativePath">The path relative to the root.</param>
    /// <param name="content">The file's contents.</param>
    public string File_(string relativePath, string content = "")
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Deletes the whole tree, tolerating a file another process is still holding.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
