using CodeIndexer.Core.Indexing;
using CodeIndexer.Core.Loading;
using CodeIndexer.Core.Queries;
using System.CommandLine;

var rootCommand = new RootCommand("CodeIndexer - a .NET code indexer using Roslyn");

var globalDbOption = new Option<string>("--db", () => "", "Path to the output SQLite database");
var globalSolutionOption = new Option<string>("--solution", () => "", "Solution name (for catalog lookup)");
var formatOption = new Option<string>("--format", () => "json", "Output format: json or table");

// index command ----------------------------------------------------------
var indexCommand = new Command("index", "Index a .NET solution into a SQLite database");
var solutionArg = new Argument<FileInfo>("solution", "Path to the .sln, .slnx, or .csproj file");
var includeOption = new Option<string[]>("--include", () => Array.Empty<string>(), "Project name patterns to include");
var excludeOption = new Option<string[]>("--exclude", () => Array.Empty<string>(), "Project name patterns to exclude");
indexCommand.AddArgument(solutionArg);
indexCommand.AddOption(globalDbOption);
indexCommand.AddOption(includeOption);
indexCommand.AddOption(excludeOption);

indexCommand.SetHandler(async (FileInfo solution, string db, string[] include, string[] exclude) =>
{
    var filterBuilder = new ProjectFilter();
    foreach (var inc in include) filterBuilder.Include(inc);
    foreach (var exc in exclude) filterBuilder.Exclude(exc);
    var loader = new SolutionLoader(filterBuilder);
    var loadResult = await loader.LoadAsync(solution.FullName);
    SolutionLoader.PrintSummary(loadResult);
    var dbPath = db;
    if (string.IsNullOrEmpty(dbPath))
    {
        var solutionDir = Path.GetDirectoryName(solution.FullName) ?? Directory.GetCurrentDirectory();
        var solutionName = Path.GetFileNameWithoutExtension(solution.FullName);
        var codeIndexDir = Path.Combine(solutionDir, ".codeindex");
        Directory.CreateDirectory(codeIndexDir);
        dbPath = Path.Combine(codeIndexDir, $"{solutionName}.db");
    }
    Console.WriteLine($"\nIndexing to: {dbPath}");
    var repoRoot = FindRepoRoot(solution.FullName);
    var engine = new IndexEngine(repoRoot);
    var result = await engine.IndexSolutionAsync(dbPath, loadResult);
    Console.WriteLine($"Indexed: {result.ProjectsIndexed} projects, {result.FilesIndexed} files, {result.SymbolsIndexed} symbols");
    foreach (var d in result.Diagnostics)
        Console.WriteLine($"  [diag] {d}");
}, solutionArg, globalDbOption, includeOption, excludeOption);
rootCommand.AddCommand(indexCommand);

// info command ------------------------------------------------------------
var infoCommand = new Command("info", "Show index statistics and diagnostics");
infoCommand.AddOption(globalDbOption);
infoCommand.AddOption(globalSolutionOption);
infoCommand.SetHandler((string db, string solution) =>
{
    var dbPath = ResolveDbPath(db, solution);
    if (dbPath is null || !File.Exists(dbPath))
    {
        Console.Error.WriteLine("No index database found. Run 'codeindex index' first.");
        return;
    }
    using var infoDb = new CodeIndexer.Core.Storage.IndexDatabase(dbPath, "");
    Console.WriteLine($"Database: {dbPath}");
    Console.WriteLine($"Projects: {infoDb.GetProjectCount()}");
    Console.WriteLine($"Files: {infoDb.GetFileCount()}");
    Console.WriteLine($"Symbols: {infoDb.GetSymbolCount()}");
    Console.WriteLine($"Tool version: {infoDb.GetMeta("tool_version")}");
    Console.WriteLine($"Indexed at: {infoDb.GetMeta("indexed_at")}");
    Console.WriteLine($"Repo root: {infoDb.GetMeta("repo_root")}");
    Console.WriteLine($"Git commit: {infoDb.GetMeta("git_commit")}");
    var diagnostics = infoDb.GetDiagnostics();
    Console.WriteLine($"Diagnostics: {diagnostics.Count(d => d.Severity == "Error")} errors, " +
                      $"{diagnostics.Count(d => d.Severity == "Warning")} warnings, " +
                      $"{diagnostics.Count(d => d.Severity == "Info")} info");
    foreach (var (severity, message) in diagnostics)
        Console.WriteLine($"  [{severity}] {message}");
}, globalDbOption, globalSolutionOption);
rootCommand.AddCommand(infoCommand);

// list command ------------------------------------------------------------
var listCommand = new Command("list", "List all indexed solutions in the catalog");
listCommand.SetHandler(() =>
{
    var catalogPath = FindCatalogPath();
    if (catalogPath is null || !File.Exists(catalogPath))
    {
        Console.WriteLine("No catalog found. No solutions have been indexed yet.");
        return;
    }
    using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={catalogPath};Pooling=False");
    conn.Open();
    var entries = CodeIndexer.Core.Storage.CatalogDatabase.List(conn);
    Console.WriteLine($"Catalog: {catalogPath}\n");
    foreach (var e in entries)
    {
        Console.WriteLine($"  {e.SolutionName}");
        Console.WriteLine($"    Solution: {e.SolutionPath}");
        Console.WriteLine($"    Database: {e.DatabasePath}");
        Console.WriteLine($"    Commit:   {e.IndexedCommit}");
        Console.WriteLine($"    Indexed:  {e.IndexedAt}");
        Console.WriteLine();
    }
});
rootCommand.AddCommand(listCommand);

// resolve command ---------------------------------------------------------
var resolveCommand = new Command("resolve", "Find which database indexes a given solution or file");
var queryArg = new Argument<string>("query", "Solution name or file path to resolve");
resolveCommand.AddArgument(queryArg);
resolveCommand.SetHandler((string query) =>
{
    var catalogPath = FindCatalogPath();
    if (catalogPath is null || !File.Exists(catalogPath))
    {
        Console.WriteLine("No catalog found.");
        return;
    }
    using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={catalogPath};Pooling=False");
    conn.Open();
    var entry = CodeIndexer.Core.Storage.CatalogDatabase.Resolve(conn, query);
    if (entry is null)
    {
        Console.WriteLine($"No database found for '{query}'. Run 'codeindex index <solution>' first.");
        return;
    }
    Console.WriteLine($"Solution: {entry.SolutionName}");
    Console.WriteLine($"Database: {entry.DatabasePath}");
    Console.WriteLine($"Commit:   {entry.IndexedCommit}");
    Console.WriteLine($"Indexed:  {entry.IndexedAt}");
}, queryArg);
rootCommand.AddCommand(resolveCommand);

// find-symbol command -----------------------------------------------------
var findSymbolCmd = new Command("find-symbol", "Find symbols by name (exact, prefix, FTS)");
var findNameArg = new Argument<string>("name", "Symbol name or search pattern");
var findKindOption = new Option<string>("--kind", () => "", "Filter by symbol kind (class, method, etc.)");
var findProjectOption = new Option<string>("--project", () => "", "Filter by project name");
var findIncludeGenOption = new Option<bool>("--include-generated", () => false, "Include generated code");
var limitOption = new Option<int>("--limit", () => 20, "Max results");
var offsetOption = new Option<int>("--offset", () => 0, "Result offset");
findSymbolCmd.AddArgument(findNameArg);
findSymbolCmd.AddOption(globalDbOption);
findSymbolCmd.AddOption(globalSolutionOption);
findSymbolCmd.AddOption(formatOption);
findSymbolCmd.AddOption(findKindOption);
findSymbolCmd.AddOption(findProjectOption);
findSymbolCmd.AddOption(findIncludeGenOption);
findSymbolCmd.AddOption(limitOption);
findSymbolCmd.AddOption(offsetOption);
findSymbolCmd.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    var name = ctx.ParseResult.GetValueForArgument(findNameArg);
    var db = ctx.ParseResult.GetValueForOption(globalDbOption);
    var solution = ctx.ParseResult.GetValueForOption(globalSolutionOption);
    var format = ctx.ParseResult.GetValueForOption(formatOption);
    var kind = ctx.ParseResult.GetValueForOption(findKindOption);
    var project = ctx.ParseResult.GetValueForOption(findProjectOption);
    var includeGen = ctx.ParseResult.GetValueForOption(findIncludeGenOption);
    var limit = ctx.ParseResult.GetValueForOption(limitOption);
    var offset = ctx.ParseResult.GetValueForOption(offsetOption);

    var dbPath = ResolveDbPath(db, solution);
    if (dbPath is null) { Console.Error.WriteLine("No index database found."); return; }
    using var queries = new SymbolQueries(dbPath);
    var result = queries.FindSymbol(name, string.IsNullOrEmpty(kind) ? null : kind,
        string.IsNullOrEmpty(project) ? null : project, includeGen, limit, offset);
    Console.WriteLine(result.ToJson());
});
rootCommand.AddCommand(findSymbolCmd);

// outline command ----------------------------------------------------------
var outlineCmd = new Command("outline", "Show outline of a file: all symbols with their line ranges");
var outlineFileArg = new Argument<string>("file", "File path (partial match)");
outlineCmd.AddArgument(outlineFileArg);
outlineCmd.AddOption(globalDbOption);
outlineCmd.AddOption(globalSolutionOption);
outlineCmd.AddOption(formatOption);
outlineCmd.SetHandler((string file, string db, string solution, string format) =>
{
    var dbPath = ResolveDbPath(db, solution);
    if (dbPath is null) { Console.Error.WriteLine("No index database found."); return; }
    using var queries = new SymbolQueries(dbPath);
    var result = queries.Outline(file);
    Console.WriteLine(result.ToJson());
}, outlineFileArg, globalDbOption, globalSolutionOption, formatOption);
rootCommand.AddCommand(outlineCmd);

// show command ------------------------------------------------------------
var showCmd = new Command("show", "Show details for a specific symbol");
var showSymbolArg = new Argument<string>("symbol", "Symbol name or full name");
var showSourceOption = new Option<bool>("--source", () => false, "Include source code snippet");
showCmd.AddArgument(showSymbolArg);
showCmd.AddOption(globalDbOption);
showCmd.AddOption(globalSolutionOption);
showCmd.AddOption(formatOption);
showCmd.AddOption(showSourceOption);
showCmd.SetHandler((string symbol, string db, string solution, string format, bool source) =>
{
    var dbPath = ResolveDbPath(db, solution);
    if (dbPath is null) { Console.Error.WriteLine("No index database found."); return; }
    using var queries = new SymbolQueries(dbPath);
    var result = queries.Show(symbol, source);
    Console.WriteLine(result.ToJson());
}, showSymbolArg, globalDbOption, globalSolutionOption, formatOption, showSourceOption);
rootCommand.AddCommand(showCmd);

// search command ----------------------------------------------------------
var searchCmd = new Command("search", "Full-text search over indexed code");
var searchTextArg = new Argument<string>("text", "Search text (FTS query syntax)");
var searchIncludeGenOption = new Option<bool>("--include-generated", () => false, "Include generated code");
var searchLimitOption = new Option<int>("--limit", () => 20, "Max results");
searchCmd.AddArgument(searchTextArg);
searchCmd.AddOption(globalDbOption);
searchCmd.AddOption(globalSolutionOption);
searchCmd.AddOption(formatOption);
searchCmd.AddOption(searchIncludeGenOption);
searchCmd.AddOption(searchLimitOption);
searchCmd.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    var text = ctx.ParseResult.GetValueForArgument(searchTextArg);
    var db = ctx.ParseResult.GetValueForOption(globalDbOption);
    var solution = ctx.ParseResult.GetValueForOption(globalSolutionOption);
    var format = ctx.ParseResult.GetValueForOption(formatOption);
    var includeGen = ctx.ParseResult.GetValueForOption(searchIncludeGenOption);
    var limit = ctx.ParseResult.GetValueForOption(searchLimitOption);

    var dbPath = ResolveDbPath(db, solution);
    if (dbPath is null) { Console.Error.WriteLine("No index database found."); return; }
    using var queries = new SymbolQueries(dbPath);
    var result = queries.Search(text, includeGen, limit);
    Console.WriteLine(result.ToJson());
});
rootCommand.AddCommand(searchCmd);

// refs command ------------------------------------------------------------
var refsCmd = new Command("refs", "Find all references to a symbol");
var refsSymbolArg = new Argument<string>("symbol", "Symbol name or full name");
var refsLimitOption = new Option<int>("--limit", () => 100, "Max references");
refsCmd.AddArgument(refsSymbolArg);
refsCmd.AddOption(globalDbOption);
refsCmd.AddOption(globalSolutionOption);
refsCmd.AddOption(formatOption);
refsCmd.AddOption(refsLimitOption);
refsCmd.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    var symbol = ctx.ParseResult.GetValueForArgument(refsSymbolArg);
    var db = ctx.ParseResult.GetValueForOption(globalDbOption);
    var solution = ctx.ParseResult.GetValueForOption(globalSolutionOption);
    var format = ctx.ParseResult.GetValueForOption(formatOption);
    var limit = ctx.ParseResult.GetValueForOption(refsLimitOption);

    var dbPath = ResolveDbPath(db, solution);
    if (dbPath is null) { Console.Error.WriteLine("No index database found."); return; }
    using var queries = new SymbolQueries(dbPath);
    var result = queries.Refs(symbol, limit);
    Console.WriteLine(result.ToJson());
});
rootCommand.AddCommand(refsCmd);

// callers command ----------------------------------------------------------
var callersCmd = new Command("callers", "Find symbols that call the target symbol");
var callersSymbolArg = new Argument<string>("symbol", "Symbol name or full name");
var callersDepthOption = new Option<int>("--depth", () => 1, "Recursion depth (0 = no limit)");
var callersLimitOption = new Option<int>("--limit", () => 50, "Max results");
callersCmd.AddArgument(callersSymbolArg);
callersCmd.AddOption(globalDbOption);
callersCmd.AddOption(globalSolutionOption);
callersCmd.AddOption(formatOption);
callersCmd.AddOption(callersDepthOption);
callersCmd.AddOption(callersLimitOption);
callersCmd.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    var symbol = ctx.ParseResult.GetValueForArgument(callersSymbolArg);
    var db = ctx.ParseResult.GetValueForOption(globalDbOption);
    var solution = ctx.ParseResult.GetValueForOption(globalSolutionOption);
    var format = ctx.ParseResult.GetValueForOption(formatOption);
    var depth = ctx.ParseResult.GetValueForOption(callersDepthOption);
    var limit = ctx.ParseResult.GetValueForOption(callersLimitOption);

    var dbPath = ResolveDbPath(db, solution);
    if (dbPath is null) { Console.Error.WriteLine("No index database found."); return; }
    using var queries = new SymbolQueries(dbPath);
    var result = queries.Callers(symbol, depth, limit);
    Console.WriteLine(result.ToJson());
});
rootCommand.AddCommand(callersCmd);

// callees command ----------------------------------------------------------
var calleesCmd = new Command("callees", "Find symbols called by the target symbol");
var calleesSymbolArg = new Argument<string>("symbol", "Symbol name or full name");
var calleesDepthOption = new Option<int>("--depth", () => 1, "Recursion depth (0 = no limit)");
var calleesLimitOption = new Option<int>("--limit", () => 50, "Max results");
calleesCmd.AddArgument(calleesSymbolArg);
calleesCmd.AddOption(globalDbOption);
calleesCmd.AddOption(globalSolutionOption);
calleesCmd.AddOption(formatOption);
calleesCmd.AddOption(calleesDepthOption);
calleesCmd.AddOption(calleesLimitOption);
calleesCmd.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    var symbol = ctx.ParseResult.GetValueForArgument(calleesSymbolArg);
    var db = ctx.ParseResult.GetValueForOption(globalDbOption);
    var solution = ctx.ParseResult.GetValueForOption(globalSolutionOption);
    var format = ctx.ParseResult.GetValueForOption(formatOption);
    var depth = ctx.ParseResult.GetValueForOption(calleesDepthOption);
    var limit = ctx.ParseResult.GetValueForOption(calleesLimitOption);

    var dbPath = ResolveDbPath(db, solution);
    if (dbPath is null) { Console.Error.WriteLine("No index database found."); return; }
    using var queries = new SymbolQueries(dbPath);
    var result = queries.Callees(symbol, depth, limit);
    Console.WriteLine(result.ToJson());
});
rootCommand.AddCommand(calleesCmd);

// implementations command --------------------------------------------------
var implCmd = new Command("implementations", "Find types implementing an interface");
var implInterfaceArg = new Argument<string>("interface", "Interface name or full name");
var implLimitOption = new Option<int>("--limit", () => 50, "Max results");
implCmd.AddArgument(implInterfaceArg);
implCmd.AddOption(globalDbOption);
implCmd.AddOption(globalSolutionOption);
implCmd.AddOption(formatOption);
implCmd.AddOption(implLimitOption);
implCmd.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    var iface = ctx.ParseResult.GetValueForArgument(implInterfaceArg);
    var db = ctx.ParseResult.GetValueForOption(globalDbOption);
    var solution = ctx.ParseResult.GetValueForOption(globalSolutionOption);
    var format = ctx.ParseResult.GetValueForOption(formatOption);
    var limit = ctx.ParseResult.GetValueForOption(implLimitOption);

    var dbPath = ResolveDbPath(db, solution);
    if (dbPath is null) { Console.Error.WriteLine("No index database found."); return; }
    using var queries = new SymbolQueries(dbPath);
    var result = queries.Implementations(iface, limit);
    Console.WriteLine(result.ToJson());
});
rootCommand.AddCommand(implCmd);

// hierarchy command --------------------------------------------------------
var hierarchyCmd = new Command("hierarchy", "Show type inheritance / implementation hierarchy");
var hierarchyTypeArg = new Argument<string>("type", "Type name or full name");
var hierarchyDepthOption = new Option<int>("--depth", () => 3, "Max hierarchy depth");
hierarchyCmd.AddArgument(hierarchyTypeArg);
hierarchyCmd.AddOption(globalDbOption);
hierarchyCmd.AddOption(globalSolutionOption);
hierarchyCmd.AddOption(formatOption);
hierarchyCmd.AddOption(hierarchyDepthOption);
hierarchyCmd.SetHandler((System.CommandLine.Invocation.InvocationContext ctx) =>
{
    var type = ctx.ParseResult.GetValueForArgument(hierarchyTypeArg);
    var db = ctx.ParseResult.GetValueForOption(globalDbOption);
    var solution = ctx.ParseResult.GetValueForOption(globalSolutionOption);
    var format = ctx.ParseResult.GetValueForOption(formatOption);
    var depth = ctx.ParseResult.GetValueForOption(hierarchyDepthOption);

    var dbPath = ResolveDbPath(db, solution);
    if (dbPath is null) { Console.Error.WriteLine("No index database found."); return; }
    using var queries = new SymbolQueries(dbPath);
    var result = queries.Hierarchy(type, depth);
    Console.WriteLine(result.ToJson());
});
rootCommand.AddCommand(hierarchyCmd);

// helper: output formatting ------------------------------------------------
static string FindRepoRoot(string solutionPath)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(solutionPath));
    while (dir is not null)
    {
        // ".git" is a directory in a normal clone and a file in a worktree or submodule.
        var git = Path.Combine(dir, ".git");
        if (Directory.Exists(git) || File.Exists(git)) return dir;
        dir = Path.GetDirectoryName(dir);
    }
    return Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? ".";
}

static string? FindCatalogPath()
{
    var searchDirs = new[]
    {
        Directory.GetCurrentDirectory(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codeindex"),
    };
    foreach (var dir in searchDirs)
    {
        var probe = dir;
        while (probe is not null)
        {
            var candidate = Path.Combine(probe, ".codeindex", "catalog.db");
            if (File.Exists(candidate)) return candidate;
            var parent = Path.GetDirectoryName(probe);
            if (parent == probe) break;
            probe = parent;
        }
    }
    return null;
}

static string? ResolveDbPath(string db, string solution)
{
    if (!string.IsNullOrEmpty(db) && File.Exists(db)) return db;
    if (!string.IsNullOrEmpty(solution))
    {
        var catalogPath = FindCatalogPath();
        if (catalogPath is not null)
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={catalogPath};Pooling=False");
            conn.Open();
            var entry = CodeIndexer.Core.Storage.CatalogDatabase.Resolve(conn, solution);
            if (entry is not null && File.Exists(entry.DatabasePath))
                return entry.DatabasePath;
        }
    }
    return null;
}

await rootCommand.InvokeAsync(args);
