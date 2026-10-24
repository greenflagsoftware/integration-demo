namespace CodeIndexer.Core.Queries;

public sealed record RefsOptions(
    string[]? Kinds = null, string? File = null, string? Project = null, bool IncludeGenerated = false,
    bool Context = false, string GroupBy = "none", int Limit = 100, int Offset = 0);

public sealed record CallOptions(
    int Depth = 1, int Limit = 50, bool ThroughDispatch = false, bool IncludeCreates = false,
    bool IncludeExternal = false, bool ExcludeGenerated = false);

public sealed partial class SymbolQueries
{
    private static readonly string[] TypeKinds = { "class", "struct", "interface", "enum", "delegate" };
    private static readonly string[] MemberKinds = { "method", "property", "event" };
    private const int MaxCallSites = 10;

    private static string InList(IEnumerable<long> ids) => string.Join(",", ids);
    private static string InList(IEnumerable<string> values) => string.Join(",", values.Select(v => "'" + v.Replace("'", "''") + "'"));

    // ---- refs ----------------------------------------------------------------------------------

    /// <summary>Every recorded use of a symbol: where, what kind of use, and which member contains it.</summary>
    public QueryResponse<RefsResult> Refs(SymbolSelector selector, RefsOptions? options = null)
    {
        options ??= new RefsOptions();
        var empty = new RefsResult { References = new() };
        var resolution = Resolve(selector);
        if (!resolution.IsResolved) return Unresolved("refs", empty, resolution);
        var symbol = resolution.Symbol!;

        var where = new List<string> { "r.symbol_id = @s" };
        var args = new List<(string, object?)> { ("@s", symbol.Id) };
        if (options.Kinds is { Length: > 0 })
            where.Add($"r.kind IN ({InList(options.Kinds)})");
        if (!string.IsNullOrEmpty(options.File))
        {
            where.Add("f.path LIKE @file ESCAPE '\\'");
            args.Add(("@file", "%" + EscapeLike(PathUtil.Normalize(options.File)) + "%"));
        }
        if (!string.IsNullOrEmpty(options.Project))
        {
            where.Add("(p.name LIKE @proj ESCAPE '\\' OR p.path LIKE @proj ESCAPE '\\')");
            args.Add(("@proj", "%" + EscapeLike(options.Project) + "%"));
        }

        const string from = "symbol_references r JOIN files f ON f.id = r.file_id LEFT JOIN projects p ON p.id = f.project_id LEFT JOIN symbols e ON e.id = r.enclosing_symbol_id";
        var warnings = new List<string>();
        if (!options.IncludeGenerated)
        {
            var hidden = Count($"SELECT COUNT(*) FROM {from} WHERE {string.Join(" AND ", where)} AND f.is_generated = 1", args.ToArray());
            if (hidden > 0)
                warnings.Add($"{hidden} reference(s) in generated files were excluded; use --include-generated to see them.");
            where.Add("f.is_generated = 0");
        }

        var whereSql = string.Join(" AND ", where);
        var total = Count($"SELECT COUNT(*) FROM {from} WHERE {whereSql}", args.ToArray());
        var rows = Read(
            $@"SELECT r.kind, r.line, r.column, f.path, f.is_generated, e.id, e.key, e.name, e.full_name, e.kind
               FROM {from} WHERE {whereSql} ORDER BY f.path, r.line, r.column, r.id LIMIT @lim OFFSET @off",
            r => new ReferenceResult
            {
                Kind = r.GetString(0), Line = r.GetInt32(1), Column = r.GetInt32(2), FilePath = r.GetString(3), IsGenerated = r.GetInt32(4) != 0,
                Enclosing = r.IsDBNull(5) ? null : new SymbolRef { Id = r.GetInt64(5), Key = r.GetString(6), Name = r.GetString(7), FullName = r.IsDBNull(8) ? null : r.GetString(8), Kind = r.GetString(9) },
            },
            args.Concat(new (string, object?)[] { ("@lim", options.Limit + 1), ("@off", options.Offset) }).ToArray());

        bool truncated = rows.Count > options.Limit;
        if (truncated) rows = rows.Take(options.Limit).ToList();

        if (options.Context)
            rows = rows.Select(x => WithContext(x)).ToList();

        RefsResult data = options.GroupBy.ToLowerInvariant() switch
        {
            "file" => Grouped(rows, x => x.FilePath),
            "member" => Grouped(rows, x => x.Enclosing?.FullName?.Replace("global::", "") ?? "(outside any member)"),
            "kind" => Grouped(rows, x => x.Kind),
            _ => new RefsResult { References = rows },
        };

        return Respond("refs", data, rows.Count, limit: options.Limit, truncated: truncated, totalMatches: (int)total,
            target: symbol, warnings: warnings);
    }

    private ReferenceResult WithContext(ReferenceResult r)
    {
        var lines = ReadFileLines(r.FilePath);
        string? text = lines is not null && r.Line >= 1 && r.Line <= lines.Length ? lines[r.Line - 1].Trim() : null;
        if (text is { Length: > 200 }) text = text[..200] + "...";
        return new ReferenceResult
        {
            FilePath = r.FilePath, Line = r.Line, Column = r.Column, Kind = r.Kind, IsGenerated = r.IsGenerated,
            Enclosing = r.Enclosing, Context = text,
        };
    }

    private static RefsResult Grouped(List<ReferenceResult> rows, Func<ReferenceResult, string> key) => new()
    {
        References = new(),
        Groups = rows.GroupBy(key).Select(g => new RefGroup { Group = g.Key, Count = g.Count(), References = g.ToList() }).ToList(),
    };

    // ---- callers / callees ---------------------------------------------------------------------

    /// <summary>
    /// Members that call the symbol (incoming calls), breadth first to --depth (0 = unlimited). With
    /// <c>ThroughDispatch</c>, a call to an interface, abstract, or base member that this symbol implements or
    /// overrides counts as a call to it.
    /// </summary>
    public QueryResponse<List<CallNode>> Callers(SymbolSelector selector, CallOptions? options = null) =>
        Traverse("callers", selector, options ?? new CallOptions(), incoming: true);

    /// <summary>Members the symbol calls (outgoing calls), breadth first. With <c>ThroughDispatch</c>, a call to an
    /// interface or abstract member also lists the members that implement or override it.</summary>
    public QueryResponse<List<CallNode>> Callees(SymbolSelector selector, CallOptions? options = null) =>
        Traverse("callees", selector, options ?? new CallOptions(), incoming: false);

    private QueryResponse<List<CallNode>> Traverse(string command, SymbolSelector selector, CallOptions options, bool incoming)
    {
        var resolution = Resolve(selector);
        if (!resolution.IsResolved) return Unresolved(command, new List<CallNode>(), resolution);
        var start = resolution.Symbol!;

        bool canTraverse = start.Kind == "method" || (options.IncludeCreates && start.Kind is "class" or "struct");
        if (!canTraverse)
        {
            var hint = start.Kind switch
            {
                "property" or "field" or "event" => $"Accesses to a {start.Kind} are references, not calls; use 'refs' (with --ref-kind read,write).",
                "class" or "struct" => "To find who creates a type, use --include-creates (or 'refs --ref-kind create,type-use').",
                _ => $"{command} applies to methods and constructors, not to a {start.Kind}.",
            };
            return Respond(command, new List<CallNode>(), 0, target: start, warnings: new List<string> { hint });
        }

        var edgeKinds = options.IncludeCreates ? new[] { "calls", "creates" } : new[] { "calls" };
        var refKinds = options.IncludeCreates ? new[] { "call", "create", "type-use" } : new[] { "call" };
        var results = new List<CallNode>();
        var visited = new HashSet<long> { start.Id };
        var queue = new Queue<(long Id, int Depth)>();
        queue.Enqueue((start.Id, 0));
        bool truncated = false;

        while (queue.Count > 0 && !truncated)
        {
            var (current, depth) = queue.Dequeue();
            if (options.Depth > 0 && depth >= options.Depth) continue;

            var found = incoming
                ? IncomingStep(current, options, edgeKinds, refKinds)
                : OutgoingStep(current, options, edgeKinds, refKinds);

            foreach (var (node, via, sites) in found)
            {
                if (!visited.Add(node.Id)) continue;
                if (node.IsExternal && !options.IncludeExternal) continue;
                if (node.IsGenerated && options.ExcludeGenerated) continue;

                if (results.Count >= options.Limit) { truncated = true; break; }
                results.Add(new CallNode
                {
                    Symbol = node, Depth = depth + 1, Via = via is null ? null : ToRef(via),
                    CallSites = sites.Take(MaxCallSites).ToList(), CallSiteCount = sites.Count,
                });
                if (!node.IsExternal) queue.Enqueue((node.Id, depth + 1));
            }
        }

        var warnings = new List<string>();
        if (options.Depth > 1 || options.Depth == 0)
            warnings.Add("Each member appears once, at the shallowest depth it is reached; call sites are for that first path.");
        return Respond(command, results, results.Count, limit: options.Limit, truncated: truncated, target: start, warnings: warnings);
    }

    private List<(SymbolResult Node, SymbolResult? Via, List<CallSite> Sites)> IncomingStep(
        long current, CallOptions options, string[] edgeKinds, string[] refKinds)
    {
        var targets = options.ThroughDispatch ? Closure(current, up: true) : new List<long> { current };
        var edges = Read(
            $"SELECT DISTINCT from_symbol_id, to_symbol_id FROM edges WHERE to_symbol_id IN ({InList(targets)}) AND kind IN ({InList(edgeKinds)}) ORDER BY from_symbol_id, to_symbol_id",
            r => (From: r.GetInt64(0), To: r.GetInt64(1)));

        var result = new List<(SymbolResult, SymbolResult?, List<CallSite>)>();
        foreach (var group in edges.GroupBy(e => e.From))
        {
            var node = GetSymbol(group.Key);
            if (node is null) continue;
            var called = group.Select(e => e.To).ToList();
            var viaId = called.FirstOrDefault(t => t != current);
            result.Add((node, viaId == 0 ? null : GetSymbol(viaId), CallSitesOf(group.Key, called, refKinds)));
        }
        return result;
    }

    private List<(SymbolResult Node, SymbolResult? Via, List<CallSite> Sites)> OutgoingStep(
        long current, CallOptions options, string[] edgeKinds, string[] refKinds)
    {
        var callees = Read(
            $"SELECT DISTINCT to_symbol_id FROM edges WHERE from_symbol_id = @c AND kind IN ({InList(edgeKinds)}) ORDER BY to_symbol_id",
            r => r.GetInt64(0), ("@c", current));

        var result = new List<(SymbolResult, SymbolResult?, List<CallSite>)>();
        foreach (var calleeId in callees)
        {
            var callee = GetSymbol(calleeId);
            if (callee is null) continue;
            var sites = CallSitesOf(current, new List<long> { calleeId }, refKinds);
            result.Add((callee, null, sites));

            if (!options.ThroughDispatch) continue;
            foreach (var implId in Closure(calleeId, up: false).Where(i => i != calleeId))
                if (GetSymbol(implId) is { } impl)
                    result.Add((impl, callee, sites));
        }
        return result;
    }

    private List<CallSite> CallSitesOf(long enclosingId, IEnumerable<long> symbolIds, string[] refKinds) =>
        Read(
            $@"SELECT f.path, r.line, r.column FROM symbol_references r JOIN files f ON f.id = r.file_id
               WHERE r.enclosing_symbol_id = @e AND r.symbol_id IN ({InList(symbolIds)}) AND r.kind IN ({InList(refKinds)})
               ORDER BY f.path, r.line, r.column",
            r => new CallSite { FilePath = r.GetString(0), Line = r.GetInt32(1), Column = r.GetInt32(2) },
            ("@e", enclosingId));

    /// <summary>
    /// The symbol and everything reachable over member-level implements/overrides edges:
    /// upward (the interface and base members it implements or overrides) or downward (its implementers/overriders).
    /// </summary>
    private List<long> Closure(long id, bool up)
    {
        var seen = new List<long> { id };
        var set = new HashSet<long> { id };
        for (int i = 0; i < seen.Count; i++)
        {
            var next = Read(
                up ? "SELECT to_symbol_id FROM edges WHERE from_symbol_id = @i AND kind IN ('implements', 'overrides')"
                   : "SELECT from_symbol_id FROM edges WHERE to_symbol_id = @i AND kind IN ('implements', 'overrides')",
                r => r.GetInt64(0), ("@i", seen[i]));
            foreach (var n in next)
                if (set.Add(n)) seen.Add(n);
        }
        return seen;
    }

    // ---- implementations -----------------------------------------------------------------------

    /// <summary>
    /// For a type: the types that implement or inherit from it. For a method, property, or event: the members that
    /// implement or override it. Direct ones by default; <c>transitive</c> follows the chain (an interface member's
    /// implementation and then that implementation's overrides).
    /// </summary>
    public QueryResponse<List<ImplementationResult>> Implementations(SymbolSelector selector, bool transitive = false, int limit = 50)
    {
        var resolution = Resolve(selector);
        if (!resolution.IsResolved) return Unresolved("implementations", new List<ImplementationResult>(), resolution);
        var symbol = resolution.Symbol!;

        string[] kinds;
        if (TypeKinds.Contains(symbol.Kind)) kinds = new[] { "implements", "inherits" };
        else if (MemberKinds.Contains(symbol.Kind)) kinds = new[] { "implements", "overrides" };
        else
            return Respond("implementations", new List<ImplementationResult>(), 0, target: symbol,
                warnings: new List<string> { $"implementations applies to types, methods, properties, and events, not to a {symbol.Kind}." });

        var results = new List<ImplementationResult>();
        var visited = new HashSet<long> { symbol.Id };
        var queue = new Queue<(long Id, int Depth)>();
        queue.Enqueue((symbol.Id, 0));
        bool truncated = false;

        while (queue.Count > 0 && !truncated)
        {
            var (current, depth) = queue.Dequeue();
            if (!transitive && depth >= 1) continue;

            var rows = Read(
                $"SELECT from_symbol_id, kind FROM edges WHERE to_symbol_id = @c AND kind IN ({InList(kinds)}) ORDER BY from_symbol_id",
                r => (Id: r.GetInt64(0), Kind: r.GetString(1)), ("@c", current));
            foreach (var (id, kind) in rows)
            {
                if (!visited.Add(id)) continue;
                if (results.Count >= limit) { truncated = true; break; }
                if (GetSymbol(id) is not { } impl) continue;
                results.Add(new ImplementationResult { Symbol = impl, Relation = kind, Depth = depth + 1 });
                queue.Enqueue((id, depth + 1));
            }
        }

        return Respond("implementations", results, results.Count, limit: limit, truncated: truncated, target: symbol);
    }

    // ---- hierarchy -----------------------------------------------------------------------------

    /// <summary>The supertypes (base class and interfaces) and subtypes (derived types and implementers) of a type.</summary>
    public QueryResponse<HierarchyResult> Hierarchy(SymbolSelector selector, string direction = "both", int depth = 3, int limit = 200)
    {
        var empty = new HierarchyResult { Supertypes = new(), Subtypes = new() };
        var resolution = Resolve(selector);
        if (!resolution.IsResolved) return Unresolved("hierarchy", empty, resolution);
        var symbol = resolution.Symbol!;
        if (!TypeKinds.Contains(symbol.Kind))
            return Respond("hierarchy", empty, 0, target: symbol,
                warnings: new List<string> { $"hierarchy applies to types, not to a {symbol.Kind}." });

        int count = 0;
        bool truncated = false;
        bool wantUp = !direction.Equals("down", StringComparison.OrdinalIgnoreCase);
        bool wantDown = !direction.Equals("up", StringComparison.OrdinalIgnoreCase);

        List<HierarchyNode> Walk(long id, bool up, int remaining, HashSet<long> path)
        {
            var nodes = new List<HierarchyNode>();
            if (remaining <= 0) return nodes;
            var rows = Read(
                up ? "SELECT to_symbol_id, kind FROM edges WHERE from_symbol_id = @i AND kind IN ('inherits', 'implements') ORDER BY kind DESC, to_symbol_id"
                   : "SELECT from_symbol_id, kind FROM edges WHERE to_symbol_id = @i AND kind IN ('inherits', 'implements') ORDER BY kind DESC, from_symbol_id",
                r => (Id: r.GetInt64(0), Kind: r.GetString(1)), ("@i", id));
            foreach (var (next, kind) in rows)
            {
                if (path.Contains(next)) continue;
                if (count >= limit) { truncated = true; break; }
                if (GetSymbol(next) is not { } s) continue;
                count++;
                path.Add(next);
                nodes.Add(new HierarchyNode { Symbol = s, Relation = kind, Children = Walk(next, up, remaining - 1, path) });
                path.Remove(next);
            }
            return nodes;
        }

        var data = new HierarchyResult
        {
            Supertypes = wantUp ? Walk(symbol.Id, true, depth, new HashSet<long> { symbol.Id }) : new(),
            Subtypes = wantDown ? Walk(symbol.Id, false, depth, new HashSet<long> { symbol.Id }) : new(),
        };
        return Respond("hierarchy", data, count, truncated: truncated, target: symbol);
    }
}
