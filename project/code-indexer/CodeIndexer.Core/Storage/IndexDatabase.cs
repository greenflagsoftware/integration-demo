using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CodeIndexer.Core.Storage;

public sealed class IndexDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _repoRoot;
    private bool _disposed;
    private SqliteTransaction? _transaction;

    public SqliteConnection Connection => _connection;

    public IndexDatabase(string dbPath, string repoRoot)
    {
        _repoRoot = repoRoot;
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = false };
        _connection = new SqliteConnection(builder.ConnectionString);
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        DatabaseSchema.EnsureSchema(_connection);
    }

    public void BeginTransaction() => _transaction = _connection.BeginTransaction();

    public void CommitTransaction()
    {
        _transaction?.Commit();
        _transaction?.Dispose();
        _transaction = null;
    }

    public long InsertSolution(string name, string relativePath, DateTime indexedAt)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO solutions (name, path, indexed_at) VALUES (@name, @path, @at)";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@path", relativePath);
        cmd.Parameters.AddWithValue("@at", indexedAt.ToString("O"));
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT last_insert_rowid();";
        return (long)(cmd.ExecuteScalar()!);
    }

    public long InsertProject(string name, string relativePath, string targetFrameworks, string indexedTfm, string language, bool isExternal)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO projects (name, path, target_frameworks, indexed_tfm, language, is_external) VALUES (@name, @path, @tfms, @tfm, @lang, @ext)";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@path", relativePath);
        cmd.Parameters.AddWithValue("@tfms", targetFrameworks);
        cmd.Parameters.AddWithValue("@tfm", indexedTfm);
        cmd.Parameters.AddWithValue("@lang", language);
        cmd.Parameters.AddWithValue("@ext", isExternal ? 1 : 0);
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT id FROM projects WHERE path = @p2";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@p2", relativePath);
        return (long)(cmd.ExecuteScalar()!);
    }

    public void LinkSolutionProject(long solutionId, long projectId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO solution_projects (solution_id, project_id) VALUES (@sid, @pid)";
        cmd.Parameters.AddWithValue("@sid", solutionId);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.ExecuteNonQuery();
    }

    public static string ComputeContentHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }

    public long InsertFile(long projectId, string relativePath, string contentHash, int lineCount, bool isGenerated)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO files (project_id, path, content_hash, line_count, is_generated) VALUES (@pid, @path, @hash, @lines, @gen)";
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@path", relativePath);
        cmd.Parameters.AddWithValue("@hash", contentHash);
        cmd.Parameters.AddWithValue("@lines", lineCount);
        cmd.Parameters.AddWithValue("@gen", isGenerated ? 1 : 0);
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT id FROM files WHERE project_id = @p2 AND path = @p3";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@p2", projectId);
        cmd.Parameters.AddWithValue("@p3", relativePath);
        return (long)(cmd.ExecuteScalar()!);
    }

    public long InsertSymbol(long fileId, string kind, string name, string fullName, long? containerId, string? signature, string? accessibility, int? startLine, int? endLine, string? docSummary, bool isGenerated)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO symbols (file_id, kind, name, full_name, container_id, signature, accessibility, start_line, end_line, doc_summary, is_generated) VALUES (@fid, @kind, @name, @full, @cid, @sig, @acc, @start, @end, @doc, @gen)";
        cmd.Parameters.AddWithValue("@fid", fileId);
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@full", fullName);
        cmd.Parameters.AddWithValue("@cid", containerId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sig", signature ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@acc", accessibility ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@start", startLine ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@end", endLine ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@doc", docSummary ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@gen", isGenerated ? 1 : 0);
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT last_insert_rowid();";
        return (long)(cmd.ExecuteScalar()!);
    }

    public void RebuildFtsIndex()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO symbols_fts(rowid, name, full_name, signature, doc_summary) SELECT id, name, full_name, signature, doc_summary FROM symbols;";
        cmd.ExecuteNonQuery();
    }
    public void InsertReference(long symbolId, long fileId, int line, int column, string kind, string? referenceName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO symbol_references (symbol_id, file_id, line, column, kind, reference_name) VALUES (@sid, @fid, @line, @col, @kind, @rname)";
        cmd.Parameters.AddWithValue("@sid", symbolId);
        cmd.Parameters.AddWithValue("@fid", fileId);
        cmd.Parameters.AddWithValue("@line", line);
        cmd.Parameters.AddWithValue("@col", column);
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@rname", referenceName ?? (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    public void InsertEdge(long fromSymbolId, long toSymbolId, string kind)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO edges (from_symbol_id, to_symbol_id, kind) VALUES (@fid, @tid, @kind)";
        cmd.Parameters.AddWithValue("@fid", fromSymbolId);
        cmd.Parameters.AddWithValue("@tid", toSymbolId);
        cmd.Parameters.AddWithValue("@kind", kind);
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

    public void SetMeta(string key, string value) => DatabaseSchema.SetMeta(_connection, key, value);
    public string? GetMeta(string key) => DatabaseSchema.GetMeta(_connection, key);

    public int GetSymbolCount() { using var cmd = _connection.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM symbols;"; return Convert.ToInt32(cmd.ExecuteScalar()); }
    public int GetFileCount() { using var cmd = _connection.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM files;"; return Convert.ToInt32(cmd.ExecuteScalar()); }
    public int GetProjectCount() { using var cmd = _connection.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM projects;"; return Convert.ToInt32(cmd.ExecuteScalar()); }

    public void Dispose()
    {
        if (!_disposed)
        {
            _transaction?.Rollback();
            _transaction?.Dispose();
            _connection.Close();
            _connection.Dispose();
            _disposed = true;
        }
    }
}
