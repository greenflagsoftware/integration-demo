using CodeIndexer.Core.Loading;
using CodeIndexer.Core.Models;
using CodeIndexer.Core.Storage;
using Microsoft.CodeAnalysis;

namespace CodeIndexer.Core.Indexing;

public sealed class IndexEngine
{
    private readonly string _repoRoot;
    public IndexEngine(string repoRoot) => _repoRoot = repoRoot;

    public async Task<IndexResult> IndexSolutionAsync(string dbPath, SolutionLoadResult loadResult)
    {
        var result = new IndexResult();
        using var db = new IndexDatabase(dbPath, _repoRoot);

        var solutionDir = Path.GetDirectoryName(loadResult.SolutionPath) ?? _repoRoot;
        var repoRootDir = _repoRoot;

        db.SetMeta("tool_version", "0.1.0");
        db.SetMeta("indexed_at", DateTime.UtcNow.ToString("O"));
        db.SetMeta("repo_root", repoRootDir);
        db.SetMeta("git_commit", GetGitCommit(repoRootDir));

        var solutionName = Path.GetFileNameWithoutExtension(loadResult.SolutionPath);
        var solutionRelPath = Path.GetRelativePath(repoRootDir, loadResult.SolutionPath);

        db.BeginTransaction();

        var solutionId = db.InsertSolution(solutionName, solutionRelPath, DateTime.UtcNow);

        foreach (var project in loadResult.IndexableProjects)
        {
            var roslynProject = project.RoslynProject!;
            var projectPath = roslynProject.FilePath ?? "";
            var projectRelativePath = Path.GetRelativePath(repoRootDir, projectPath);
            bool isExternal = !string.IsNullOrEmpty(projectPath) && !projectPath.StartsWith(solutionDir, StringComparison.OrdinalIgnoreCase);

            var projectDir = Path.GetDirectoryName(projectPath);
            var assetsPath = projectDir is not null ? Path.Combine(projectDir, "obj", "project.assets.json") : null;
            if (assetsPath is not null && !File.Exists(assetsPath))
            {
                result.Diagnostics.Add(new DiagnosticEntry
                {
                    Severity = Models.DiagnosticSeverity.Warning,
                    Message = $"Project '{project.Name}' not restored. Run: dotnet restore \"{projectPath}\"",
                    ProjectPath = projectPath,
                });
            }

            var projectId = db.InsertProject(
                project.Name, projectRelativePath,
                string.Join(";", project.AllTargetFrameworks),
                project.TargetFramework, project.Language, isExternal);

            db.LinkSolutionProject(solutionId, projectId);
            result.ProjectsIndexed++;

            foreach (var doc in roslynProject.Documents)
            {
                if (doc.SourceCodeKind != SourceCodeKind.Regular && doc.SourceCodeKind != SourceCodeKind.Script)
                    continue;

                var text = await doc.GetTextAsync();
                var content = text.ToString();
                var contentHash = IndexDatabase.ComputeContentHash(content);
                var lineCount = text.Lines.Count;

                var syntaxTree = await doc.GetSyntaxTreeAsync();
                var fileRelPath = doc.FilePath is not null ? Path.GetRelativePath(repoRootDir, doc.FilePath) : doc.Name;
                bool isGenerated = false;
                if (syntaxTree is not null)
                    isGenerated = SymbolExtractor.DetectGenerated(await syntaxTree.GetRootAsync());
                if (!isGenerated)
                    isGenerated = SymbolExtractor.IsGeneratedFilePath(fileRelPath);

                var fileId = db.InsertFile(projectId, fileRelPath, contentHash, lineCount, isGenerated);
                result.FilesIndexed++;

                var compilation = await roslynProject.GetCompilationAsync();
                if (compilation is null) continue;

                var extractor = new SymbolExtractor(compilation);
                var symbolRows = extractor.Extract(new[] { doc });

                foreach (var sym in symbolRows)
                {
                    sym.FileId = fileId;
                    sym.Id = db.InsertSymbol(fileId, sym.Kind, sym.Name, sym.FullName, sym.ContainerId,
                        sym.Signature, sym.Accessibility, sym.StartLine, sym.EndLine,
                        sym.DocSummary, sym.IsGenerated);
                    result.SymbolsIndexed++;
                }
            }
        }

        // Phase 3: Extract references and edges (second pass after all symbols are in the DB).
        var symbolMap = ReferenceExtractor.LoadSymbolMap(db.Connection);

        foreach (var project in loadResult.IndexableProjects)
        {
            var roslynProject = project.RoslynProject!;
            var projCompilation = await roslynProject.GetCompilationAsync();
            if (projCompilation is null) continue;
            var projPath = roslynProject.FilePath ?? "";
            var projRelativePath = Path.GetRelativePath(repoRootDir, projPath);
            var projId = db.GetProjectIdByPath(projRelativePath);
            if (projId is null) continue;

            var refExtractor = new ReferenceExtractor(projCompilation) { SymbolMap = symbolMap };

            foreach (var doc in roslynProject.Documents)
            {
                if (doc.SourceCodeKind != SourceCodeKind.Regular && doc.SourceCodeKind != SourceCodeKind.Script)
                    continue;

                var fileRelPath = doc.FilePath is not null ? Path.GetRelativePath(repoRootDir, doc.FilePath) : doc.Name;
                var fileId = db.GetFileIdByProjectAndPath(projId.Value, fileRelPath);
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

        db.RebuildFtsIndex();
        db.CommitTransaction();

        UpdateCatalog(dbPath, solutionName, solutionRelPath, repoRootDir);

        return result;
    }

    private static void UpdateCatalog(string dbPath, string solutionName, string solutionRelPath, string repoRootDir)
    {
        var catalogDir = Path.GetDirectoryName(dbPath) ?? repoRootDir;
        var catalogPath = Path.Combine(catalogDir, "catalog.db");
        using var catConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={catalogPath};Pooling=False");
        catConn.Open();
        CatalogDatabase.EnsureSchema(catConn);
        CatalogDatabase.Upsert(catConn, new CatalogEntry
        {
            SolutionName = solutionName,
            SolutionPath = solutionRelPath,
            DatabasePath = dbPath,
            IndexedCommit = GetGitCommit(repoRootDir),
            IndexedAt = DateTime.UtcNow.ToString("O"),
        });
    }

    private static string GetGitCommit(string repoRoot)
    {
        try
        {
            var gitDir = Path.Combine(repoRoot, ".git");
            if (!Directory.Exists(gitDir)) return "unknown";
            var headFile = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headFile)) return "unknown";
            var head = File.ReadAllText(headFile).Trim();
            if (head.StartsWith("ref: "))
            {
                var refPath = Path.Combine(gitDir, head.Substring(5));
                if (File.Exists(refPath)) return File.ReadAllText(refPath).Trim();
            }
            return head;
        }
        catch { return "unknown"; }
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
