using CodeIndexer.Core.Indexing;
using CodeIndexer.Core.Loading;
using CodeIndexer.Core.Models;
using CodeIndexer.Core.Storage;

namespace CodeIndexer.Tests;

public class IndexEngineTests
{
    private static readonly string FixtureSolutionPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tests", "Fixtures", "SimpleClassLib", "SimpleClassLib.sln"));

    private static string GetDbPath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"codeindex_test_{Guid.NewGuid():N}.db");

    private static void CleanupDb(string dbPath)
    {
        if (!File.Exists(dbPath)) return;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { File.Delete(dbPath + "-wal"); } catch { }
        try { File.Delete(dbPath + "-shm"); } catch { }
        try { File.Delete(dbPath); } catch { }
    }

    [Fact]
    public async Task IndexSolution_CreatesDatabaseFile()
    {
        var dbPath = GetDbPath();
        try
        {
            var loader = new SolutionLoader();
            var loadResult = await loader.LoadAsync(FixtureSolutionPath);
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
            var engine = new IndexEngine(repoRoot);
            var result = await engine.IndexSolutionAsync(dbPath, loadResult);

            Assert.True(File.Exists(dbPath), "Database file should exist");
            Assert.True(result.SymbolsIndexed > 0, "Should have indexed symbols");
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public async Task IndexSolution_HasExpectedSchemaTables()
    {
        var dbPath = GetDbPath();
        try
        {
            var loader = new SolutionLoader();
            var loadResult = await loader.LoadAsync(FixtureSolutionPath);
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
            var engine = new IndexEngine(repoRoot);
            await engine.IndexSolutionAsync(dbPath, loadResult);

            using var db = new IndexDatabase(dbPath, repoRoot);
            Assert.True(db.GetProjectCount() > 0);
            Assert.True(db.GetFileCount() > 0);
            Assert.True(db.GetSymbolCount() > 0);
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public async Task IndexSolution_HasCorrectSymbolCount()
    {
        var dbPath = GetDbPath();
        try
        {
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));

            var filter = new ProjectFilter().Include("SimpleLib");
            var loader = new SolutionLoader(filter);
            var loadResult = await loader.LoadAsync(FixtureSolutionPath);

            var engine = new IndexEngine(repoRoot);
            var result = await engine.IndexSolutionAsync(dbPath, loadResult);

            Assert.True(result.SymbolsIndexed >= 10, $"Expected at least 10 symbols, got {result.SymbolsIndexed}");
            Assert.True(result.FilesIndexed >= 3, $"Expected at least 3 files, got {result.FilesIndexed}");
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public async Task IndexSolution_StoresMetadata()
    {
        var dbPath = GetDbPath();
        try
        {
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
            var loader = new SolutionLoader();
            var loadResult = await loader.LoadAsync(FixtureSolutionPath);
            var engine = new IndexEngine(repoRoot);
            await engine.IndexSolutionAsync(dbPath, loadResult);

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False");
            conn.Open();
            var meta = new Dictionary<string, string?>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM meta";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                meta[reader.GetString(0)] = reader.GetString(1);

            Assert.Equal("2", meta["schema_version"]);
            Assert.Equal("0.1.0", meta["tool_version"]);
            Assert.NotNull(meta["indexed_at"]);
            Assert.NotNull(meta["repo_root"]);
            Assert.NotNull(meta["git_commit"]);
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public async Task IndexSolution_FtsSearchReturnsSymbols()
    {
        var dbPath = GetDbPath();
        try
        {
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
            var loader = new SolutionLoader();
            var loadResult = await loader.LoadAsync(FixtureSolutionPath);
            var engine = new IndexEngine(repoRoot);
            await engine.IndexSolutionAsync(dbPath, loadResult);

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM symbols_fts WHERE symbols_fts MATCH 'calculator' ORDER BY rank";
            var names = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                names.Add(reader.GetString(0));

            Assert.Contains("Calculator", names);
            Assert.Contains("ICalculator", names);
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public async Task IndexSolution_IdentifiesGeneratedFiles()
    {
        var dbPath = GetDbPath();
        try
        {
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
            var loader = new SolutionLoader();
            var loadResult = await loader.LoadAsync(FixtureSolutionPath);
            var engine = new IndexEngine(repoRoot);
            await engine.IndexSolutionAsync(dbPath, loadResult);

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT is_generated FROM files WHERE path LIKE '%AssemblyInfo%' OR path LIKE '%GlobalUsings%'";
            using var reader = cmd.ExecuteReader();
            int generatedCount = 0;
            while (reader.Read())
            {
                if (reader.GetInt32(0) == 1) generatedCount++;
            }
            Assert.True(generatedCount >= 2, $"Expected at least 2 generated files, found {generatedCount}");
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public async Task CatalogDatabase_UpsertAndResolve()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_catalog_test_{Guid.NewGuid():N}.db");
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False");
            conn.Open();
            CatalogDatabase.EnsureSchema(conn);

            CatalogDatabase.Upsert(conn, new CatalogEntry
            {
                SolutionName = "TestLib",
                SolutionPath = "solutions/test.sln",
                DatabasePath = dbPath,
                IndexedCommit = "abc123",
                IndexedAt = DateTime.UtcNow.ToString("O"),
            });

            var entry = CatalogDatabase.Resolve(conn, "TestLib");
            Assert.NotNull(entry);
            Assert.Equal("TestLib", entry.SolutionName);

            var entries = CatalogDatabase.List(conn);
            Assert.Single(entries);
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public async Task IndexSolution_ExtractsEdges()
    {
        var dbPath = GetDbPath();
        try
        {
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
            var loader = new SolutionLoader();
            var loadResult = await loader.LoadAsync(FixtureSolutionPath);
            var engine = new IndexEngine(repoRoot);
            await engine.IndexSolutionAsync(dbPath, loadResult);

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM edges";
            var edgeCount = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.True(edgeCount >= 1, $"Expected at least 1 edge, got {edgeCount}");

            cmd.CommandText = "SELECT kind, COUNT(*) FROM edges GROUP BY kind";
            using var reader = cmd.ExecuteReader();
            bool hasImplements = false;
            while (reader.Read())
            {
                var kind = reader.GetString(0);
                if (kind == "implements") hasImplements = true;
            }
            Assert.True(hasImplements, "Should have at least one 'implements' edge");
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }
}