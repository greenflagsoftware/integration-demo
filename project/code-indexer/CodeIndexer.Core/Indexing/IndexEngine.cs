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
                var total = System.Diagnostics.Stopwatch.StartNew();
                await WriteIndexAsync(db, loadResult, solutionName, solutionRelPath, indexedAt, result);
                result.Timings["references"] = total.Elapsed - result.Timings["files and symbols"];
                total.Restart();
                db.RebuildFtsIndex();
                db.CommitTransaction();
                db.Checkpoint();
                result.Timings["finish"] = total.Elapsed;
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

    private sealed class ProjectWork
    {
        public required IndexedProject Project { get; init; }
        public required Compilation? Compilation { get; init; }
        public required List<(SyntaxTree Tree, long FileId)> Trees { get; init; }
        public List<SymbolEdge> Edges { get; set; } = new();
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

        // Symbol identity: every row is reachable by its key, whichever project or syntax node it came from.
        var keyToId = new Dictionary<string, long>(StringComparer.Ordinal);
        var keyCache = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);

        string KeyOf(ISymbol symbol)
        {
            var definition = symbol.OriginalDefinition;
            if (!keyCache.TryGetValue(definition, out var key))
                keyCache[definition] = key = SymbolKey.For(definition);
            return key;
        }

        long? Lookup(ISymbol symbol) => keyToId.TryGetValue(KeyOf(symbol), out var id) ? id : null;

        // Symbols that are used but not declared in an indexed project (framework, NuGet, excluded projects)
        // get a stub row so calls to them can be queried.
        long? ResolveOrAdd(ISymbol symbol)
        {
            symbol = symbol.OriginalDefinition;
            if (Lookup(symbol) is { } existing) return existing;
            if (!CanStub(symbol)) return null;

            long? containerId = symbol.ContainingType is { } containingType ? ResolveOrAdd(containingType) : null;
            var id = db.InsertSymbol(null, KeyOf(symbol), SymbolFacts.Kind(symbol), symbol.Name, SymbolFacts.FullName(symbol),
                containerId, SymbolFacts.Signature(symbol), SymbolFacts.Accessibility(symbol), null, null, null,
                isGenerated: false, isExternal: true, assembly: symbol.ContainingAssembly?.Name);
            keyToId[KeyOf(symbol)] = id;
            result.ExternalSymbolsIndexed++;
            return id;
        }

        void WriteSymbol(SymbolRow row)
        {
            if (keyToId.TryGetValue(row.Key, out var existingId))
            {
                // Declared again (a namespace shared by projects): only the extra locations are new.
                foreach (var loc in row.Locations)
                    db.InsertLocation(existingId, loc.FileId, loc.StartLine, loc.StartColumn, loc.EndLine, loc.EndColumn, false);
                return;
            }

            long? containerId = row.ContainerKey is not null && keyToId.TryGetValue(row.ContainerKey, out var cid) ? cid : null;
            var primary = row.Primary;
            bool isNamespace = row.Kind == "namespace";
            var id = db.InsertSymbol(isNamespace ? null : primary.FileId, row.Key, row.Kind, row.Name, row.FullName, containerId,
                row.Signature, row.Accessibility, isNamespace ? null : primary.StartLine, isNamespace ? null : primary.EndLine,
                row.DocSummary, row.IsGenerated);
            keyToId[row.Key] = id;
            foreach (var loc in row.Locations)
                db.InsertLocation(id, loc.FileId, loc.StartLine, loc.StartColumn, loc.EndLine, loc.EndColumn, loc.IsPrimary);
            if (!isNamespace) result.SymbolsIndexed++;
        }

        var work = new List<ProjectWork>();
        var projectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: projects, files, declared symbols.
        var phase = System.Diagnostics.Stopwatch.StartNew();
        foreach (var project in loadResult.IndexableProjects)
        {
            var roslynProject = project.RoslynProject!;
            var projectPath = roslynProject.FilePath ?? "";
            var projectRelativePath = PathUtil.ToRepoRelative(_repoRoot, projectPath);
            if (!projectPaths.Add(projectRelativePath))
                continue;

            bool isExternal = !string.IsNullOrEmpty(projectPath) && !PathUtil.IsUnderDirectory(projectPath, solutionDir);

            var projectId = db.InsertProject(
                project.Name, projectRelativePath,
                string.Join(";", project.AllTargetFrameworks),
                project.TargetFramework, project.Language, isExternal);
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

            if (roslynProject.Language != LanguageNames.CSharp)
            {
                var unsupported = new DiagnosticEntry
                {
                    Severity = Models.DiagnosticSeverity.Warning,
                    Message = $"Project '{project.Name}' is {roslynProject.Language}; only C# projects are indexed.",
                    ProjectPath = projectPath,
                };
                result.Diagnostics.Add(unsupported);
                db.InsertDiagnostic(unsupported, projectId, null);
                continue;
            }

            var compilation = await roslynProject.GetCompilationAsync();
            var files = new Dictionary<SyntaxTree, FileRef>();
            var projectWork = new ProjectWork { Project = project, Compilation = compilation, Trees = new() };

            foreach (var doc in roslynProject.Documents)
            {
                if (doc.SourceCodeKind != SourceCodeKind.Regular && doc.SourceCodeKind != SourceCodeKind.Script)
                    continue;

                var text = await doc.GetTextAsync();

                // Hash the bytes on disk (what the staleness check will hash later); source-generator output has no file.
                string contentHash;
                long? sizeBytes = null, modifiedTicks = null;
                if (doc.FilePath is not null && File.Exists(doc.FilePath))
                {
                    contentHash = IndexDatabase.ComputeContentHash(File.ReadAllBytes(doc.FilePath));
                    var info = new FileInfo(doc.FilePath);
                    sizeBytes = info.Length;
                    modifiedTicks = info.LastWriteTimeUtc.Ticks;
                }
                else
                {
                    contentHash = IndexDatabase.ComputeContentHash(text.ToString());
                }

                var syntaxTree = await doc.GetSyntaxTreeAsync();
                var fileRelPath = doc.FilePath is not null ? PathUtil.ToRepoRelative(_repoRoot, doc.FilePath) : PathUtil.Normalize(doc.Name);
                bool isGenerated = false;
                if (syntaxTree is not null)
                    isGenerated = SymbolExtractor.DetectGenerated(await syntaxTree.GetRootAsync());
                if (!isGenerated)
                    isGenerated = SymbolExtractor.IsGeneratedFilePath(fileRelPath);

                var fileId = db.InsertFile(projectId, fileRelPath, contentHash, text.Lines.Count, isGenerated, sizeBytes, modifiedTicks);
                result.FilesIndexed++;

                if (syntaxTree is null) continue;
                files[syntaxTree] = new FileRef(fileId, isGenerated, fileRelPath);
                projectWork.Trees.Add((syntaxTree, fileId));
            }

            work.Add(projectWork);
            if (compilation is null) continue;

            var extraction = new SymbolExtractor(compilation).Extract(files);
            foreach (var row in extraction.Symbols)
                WriteSymbol(row);
            projectWork.Edges = extraction.Edges;
        }

        result.Timings["files and symbols"] = phase.Elapsed;
        phase.Restart();

        // Pass 2: relationships and references, once every declared symbol has an id.
        foreach (var item in work)
        {
            if (item.Compilation is null) continue;

            foreach (var edge in item.Edges)
            {
                var from = ResolveOrAdd(edge.From);
                var to = ResolveOrAdd(edge.To);
                if (from is null || to is null) continue;
                db.InsertEdge(from.Value, to.Value, edge.Kind);
                result.EdgesExtracted++;
            }

            var extractor = new ReferenceExtractor(item.Compilation, ResolveOrAdd, Lookup);
            foreach (var (tree, fileId) in item.Trees)
            {
                var (refs, edges) = extractor.Extract(tree, fileId);

                foreach (var r in refs)
                    db.InsertReference(r.SymbolId, r.FileId, r.Line, r.Column, r.Length, r.Kind, r.EnclosingSymbolId, r.ReferenceName);
                foreach (var e in edges)
                    db.InsertEdge(e.FromSymbolId, e.ToSymbolId, e.Kind);

                result.ReferencesExtracted += refs.Count;
                result.EdgesExtracted += edges.Count;
            }

            if (extractor.ErrorCounts.Count > 0)
            {
                var total = extractor.ErrorCounts.Values.Sum();
                var top = string.Join(", ", extractor.ErrorCounts.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key} x{kv.Value}"));
                var diagnostic = new DiagnosticEntry
                {
                    Severity = Models.DiagnosticSeverity.Warning,
                    Message = $"Project '{item.Project.Name}' has {total} compile error(s) in {extractor.FilesWithErrors} file(s) ({top}). " +
                              "Names involved could not be resolved, so references through them are missing from the index. " +
                              "Usually a missing restore, project reference, or generated file.",
                    ProjectPath = item.Project.FilePath,
                };
                result.Diagnostics.Add(diagnostic);
                db.InsertDiagnostic(diagnostic, db.GetProjectIdByPath(PathUtil.ToRepoRelative(_repoRoot, item.Project.FilePath)), null);
            }
        }
    }

    // (pass 2 timing is recorded by the caller, which also times the finish)

    /// <summary>Whether a used-but-not-indexed symbol is worth a stub row.</summary>
    private static bool CanStub(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => type.TypeKind != TypeKind.Error && !type.IsAnonymousType,
        IMethodSymbol method => !SymbolFacts.IsAccessor(method) && method.MethodKind is not
            (MethodKind.LocalFunction or MethodKind.AnonymousFunction or MethodKind.BuiltinOperator
            or MethodKind.FunctionPointerSignature or MethodKind.DelegateInvoke),
        IPropertySymbol or IFieldSymbol or IEventSymbol => true,
        _ => false,
    };

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
    public int ExternalSymbolsIndexed { get; set; }
    /// <summary>Wall-clock time per phase: "files and symbols", "references", "finish" (FTS, commit, move).</summary>
    public Dictionary<string, TimeSpan> Timings { get; } = new();
    public List<DiagnosticEntry> Diagnostics { get; init; } = new();
}
