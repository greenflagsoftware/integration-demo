using System.Security.Cryptography;
using System.Text;
using CodeIndexer.Core.Indexing;
using CodeIndexer.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndexer.Core.Storage;

public sealed class IndexDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _repoRoot;
    private bool _disposed;
    private SqliteTransaction? _transaction;

    // Prepared commands reused for every row of a bulk write; created lazily, disposed with the database.
    private readonly Dictionary<string, SqliteCommand> _prepared = new();

    public SqliteConnection Connection => _connection;

    public IndexDatabase(string dbPath, string repoRoot)
    {
        _repoRoot = repoRoot;
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = false };
        _connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            _connection.Open();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
            cmd.ExecuteNonQuery();
            DatabaseSchema.EnsureSchema(_connection);
        }
        catch
        {
            // A constructor that throws is never disposed; close the file so the caller can replace it.
            _connection.Dispose();
            throw;
        }
    }

    public void BeginTransaction() => _transaction = _connection.BeginTransaction();

    public void CommitTransaction()
    {
        _transaction?.Commit();
        _transaction?.Dispose();
        _transaction = null;
    }

    /// <summary>Folds the write-ahead log into the main file so the database can be moved as a single file.</summary>
    public void Checkpoint()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Prepared(string sql, params string[] parameterNames)
    {
        if (_prepared.TryGetValue(sql, out var cmd))
            return cmd;
        cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var name in parameterNames)
            cmd.Parameters.Add(new SqliteParameter(name, null));
        _prepared[sql] = cmd;
        return cmd;
    }

    private static object Db(object? value) => value ?? DBNull.Value;

    public long InsertSolution(string name, string relativePath, DateTime indexedAt)
    {
        var cmd = Prepared(
            "INSERT INTO solutions (name, path, indexed_at) VALUES (@name, @path, @at) RETURNING id",
            "@name", "@path", "@at");
        cmd.Parameters["@name"].Value = name;
        cmd.Parameters["@path"].Value = relativePath;
        cmd.Parameters["@at"].Value = indexedAt.ToString("O");
        return (long)cmd.ExecuteScalar()!;
    }

    public long InsertProject(string name, string relativePath, string targetFrameworks, string indexedTfm, string language, bool isExternal)
    {
        var cmd = Prepared(
            "INSERT INTO projects (name, path, target_frameworks, indexed_tfm, language, is_external) VALUES (@name, @path, @tfms, @tfm, @lang, @ext) RETURNING id",
            "@name", "@path", "@tfms", "@tfm", "@lang", "@ext");
        cmd.Parameters["@name"].Value = name;
        cmd.Parameters["@path"].Value = relativePath;
        cmd.Parameters["@tfms"].Value = targetFrameworks;
        cmd.Parameters["@tfm"].Value = indexedTfm;
        cmd.Parameters["@lang"].Value = language;
        cmd.Parameters["@ext"].Value = isExternal ? 1 : 0;
        return (long)cmd.ExecuteScalar()!;
    }

    public void LinkSolutionProject(long solutionId, long projectId)
    {
        var cmd = Prepared(
            "INSERT OR IGNORE INTO solution_projects (solution_id, project_id) VALUES (@sid, @pid)",
            "@sid", "@pid");
        cmd.Parameters["@sid"].Value = solutionId;
        cmd.Parameters["@pid"].Value = projectId;
        cmd.ExecuteNonQuery();
    }

    public static string ComputeContentHash(string text) => ComputeContentHash(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// SHA-256 of a file's bytes. The staleness check hashes the file on disk the same way, so the two always agree.
    /// </summary>
    public static string ComputeContentHash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public long InsertFile(long projectId, string relativePath, string contentHash, int lineCount, bool isGenerated, long? sizeBytes = null, long? modifiedTicks = null)
    {
        var cmd = Prepared(
            "INSERT INTO files (project_id, path, content_hash, line_count, is_generated, size_bytes, modified_ticks) VALUES (@pid, @path, @hash, @lines, @gen, @size, @mtime) RETURNING id",
            "@pid", "@path", "@hash", "@lines", "@gen", "@size", "@mtime");
        cmd.Parameters["@pid"].Value = projectId;
        cmd.Parameters["@path"].Value = relativePath;
        cmd.Parameters["@hash"].Value = contentHash;
        cmd.Parameters["@lines"].Value = lineCount;
        cmd.Parameters["@gen"].Value = isGenerated ? 1 : 0;
        cmd.Parameters["@size"].Value = Db(sizeBytes);
        cmd.Parameters["@mtime"].Value = Db(modifiedTicks);
        return (long)cmd.ExecuteScalar()!;
    }

    public long InsertSymbol(long? fileId, string key, string kind, string name, string fullName, long? containerId, string? signature, string? accessibility, int? startLine, int? endLine, string? docSummary, bool isGenerated, bool isExternal = false, string? assembly = null)
    {
        var cmd = Prepared(
            "INSERT INTO symbols (file_id, key, kind, name, full_name, container_id, signature, accessibility, start_line, end_line, doc_summary, is_generated, is_external, assembly) VALUES (@fid, @key, @kind, @name, @full, @cid, @sig, @acc, @start, @end, @doc, @gen, @ext, @asm) RETURNING id",
            "@fid", "@key", "@kind", "@name", "@full", "@cid", "@sig", "@acc", "@start", "@end", "@doc", "@gen", "@ext", "@asm");
        cmd.Parameters["@fid"].Value = Db(fileId);
        cmd.Parameters["@key"].Value = key;
        cmd.Parameters["@kind"].Value = kind;
        cmd.Parameters["@name"].Value = name;
        cmd.Parameters["@full"].Value = fullName;
        cmd.Parameters["@cid"].Value = Db(containerId);
        cmd.Parameters["@sig"].Value = Db(signature);
        cmd.Parameters["@acc"].Value = Db(accessibility);
        cmd.Parameters["@start"].Value = Db(startLine);
        cmd.Parameters["@end"].Value = Db(endLine);
        cmd.Parameters["@doc"].Value = Db(docSummary);
        cmd.Parameters["@gen"].Value = isGenerated ? 1 : 0;
        cmd.Parameters["@ext"].Value = isExternal ? 1 : 0;
        cmd.Parameters["@asm"].Value = Db(assembly);
        return (long)cmd.ExecuteScalar()!;
    }

    public void InsertLocation(long symbolId, long fileId, int startLine, int startCol, int endLine, int endCol, bool isPrimary)
    {
        var cmd = Prepared(
            "INSERT INTO symbol_locations (symbol_id, file_id, start_line, start_col, end_line, end_col, is_primary) VALUES (@sid, @fid, @sl, @sc, @el, @ec, @p)",
            "@sid", "@fid", "@sl", "@sc", "@el", "@ec", "@p");
        cmd.Parameters["@sid"].Value = symbolId;
        cmd.Parameters["@fid"].Value = fileId;
        cmd.Parameters["@sl"].Value = startLine;
        cmd.Parameters["@sc"].Value = startCol;
        cmd.Parameters["@el"].Value = endLine;
        cmd.Parameters["@ec"].Value = endCol;
        cmd.Parameters["@p"].Value = isPrimary ? 1 : 0;
        cmd.ExecuteNonQuery();
    }

    public void InsertDiagnostic(DiagnosticEntry diagnostic, long? projectId, long? fileId)
    {
        var cmd = Prepared(
            "INSERT INTO diagnostics (severity, message, project_id, file_id) VALUES (@sev, @msg, @pid, @fid)",
            "@sev", "@msg", "@pid", "@fid");
        cmd.Parameters["@sev"].Value = diagnostic.Severity.ToString();
        cmd.Parameters["@msg"].Value = diagnostic.Message;
        cmd.Parameters["@pid"].Value = Db(projectId);
        cmd.Parameters["@fid"].Value = Db(fileId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Rebuilds the external-content FTS index from the symbols table.</summary>
    public void RebuildFtsIndex()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO symbols_fts(symbols_fts) VALUES('rebuild');";
        cmd.ExecuteNonQuery();
    }

    public void InsertReference(long symbolId, long fileId, int line, int column, int length, string kind, long? enclosingSymbolId, string? referenceName)
    {
        var cmd = Prepared(
            "INSERT INTO symbol_references (symbol_id, file_id, line, column, length, kind, enclosing_symbol_id, reference_name) VALUES (@sid, @fid, @line, @col, @len, @kind, @enc, @rname)",
            "@sid", "@fid", "@line", "@col", "@len", "@kind", "@enc", "@rname");
        cmd.Parameters["@sid"].Value = symbolId;
        cmd.Parameters["@fid"].Value = fileId;
        cmd.Parameters["@line"].Value = line;
        cmd.Parameters["@col"].Value = column;
        cmd.Parameters["@len"].Value = length;
        cmd.Parameters["@kind"].Value = kind;
        cmd.Parameters["@enc"].Value = Db(enclosingSymbolId);
        cmd.Parameters["@rname"].Value = Db(referenceName);
        cmd.ExecuteNonQuery();
    }

    public void InsertEdge(long fromSymbolId, long toSymbolId, string kind)
    {
        var cmd = Prepared(
            "INSERT OR IGNORE INTO edges (from_symbol_id, to_symbol_id, kind) VALUES (@fid, @tid, @kind)",
            "@fid", "@tid", "@kind");
        cmd.Parameters["@fid"].Value = fromSymbolId;
        cmd.Parameters["@tid"].Value = toSymbolId;
        cmd.Parameters["@kind"].Value = kind;
        cmd.ExecuteNonQuery();
    }

    public long? GetProjectIdByPath(string relativePath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM projects WHERE path = @p";
        cmd.Parameters.AddWithValue("@p", relativePath);
        return cmd.ExecuteScalar() as long?;
    }

    public long? GetFileIdByProjectAndPath(long projectId, string relativePath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM files WHERE project_id = @pid AND path = @p";
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@p", relativePath);
        return cmd.ExecuteScalar() as long?;
    }

    // ---- inputs: everything the index depends on, for incremental updates and staleness ----------

    public void InsertInput(InputRecord input)
    {
        var cmd = Prepared(
            "INSERT OR REPLACE INTO inputs (path, kind, owner, indexed, hash, size_bytes, modified_ticks) VALUES (@p, @k, @o, @i, @h, @s, @m)",
            "@p", "@k", "@o", "@i", "@h", "@s", "@m");
        cmd.Parameters["@p"].Value = input.Path;
        cmd.Parameters["@k"].Value = input.Kind;
        cmd.Parameters["@o"].Value = input.Owner;
        cmd.Parameters["@i"].Value = input.Indexed ? 1 : 0;
        cmd.Parameters["@h"].Value = input.Hash;
        cmd.Parameters["@s"].Value = input.Size;
        cmd.Parameters["@m"].Value = input.ModifiedTicks;
        cmd.ExecuteNonQuery();
    }

    public List<InputRecord> GetInputs()
    {
        var list = new List<InputRecord>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT path, kind, owner, indexed, hash, size_bytes, modified_ticks FROM inputs ORDER BY path, kind, owner";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new InputRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3) != 0, r.GetString(4), r.GetInt64(5), r.GetInt64(6)));
        return list;
    }

    /// <summary>Removes the inputs owned by a project (owner = its repo-relative path), or the build-wide ones (owner = "").</summary>
    public void DeleteInputsByOwner(string owner)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM inputs WHERE owner = @o";
        cmd.Parameters.AddWithValue("@o", owner);
        cmd.ExecuteNonQuery();
    }

    // ---- incremental update helpers ---------------------------------------------------------------

    public List<(long Id, string Path)> GetProjects()
    {
        var list = new List<(long, string)>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, path FROM projects";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1)));
        return list;
    }

    /// <summary>
    /// Deletes a project and, through the foreign keys, its files, symbols, locations, references and edges, together with
    /// the references and edges other rows hold to those symbols. Diagnostics are deleted explicitly because their
    /// project link is "set null", which would leave them looking solution-wide.
    /// </summary>
    public void DeleteProject(long projectId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM diagnostics WHERE project_id = @p; DELETE FROM projects WHERE id = @p;";
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteSolutionLevelDiagnostics()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM diagnostics WHERE project_id IS NULL";
        cmd.ExecuteNonQuery();
    }

    public long? GetFirstSolutionId()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM solutions ORDER BY id LIMIT 1";
        return cmd.ExecuteScalar() as long?;
    }

    public void UpdateSolution(long solutionId, string name, string path, DateTime indexedAt)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE solutions SET name = @n, path = @p, indexed_at = @a WHERE id = @i";
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@p", path);
        cmd.Parameters.AddWithValue("@a", indexedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@i", solutionId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Key to row id for every symbol, so an update can resolve references into projects it did not touch.</summary>
    public Dictionary<string, long> LoadSymbolKeys()
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT key, id FROM symbols";
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.GetInt64(1);
        return map;
    }

    /// <summary>
    /// Removes rows that exist only because of something that was deleted: namespaces nothing declares any more, and
    /// external stubs that no reference, edge or other stub points at. A clean index never contains them, so this keeps an
    /// updated index identical to a rebuilt one. Returns the number of rows removed.
    /// </summary>
    public int CollectGarbage()
    {
        int total = 0;
        using var cmd = _connection.CreateCommand();

        cmd.CommandText = @"DELETE FROM symbols WHERE kind = 'namespace' AND is_external = 0
                            AND NOT EXISTS (SELECT 1 FROM symbol_locations l WHERE l.symbol_id = symbols.id)";
        total += cmd.ExecuteNonQuery();

        // Removing a stub can free the stub that contained it, so repeat until nothing more goes.
        cmd.CommandText = @"DELETE FROM symbols WHERE is_external = 1
                            AND NOT EXISTS (SELECT 1 FROM symbol_references r WHERE r.symbol_id = symbols.id)
                            AND NOT EXISTS (SELECT 1 FROM edges e WHERE e.from_symbol_id = symbols.id OR e.to_symbol_id = symbols.id)
                            AND NOT EXISTS (SELECT 1 FROM symbols c WHERE c.container_id = symbols.id)";
        int removed;
        while ((removed = cmd.ExecuteNonQuery()) > 0) total += removed;
        return total;
    }

    public void SetMeta(string key, string value) => DatabaseSchema.SetMeta(_connection, key, value);
    public string? GetMeta(string key) => DatabaseSchema.GetMeta(_connection, key);

    public int GetSymbolCount() { using var cmd = _connection.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM symbols;"; return Convert.ToInt32(cmd.ExecuteScalar()); }
    public int GetFileCount() { using var cmd = _connection.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM files;"; return Convert.ToInt32(cmd.ExecuteScalar()); }
    public int GetProjectCount() { using var cmd = _connection.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM projects;"; return Convert.ToInt32(cmd.ExecuteScalar()); }

    /// <summary>Diagnostic counts by severity, then the messages, most severe first.</summary>
    public List<(string Severity, string Message)> GetDiagnostics()
    {
        var list = new List<(string, string)>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT severity, message FROM diagnostics ORDER BY CASE severity WHEN 'Error' THEN 0 WHEN 'Warning' THEN 1 ELSE 2 END, id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add((reader.GetString(0), reader.GetString(1)));
        return list;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (var cmd in _prepared.Values)
                cmd.Dispose();
            _prepared.Clear();
            _transaction?.Rollback();
            _transaction?.Dispose();
            _connection.Close();
            _connection.Dispose();
            _disposed = true;
        }
    }
}
