namespace CodeIndexer.Core.Queries;

public sealed partial class SymbolQueries
{
    /// <summary>
    /// The symbol at a source position (1-based line, optional 1-based column). On a use of a symbol, that is the
    /// symbol being used (what "go to definition" starts from). Elsewhere it is the innermost declaration that
    /// contains the position. With no column, a line holding several uses is ambiguous and returns candidates.
    /// </summary>
    public QueryResponse<AtResult> At(string file, int line, int? column = null)
    {
        AtResult Nothing(string path) => new()
        {
            Match = "none", FilePath = path, Line = line, Column = column,
            Symbol = new SymbolResult { Name = "", Kind = "unknown" },
        };
        QueryResponse<AtResult> Fail(string path, string warning, List<SymbolResult>? candidates = null) =>
            Respond("at", Nothing(path), 0, candidates: candidates, warnings: new List<string> { warning });

        var files = FindFiles(file);
        if (files.Count == 0)
            return Fail(file, $"File not indexed: {file}. Run 'codeindex index' for the solution that contains it.");
        if (files.Count > 1)
            return Fail(file, $"'{file}' matches {files.Count} files: {string.Join(", ", files.Take(MaxCandidates).Select(f => f.Path))}. Use a longer path.");
        var target = files[0];

        // 1. A use of a symbol at this position.
        var uses = Read(
            "SELECT symbol_id, kind, column, length, enclosing_symbol_id FROM symbol_references WHERE file_id = @f AND line = @l ORDER BY column, id",
            r => (SymbolId: r.GetInt64(0), Kind: r.GetString(1), Column: r.GetInt32(2), Length: r.GetInt32(3), Enclosing: r.IsDBNull(4) ? (long?)null : r.GetInt64(4)),
            ("@f", target.Id), ("@l", line));
        if (column is { } col)
            uses = uses.Where(u => col >= u.Column && col < u.Column + u.Length).ToList();

        var distinct = uses.GroupBy(u => u.SymbolId).Select(g => g.OrderBy(x => x.Kind == "write" ? 0 : 1).First()).ToList();
        if (distinct.Count == 1)
        {
            var use = distinct[0];
            var symbol = GetSymbol(use.SymbolId)!;
            var enclosing = use.Enclosing is { } eid && GetSymbol(eid) is { } e ? ToRef(e) : null;
            return Respond("at",
                new AtResult { Match = "reference", Symbol = symbol, ReferenceKind = use.Kind, Enclosing = enclosing, FilePath = target.Path, Line = line, Column = column },
                1, target: symbol);
        }
        if (distinct.Count > 1)
        {
            var candidates = distinct.Select(u => GetSymbol(u.SymbolId)!).ToList();
            var where = column is null ? "that line" : "that position";
            return Fail(target.Path, $"{distinct.Count} symbols are used at {where}" + (column is null ? "; add a column (file:line:col) or pick one with --id." : "; pick one with --id."), candidates);
        }

        // 2. The innermost declaration containing the position.
        var containing = Read(
            $@"SELECT {SymbolColumns}, l.start_line FROM symbol_locations l JOIN symbols s ON s.id = l.symbol_id
               LEFT JOIN files f ON f.id = s.file_id LEFT JOIN projects p ON p.id = f.project_id
               WHERE l.file_id = @f AND l.start_line <= @l AND l.end_line >= @l AND s.kind <> 'namespace'
               ORDER BY (l.end_line - l.start_line), (l.end_col - l.start_col), s.id LIMIT 1",
            r => (Symbol: ReadSymbol(r), StartLine: r.GetInt32(14)),
            ("@f", target.Id), ("@l", line));
        if (containing.Count == 1)
        {
            var (symbol, startLine) = containing[0];
            return Respond("at",
                new AtResult { Match = startLine == line ? "declaration" : "containing", Symbol = symbol, FilePath = target.Path, Line = line, Column = column },
                1, target: symbol);
        }

        return Fail(target.Path, $"Nothing indexed at {target.Path}:{line}" + (column is null ? "" : $":{column}") + ".");
    }
}
