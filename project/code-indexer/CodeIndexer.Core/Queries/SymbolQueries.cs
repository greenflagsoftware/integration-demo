using Microsoft.Data.Sqlite;

namespace CodeIndexer.Core.Queries;

/// <summary>
/// Read-only questions about an index database. This part holds what every query shares: the connection, the
/// response envelope (solution context, staleness), and the row readers. Commands live in the other parts:
/// Resolve (selectors), Search (find, search, show, definition, outline), Position (at), Graph (refs, callers,
/// callees, implementations, hierarchy).
/// </summary>
public sealed partial class SymbolQueries : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _dbPath;
    private bool _disposed;
    private string? _repoRoot;
    private StalenessResult? _staleness;

    public SymbolQueries(string dbPath)
    {
        _dbPath = dbPath;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        _connection = new SqliteConnection(builder.ConnectionString);
        _connection.Open();
        _repoRoot = GetMeta("repo_root");
    }

    public (string name, string path, string indexedAt, string commit) GetSolutionContext()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT name, path, indexed_at FROM solutions LIMIT 1";
        string name = "", path = "", indexedAt = "";
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
            {
                name = reader.GetString(0);
                path = reader.GetString(1);
                indexedAt = reader.GetString(2);
            }
        }
        return (name, path, indexedAt, GetMeta("git_commit") ?? "unknown");
    }

    /// <summary>Whether indexed files changed on disk since indexing. Computed once per instance.</summary>
    public StalenessResult GetStaleness() => _staleness ??= Staleness.Check(_connection, _repoRoot);

    private string? GetMeta(string key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = @k";
        cmd.Parameters.AddWithValue("@k", key);
        return cmd.ExecuteScalar() as string;
    }

    // ---- envelope ------------------------------------------------------------------------------

    private QueryResponse<T> Respond<T>(
        string command, T data, int totalCount,
        int? limit = null, bool truncated = false, int? totalMatches = null,
        SymbolResult? target = null, List<SymbolResult>? candidates = null, List<string>? warnings = null)
    {
        var (solName, solPath, solIndexed, solCommit) = GetSolutionContext();
        var staleness = GetStaleness();

        warnings = warnings is { Count: > 0 } ? new List<string>(warnings) : new List<string>();
        if (staleness.IsStale)
        {
            var sample = string.Join(", ", staleness.AllFiles.Take(3));
            warnings.Insert(0, $"Index is stale: {staleness.ChangedFiles.Count} file(s) changed and {staleness.MissingFiles.Count} missing since " +
                               $"indexing (for example {sample}). Results may be wrong; re-run 'codeindex index'.");
        }

        return new QueryResponse<T>
        {
            Command = command,
            SolutionName = solName,
            SolutionPath = solPath,
            IndexedAt = solIndexed,
            IndexedCommit = solCommit,
            Stale = staleness.IsStale,
            StaleFiles = staleness.IsStale ? staleness.AllFiles.Take(20).ToList() : null,
            Target = target,
            Candidates = candidates is { Count: > 0 } ? candidates : null,
            Data = data,
            TotalCount = totalCount,
            TotalMatches = totalMatches,
            Limit = limit,
            Truncated = truncated,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    /// <summary>The response for a selector that matched nothing or was ambiguous.</summary>
    private QueryResponse<T> Unresolved<T>(string command, T emptyData, Resolution resolution) =>
        Respond(command, emptyData, 0, candidates: resolution.Candidates,
            warnings: resolution.Warning is null ? null : new List<string> { resolution.Warning });

    // ---- readers -------------------------------------------------------------------------------

    /// <summary>Columns read by <see cref="ReadSymbol"/>; the FROM clause must define s, f (nullable), and p (nullable).</summary>
    private const string SymbolColumns =
        "s.id, s.key, s.name, s.full_name, s.kind, s.accessibility, s.signature, s.start_line, s.end_line, " +
        "f.path, s.doc_summary, s.is_generated, s.is_external, p.name";

    private const string SymbolFrom =
        "symbols s LEFT JOIN files f ON f.id = s.file_id LEFT JOIN projects p ON p.id = f.project_id";

    private static SymbolResult ReadSymbol(SqliteDataReader r, int o = 0) => new()
    {
        Id = r.GetInt64(o),
        Key = r.GetString(o + 1),
        Name = r.GetString(o + 2),
        FullName = r.IsDBNull(o + 3) ? null : r.GetString(o + 3),
        Kind = r.GetString(o + 4),
        Accessibility = r.IsDBNull(o + 5) ? null : r.GetString(o + 5),
        Signature = r.IsDBNull(o + 6) ? null : r.GetString(o + 6),
        StartLine = r.IsDBNull(o + 7) ? null : r.GetInt32(o + 7),
        EndLine = r.IsDBNull(o + 8) ? null : r.GetInt32(o + 8),
        FilePath = r.IsDBNull(o + 9) ? null : r.GetString(o + 9),
        DocSummary = r.IsDBNull(o + 10) ? null : r.GetString(o + 10),
        IsGenerated = r.GetInt32(o + 11) != 0,
        IsExternal = r.GetInt32(o + 12) != 0,
        ProjectName = r.IsDBNull(o + 13) ? null : r.GetString(o + 13),
    };

    private static OutlineNode ToNode(SymbolResult s) => new()
    {
        Id = s.Id, Key = s.Key, Name = s.Name, FullName = s.FullName, Kind = s.Kind, Accessibility = s.Accessibility,
        Signature = s.Signature, StartLine = s.StartLine, EndLine = s.EndLine, FilePath = s.FilePath,
        DocSummary = s.DocSummary, IsGenerated = s.IsGenerated, IsExternal = s.IsExternal, ProjectName = s.ProjectName,
    };

    private static SymbolRef ToRef(SymbolResult s) => new() { Id = s.Id, Key = s.Key, Name = s.Name, FullName = s.FullName, Kind = s.Kind };

    private List<T> Read<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var list = new List<T>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(map(reader));
        return list;
    }

    private long Count(string sql, params (string Name, object? Value)[] args) =>
        Read(sql, r => r.GetInt64(0), args).Single();

    private SymbolResult? GetSymbol(long id) =>
        Read($"SELECT {SymbolColumns} FROM {SymbolFrom} WHERE s.id = @id", r => ReadSymbol(r), ("@id", id)).FirstOrDefault();

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection.Close();
            _connection.Dispose();
            _disposed = true;
        }
    }
}
