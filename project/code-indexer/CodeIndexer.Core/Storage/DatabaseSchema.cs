using Microsoft.Data.Sqlite;
namespace CodeIndexer.Core.Storage;
public static class DatabaseSchema
{
    public const int SchemaVersion = 6;
    private static readonly string[] CreateStatements =
    {
        "CREATE TABLE IF NOT EXISTS meta ( key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL );",
        "CREATE TABLE IF NOT EXISTS solutions ( id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, path TEXT NOT NULL, indexed_at TEXT NOT NULL );",
        "CREATE TABLE IF NOT EXISTS solution_projects ( solution_id INTEGER NOT NULL REFERENCES solutions(id), project_id INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE, PRIMARY KEY (solution_id, project_id) );",
        "CREATE TABLE IF NOT EXISTS projects ( id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, path TEXT NOT NULL, target_frameworks TEXT NOT NULL, indexed_tfm TEXT NOT NULL, language TEXT NOT NULL DEFAULT 'C#', is_external INTEGER NOT NULL DEFAULT 0 );",
        "CREATE TABLE IF NOT EXISTS files ( id INTEGER PRIMARY KEY AUTOINCREMENT, project_id INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE, path TEXT NOT NULL, content_hash TEXT NOT NULL, line_count INTEGER NOT NULL DEFAULT 0, is_generated INTEGER NOT NULL DEFAULT 0, size_bytes INTEGER, modified_ticks INTEGER );",
        "CREATE TABLE IF NOT EXISTS symbols ( id INTEGER PRIMARY KEY AUTOINCREMENT, file_id INTEGER REFERENCES files(id) ON DELETE CASCADE, key TEXT NOT NULL, kind TEXT NOT NULL, name TEXT NOT NULL, full_name TEXT NOT NULL, container_id INTEGER REFERENCES symbols(id) ON DELETE SET NULL, signature TEXT, accessibility TEXT, start_line INTEGER, end_line INTEGER, doc_summary TEXT, is_generated INTEGER NOT NULL DEFAULT 0, is_external INTEGER NOT NULL DEFAULT 0, assembly TEXT );",
        "CREATE TABLE IF NOT EXISTS symbol_locations ( symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE, file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE, start_line INTEGER NOT NULL, start_col INTEGER NOT NULL, end_line INTEGER NOT NULL, end_col INTEGER NOT NULL, is_primary INTEGER NOT NULL DEFAULT 0 );",
        "CREATE TABLE IF NOT EXISTS diagnostics ( id INTEGER PRIMARY KEY AUTOINCREMENT, severity TEXT NOT NULL, message TEXT NOT NULL, project_id INTEGER REFERENCES projects(id) ON DELETE SET NULL, file_id INTEGER REFERENCES files(id) ON DELETE SET NULL );",
        "CREATE TABLE IF NOT EXISTS symbol_references ( id INTEGER PRIMARY KEY AUTOINCREMENT, symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE, file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE, line INTEGER NOT NULL, column INTEGER NOT NULL, length INTEGER NOT NULL DEFAULT 1, kind TEXT NOT NULL DEFAULT 'use', reference_name TEXT, enclosing_symbol_id INTEGER REFERENCES symbols(id) ON DELETE SET NULL );",
        "CREATE TABLE IF NOT EXISTS edges ( from_symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE, to_symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE, kind TEXT NOT NULL, PRIMARY KEY (from_symbol_id, to_symbol_id, kind) ) WITHOUT ROWID;",
        "CREATE TABLE IF NOT EXISTS inputs ( path TEXT NOT NULL, kind TEXT NOT NULL, owner TEXT NOT NULL DEFAULT '', indexed INTEGER NOT NULL DEFAULT 1, hash TEXT NOT NULL, size_bytes INTEGER NOT NULL DEFAULT 0, modified_ticks INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (path, kind, owner) );",
        "CREATE INDEX IF NOT EXISTS idx_inputs_owner ON inputs(owner);",
        "CREATE VIRTUAL TABLE IF NOT EXISTS symbols_fts USING fts5(name, full_name, signature, doc_summary, content='symbols', content_rowid='id', tokenize='porter unicode61');",
        "CREATE UNIQUE INDEX IF NOT EXISTS idx_symbols_key ON symbols(key);",
        "CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);",
        "CREATE INDEX IF NOT EXISTS idx_symbols_full_name ON symbols(full_name);",
        "CREATE INDEX IF NOT EXISTS idx_symbols_file_id ON symbols(file_id);",
        "CREATE INDEX IF NOT EXISTS idx_symbols_container ON symbols(container_id);",
        "CREATE INDEX IF NOT EXISTS idx_files_project_id ON files(project_id);",
        "CREATE UNIQUE INDEX IF NOT EXISTS idx_files_project_path ON files(project_id, path);",
        "CREATE INDEX IF NOT EXISTS idx_files_path ON files(path);",
        "CREATE UNIQUE INDEX IF NOT EXISTS idx_projects_path ON projects(path);",
        "CREATE INDEX IF NOT EXISTS idx_solution_projects_solution ON solution_projects(solution_id);",
        "CREATE INDEX IF NOT EXISTS idx_references_symbol_id ON symbol_references(symbol_id);",
        "CREATE INDEX IF NOT EXISTS idx_references_file_id ON symbol_references(file_id, line);",
        "CREATE INDEX IF NOT EXISTS idx_references_enclosing ON symbol_references(enclosing_symbol_id);",
        "CREATE INDEX IF NOT EXISTS idx_locations_symbol ON symbol_locations(symbol_id);",
        "CREATE INDEX IF NOT EXISTS idx_locations_file_line ON symbol_locations(file_id, start_line);",
        "CREATE INDEX IF NOT EXISTS idx_edges_from ON edges(from_symbol_id);",
        "CREATE INDEX IF NOT EXISTS idx_edges_to ON edges(to_symbol_id);",
    };
    public static void EnsureSchema(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta'";
        if (Convert.ToInt32(cmd.ExecuteScalar()) > 0)
        {
            cmd.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
            if (cmd.ExecuteScalar() is string old && int.TryParse(old, out var oldVersion) && oldVersion < SchemaVersion)
                throw new InvalidOperationException("Database has schema version " + oldVersion + ", but this tool needs " + SchemaVersion + ". Re-run 'codeindex index' to rebuild it.");
        }
        foreach (var stmt in CreateStatements)
        {
            cmd.CommandText = stmt;
            cmd.ExecuteNonQuery();
        }
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
        var existing = cmd.ExecuteScalar() as string;
        if (existing is not null)
        {
            if (int.TryParse(existing, out var version) && version > SchemaVersion)
                throw new InvalidOperationException("Database has schema version " + version + ", but this tool only supports up to " + SchemaVersion + ".");
        }
        cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', @v)";
        cmd.Parameters.AddWithValue("@v", SchemaVersion.ToString());
        cmd.ExecuteNonQuery();
    }
    public static void SetMeta(SqliteConnection connection, string key, string value)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES (@k, @v)";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }
    public static string? GetMeta(SqliteConnection connection, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = @k";
        cmd.Parameters.AddWithValue("@k", key);
        return cmd.ExecuteScalar() as string;
    }
}
