using CodeIndexer.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CodeIndexer.Core.Queries;

public sealed record StalenessResult(bool IsStale, IReadOnlyList<string> ChangedFiles, IReadOnlyList<string> MissingFiles)
{
    public static readonly StalenessResult Fresh = new(false, Array.Empty<string>(), Array.Empty<string>());
    public IEnumerable<string> AllFiles => ChangedFiles.Concat(MissingFiles);
}

/// <summary>
/// Compares every indexed file with the file on disk. Size and modification time are checked first, so an untouched
/// file costs one stat; only a file whose stat differs is hashed, and it counts as changed only if the hash differs.
/// Detects modified and deleted files; it cannot know about files added since indexing.
/// </summary>
public static class Staleness
{
    public static StalenessResult Check(SqliteConnection connection, string? repoRoot)
    {
        if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
            return StalenessResult.Fresh;

        var changed = new List<string>();
        var missing = new List<string>();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT path, content_hash, size_bytes, modified_ticks FROM files";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var relative = reader.GetString(0);
            var storedHash = reader.GetString(1);
            long? size = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            long? ticks = reader.IsDBNull(3) ? null : reader.GetInt64(3);

            // Source-generator output has no file on disk and was indexed without a stat; nothing to compare.
            if (size is null) continue;

            var info = new FileInfo(Path.Combine(repoRoot, relative));
            if (!info.Exists)
            {
                missing.Add(relative);
                continue;
            }
            if (info.Length == size && info.LastWriteTimeUtc.Ticks == ticks)
                continue;

            if (IndexDatabase.ComputeContentHash(File.ReadAllBytes(info.FullName)) != storedHash)
                changed.Add(relative);
        }

        return changed.Count == 0 && missing.Count == 0
            ? StalenessResult.Fresh
            : new StalenessResult(true, changed, missing);
    }
}
