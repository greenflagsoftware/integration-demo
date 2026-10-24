using System.Diagnostics;
using CodeIndexer.Core.Loading;
using CodeIndexer.Core.Models;
using CodeIndexer.Core.Storage;
using Microsoft.CodeAnalysis;

namespace CodeIndexer.Core.Indexing;

public sealed class IndexEngine
{
    private readonly string _repoRoot;
    public IndexEngine(string repoRoot) => _repoRoot = repoRoot;

    /// <summary>
    /// Builds a fresh index database. The database is written to a temporary file and then moved over
    /// <paramref name="dbPath"/>, so re-running never accumulates rows from an earlier run and a failed run
    /// leaves the previous database untouched.
    /// </summary>
    public async Task<IndexResult> IndexSolutionAsync(string dbPath, SolutionLoadResult loadResult)
    {
        var result = new IndexResult();
        var fullDbPath = Path.GetFullPath(dbPath);
        var tempPath = fullDbPath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(fullDbPath)!);
        DeleteDatabaseFiles(tempPath);

        var gitCommit = GetGitCommit(_repoRoot);
        var indexedAt = DateTime.UtcNow;
        var solutionName = Path.GetFileNameWithoutExtension(loadResult.SolutionPath);
        var solutionRelPath = PathUtil.ToRepoRelative(_repoRoot, loadResult.SolutionPath);

        try
        {
            using (var db = new IndexDatabase(tempPath, _repoRoot))
            {
                db.SetMeta("tool_version", "0.1.0");
                db.SetMeta("indexed_at", indexedAt.ToString("O"));
                db.SetMeta("repo_root", PathUtil.Normalize(_repoRoot));
                db.SetMeta("git_commit", gitCommit);

                db.BeginTransaction();
                await WriteIndexAsync(db, loadResult, solutionName, solutionRelPath, indexedAt, result);
                db.RebuildFtsIndex();
                db.CommitTransaction();
                db.Checkpoint();
            }

            DeleteDatabaseFiles(fullDbPath);
            File.Move(tempPath, fullDbPath);
        }
        catch
        {
            DeleteDatabaseFiles(tempPath);
            throw;
        }

        UpdateCatalog(fullDbPath, solutionName, solutionRelPath, gitCommit, indexedAt);
        return result;
    }

    private async Task WriteIndexAsync(
        IndexDatabase db, SolutionLoadResult loadResult, string solutionName, string solutionRelPath,
        DateTime indexedAt, IndexResult result)
    {
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(loadResult.SolutionPath)) ?? _repoRoot;
        var solutionId = db.InsertSolution(solutionName, solutionRelPath, indexedAt);

        // Solution-level load diagnostics are not tied to a project.
        foreach (var d in loadResult.Diagnostics)
            db.InsertDiagnostic(d, null, null);

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var projectIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var indexed = new List<(IndexedProject Project, long ProjectId)>();

        // Pass 1: projects, files, symbols.
        foreach (var project in loadResult.IndexableProjects)
        {
            var roslynProject = project.RoslynProject!;
            var projectPath = roslynProject.FilePath ?? "";
            var projectRelativePath = PathUtil.ToRepoRelative(_repoRoot, projectPath);
            if (projectIds.ContainsKey(projectRelativePath))
                continue;

            bool isExternal = !string.IsNullOrEmpty(projectPath) && !PathUtil.IsUnderDirectory(projectPath, solutionDir);

            var projectId = db.InsertProject(
                project.Name, projectRelativePath,
                string.Join(";", project.AllTargetFrameworks),
                project.TargetFramework, project.Language, isExternal);
            projectIds[projectRelativePath] = projectId;
            indexed.Add((project, projectId));

            db.LinkSolutionProject(solutionId, projectId);
            result.ProjectsIndexed++;

            foreach (var d in project.Diagnostics)
                db.InsertDiagnostic(d, projectId, null);

            var projectDir = Path.GetDirectoryName(projectPath);
            var assetsPath = projectDir is not null ? Path.Combine(projectDir, "obj", "project.assets.json") : null;
            if (assetsPath is not null && !File.Exists(assetsPath))
            {
                var restore = new DiagnosticEntry
                {
                    Severity = Models.DiagnosticSeverity.Warning,
                    Message = $"Project '{project.Name}' not restored. Run: dotnet restore \"{projectPath}\"",
                    ProjectPath = projectPath,
                };
                result.Diagnostics.Add(restore);
                db.InsertDiagnostic(restore, projectId, null);
            }

            var compilation = await roslynProject.GetCompilationAsync();
            var extractor = compilation is null ? null : new SymbolExtractor(compilation);

            foreach (var doc in roslynProject.Documents)
            {
                if (doc.SourceCodeKind != SourceCodeKind.Regular && doc.SourceCodeKind != SourceCodeKind.Script)
                    continue;

                var text = await doc.GetTextAsync();
                var contentHash = IndexDatabase.ComputeContentHash(text.ToString());

                var syntaxTree = await doc.GetSyntaxTreeAsync();
                var fileRelPath = doc.FilePath is not null ? PathUtil.ToRepoRelative(_repoRoot, doc.FilePath) : PathUtil.Normalize(doc.Name);
                bool isGenerated = false;
                if (syntaxTree is not null)
                    isGenerated = SymbolExtractor.DetectGenerated(await syntaxTree.GetRootAsync());
                if (!isGenerated)
                    isGenerated = SymbolExtractor.IsGeneratedFilePath(fileRelPath);

                var fileId = db.InsertFile(projectId, fileRelPath, contentHash, text.Lines.Count, isGenerated);
                result.FilesIndexed++;

                if (extractor is null) continue;

                foreach (var sym in extractor.Extract(new[] { doc }))
                {
                    // A partial type is declared in several documents; the first declaration owns the row.
                    if (!seenKeys.Add(sym.Key)) continue;

                    sym.FileId = fileId;
                    sym.Id = db.InsertSymbol(fileId, sym.Key, sym.Kind, sym.Name, sym.FullName, sym.ContainerId,
                        sym.Signature, sym.Accessibility, sym.StartLine, sym.EndLine,
                        sym.DocSummary, sym.IsGenerated);
                    result.SymbolsIndexed++;
                }
            }
        }

        // Pass 2: references and edges, once every symbol has an id.
        var symbolMap = ReferenceExtractor.LoadSymbolMap(db.Connection);

        foreach (var (project, projId) in indexed)
        {
            var roslynProject = project.RoslynProject!;
            var projCompilation = await roslynProject.GetCompilationAsync();
            if (projCompilation is null) continue;

            var refExtractor = new ReferenceExtractor(projCompilation) { SymbolMap = symbolMap };

            foreach (var doc in roslynProject.Documents)
            {
                if (doc.SourceCodeKind != SourceCodeKind.Regular && doc.SourceCodeKind != SourceCodeKind.Script)
                    continue;

                var fileRelPath = doc.FilePath is not null ? PathUtil.ToRepoRelative(_repoRoot, doc.FilePath) : PathUtil.Normalize(doc.Name);
                var fileId = db.GetFileIdByProjectAndPath(projId, fileRelPath);
                if (fileId is null) continue;

                var (refs, edges) = refExtractor.Extract(doc, fileId.Value, includeReferences: true);

                foreach (var r in refs)
                {
                    if (r.SymbolId > 0)
                        db.InsertReference(r.SymbolId, r.FileId, r.Line, r.Column, r.Kind, r.ReferenceName);
                }

                foreach (var e in edges)
                {
                    if (e.FromSymbolId > 0 && e.ToSymbolId > 0)
                        db.InsertEdge(e.FromSymbolId, e.ToSymbolId, e.Kind);
                }

                result.ReferencesExtracted += refs.Count;
                result.EdgesExtracted += edges.Count;
            }
        }
    }

    private static void DeleteDatabaseFiles(string dbPath)
    {
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void UpdateCatalog(string dbPath, string solutionName, string solutionRelPath, string gitCommit, DateTime indexedAt)
    {
        var catalogPath = Path.Combine(Path.GetDirectoryName(dbPath)!, "catalog.db");
        using var catConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={catalogPath};Pooling=False");
        catConn.Open();
        CatalogDatabase.EnsureSchema(catConn);
        CatalogDatabase.Upsert(catConn, new CatalogEntry
        {
            SolutionName = solutionName,
            SolutionPath = solutionRelPath,
            DatabasePath = dbPath,
            IndexedCommit = gitCommit,
            IndexedAt = indexedAt.ToString("O"),
        });
    }

    /// <summary>The HEAD commit of the repository containing <paramref name="repoRoot"/>, or "unknown".</summary>
    public static string GetGitCommit(string repoRoot)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return "unknown";
            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(5000)) { process.Kill(); return "unknown"; }
            return process.ExitCode == 0 && output.Length >= 7 ? output : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}

public sealed class IndexResult
{
    public int ProjectsIndexed { get; set; }
    public int FilesIndexed { get; set; }
    public int SymbolsIndexed { get; set; }
    public int ReferencesExtracted { get; set; }
    public int EdgesExtracted { get; set; }
    public List<DiagnosticEntry> Diagnostics { get; init; } = new();
}
