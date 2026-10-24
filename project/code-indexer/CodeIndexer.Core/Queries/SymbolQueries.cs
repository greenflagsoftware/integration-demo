using Microsoft.Data.Sqlite;

namespace CodeIndexer.Core.Queries;

public sealed class SymbolQueries : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _dbPath;
    private bool _disposed;

    public SymbolQueries(string dbPath)
    {
        _dbPath = dbPath;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        _connection = new SqliteConnection(builder.ConnectionString);
        _connection.Open();
    }

    public (string name, string path, string indexedAt, string commit) GetSolutionContext()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT name, path, indexed_at FROM solutions LIMIT 1";
        string name = "", path = "", indexedAt = "";
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
            {
                name = reader.GetString(0);
                path = reader.GetString(1);
                indexedAt = reader.GetString(2);
            }
        }
        cmd.CommandText = "SELECT COALESCE((SELECT value FROM meta WHERE key = 'git_commit'), 'unknown')";
        var commit = (cmd.ExecuteScalar() as string) ?? "unknown";
        return (name, path, indexedAt, commit);
    }

    public QueryResponse<List<SymbolResult>> FindSymbol(
        string query, string? kindFilter, string? projectFilter,
        bool includeGenerated, int limit, int offset)
    {
        var (solName, solPath, solIndexed, solCommit) = GetSolutionContext();
        var symbols = new List<SymbolResult>();
        using var cmd = _connection.CreateCommand();

        bool isExactAlpha = query.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '_');

        string sql;
        if (isExactAlpha)
        {
            sql = @"
                SELECT s.name, s.full_name, s.kind, s.accessibility, s.signature,
                       s.start_line, s.end_line, f.path, s.doc_summary, s.is_generated, p.name AS proj_name
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                JOIN projects p ON p.id = f.project_id
                WHERE (s.name = @q OR s.name LIKE @prefix OR s.name LIKE @like
                       OR s.full_name = @q OR s.full_name LIKE @prefix2)
                {gen_filter}
                {kind_filter}
                {proj_filter}
                ORDER BY
                    CASE WHEN s.name = @q THEN 0
                         WHEN s.name LIKE @prefix THEN 1
                         WHEN s.full_name = @q THEN 2
                         ELSE 3 END,
                    s.name
                LIMIT @lim OFFSET @off";
        }
        else
        {
            sql = @"
                SELECT s.name, s.full_name, s.kind, s.accessibility, s.signature,
                       s.start_line, s.end_line, f.path, s.doc_summary, s.is_generated, p.name AS proj_name
                FROM symbols_fts fts
                JOIN symbols s ON s.id = fts.rowid
                JOIN files f ON f.id = s.file_id
                JOIN projects p ON p.id = f.project_id
                WHERE symbols_fts MATCH @q
                {gen_filter}
                {kind_filter}
                {proj_filter}
                ORDER BY rank
                LIMIT @lim OFFSET @off";
        }

        var genFilterSql = includeGenerated ? "" : "AND s.is_generated = 0";
        var kindFilterSql = string.IsNullOrEmpty(kindFilter) ? "" : "AND s.kind = @kind";
        var projFilterSql = string.IsNullOrEmpty(projectFilter) ? "" : "AND (p.name LIKE @proj OR p.path LIKE @proj2)";

        sql = sql.Replace("{gen_filter}", genFilterSql)
                 .Replace("{kind_filter}", kindFilterSql)
                 .Replace("{proj_filter}", projFilterSql);

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@q", query);
        cmd.Parameters.AddWithValue("@prefix", query + "%");
        cmd.Parameters.AddWithValue("@prefix2", "%" + query + "%");
        cmd.Parameters.AddWithValue("@like", "%" + query + "%");
        cmd.Parameters.AddWithValue("@lim", limit + 1);
        cmd.Parameters.AddWithValue("@off", offset);

        if (!string.IsNullOrEmpty(kindFilter))
            cmd.Parameters.AddWithValue("@kind", kindFilter);
        if (!string.IsNullOrEmpty(projectFilter))
        {
            cmd.Parameters.AddWithValue("@proj", $"%{projectFilter}%");
            cmd.Parameters.AddWithValue("@proj2", $"%{projectFilter}%");
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            symbols.Add(new SymbolResult
            {
                Name = reader.GetString(0),
                FullName = reader.IsDBNull(1) ? null : reader.GetString(1),
                Kind = reader.GetString(2),
                Accessibility = reader.IsDBNull(3) ? null : reader.GetString(3),
                Signature = reader.IsDBNull(4) ? null : reader.GetString(4),
                StartLine = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                EndLine = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                FilePath = reader.IsDBNull(7) ? null : reader.GetString(7),
                DocSummary = reader.IsDBNull(8) ? null : reader.GetString(8),
                IsGenerated = reader.GetInt32(9) != 0,
                ProjectName = reader.IsDBNull(10) ? null : reader.GetString(10),
            });
        }

        bool truncated = symbols.Count > limit;
        if (truncated) symbols = symbols.Take(limit).ToList();

        return new QueryResponse<List<SymbolResult>>
        {
            Command = "find-symbol",
            SolutionName = solName,
            SolutionPath = solPath,
            IndexedAt = solIndexed,
            IndexedCommit = solCommit,
            Data = symbols,
            TotalCount = symbols.Count,
            Limit = limit,
            Truncated = truncated,
        };
    }

    public QueryResponse<OutlineResult> Outline(string filePath)
    {
        var (solName, solPath, solIndexed, solCommit) = GetSolutionContext();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT f.path, f.line_count, f.is_generated,
                   s.name, s.full_name, s.kind, s.accessibility, s.signature,
                   s.start_line, s.end_line, s.doc_summary, s.is_generated
            FROM files f
            LEFT JOIN symbols s ON s.file_id = f.id
            WHERE f.path LIKE @path
            ORDER BY s.start_line";
        cmd.Parameters.AddWithValue("@path", $"%{PathUtil.Normalize(filePath)}%");

        var symbols = new List<SymbolResult>();
        string resolvedPath = "";
        int lineCount = 0;
        bool isGenerated = false;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            resolvedPath = reader.GetString(0);
            lineCount = reader.GetInt32(1);
            isGenerated = reader.GetInt32(2) != 0;
            if (!reader.IsDBNull(3))
            {
                symbols.Add(new SymbolResult
                {
                    Name = reader.GetString(3),
                    FullName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Kind = reader.GetString(5),
                    Accessibility = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Signature = reader.IsDBNull(7) ? null : reader.GetString(7),
                    StartLine = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    EndLine = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    DocSummary = reader.IsDBNull(10) ? null : reader.GetString(10),
                    IsGenerated = reader.GetInt32(11) != 0,
                    FilePath = resolvedPath,
                });
            }
        }

        return new QueryResponse<OutlineResult>
        {
            Command = "outline",
            SolutionName = solName,
            SolutionPath = solPath,
            IndexedAt = solIndexed,
            IndexedCommit = solCommit,
            Data = new OutlineResult
            {
                FilePath = resolvedPath,
                LineCount = lineCount,
                IsGenerated = isGenerated,
                Symbols = symbols,
            },
            TotalCount = symbols.Count,
        };
    }

    public QueryResponse<ShowResult> Show(string symbolName, bool includeSource)
    {
        var (solName, solPath, solIndexed, solCommit) = GetSolutionContext();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.name, s.full_name, s.kind, s.accessibility, s.signature,
                   s.start_line, s.end_line, f.path, s.doc_summary, s.is_generated, p.name AS proj_name
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            JOIN projects p ON p.id = f.project_id
            WHERE s.name = @q OR s.full_name = @q2
            LIMIT 1";
        cmd.Parameters.AddWithValue("@q", symbolName);
        cmd.Parameters.AddWithValue("@q2", symbolName);

        SymbolResult? symbol = null;
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
            {
                symbol = new SymbolResult
                {
                    Name = reader.GetString(0),
                    FullName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Kind = reader.GetString(2),
                    Accessibility = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Signature = reader.IsDBNull(4) ? null : reader.GetString(4),
                    StartLine = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    EndLine = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    FilePath = reader.IsDBNull(7) ? null : reader.GetString(7),
                    DocSummary = reader.IsDBNull(8) ? null : reader.GetString(8),
                    IsGenerated = reader.GetInt32(9) != 0,
                    ProjectName = reader.IsDBNull(10) ? null : reader.GetString(10),
                };
            }
        }

        string? sourceCode = null;
        if (includeSource && symbol?.FilePath is not null && symbol.StartLine is not null && symbol.EndLine is not null)
        {
            var repoRoot = GetMeta("repo_root");
            if (repoRoot is not null)
            {
                var fullPath = Path.Combine(repoRoot, symbol.FilePath);
                if (File.Exists(fullPath))
                {
                    var lines = File.ReadAllLines(fullPath);
                    int start = Math.Max(0, symbol.StartLine.Value - 1);
                    int end = Math.Min(lines.Length, symbol.EndLine.Value);
                    if (end > start)
                        sourceCode = string.Join(Environment.NewLine, lines[start..end]);
                }
            }
        }

        return new QueryResponse<ShowResult>
        {
            Command = "show",
            SolutionName = solName,
            SolutionPath = solPath,
            IndexedAt = solIndexed,
            IndexedCommit = solCommit,
            Data = new ShowResult
            {
                Symbol = symbol ?? new SymbolResult { Name = symbolName, Kind = "unknown", FullName = symbolName },
                SourceCode = sourceCode,
            },
            TotalCount = symbol is null ? 0 : 1,
        };
    }

    public QueryResponse<List<SearchHit>> Search(string text, bool includeGenerated, int limit)
    {
        var (solName, solPath, solIndexed, solCommit) = GetSolutionContext();
        var hits = new List<SearchHit>();
        using var cmd = _connection.CreateCommand();
        var genFilter = includeGenerated ? "" : "AND s.is_generated = 0";

        cmd.CommandText = $@"
            SELECT s.name, s.full_name, s.kind, s.signature, f.path, s.doc_summary,
                   s.start_line, s.is_generated, p.name AS proj_name, fts.rank
            FROM symbols_fts fts
            JOIN symbols s ON s.id = fts.rowid
            JOIN files f ON f.id = s.file_id
            JOIN projects p ON p.id = f.project_id
            WHERE symbols_fts MATCH @q
            {genFilter}
            ORDER BY rank
            LIMIT @lim";
        cmd.Parameters.AddWithValue("@q", text);
        cmd.Parameters.AddWithValue("@lim", limit + 1);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            hits.Add(new SearchHit
            {
                Name = reader.GetString(0),
                FullName = reader.IsDBNull(1) ? null : reader.GetString(1),
                Kind = reader.GetString(2),
                Signature = reader.IsDBNull(3) ? null : reader.GetString(3),
                FilePath = reader.IsDBNull(4) ? null : reader.GetString(4),
                DocSummary = reader.IsDBNull(5) ? null : reader.GetString(5),
                StartLine = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                IsGenerated = reader.GetInt32(7) != 0,
                ProjectName = reader.IsDBNull(8) ? null : reader.GetString(8),
                Rank = reader.GetDouble(9),
            });
        }

        bool truncated = hits.Count > limit;
        if (truncated) hits = hits.Take(limit).ToList();

        return new QueryResponse<List<SearchHit>>
        {
            Command = "search",
            SolutionName = solName,
            SolutionPath = solPath,
            IndexedAt = solIndexed,
            IndexedCommit = solCommit,
            Data = hits,
            TotalCount = hits.Count,
            Limit = limit,
            Truncated = truncated,
        };
    }

    private string? GetMeta(string key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = @k";
        cmd.Parameters.AddWithValue("@k", key);
        return cmd.ExecuteScalar() as string;
    }

    // ──────────────── Phase 3 queries ────────────────

    public QueryResponse<List<ReferenceResult>> Refs(string symbolName, int limit)
    {
        var (solName, solPath, solIndexed, solCommit) = GetSolutionContext();
        var results = new List<ReferenceResult>();
        using var cmd = _connection.CreateCommand();

        // Resolve symbol_id by name or full_name
        cmd.CommandText = "SELECT id, name, full_name FROM symbols WHERE (name = @q OR full_name = @q) AND is_external = 0 AND kind <> 'namespace' LIMIT 1";
        cmd.Parameters.AddWithValue("@q", symbolName);
        long symId = 0;
        string symName = "", symFull = "";
        using (var r = cmd.ExecuteReader())
        {
            if (r.Read()) { symId = r.GetInt64(0); symName = r.GetString(1); symFull = r.IsDBNull(2) ? "" : r.GetString(2); }
        }
        if (symId == 0)
            return new QueryResponse<List<ReferenceResult>>
            {
                Command = "refs", SolutionName = solName, SolutionPath = solPath,
                IndexedAt = solIndexed, IndexedCommit = solCommit,
                Data = results, TotalCount = 0, Warnings = ["Symbol not found: " + symbolName],
            };

        cmd.Parameters.Clear();
        cmd.CommandText = @"
            SELECT r.line, r.column, r.kind, r.reference_name, f.path
            FROM symbol_references r
            JOIN files f ON f.id = r.file_id
            WHERE r.symbol_id = @sid
            ORDER BY f.path, r.line
            LIMIT @lim";
        cmd.Parameters.AddWithValue("@sid", symId);
        cmd.Parameters.AddWithValue("@lim", limit + 1);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ReferenceResult
            {
                SymbolName = symName,
                SymbolFullName = string.IsNullOrEmpty(symFull) ? null : symFull,
                FilePath = reader.GetString(4),
                Line = reader.GetInt32(0),
                Column = reader.GetInt32(1),
                Kind = reader.GetString(2),
                ReferenceName = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }

        bool truncated = results.Count > limit;
        if (truncated) results = results.Take(limit).ToList();

        return new QueryResponse<List<ReferenceResult>>
        {
            Command = "refs", SolutionName = solName, SolutionPath = solPath,
            IndexedAt = solIndexed, IndexedCommit = solCommit,
            Data = results, TotalCount = results.Count, Limit = limit, Truncated = truncated,
        };
    }

    public QueryResponse<List<CallerCalleeResult>> Callers(string symbolName, int depth, int limit)
    {
        var (solName, solPath, solIndexed, solCommit) = GetSolutionContext();
        var results = new List<CallerCalleeResult>();
        var symId = ResolveSymbolId(symbolName);
        if (symId is null)
            return new QueryResponse<List<CallerCalleeResult>>
            {
                Command = "callers", SolutionName = solName, SolutionPath = solPath,
                IndexedAt = solIndexed, IndexedCommit = solCommit,
                Data = results, TotalCount = 0, Warnings = ["Symbol not found: " + symbolName],
            };

        var visited = new HashSet<long>();
        var queue = new Queue<(long sid, int d)>();
        queue.Enqueue((symId.Value, 0));
        visited.Add(symId.Value);

        while (queue.Count > 0 && results.Count < limit)
        {
            var (currentId, currentDepth) = queue.Dequeue();
            if (currentDepth > 0)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT s.id, s.name, s.full_name, s.kind, f.path
                    FROM symbols s
                    JOIN files f ON f.id = s.file_id
                    WHERE s.id = @sid";
                cmd.Parameters.AddWithValue("@sid", currentId);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    results.Add(new CallerCalleeResult
                    {
                        SymbolId = r.GetInt64(0), Name = r.GetString(1),
                        FullName = r.IsDBNull(2) ? null : r.GetString(2),
                        Kind = r.GetString(3), FilePath = r.IsDBNull(4) ? null : r.GetString(4),
                        Depth = currentDepth,
                    });
                }
            }

            if (currentDepth < depth)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT from_symbol_id FROM edges WHERE to_symbol_id = @sid AND kind = 'calls'";
                cmd.Parameters.AddWithValue("@sid", currentId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var nextId = r.GetInt64(0);
                    if (visited.Add(nextId))
                        queue.Enqueue((nextId, currentDepth + 1));
                }
            }
        }

        return new QueryResponse<List<CallerCalleeResult>>
        {
            Command = "callers", SolutionName = solName, SolutionPath = solPath,
            IndexedAt = solIndexed, IndexedCommit = solCommit,
            Data = results, TotalCount = results.Count, Limit = limit,
        };
    }

    public QueryResponse<List<CallerCalleeResult>> Callees(string symbolName, int depth, int limit)
    {
        var (solName, solPath, solIndexed, solCommit) = GetSolutionContext();
        var results = new List<CallerCalleeResult>();
        var symId = ResolveSymbolId(symbolName);
        if (symId is null)
            return new QueryResponse<List<CallerCalleeResult>>
            {
                Command = "callees", SolutionName = solName, SolutionPath = solPath,
                IndexedAt = solIndexed, IndexedCommit = solCommit,
                Data = results, TotalCount = 0, Warnings = ["Symbol not found: " + symbolName],
            };

        var visited = new HashSet<long>();
        var queue = new Queue<(long sid, int d)>();
        queue.Enqueue((symId.Value, 0));
        visited.Add(symId.Value);

        while (queue.Count > 0 && results.Count < limit)
        {
            var (currentId, currentDepth) = queue.Dequeue();
            if (currentDepth > 0)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT s.id, s.name, s.full_name, s.kind, f.path
                    FROM symbols s
                    JOIN files f ON f.id = s.file_id
                    WHERE s.id = @sid";
                cmd.Parameters.AddWithValue("@sid", currentId);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    results.Add(new CallerCalleeResult
                    {
                        SymbolId = r.GetInt64(0), Name = r.GetString(1),
                        FullName = r.IsDBNull(2) ? null : r.GetString(2),
                        Kind = r.GetString(3), FilePath = r.IsDBNull(4) ? null : r.GetString(4),
                        Depth = currentDepth,
                    });
                }
            }

            if (currentDepth < depth)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT to_symbol_id FROM edges WHERE from_symbol_id = @sid AND kind = 'calls'";
                cmd.Parameters.AddWithValue("@sid", currentId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var nextId = r.GetInt64(0);
                    if (visited.Add(nextId))
                        queue.Enqueue((nextId, currentDepth + 1));
                }
            }
        }

        return new QueryResponse<List<CallerCalleeResult>>
        {
            Command = "callees", SolutionName = solName, SolutionPath = solPath,
            IndexedAt = solIndexed, IndexedCommit = solCommit,
            Data = results, TotalCount = results.Count, Limit = limit,
        };
    }

    public QueryResponse<List<CallerCalleeResult>> Implementations(string interfaceName, int limit)
    {
        var (solName, solPath, solIndexed, solCommit) = GetSolutionContext();
        var results = new List<CallerCalleeResult>();
        using var cmd = _connection.CreateCommand();

        cmd.CommandText = "SELECT id FROM symbols WHERE (name = @q OR full_name = @q) AND is_external = 0 AND kind <> 'namespace' LIMIT 1";
        cmd.Parameters.AddWithValue("@q", interfaceName);
        var ifaceId = cmd.ExecuteScalar() as long?;

        if (ifaceId is null)
            return new QueryResponse<List<CallerCalleeResult>>
            {
                Command = "implementations", SolutionName = solName, SolutionPath = solPath,
                IndexedAt = solIndexed, IndexedCommit = solCommit,
                Data = results, TotalCount = 0, Warnings = ["Interface not found: " + interfaceName],
            };

        cmd.Parameters.Clear();
        cmd.CommandText = @"
            SELECT s.id, s.name, s.full_name, s.kind, f.path
            FROM edges e
            JOIN symbols s ON s.id = e.from_symbol_id
            JOIN files f ON f.id = s.file_id
            WHERE e.to_symbol_id = @ifid AND e.kind = 'implements'
            LIMIT @lim";
        cmd.Parameters.AddWithValue("@ifid", ifaceId.Value);
        cmd.Parameters.AddWithValue("@lim", limit + 1);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new CallerCalleeResult
            {
                SymbolId = reader.GetInt64(0), Name = reader.GetString(1),
                FullName = reader.IsDBNull(2) ? null : reader.GetString(2),
                Kind = reader.GetString(3), FilePath = reader.IsDBNull(4) ? null : reader.GetString(4),
                Depth = 0,
            });
        }

        bool truncated = results.Count > limit;
        if (truncated) results = results.Take(limit).ToList();

        return new QueryResponse<List<CallerCalleeResult>>
        {
            Command = "implementations", SolutionName = solName, SolutionPath = solPath,
            IndexedAt = solIndexed, IndexedCommit = solCommit,
            Data = results, TotalCount = results.Count, Limit = limit, Truncated = truncated,
        };
    }

    public QueryResponse<HierarchyNode> Hierarchy(string typeName, int maxDepth)
    {
        var (solName, solPath, solIndexed, solCommit) = GetSolutionContext();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, full_name, kind, accessibility FROM symbols WHERE (name = @q OR full_name = @q) AND is_external = 0 AND kind <> 'namespace' ORDER BY kind LIMIT 1";
        cmd.Parameters.AddWithValue("@q", typeName);

        long rootId;
        HierarchyNode root;
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read())
            {
                return new QueryResponse<HierarchyNode>
                {
                    Command = "hierarchy", SolutionName = solName, SolutionPath = solPath,
                    IndexedAt = solIndexed, IndexedCommit = solCommit,
                    Data = new HierarchyNode { SymbolId = 0, Name = typeName, Kind = "unknown", EdgeKind = "root" },
                    TotalCount = 0, Warnings = ["Type not found: " + typeName],
                };
            }
            rootId = r.GetInt64(0);
            root = new HierarchyNode
            {
                SymbolId = rootId, Name = r.GetString(1),
                FullName = r.IsDBNull(2) ? null : r.GetString(2),
                Kind = r.GetString(3), Accessibility = r.IsDBNull(4) ? null : r.GetString(4),
                EdgeKind = "root",
            };
        }

        BuildHierarchyTree(root, maxDepth, new HashSet<long> { rootId });
        return new QueryResponse<HierarchyNode>
        {
            Command = "hierarchy", SolutionName = solName, SolutionPath = solPath,
            IndexedAt = solIndexed, IndexedCommit = solCommit,
            Data = root, TotalCount = CountHierarchyNodes(root),
        };
    }

    private long? ResolveSymbolId(string name)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM symbols WHERE (name = @q OR full_name = @q) AND is_external = 0 AND kind <> 'namespace' LIMIT 1";
        cmd.Parameters.AddWithValue("@q", name);
        return cmd.ExecuteScalar() as long?;
    }

    private void BuildHierarchyTree(HierarchyNode node, int maxDepth, HashSet<long> visited)
    {
        if (maxDepth <= 0) return;

        // Parents: types this type inherits from or implements (edges where this is the source).
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT s.id, s.name, s.full_name, s.kind, s.accessibility, f.path, e.kind
                FROM edges e
                JOIN symbols s ON s.id = e.to_symbol_id
                JOIN files f ON f.id = s.file_id
                WHERE e.from_symbol_id = @sid AND e.kind IN ('inherits', 'implements')
                AND s.id != @sid";
            cmd.Parameters.AddWithValue("@sid", node.SymbolId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var parentId = reader.GetInt64(0);
                if (!visited.Add(parentId)) continue;

                var parent = new HierarchyNode
                {
                    SymbolId = parentId,
                    Name = reader.GetString(1),
                    FullName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Kind = reader.GetString(3),
                    Accessibility = reader.IsDBNull(4) ? null : reader.GetString(4),
                    FilePath = reader.IsDBNull(5) ? null : reader.GetString(5),
                    EdgeKind = reader.GetString(6),
                };
                node.Children.Add(parent);
                BuildHierarchyTree(parent, maxDepth - 1, visited);
            }
        }

        // Children: types that inherit from or implement this type.
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT s.id, s.name, s.full_name, s.kind, s.accessibility, f.path, e.kind
                FROM edges e
                JOIN symbols s ON s.id = e.from_symbol_id
                JOIN files f ON f.id = s.file_id
                WHERE e.to_symbol_id = @sid AND e.kind IN ('inherits', 'implements')
                AND s.id != @sid";
            cmd.Parameters.AddWithValue("@sid", node.SymbolId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var childId = reader.GetInt64(0);
                if (!visited.Add(childId)) continue;

                node.Children.Add(new HierarchyNode
                {
                    SymbolId = childId,
                    Name = reader.GetString(1),
                    FullName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Kind = reader.GetString(3),
                    Accessibility = reader.IsDBNull(4) ? null : reader.GetString(4),
                    FilePath = reader.IsDBNull(5) ? null : reader.GetString(5),
                    EdgeKind = reader.GetString(6),
                });
            }
        }
    }

    private static int CountHierarchyNodes(HierarchyNode node)
    {
        int count = 1;
        foreach (var c in node.Children) count += CountHierarchyNodes(c);
        return count;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection.Close();
            _connection.Dispose();
            _disposed = true;
        }
    }
}
