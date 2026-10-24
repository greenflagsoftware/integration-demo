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
    "CodeIndexer: semantic code navigation for .NET, from a Roslyn-built index in SQLite. It answers where a symbol is defined, " +
    "used, called, or implemented exactly, where grep can only guess.\n" +
    "\n" +
    "Workflow:\n" +
    "  codeindex index MyApp.sln                  build the index, or update it (fast when nothing changed)\n" +
    "  codeindex find-symbol OrderService          locate a symbol; the result includes its id\n" +
    "  codeindex callers --id 42 --depth 2         then ask about it: definition, refs, callers, callees,\n" +
    "                                              implementations, hierarchy, outline, show\n" +
    "\n" +
    "Naming a symbol: by name (Add, Calculator.Add), by --id or --key from a previous result, or by position (src/Foo.cs:12:5).\n" +
    "A name matching several symbols returns \"candidates\" instead of guessing; pick one by id.\n" +
    "Output is JSON; add --format table for people. Every result says whether the index is stale.\n" +
    "AI agents: 'codeindex mcp' serves these commands over the Model Context Protocol.") { Name = "codeindex" };

// ---- shared options ------------------------------------------------------------------------------

var dbOption = new Option<string>("--db", "Path to the index database (default: the only indexed solution)");
var solutionOption = new Option<string>("--solution", "Name of an indexed solution (see 'list')");
var formatOption = new Option<string>("--format", () => "json", "Output format: json or table");

var idOption = new Option<long?>("--id", "Symbol id from a previous result");
var keyOption = new Option<string>("--key", "Symbol key (or documentation id) from a previous result");
var kindOption = new Option<string>("--kind", "Only symbols of this kind (class, method, property, ...)");
var includeGeneratedOption = new Option<bool>("--include-generated", "Include generated code");
var includeExternalOption = new Option<bool>("--include-external", "Include symbols declared outside the indexed projects");

Argument<string> SymbolArg(string name = "symbol", string description = "Symbol: a name, Type.Member, documentation id, or file:line[:col] (or use --id / --key)", bool required = false) =>
    new(name, description) { Arity = required ? ArgumentArity.ExactlyOne : ArgumentArity.ZeroOrOne };

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
        var dbPath = IndexLocator.ResolveDbPath(ctx.ParseResult.GetValueForOption(dbOption), ctx.ParseResult.GetValueForOption(solutionOption), out var problem);
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

var indexCommand = new Command("index",
    "Build or update the index for a solution. Redoes only what a change affects; does nothing if nothing changed");
var solutionArg = new Argument<FileInfo>("solution", "Path to the .sln, .slnx, or .csproj file");
var includeOption = new Option<string[]>("--include", "Project names to include (exact, or with * and ? wildcards)");
var excludeOption = new Option<string[]>("--exclude", "Project names to exclude (exact, or with * and ? wildcards)");
var indexDbOption = new Option<string>("--db", "Where to write the index (default: .codeindex/<solution>.db next to the solution)");
var fullOption = new Option<bool>("--full", "Rebuild the whole index instead of updating it");
var dryRunOption = new Option<bool>("--dry-run", "Show what would be redone and why, without changing the index");
indexCommand.AddArgument(solutionArg);
indexCommand.AddOption(indexDbOption);
indexCommand.AddOption(includeOption);
indexCommand.AddOption(excludeOption);
indexCommand.AddOption(fullOption);
indexCommand.AddOption(dryRunOption);

indexCommand.SetHandler(async (FileInfo solution, string db, string[] include, string[] exclude, bool full, bool dryRun) =>
{
    var filter = new ProjectFilter();
    foreach (var inc in include) filter.Include(inc);
    foreach (var exc in exclude) filter.Exclude(exc);

    var dbPath = db;
    if (string.IsNullOrEmpty(dbPath))
    {
        var solutionDir = Path.GetDirectoryName(solution.FullName) ?? Directory.GetCurrentDirectory();
        var codeIndexDir = Path.Combine(solutionDir, ".codeindex");
        Directory.CreateDirectory(codeIndexDir);
        dbPath = Path.Combine(codeIndexDir, $"{Path.GetFileNameWithoutExtension(solution.FullName)}.db");
    }

    var timer = System.Diagnostics.Stopwatch.StartNew();
    var run = await IndexRunner.RunAsync(new IndexRunOptions(solution.FullName, dbPath, FindRepoRoot(solution.FullName), filter, full, dryRun));

    if (run.LoadResult is not null)
        SolutionLoader.PrintSummary(run.LoadResult);

    var verb = run.WasDryRun ? "Would " : "";
    switch (run.Mode)
    {
        case IndexMode.UpToDate:
            Console.WriteLine($"Index is up to date ({run.Reason}). Nothing was loaded or written.");
            return;
        case IndexMode.Full:
            Console.WriteLine($"\n{verb}rebuild the whole index ({run.Reason}).".TrimStart());
            break;
        case IndexMode.Incremental:
            Console.WriteLine($"\n{verb}update incrementally ({run.Reason}).".TrimStart());
            foreach (var c in run.ChangedInputs.Take(8)) Console.WriteLine($"  changed: {c}");
            if (run.ChangedInputs.Count > 8) Console.WriteLine($"  ... and {run.ChangedInputs.Count - 8} more");
            foreach (var r in run.Reindexed) Console.WriteLine($"  redo:    {r.Name} ({r.Reason})");
            foreach (var u in run.Unchanged) Console.WriteLine($"  keep:    {u}");
            foreach (var x in run.Removed) Console.WriteLine($"  removed: {x}");
            break;
    }
    if (run.WasDryRun) { Console.WriteLine("Dry run: the index was not changed."); return; }

    var result = run.Result!;
    Console.WriteLine($"Index: {dbPath}");
    Console.WriteLine($"Indexed: {result.ProjectsIndexed} projects, {result.FilesIndexed} files, {result.SymbolsIndexed} symbols " +
                      $"(+{result.ExternalSymbolsIndexed} external), {result.ReferencesExtracted} references, {result.EdgesExtracted} edges");
    Console.WriteLine("Time: " + timer.Elapsed.TotalSeconds.ToString("F1") + "s total; " +
                      string.Join(", ", result.Timings.Select(t => $"{t.Key} {t.Value.TotalSeconds:F1}s")));
    foreach (var d in result.Diagnostics)
        Console.WriteLine($"  [diag] {d}");
}, solutionArg, indexDbOption, includeOption, excludeOption, fullOption, dryRunOption);
rootCommand.AddCommand(indexCommand);

// ---- info / list / resolve -----------------------------------------------------------------------

var infoCommand = new Command("info", "Show index statistics, staleness, and diagnostics");
infoCommand.AddOption(dbOption);
infoCommand.AddOption(solutionOption);
infoCommand.SetHandler(ctx =>
{
    var dbPath = IndexLocator.ResolveDbPath(ctx.ParseResult.GetValueForOption(dbOption), ctx.ParseResult.GetValueForOption(solutionOption), out var problem);
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
    var entries = IndexLocator.LoadCatalog(out var catalogPath);
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
    var catalogPath = IndexLocator.FindCatalogPath();
    if (catalogPath is null) { Console.WriteLine("No catalog found."); return; }
    using var conn = new SqliteConnection($"Data Source={catalogPath};Pooling=False");
    conn.Open();
    var entry = CatalogDatabase.Resolve(conn, query);
    if (entry is null) { Console.WriteLine($"No database found for '{query}'. Run 'codeindex index <solution>' first."); return; }
    Console.WriteLine($"Solution: {entry.SolutionName}\nDatabase: {entry.DatabasePath}\nCommit:   {entry.IndexedCommit}\nIndexed:  {entry.IndexedAt}");
}, resolveArg);
rootCommand.AddCommand(resolveCommand);

// ---- search and lookup ---------------------------------------------------------------------------

var findNameArg = SymbolArg("name", "Name, prefix, or part of a name to look for (other input is a full-text query)", required: true);
var limit20 = () => new Option<int>("--limit", () => 20, "Maximum results");
var findLimit = limit20();
var findOffset = new Option<int>("--offset", () => 0, "Results to skip");
var findProject = new Option<string>("--project", "Only symbols in projects whose name or path contains this");
rootCommand.AddCommand(Query("find-symbol", "Find symbols by name (exact, then prefix, then substring) and get their ids", findNameArg,
    c => { c.AddOption(kindOption); c.AddOption(findProject); c.AddOption(includeGeneratedOption); c.AddOption(includeExternalOption); c.AddOption(findLimit); c.AddOption(findOffset); },
    (ctx, q) => q.FindSymbol(ctx.ParseResult.GetValueForArgument(findNameArg), new FindOptions(
        Blank(ctx.ParseResult.GetValueForOption(kindOption)), Blank(ctx.ParseResult.GetValueForOption(findProject)),
        ctx.ParseResult.GetValueForOption(includeGeneratedOption), ctx.ParseResult.GetValueForOption(includeExternalOption),
        ctx.ParseResult.GetValueForOption(findLimit), ctx.ParseResult.GetValueForOption(findOffset)))));

var searchTextArg = SymbolArg("text", "Full-text query (FTS5 syntax: words, \"phrases\", prefix*, AND/OR)", required: true);
var searchLimit = limit20();
rootCommand.AddCommand(Query("search", "Full-text search over symbol names, signatures, and XML documentation", searchTextArg,
    c => { c.AddOption(includeGeneratedOption); c.AddOption(includeExternalOption); c.AddOption(searchLimit); },
    (ctx, q) => q.Search(ctx.ParseResult.GetValueForArgument(searchTextArg), new SearchOptions(
        ctx.ParseResult.GetValueForOption(includeGeneratedOption), ctx.ParseResult.GetValueForOption(includeExternalOption),
        ctx.ParseResult.GetValueForOption(searchLimit)))));

var atArg = SymbolArg("position", "file:line or file:line:col (1-based; the file may be any unique path suffix)", required: true);
rootCommand.AddCommand(Query("at", "The symbol at a source position: what is used there, else the declaration containing it", atArg, null,
    (ctx, q) =>
    {
        var selector = new SymbolSelector(Text: ctx.ParseResult.GetValueForArgument(atArg));
        if (!selector.TryGetPosition(out var file, out var line, out var column))
            throw new ArgumentException("Expected file:line or file:line:col.");
        return q.At(file, line, column);
    }));

var defArg = SymbolArg();
rootCommand.AddCommand(Query("definition", "Where a symbol is declared (one location per declaration; a partial class has several)", defArg, AddSelectorOptions,
    (ctx, q) => q.Definition(Selector(ctx, defArg))));

var showArg = SymbolArg();
var showSource = new Option<bool>("--source", "Include the source of the declaration");
var showMaxLines = new Option<int>("--max-lines", () => 200, "Maximum source lines with --source");
rootCommand.AddCommand(Query("show", "A symbol's signature, documentation, and (with --source) its source code", showArg,
    c => { AddSelectorOptions(c); c.AddOption(showSource); c.AddOption(showMaxLines); },
    (ctx, q) => q.Show(Selector(ctx, showArg), ctx.ParseResult.GetValueForOption(showSource), ctx.ParseResult.GetValueForOption(showMaxLines))));

var outlineArg = SymbolArg("target", "A file path (outlines the file) or a type name (outlines its members); or use --id / --key");
rootCommand.AddCommand(Query("outline", "Structure of a file or type: its symbols nested by container, with line numbers", outlineArg, AddSelectorOptions,
    (ctx, q) => q.Outline(Selector(ctx, outlineArg))));

// ---- references and call graph -------------------------------------------------------------------

var refsArg = SymbolArg();
var refKindOption = new Option<string>("--ref-kind", "Only these reference kinds, comma separated (call, read, write, type-use, create, inherit, implement, attribute, typeof, nameof, use)");
var refsFile = new Option<string>("--file", "Only references in files whose path contains this");
var refsProject = new Option<string>("--project", "Only references in projects whose name or path contains this");
var refsContext = new Option<bool>("--context", "Include the source line of each reference");
var refsGroup = new Option<string>("--group-by", () => "none", "Group results: none, file, member, or kind");
var refsLimit = new Option<int>("--limit", () => 100, "Maximum references");
var refsOffset = new Option<int>("--offset", () => 0, "References to skip");
rootCommand.AddCommand(Query("refs", "Every use of a symbol, with the kind of use and the member containing it", refsArg,
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
    var dispatch = new Option<bool>("--through-dispatch",
        incoming ? "Count calls made through an interface/abstract/base member this symbol implements or overrides"
                 : "Also list the members that implement or override an interface/abstract member that is called");
    var creates = new Option<bool>("--include-creates", "Also follow object creation (new)");
    var excludeGenerated = new Option<bool>("--exclude-generated", "Leave out members declared in generated code");
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
rootCommand.AddCommand(CallCommand("callers", "Methods that call a symbol (incoming call hierarchy), with call sites", incoming: true));
rootCommand.AddCommand(CallCommand("callees", "Methods a symbol calls (outgoing call hierarchy), with call sites", incoming: false));

var implArg = SymbolArg();
var implTransitive = new Option<bool>("--transitive", "Follow the chain (implementers of implementers, overrides of overrides)");
var implLimit = new Option<int>("--limit", () => 50, "Maximum results");
rootCommand.AddCommand(Query("implementations", "Types that implement or derive from a type, or members that implement or override a member", implArg,
    c => { AddSelectorOptions(c); c.AddOption(implTransitive); c.AddOption(implLimit); },
    (ctx, q) => q.Implementations(Selector(ctx, implArg), ctx.ParseResult.GetValueForOption(implTransitive), ctx.ParseResult.GetValueForOption(implLimit))));

var hierArg = SymbolArg();
var hierDirection = new Option<string>("--direction", () => "both", "up (supertypes), down (subtypes), or both");
var hierDepth = new Option<int>("--depth", () => 3, "Maximum depth");
var hierLimit = new Option<int>("--limit", () => 200, "Maximum nodes");
rootCommand.AddCommand(Query("hierarchy", "Base types and derived types of a type", hierArg,
    c => { AddSelectorOptions(c); c.AddOption(hierDirection); c.AddOption(hierDepth); c.AddOption(hierLimit); },
    (ctx, q) => q.Hierarchy(Selector(ctx, hierArg), ctx.ParseResult.GetValueForOption(hierDirection)!, ctx.ParseResult.GetValueForOption(hierDepth), ctx.ParseResult.GetValueForOption(hierLimit))));

// ---- mcp -----------------------------------------------------------------------------------------

var mcpCommand = new Command("mcp",
    "Serve these commands to an AI agent over the Model Context Protocol (stdio, read-only; one warm process, so lookups take milliseconds)");
mcpCommand.AddOption(dbOption);
mcpCommand.AddOption(solutionOption);
mcpCommand.SetHandler(async ctx =>
{
    // stdout carries only protocol messages; anything else goes to stderr.
    Console.InputEncoding = new UTF8Encoding(false);
    var server = new CodeIndexer.Core.Mcp.McpServer(
        Blank(ctx.ParseResult.GetValueForOption(dbOption)), Blank(ctx.ParseResult.GetValueForOption(solutionOption)));
    await server.RunAsync(Console.In, Console.Out, ctx.GetCancellationToken());
});
rootCommand.AddCommand(mcpCommand);

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

return await rootCommand.InvokeAsync(args);
