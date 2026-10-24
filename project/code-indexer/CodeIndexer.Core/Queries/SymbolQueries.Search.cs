using Microsoft.Data.Sqlite;

namespace CodeIndexer.Core.Queries;

public sealed record FindOptions(
    string? Kind = null, string? Project = null, bool IncludeGenerated = false, bool IncludeExternal = false,
    int Limit = 20, int Offset = 0);

public sealed record SearchOptions(bool IncludeGenerated = false, bool IncludeExternal = false, int Limit = 20);

public sealed partial class SymbolQueries
{
    // ---- find-symbol ---------------------------------------------------------------------------

    /// <summary>Finds symbols by name: exact, then prefix, then substring; falls back to full-text for other input.</summary>
    public QueryResponse<List<SymbolResult>> FindSymbol(string query, FindOptions? options = null)
    {
        options ??= new FindOptions();
        bool plain = query.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '_');

        var where = new List<string>();
        var args = new List<(string, object?)>();
        string from, order;

        if (plain)
        {
            from = SymbolFrom;
            where.Add("(s.name = @q OR s.name LIKE @prefix ESCAPE '\\' OR s.name LIKE @like ESCAPE '\\' OR s.full_name = @q OR s.full_name LIKE @like ESCAPE '\\')");
            args.Add(("@q", query));
            args.Add(("@prefix", EscapeLike(query) + "%"));
            args.Add(("@like", "%" + EscapeLike(query) + "%"));
            order = "CASE WHEN s.name = @q THEN 0 WHEN s.name LIKE @prefix ESCAPE '\\' THEN 1 WHEN s.full_name = @q THEN 2 ELSE 3 END, s.name, s.id";
        }
        else
        {
            from = "symbols_fts fts JOIN symbols s ON s.id = fts.rowid LEFT JOIN files f ON f.id = s.file_id LEFT JOIN projects p ON p.id = f.project_id";
            where.Add("symbols_fts MATCH @q");
            args.Add(("@q", query));
            order = "fts.rank, s.id";
        }

        if (!options.IncludeGenerated) where.Add("s.is_generated = 0");
        if (!options.IncludeExternal) where.Add("s.is_external = 0");
        if (!string.IsNullOrEmpty(options.Kind))
        {
            where.Add("s.kind = @kind");
            args.Add(("@kind", options.Kind));
        }
        if (!string.IsNullOrEmpty(options.Project))
        {
            where.Add("(p.name LIKE @proj ESCAPE '\\' OR p.path LIKE @proj ESCAPE '\\')");
            args.Add(("@proj", "%" + EscapeLike(options.Project) + "%"));
        }

        var whereSql = string.Join(" AND ", where);
        List<SymbolResult> rows;
        long total;
        try
        {
            total = Count($"SELECT COUNT(*) FROM {from} WHERE {whereSql}", args.ToArray());
            rows = Read(
                $"SELECT {SymbolColumns} FROM {from} WHERE {whereSql} ORDER BY {order} LIMIT @lim OFFSET @off",
                r => ReadSymbol(r),
                args.Concat(new (string, object?)[] { ("@lim", options.Limit + 1), ("@off", options.Offset) }).ToArray());
        }
        catch (SqliteException ex) when (!plain)
        {
            return Respond("find-symbol", new List<SymbolResult>(), 0,
                warnings: new List<string> { $"Invalid full-text query '{query}': {ex.Message}" });
        }

        bool truncated = rows.Count > options.Limit;
        if (truncated) rows = rows.Take(options.Limit).ToList();

        return Respond("find-symbol", rows, rows.Count, limit: options.Limit, truncated: truncated, totalMatches: (int)total);
    }

    // ---- search --------------------------------------------------------------------------------

    /// <summary>Full-text search over names, signatures, and documentation (FTS5 query syntax).</summary>
    public QueryResponse<List<SearchHit>> Search(string text, SearchOptions? options = null)
    {
        options ??= new SearchOptions();
        var where = "symbols_fts MATCH @q"
                    + (options.IncludeGenerated ? "" : " AND s.is_generated = 0")
                    + (options.IncludeExternal ? "" : " AND s.is_external = 0");
        try
        {
            var rows = Read(
                $"SELECT {SymbolColumns}, fts.rank FROM symbols_fts fts JOIN symbols s ON s.id = fts.rowid " +
                $"LEFT JOIN files f ON f.id = s.file_id LEFT JOIN projects p ON p.id = f.project_id WHERE {where} ORDER BY fts.rank, s.id LIMIT @lim",
                r =>
                {
                    var s = ReadSymbol(r);
                    return new SearchHit
                    {
                        Id = s.Id, Key = s.Key, Name = s.Name, FullName = s.FullName, Kind = s.Kind, Signature = s.Signature,
                        FilePath = s.FilePath, DocSummary = s.DocSummary, StartLine = s.StartLine, IsGenerated = s.IsGenerated,
                        IsExternal = s.IsExternal, ProjectName = s.ProjectName, Rank = r.GetDouble(14),
                    };
                },
                ("@q", text), ("@lim", options.Limit + 1));

            bool truncated = rows.Count > options.Limit;
            if (truncated) rows = rows.Take(options.Limit).ToList();
            return Respond("search", rows, rows.Count, limit: options.Limit, truncated: truncated);
        }
        catch (SqliteException ex)
        {
            return Respond("search", new List<SearchHit>(), 0,
                warnings: new List<string> { $"Invalid full-text query '{text}': {ex.Message}" });
        }
    }

    // ---- definition / show ---------------------------------------------------------------------

    private List<LocationResult> GetLocations(long symbolId) =>
        Read(@"SELECT f.path, l.start_line, l.start_col, l.end_line, l.end_col, l.is_primary, f.is_generated
               FROM symbol_locations l JOIN files f ON f.id = l.file_id
               WHERE l.symbol_id = @s ORDER BY l.is_primary DESC, f.path, l.start_line",
            r => new LocationResult
            {
                FilePath = r.GetString(0), StartLine = r.GetInt32(1), StartColumn = r.GetInt32(2),
                EndLine = r.GetInt32(3), EndColumn = r.GetInt32(4), IsPrimary = r.GetInt32(5) != 0, IsGenerated = r.GetInt32(6) != 0,
            },
            ("@s", symbolId));

    private static DefinitionResult Placeholder(string text) =>
        new() { Symbol = new SymbolResult { Name = text, Kind = "unknown", FullName = text }, Locations = new() };

    private static string? ExternalNote(SymbolResult symbol) =>
        symbol.IsExternal
            ? $"'{symbol.FullName ?? symbol.Name}' is declared outside the indexed projects (assembly {symbol.Key.Split('|')[0]}); there is no location or source."
            : null;

    /// <summary>Where a symbol is declared: one location per declaration (several for a partial type).</summary>
    public QueryResponse<DefinitionResult> Definition(SymbolSelector selector)
    {
        var resolution = Resolve(selector);
        if (!resolution.IsResolved)
            return Unresolved("definition", Placeholder(selector.Text ?? selector.Key ?? ""), resolution);

        var symbol = resolution.Symbol!;
        var locations = GetLocations(symbol.Id);
        var note = ExternalNote(symbol);
        return Respond("definition", new DefinitionResult { Symbol = symbol, Locations = locations }, locations.Count,
            target: symbol, warnings: note is null ? null : new List<string> { note });
    }

    /// <summary>A symbol's details, optionally with the source of its primary declaration.</summary>
    public QueryResponse<ShowResult> Show(SymbolSelector selector, bool includeSource = false, int maxSourceLines = 200)
    {
        var resolution = Resolve(selector);
        if (!resolution.IsResolved)
            return Unresolved("show", new ShowResult { Symbol = Placeholder(selector.Text ?? selector.Key ?? "").Symbol, Locations = new() }, resolution);

        var symbol = resolution.Symbol!;
        var locations = GetLocations(symbol.Id);
        string? source = null;
        bool sourceTruncated = false;
        var warnings = new List<string>();

        if (ExternalNote(symbol) is { } note) warnings.Add(note);
        else if (includeSource && locations.FirstOrDefault(l => l.IsPrimary) is { } primary)
        {
            (source, sourceTruncated) = ReadSource(primary.FilePath, primary.StartLine, primary.EndLine, maxSourceLines);
            if (source is null) warnings.Add($"Source file not found: {primary.FilePath}");
            else if (sourceTruncated) warnings.Add($"Source truncated to {maxSourceLines} lines; use --max-lines to see more.");
        }

        return Respond("show", new ShowResult { Symbol = symbol, Locations = locations, SourceCode = source, SourceTruncated = sourceTruncated },
            1, target: symbol, warnings: warnings);
    }

    private (string? Source, bool Truncated) ReadSource(string relativePath, int startLine, int endLine, int maxLines)
    {
        var lines = ReadFileLines(relativePath);
        if (lines is null) return (null, false);
        int start = Math.Max(0, startLine - 1);
        int end = Math.Min(lines.Length, endLine);
        if (end <= start) return (null, false);
        bool truncated = end - start > maxLines;
        if (truncated) end = start + maxLines;
        return (string.Join(Environment.NewLine, lines[start..end]), truncated);
    }

    private readonly Dictionary<string, string[]?> _fileLines = new();

    private string[]? ReadFileLines(string relativePath)
    {
        if (_fileLines.TryGetValue(relativePath, out var cached)) return cached;
        string[]? lines = null;
        if (_repoRoot is not null)
        {
            var full = Path.Combine(_repoRoot, relativePath);
            if (File.Exists(full)) lines = File.ReadAllLines(full);
        }
        return _fileLines[relativePath] = lines;
    }

    // ---- outline -------------------------------------------------------------------------------

    /// <summary>
    /// The structure of a file (its symbols nested by container) or of a type (its members). A selector whose text
    /// names an indexed file outlines the file; anything else is resolved as a symbol.
    /// </summary>
    public QueryResponse<OutlineResult> Outline(SymbolSelector selector)
    {
        if (selector.Id is null && string.IsNullOrWhiteSpace(selector.Key) && !string.IsNullOrWhiteSpace(selector.Text))
        {
            var files = FindFiles(selector.Text!);
            if (files.Count > 1)
                return Respond("outline", new OutlineResult { Symbols = new() }, 0,
                    warnings: new List<string> { $"'{selector.Text}' matches {files.Count} files: {string.Join(", ", files.Take(MaxCandidates).Select(f => f.Path))}. Use a longer path." });
            if (files.Count == 1)
                return OutlineFile(files[0]);
        }

        var resolution = Resolve(selector);
        if (!resolution.IsResolved)
            return Unresolved("outline", new OutlineResult { Symbols = new() }, resolution);
        return OutlineSymbol(resolution.Symbol!);
    }

    private sealed record FileRow(long Id, string Path, int LineCount, bool IsGenerated);

    private List<FileRow> FindFiles(string text)
    {
        var path = PathUtil.Normalize(text.Trim());
        return Read(
            "SELECT id, path, line_count, is_generated FROM files WHERE path = @p OR path LIKE @suffix ESCAPE '\\' ORDER BY path",
            r => new FileRow(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3) != 0),
            ("@p", path), ("@suffix", "%/" + EscapeLike(path)));
    }

    private QueryResponse<OutlineResult> OutlineFile(FileRow file)
    {
        // Symbols are placed by where they are declared in this file, so a partial type shows only its members
        // from this part.
        var rows = Read(
            $@"SELECT {SymbolColumns}, s.container_id, l.start_line, l.end_line
               FROM symbol_locations l JOIN symbols s ON s.id = l.symbol_id
               LEFT JOIN files f ON f.id = s.file_id LEFT JOIN projects p ON p.id = f.project_id
               WHERE l.file_id = @f ORDER BY l.start_line, l.start_col, s.id",
            r =>
            {
                var s = ReadSymbol(r);
                return (Node: new OutlineNode
                {
                    Id = s.Id, Key = s.Key, Name = s.Name, FullName = s.FullName, Kind = s.Kind, Accessibility = s.Accessibility,
                    Signature = s.Signature, StartLine = r.GetInt32(15), EndLine = r.GetInt32(16), FilePath = file.Path,
                    DocSummary = s.DocSummary, IsGenerated = s.IsGenerated, IsExternal = false, ProjectName = s.ProjectName,
                }, ContainerId: r.IsDBNull(14) ? (long?)null : r.GetInt64(14));
            },
            ("@f", file.Id));

        var byId = rows.ToDictionary(x => x.Node.Id, x => x.Node);
        var roots = new List<OutlineNode>();
        foreach (var (node, containerId) in rows)
        {
            if (containerId is { } cid && byId.TryGetValue(cid, out var parent)) parent.Children.Add(node);
            else roots.Add(node);
        }

        return Respond("outline",
            new OutlineResult { FilePath = file.Path, LineCount = file.LineCount, IsGenerated = file.IsGenerated, Symbols = roots },
            rows.Count);
    }

    private QueryResponse<OutlineResult> OutlineSymbol(SymbolResult symbol)
    {
        var warnings = new List<string>();
        if (symbol.IsExternal)
            warnings.Add($"'{symbol.FullName}' is external; only members referenced by indexed code are listed.");

        int count = 0;
        OutlineNode Build(SymbolResult s, int depth)
        {
            count++;
            var node = ToNode(s);
            if (depth < 8)
            {
                var members = Read(
                    $"SELECT {SymbolColumns} FROM {SymbolFrom} WHERE s.container_id = @c ORDER BY f.path, s.start_line, s.id",
                    r => ReadSymbol(r), ("@c", s.Id));
                foreach (var m in members) node.Children.Add(Build(m, depth + 1));
            }
            return node;
        }

        var root = Build(symbol, 0);
        return Respond("outline", new OutlineResult { Symbols = new List<OutlineNode> { root } }, count,
            target: symbol, warnings: warnings);
    }
}
