using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using CodeIndexer.Core.Indexing;
using CodeIndexer.Core.Loading;
using CodeIndexer.Core.Queries;
using CodeIndexer.Core.Storage;
using Microsoft.Data.Sqlite;

Console.OutputEncoding = new UTF8Encoding(false);

var rootCommand = new RootCommand(
    "CodeIndexer - answers code navigation questions about a .NET solution from a Roslyn-built SQLite index. " +
    "Typical flow: 'index <solution.sln>', then 'find-symbol', 'definition', 'refs', 'callers', 'callees', " +
    "'implementations', 'hierarchy', 'outline', 'at'. Output is JSON unless --format table. " +
    "Name a symbol by name, --id, --key, or file:line[:col]; an ambiguous name returns candidates.");

// ---- shared options ------------------------------------------------------------------------------

var dbOption = new Option<string>("--db", () => "", "Path to the index database (default: the only indexed solution)");
var solutionOption = new Option<string>("--solution", () => "", "Name of an indexed solution (see 'list')");
var formatOption = new Option<string>("--format", () => "json", "Output format: json or table");

var idOption = new Option<long?>("--id", "Symbol id from a previous result");
var keyOption = new Option<string>("--key", () => "", "Symbol key (or documentation id) from a previous result");
var kindOption = new Option<string>("--kind", () => "", "Only symbols of this kind (class, method, property, ...)");
var includeGeneratedOption = new Option<bool>("--include-generated", () => false, "Include generated code");
var includeExternalOption = new Option<bool>("--include-external", () => false, "Include symbols declared outside the indexed projects");

Argument<string> SymbolArg(string name = "symbol", string description = "Symbol name, Type.Member, documentation id, or file:line[:col]") =>
    new(name, () => "", description);

SymbolSelector Selector(InvocationContext ctx, Argument<string> arg) => new(
    Id: ctx.ParseResult.GetValueForOption(idOption),
    Key: Blank(ctx.ParseResult.GetValueForOption(keyOption)),
    Text: Blank(ctx.ParseResult.GetValueForArgument(arg)),
    Kind: Blank(ctx.ParseResult.GetValueForOption(kindOption)),
    IncludeGenerated: ctx.ParseResult.GetValueForOption(includeGeneratedOption));

static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

void AddSelectorOptions(Command c)
{
    c.AddOption(idOption);
    c.AddOption(keyOption);
    c.AddOption(kindOption);
    c.AddOption(includeGeneratedOption);
}

/// <summary>Defines a command that opens the index, runs a query, and prints the response.</summary>
Command Query(string name, string description, Argument? arg, Action<Command>? configure,
              Func<InvocationContext, SymbolQueries, IQueryResponse> run)
{
    var command = new Command(name, description);
    if (arg is not null) command.AddArgument(arg);
    command.AddOption(dbOption);
    command.AddOption(solutionOption);
    command.AddOption(formatOption);
    configure?.Invoke(command);

    command.SetHandler(ctx =>
    {
        var dbPath = ResolveDbPath(ctx.ParseResult.GetValueForOption(dbOption)!, ctx.ParseResult.GetValueForOption(solutionOption)!, out var problem);
        if (dbPath is null)
        {
            Console.Error.WriteLine(problem);
            ctx.ExitCode = 2;
            return;
        }
        try
        {
            using var queries = new SymbolQueries(dbPath);
            var response = run(ctx, queries);
            var table = string.Equals(ctx.ParseResult.GetValueForOption(formatOption), "table", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine(table ? response.ToTable() : response.ToJson());
        }
        catch (SqliteException ex)
        {
            Console.Error.WriteLine($"Query failed: {ex.Message}. If the index was built by an older version, re-run 'codeindex index'.");
            ctx.ExitCode = 3;
        }
    });
    return command;
}

// ---- index ---------------------------------------------------------------------------------------

var indexCommand = new Command("index", "Index a .NET solution into a fresh SQLite database (replaces any previous index)");
var solutionArg = new Argument<FileInfo>("solution", "Path to the .sln, .slnx, or .csproj file");
var includeOption = new Option<string[]>("--include", () => Array.Empty<string>(), "Project names to include (exact, or with * and ? wildcards)");
var excludeOption = new Option<string[]>("--exclude", () => Array.Empty<string>(), "Project names to exclude (exact, or with * and ? wildcards)");
indexCommand.AddArgument(solutionArg);
indexCommand.AddOption(dbOption);
indexCommand.AddOption(includeOption);
indexCommand.AddOption(excludeOption);

indexCommand.SetHandler(async (FileInfo solution, string db, string[] include, string[] exclude) =>
{
    var filter = new ProjectFilter();
    foreach (var inc in include) filter.Include(inc);
    foreach (var exc in exclude) filter.Exclude(exc);
    var loadResult = await new SolutionLoader(filter).LoadAsync(solution.FullName);
    SolutionLoader.PrintSummary(loadResult);

    var dbPath = db;
    if (string.IsNullOrEmpty(dbPath))
    {
        var solutionDir = Path.GetDirectoryName(solution.FullName) ?? Directory.GetCurrentDirectory();
        var codeIndexDir = Path.Combine(solutionDir, ".codeindex");
        Directory.CreateDirectory(codeIndexDir);
        dbPath = Path.Combine(codeIndexDir, $"{Path.GetFileNameWithoutExtension(solution.FullName)}.db");
    }
    Console.WriteLine($"\nIndexing to: {dbPath}");
    var engine = new IndexEngine(FindRepoRoot(solution.FullName));
    var result = await engine.IndexSolutionAsync(dbPath, loadResult);
    Console.WriteLine($"Indexed: {result.ProjectsIndexed} projects, {result.FilesIndexed} files, {result.SymbolsIndexed} symbols " +
                      $"(+{result.ExternalSymbolsIndexed} external), {result.ReferencesExtracted} references, {result.EdgesExtracted} edges");
    foreach (var d in result.Diagnostics)
        Console.WriteLine($"  [diag] {d}");
}, solutionArg, dbOption, includeOption, excludeOption);
rootCommand.AddCommand(indexCommand);

// ---- info / list / resolve -----------------------------------------------------------------------

var infoCommand = new Command("info", "Show index statistics, staleness, and diagnostics");
infoCommand.AddOption(dbOption);
infoCommand.AddOption(solutionOption);
infoCommand.SetHandler(ctx =>
{
    var dbPath = ResolveDbPath(ctx.ParseResult.GetValueForOption(dbOption)!, ctx.ParseResult.GetValueForOption(solutionOption)!, out var problem);
    if (dbPath is null) { Console.Error.WriteLine(problem); ctx.ExitCode = 2; return; }

    using var infoDb = new IndexDatabase(dbPath, "");
    Console.WriteLine($"Database: {dbPath}");
    Console.WriteLine($"Projects: {infoDb.GetProjectCount()}");
    Console.WriteLine($"Files: {infoDb.GetFileCount()}");
    Console.WriteLine($"Symbols: {infoDb.GetSymbolCount()}");
    Console.WriteLine($"Tool version: {infoDb.GetMeta("tool_version")}");
    Console.WriteLine($"Indexed at: {infoDb.GetMeta("indexed_at")}");
    Console.WriteLine($"Repo root: {infoDb.GetMeta("repo_root")}");
    Console.WriteLine($"Git commit: {infoDb.GetMeta("git_commit")}");

    using (var queries = new SymbolQueries(dbPath))
    {
        var staleness = queries.GetStaleness();
        Console.WriteLine(staleness.IsStale
            ? $"Stale: yes ({staleness.ChangedFiles.Count} changed, {staleness.MissingFiles.Count} missing): {string.Join(", ", staleness.AllFiles.Take(5))}"
            : "Stale: no");
    }

    var diagnostics = infoDb.GetDiagnostics();
    Console.WriteLine($"Diagnostics: {diagnostics.Count(d => d.Severity == "Error")} errors, " +
                      $"{diagnostics.Count(d => d.Severity == "Warning")} warnings, " +
                      $"{diagnostics.Count(d => d.Severity == "Info")} info");
    foreach (var (severity, message) in diagnostics)
        Console.WriteLine($"  [{severity}] {message}");
});
rootCommand.AddCommand(infoCommand);

var listCommand = new Command("list", "List all indexed solutions in the catalog");
listCommand.SetHandler(() =>
{
    var entries = LoadCatalog(out var catalogPath);
    if (entries is null) { Console.WriteLine("No catalog found. No solutions have been indexed yet."); return; }
    Console.WriteLine($"Catalog: {catalogPath}\n");
    foreach (var e in entries)
    {
        Console.WriteLine($"  {e.SolutionName}");
        Console.WriteLine($"    Solution: {e.SolutionPath}");
        Console.WriteLine($"    Database: {e.DatabasePath}");
        Console.WriteLine($"    Commit:   {e.IndexedCommit}");
        Console.WriteLine($"    Indexed:  {e.IndexedAt}\n");
    }
});
rootCommand.AddCommand(listCommand);

var resolveCommand = new Command("resolve", "Find which database indexes a given solution or file");
var resolveArg = new Argument<string>("query", "Solution name or path to resolve");
resolveCommand.AddArgument(resolveArg);
resolveCommand.SetHandler((string query) =>
{
    var catalogPath = FindCatalogPath();
    if (catalogPath is null) { Console.WriteLine("No catalog found."); return; }
    using var conn = new SqliteConnection($"Data Source={catalogPath};Pooling=False");
    conn.Open();
    var entry = CatalogDatabase.Resolve(conn, query);
    if (entry is null) { Console.WriteLine($"No database found for '{query}'. Run 'codeindex index <solution>' first."); return; }
    Console.WriteLine($"Solution: {entry.SolutionName}\nDatabase: {entry.DatabasePath}\nCommit:   {entry.IndexedCommit}\nIndexed:  {entry.IndexedAt}");
}, resolveArg);
rootCommand.AddCommand(resolveCommand);

// ---- search and lookup ---------------------------------------------------------------------------

var findNameArg = SymbolArg("name", "Symbol name, prefix, or full-text query");
var limit20 = () => new Option<int>("--limit", () => 20, "Maximum results");
var findLimit = limit20();
var findOffset = new Option<int>("--offset", () => 0, "Results to skip");
var findProject = new Option<string>("--project", () => "", "Only symbols in projects whose name or path contains this");
rootCommand.AddCommand(Query("find-symbol", "Find symbols by name (exact, then prefix, then substring; full-text for other input)", findNameArg,
    c => { c.AddOption(kindOption); c.AddOption(findProject); c.AddOption(includeGeneratedOption); c.AddOption(includeExternalOption); c.AddOption(findLimit); c.AddOption(findOffset); },
    (ctx, q) => q.FindSymbol(ctx.ParseResult.GetValueForArgument(findNameArg), new FindOptions(
        Blank(ctx.ParseResult.GetValueForOption(kindOption)), Blank(ctx.ParseResult.GetValueForOption(findProject)),
        ctx.ParseResult.GetValueForOption(includeGeneratedOption), ctx.ParseResult.GetValueForOption(includeExternalOption),
        ctx.ParseResult.GetValueForOption(findLimit), ctx.ParseResult.GetValueForOption(findOffset)))));

var searchTextArg = SymbolArg("text", "Full-text query over names, signatures, and docs (FTS5 syntax)");
var searchLimit = limit20();
rootCommand.AddCommand(Query("search", "Full-text search over symbol names, signatures, and documentation", searchTextArg,
    c => { c.AddOption(includeGeneratedOption); c.AddOption(includeExternalOption); c.AddOption(searchLimit); },
    (ctx, q) => q.Search(ctx.ParseResult.GetValueForArgument(searchTextArg), new SearchOptions(
        ctx.ParseResult.GetValueForOption(includeGeneratedOption), ctx.ParseResult.GetValueForOption(includeExternalOption),
        ctx.ParseResult.GetValueForOption(searchLimit)))));

var atArg = SymbolArg("position", "file:line or file:line:col (1-based)");
rootCommand.AddCommand(Query("at", "The symbol at a source position: the symbol used there, else the declaration containing it", atArg, null,
    (ctx, q) =>
    {
        var selector = new SymbolSelector(Text: ctx.ParseResult.GetValueForArgument(atArg));
        if (!selector.TryGetPosition(out var file, out var line, out var column))
            throw new ArgumentException("Expected file:line or file:line:col.");
        return q.At(file, line, column);
    }));

var defArg = SymbolArg();
rootCommand.AddCommand(Query("definition", "Where a symbol is declared (one location per declaration)", defArg, AddSelectorOptions,
    (ctx, q) => q.Definition(Selector(ctx, defArg))));

var showArg = SymbolArg();
var showSource = new Option<bool>("--source", () => false, "Include the source of the declaration");
var showMaxLines = new Option<int>("--max-lines", () => 200, "Maximum source lines with --source");
rootCommand.AddCommand(Query("show", "A symbol's details, optionally with its source", showArg,
    c => { AddSelectorOptions(c); c.AddOption(showSource); c.AddOption(showMaxLines); },
    (ctx, q) => q.Show(Selector(ctx, showArg), ctx.ParseResult.GetValueForOption(showSource), ctx.ParseResult.GetValueForOption(showMaxLines))));

var outlineArg = SymbolArg("target", "A file path (outlines the file) or a type (outlines its members)");
rootCommand.AddCommand(Query("outline", "Structure of a file or type: symbols nested by container", outlineArg, AddSelectorOptions,
    (ctx, q) => q.Outline(Selector(ctx, outlineArg))));

// ---- references and call graph -------------------------------------------------------------------

var refsArg = SymbolArg();
var refKindOption = new Option<string>("--ref-kind", () => "", "Only these reference kinds, comma separated (call, read, write, type-use, create, inherit, implement, attribute, typeof, nameof, use)");
var refsFile = new Option<string>("--file", () => "", "Only references in files whose path contains this");
var refsProject = new Option<string>("--project", () => "", "Only references in projects whose name or path contains this");
var refsContext = new Option<bool>("--context", () => false, "Include the source line of each reference");
var refsGroup = new Option<string>("--group-by", () => "none", "Group results: none, file, member, or kind");
var refsLimit = new Option<int>("--limit", () => 100, "Maximum references");
var refsOffset = new Option<int>("--offset", () => 0, "References to skip");
rootCommand.AddCommand(Query("refs", "Every recorded use of a symbol, with kind and containing member", refsArg,
    c => { AddSelectorOptions(c); c.AddOption(refKindOption); c.AddOption(refsFile); c.AddOption(refsProject); c.AddOption(refsContext); c.AddOption(refsGroup); c.AddOption(refsLimit); c.AddOption(refsOffset); },
    (ctx, q) => q.Refs(Selector(ctx, refsArg), new RefsOptions(
        Kinds: Blank(ctx.ParseResult.GetValueForOption(refKindOption))?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        File: Blank(ctx.ParseResult.GetValueForOption(refsFile)),
        Project: Blank(ctx.ParseResult.GetValueForOption(refsProject)),
        IncludeGenerated: ctx.ParseResult.GetValueForOption(includeGeneratedOption),
        Context: ctx.ParseResult.GetValueForOption(refsContext),
        GroupBy: ctx.ParseResult.GetValueForOption(refsGroup)!,
        Limit: ctx.ParseResult.GetValueForOption(refsLimit),
        Offset: ctx.ParseResult.GetValueForOption(refsOffset)))));

Command CallCommand(string name, string description, bool incoming)
{
    var arg = SymbolArg();
    var depth = new Option<int>("--depth", () => 1, "How many calls deep to follow (0 = unlimited)");
    var limit = new Option<int>("--limit", () => 50, "Maximum members returned");
    var dispatch = new Option<bool>("--through-dispatch", () => false,
        incoming ? "Count calls made through an interface/abstract/base member this symbol implements or overrides"
                 : "Also list the members that implement or override an interface/abstract member that is called");
    var creates = new Option<bool>("--include-creates", () => false, "Also follow object creation (new)");
    var excludeGenerated = new Option<bool>("--exclude-generated", () => false, "Leave out members declared in generated code");
    return Query(name, description, arg,
        c => { AddSelectorOptions(c); c.AddOption(depth); c.AddOption(limit); c.AddOption(dispatch); c.AddOption(creates); c.AddOption(includeExternalOption); c.AddOption(excludeGenerated); },
        (ctx, q) =>
        {
            var options = new CallOptions(
                ctx.ParseResult.GetValueForOption(depth), ctx.ParseResult.GetValueForOption(limit), ctx.ParseResult.GetValueForOption(dispatch),
                ctx.ParseResult.GetValueForOption(creates), ctx.ParseResult.GetValueForOption(includeExternalOption),
                ctx.ParseResult.GetValueForOption(excludeGenerated));
            return incoming ? q.Callers(Selector(ctx, arg), options) : q.Callees(Selector(ctx, arg), options);
        });
}
rootCommand.AddCommand(CallCommand("callers", "Members that call a symbol (incoming calls), with call sites", incoming: true));
rootCommand.AddCommand(CallCommand("callees", "Members a symbol calls (outgoing calls), with call sites", incoming: false));

var implArg = SymbolArg();
var implTransitive = new Option<bool>("--transitive", () => false, "Follow the chain (implementers of implementers, overrides of overrides)");
var implLimit = new Option<int>("--limit", () => 50, "Maximum results");
rootCommand.AddCommand(Query("implementations", "Types implementing or inheriting a type, or members implementing or overriding a member", implArg,
    c => { AddSelectorOptions(c); c.AddOption(implTransitive); c.AddOption(implLimit); },
    (ctx, q) => q.Implementations(Selector(ctx, implArg), ctx.ParseResult.GetValueForOption(implTransitive), ctx.ParseResult.GetValueForOption(implLimit))));

var hierArg = SymbolArg();
var hierDirection = new Option<string>("--direction", () => "both", "up (supertypes), down (subtypes), or both");
var hierDepth = new Option<int>("--depth", () => 3, "Maximum depth");
var hierLimit = new Option<int>("--limit", () => 200, "Maximum nodes");
rootCommand.AddCommand(Query("hierarchy", "Supertypes and subtypes of a type", hierArg,
    c => { AddSelectorOptions(c); c.AddOption(hierDirection); c.AddOption(hierDepth); c.AddOption(hierLimit); },
    (ctx, q) => q.Hierarchy(Selector(ctx, hierArg), ctx.ParseResult.GetValueForOption(hierDirection)!, ctx.ParseResult.GetValueForOption(hierDepth), ctx.ParseResult.GetValueForOption(hierLimit))));

// ---- helpers -------------------------------------------------------------------------------------

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
    var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    foreach (var start in new[] { Directory.GetCurrentDirectory(), profile, Path.Combine(profile, ".codeindex") })
    {
        var probe = start;
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

static List<CatalogEntry>? LoadCatalog(out string? catalogPath)
{
    catalogPath = FindCatalogPath();
    if (catalogPath is null) return null;
    using var conn = new SqliteConnection($"Data Source={catalogPath};Pooling=False");
    conn.Open();
    return CatalogDatabase.List(conn);
}

/// <summary>--db wins; then --solution via the catalog; with neither, the only indexed solution if there is exactly one.</summary>
static string? ResolveDbPath(string db, string solution, out string? problem)
{
    problem = null;
    if (!string.IsNullOrEmpty(db))
    {
        if (File.Exists(db)) return db;
        problem = $"No index database at '{db}'. Run 'codeindex index <solution>' first.";
        return null;
    }

    var entries = LoadCatalog(out _);
    if (entries is null || entries.Count == 0)
    {
        problem = "No index database found. Run 'codeindex index <solution>' first, or pass --db.";
        return null;
    }

    if (!string.IsNullOrEmpty(solution))
    {
        var match = entries.FirstOrDefault(e => e.SolutionName.Equals(solution, StringComparison.OrdinalIgnoreCase))
                    ?? entries.FirstOrDefault(e => e.SolutionName.Contains(solution, StringComparison.OrdinalIgnoreCase));
        if (match is not null && File.Exists(match.DatabasePath)) return match.DatabasePath;
        problem = $"No indexed solution matches '{solution}'. Indexed: {string.Join(", ", entries.Select(e => e.SolutionName))}.";
        return null;
    }

    if (entries.Count == 1 && File.Exists(entries[0].DatabasePath)) return entries[0].DatabasePath;
    problem = $"Several solutions are indexed ({string.Join(", ", entries.Select(e => e.SolutionName))}); pass --solution <name> or --db <path>.";
    return null;
}

return await rootCommand.InvokeAsync(args);
