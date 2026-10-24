using CodeIndexer.Core.Indexing;
using Microsoft.Data.Sqlite;

namespace CodeIndexer.Core.Queries;

public sealed record StalenessResult(bool IsStale, IReadOnlyList<string> ChangedFiles, IReadOnlyList<string> MissingFiles)
{
    public static readonly StalenessResult Fresh = new(false, Array.Empty<string>(), Array.Empty<string>());
    public IEnumerable<string> AllFiles => ChangedFiles.Concat(MissingFiles);
}

/// <summary>
/// Compares what the index was built from with the disk. The recorded inputs are the source files of the indexed projects,
/// their project files and restore output, a fingerprint of each project directory's *.cs listing (so added, deleted, and
/// renamed files are noticed), and the solution and build-wide files. A file whose size and modification time are
/// unchanged costs one stat; only one whose stat differs is hashed, and it counts as changed only if its content differs.
/// Projects the filter excluded are not checked, because nothing indexed came from them.
/// </summary>
public static class Staleness
{
    public static StalenessResult Check(SqliteConnection connection, string? repoRoot)
    {
        if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
            return StalenessResult.Fresh;

        var stored = new List<InputRecord>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT path, kind, owner, indexed, hash, size_bytes, modified_ticks FROM inputs";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                stored.Add(new InputRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3) != 0, r.GetString(4), r.GetInt64(5), r.GetInt64(6)));
        }

        var changed = new List<string>();
        var missing = new List<string>();
        foreach (var change in InputScanner.FindChanges(stored, repoRoot, indexedOnly: true))
        {
            var path = change.Stored.Path;
            if (change.Reason is "deleted" or "directory deleted")
                missing.Add(path);
            else if (change.Stored.Kind == InputScanner.Listing)
                changed.Add($"{path}/ ({change.Reason})");
            else
                changed.Add(path);
        }

        return changed.Count == 0 && missing.Count == 0
            ? StalenessResult.Fresh
            : new StalenessResult(true, changed, missing);
    }
}
