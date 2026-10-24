using CodeIndexer.Core.Storage;

namespace CodeIndexer.Core.Queries;

public sealed class IndexedProjectInfo
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Tfm { get; init; }
    public bool IsExternal { get; init; }
    public int Files { get; init; }
    public int Symbols { get; init; }
}

public sealed class IndexedSolutionInfo
{
    public required string Name { get; init; }
    public required string IndexedAt { get; init; }
    public required string Commit { get; init; }
}

public sealed class IndexInfoResult
{
    public required string Database { get; init; }
    public string? ToolVersion { get; init; }
    public string? RepoRoot { get; init; }
    public required List<IndexedProjectInfo> Projects { get; init; }
    public int Files { get; init; }
    public int Symbols { get; init; }
    public int ExternalSymbols { get; init; }
    public int References { get; init; }
    public int Edges { get; init; }
    /// <summary>Warnings and errors recorded while indexing (first 10): unrestored projects, compile errors, load failures.</summary>
    public required List<string> Diagnostics { get; init; }
    public int DiagnosticCount { get; init; }
    /// <summary>Every solution in the catalog next to this database, including this one.</summary>
    public List<IndexedSolutionInfo>? IndexedSolutions { get; init; }
}

public sealed partial class SymbolQueries
{
    /// <summary>What this index covers and how much to trust it: projects, counts, indexing diagnostics, staleness.</summary>
    public QueryResponse<IndexInfoResult> Info()
    {
        var projects = Read(
            @"SELECT p.name, p.path, p.indexed_tfm, p.is_external,
                     (SELECT COUNT(*) FROM files f WHERE f.project_id = p.id),
                     (SELECT COUNT(*) FROM symbols s JOIN files f ON f.id = s.file_id WHERE f.project_id = p.id)
              FROM projects p ORDER BY p.is_external, p.name",
            r => new IndexedProjectInfo
            {
                Name = r.GetString(0), Path = r.GetString(1), Tfm = r.GetString(2), IsExternal = r.GetInt32(3) != 0,
                Files = r.GetInt32(4), Symbols = r.GetInt32(5),
            });

        var diagnostics = Read(
            "SELECT severity, message FROM diagnostics ORDER BY CASE severity WHEN 'Error' THEN 0 WHEN 'Warning' THEN 1 ELSE 2 END, id",
            r => $"[{r.GetString(0)}] {r.GetString(1)}");

        List<IndexedSolutionInfo>? catalog = null;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_dbPath));
            var catalogPath = dir is null ? null : System.IO.Path.Combine(dir, "catalog.db");
            if (catalogPath is not null && File.Exists(catalogPath))
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={catalogPath};Pooling=False;Mode=ReadOnly");
                conn.Open();
                catalog = CatalogDatabase.List(conn)
                    .Select(e => new IndexedSolutionInfo { Name = e.SolutionName, IndexedAt = e.IndexedAt, Commit = e.IndexedCommit })
                    .ToList();
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* the catalog is a convenience; ignore a bad one */ }

        var data = new IndexInfoResult
        {
            Database = _dbPath,
            ToolVersion = GetMeta("tool_version"),
            RepoRoot = _repoRoot,
            Projects = projects,
            Files = (int)Count("SELECT COUNT(*) FROM files"),
            Symbols = (int)Count("SELECT COUNT(*) FROM symbols WHERE is_external = 0 AND kind <> 'namespace'"),
            ExternalSymbols = (int)Count("SELECT COUNT(*) FROM symbols WHERE is_external = 1"),
            References = (int)Count("SELECT COUNT(*) FROM symbol_references"),
            Edges = (int)Count("SELECT COUNT(*) FROM edges"),
            Diagnostics = diagnostics.Take(10).ToList(),
            DiagnosticCount = diagnostics.Count,
            IndexedSolutions = catalog,
        };
        return Respond("index-info", data, projects.Count);
    }
}
