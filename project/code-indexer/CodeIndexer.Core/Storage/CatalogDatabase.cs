using Microsoft.Data.Sqlite;

namespace CodeIndexer.Core.Storage;

public sealed class CatalogEntry
{
    public required string SolutionName { get; init; }
    public required string SolutionPath { get; init; }
    public required string DatabasePath { get; init; }
    public required string IndexedCommit { get; init; }
    public required string IndexedAt { get; init; }
}

public static class CatalogDatabase
{
    private static readonly string CatalogSchema = "CREATE TABLE IF NOT EXISTS catalog ( solution_name TEXT PRIMARY KEY NOT NULL, solution_path TEXT NOT NULL, database_path TEXT NOT NULL, indexed_commit TEXT NOT NULL, indexed_at TEXT NOT NULL, stale INTEGER NOT NULL DEFAULT 0 );";

    public static void EnsureSchema(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = CatalogSchema;
        cmd.ExecuteNonQuery();
    }

    public static void Upsert(SqliteConnection connection, CatalogEntry entry)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO catalog (solution_name, solution_path, database_path, indexed_commit, indexed_at, stale) VALUES (@name, @spath, @dbpath, @commit, @at, 0)";
        cmd.Parameters.AddWithValue("@name", entry.SolutionName);
        cmd.Parameters.AddWithValue("@spath", entry.SolutionPath);
        cmd.Parameters.AddWithValue("@dbpath", entry.DatabasePath);
        cmd.Parameters.AddWithValue("@commit", entry.IndexedCommit);
        cmd.Parameters.AddWithValue("@at", entry.IndexedAt);
        cmd.ExecuteNonQuery();
    }

    public static List<CatalogEntry> List(SqliteConnection connection)
    {
        var entries = new List<CatalogEntry>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT solution_name, solution_path, database_path, indexed_commit, indexed_at FROM catalog ORDER BY solution_name";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new CatalogEntry
            {
                SolutionName = reader.GetString(0),
                SolutionPath = reader.GetString(1),
                DatabasePath = reader.GetString(2),
                IndexedCommit = reader.GetString(3),
                IndexedAt = reader.GetString(4),
            });
        }
        return entries;
    }

    public static CatalogEntry? Resolve(SqliteConnection connection, string nameOrPath)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT solution_name, solution_path, database_path, indexed_commit, indexed_at FROM catalog WHERE solution_name = @q OR solution_path LIKE @q2 OR database_path LIKE @q3";
        cmd.Parameters.AddWithValue("@q", nameOrPath);
        cmd.Parameters.AddWithValue("@q2", $"%{nameOrPath}%");
        cmd.Parameters.AddWithValue("@q3", $"%{nameOrPath}%");
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new CatalogEntry
            {
                SolutionName = reader.GetString(0),
                SolutionPath = reader.GetString(1),
                DatabasePath = reader.GetString(2),
                IndexedCommit = reader.GetString(3),
                IndexedAt = reader.GetString(4),
            };
        }
        return null;
    }
}
