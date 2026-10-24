namespace CodeIndexer.Core;

public static class PathUtil
{
    /// <summary>
    /// Path relative to <paramref name="root"/>, always with '/' separators so stored and emitted paths
    /// are identical on every platform.
    /// </summary>
    public static string ToRepoRelative(string root, string path) =>
        Normalize(Path.GetRelativePath(root, path));

    public static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="directory"/> or lies beneath it
    /// (separator-aware, so "apis/Foo" does not contain "apis/FooBar/x").
    /// </summary>
    public static bool IsUnderDirectory(string path, string directory)
    {
        var dir = Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }
}
