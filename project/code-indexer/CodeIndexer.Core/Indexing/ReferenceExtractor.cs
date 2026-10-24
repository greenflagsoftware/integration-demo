using Microsoft.CodeAnalysis;

namespace CodeIndexer.Core.Indexing;

public sealed class ReferenceRow
{
    public long SymbolId { get; set; }
    public long FileId { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public string Kind { get; set; } = "use";
    public string? ReferenceName { get; set; }
}

public sealed class EdgeRow
{
    public long FromSymbolId { get; set; }
    public long ToSymbolId { get; set; }
    public string Kind { get; set; } = "";
}

public struct FileSymbolId(long fileId, long symbolId)
{
    public long FileId = fileId;
    public long SymbolId = symbolId;
}

/// <summary>
    /// Walks each document's semantic model to extract resolved references
    /// and typed relationship edges (inherits, implements, calls, overrides, creates).
    /// </summary>
    public sealed class ReferenceExtractor
    {
        private readonly Compilation _compilation;

        /// <summary>
        /// Map from symbol full_name to its database row ID, populated by the caller.
        /// </summary>
        public Dictionary<string, FileSymbolId> SymbolMap { get; set; } = new();

        public ReferenceExtractor(Compilation compilation)
        {
            _compilation = compilation;
        }

        /// <summary>
        /// Build a fully qualified name consistent with SymbolExtractor.GetFullName.
        /// </summary>
        private static string GetFullName(ISymbol symbol)
        {
            if (symbol.ContainingType is not null)
            {
                var typeFull = symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                return typeFull + "." + symbol.Name;
            }
            return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

    /// <summary>
    /// Extract references and edges from a single document.
    /// Returns (references, edges).
    /// </summary>
    public (List<ReferenceRow>, List<EdgeRow>) Extract(Document doc, long fileId, bool includeReferences)
    {
        var refs = new List<ReferenceRow>();
        var edges = new List<EdgeRow>();
        var tree = doc.GetSyntaxTreeAsync().Result;
        if (tree is null) return (refs, edges);

        var model = _compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        // Extract type hierarchy edges.
        var typeDecls = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax>();
        foreach (var typeDecl in typeDecls)
        {
            if (model.GetDeclaredSymbol(typeDecl) is not Microsoft.CodeAnalysis.INamedTypeSymbol typeSymbol) continue;
            var typeFullName = GetFullName(typeSymbol);
            if (!SymbolMap.TryGetValue(typeFullName, out var fromIds)) continue;

            // Inheritance edges.
            if (typeSymbol.BaseType is not null)
            {
                var baseFull = GetFullName(typeSymbol.BaseType);
                if (SymbolMap.TryGetValue(baseFull, out var baseIds))
                    edges.Add(new EdgeRow { FromSymbolId = fromIds.SymbolId, ToSymbolId = baseIds.SymbolId, Kind = "inherits" });
            }

            // Interface implementation edges.
            foreach (var iface in typeSymbol.Interfaces)
            {
                var ifaceFull = GetFullName(iface);
                if (SymbolMap.TryGetValue(ifaceFull, out var ifaceIds))
                    edges.Add(new EdgeRow { FromSymbolId = fromIds.SymbolId, ToSymbolId = ifaceIds.SymbolId, Kind = "implements" });
            }
        }

        // Extract method override edges.
        var methodDecls = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>();
        foreach (var methodDecl in methodDecls)
        {
            if (model.GetDeclaredSymbol(methodDecl) is not Microsoft.CodeAnalysis.IMethodSymbol methodSymbol) continue;
            var methodFull = GetFullName(methodSymbol);
            if (!SymbolMap.TryGetValue(methodFull, out var fromIds)) continue;

            if (methodSymbol.OverriddenMethod is not null)
            {
                var overFull = GetFullName(methodSymbol.OverriddenMethod);
                if (SymbolMap.TryGetValue(overFull, out var overIds))
                    edges.Add(new EdgeRow { FromSymbolId = fromIds.SymbolId, ToSymbolId = overIds.SymbolId, Kind = "overrides" });
            }

            // Object creation edges.
            foreach (var creation in methodDecl.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax>())
            {
                var ctorSymbol = model.GetSymbolInfo(creation).Symbol;
                if (ctorSymbol?.ContainingType is not null)
                {
                    var ctorTypeFull = GetFullName(ctorSymbol.ContainingType);
                    if (SymbolMap.TryGetValue(ctorTypeFull, out var ctorIds))
                        edges.Add(new EdgeRow { FromSymbolId = fromIds.SymbolId, ToSymbolId = ctorIds.SymbolId, Kind = "creates" });
                }
            }

            // Call edges and references.
            if (includeReferences)
            {
                foreach (var invoke in methodDecl.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
                {
                    var invoked = model.GetSymbolInfo(invoke).Symbol;
                    if (invoked is null) continue;
                    var invokedFull = GetFullName(invoked);
                    var lineSpan = invoke.GetLocation().GetLineSpan();
                    var line = lineSpan.StartLinePosition.Line + 1;
                    var col = lineSpan.StartLinePosition.Character + 1;

                    if (SymbolMap.TryGetValue(invokedFull, out var invokedIds))
                    {
                        // Edge for tracking call relationships.
                        edges.Add(new EdgeRow { FromSymbolId = fromIds.SymbolId, ToSymbolId = invokedIds.SymbolId, Kind = "calls" });
                        // Reference for per-file lookup.
                        refs.Add(new ReferenceRow { SymbolId = invokedIds.SymbolId, FileId = fileId, Line = line, Column = col, Kind = "call", ReferenceName = invokedFull });
                    }
                    else
                    {
                        // Unresolved or external reference — still record it.
                        refs.Add(new ReferenceRow { SymbolId = 0, FileId = fileId, Line = line, Column = col, Kind = "call", ReferenceName = invokedFull });
                    }
                }

                // Field/property access references.
                foreach (var access in methodDecl.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax>())
                {
                    var member = model.GetSymbolInfo(access).Symbol;
                    if (member is null) continue;
                    var memberFull = GetFullName(member);
                    var lineSpan = access.GetLocation().GetLineSpan();
                    var line = lineSpan.StartLinePosition.Line + 1;
                    var col = lineSpan.StartLinePosition.Character + 1;

                    if (SymbolMap.TryGetValue(memberFull, out var memberIds))
                    {
                        refs.Add(new ReferenceRow { SymbolId = memberIds.SymbolId, FileId = fileId, Line = line, Column = col, Kind = "access", ReferenceName = memberFull });
                    }
                }
            }
        }

        // Constructor call edges and references.
        foreach (var ctorDecl in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax>())
        {
            var ctorSymbol = model.GetDeclaredSymbol(ctorDecl);
            if (ctorSymbol is null) continue;
            var ctorFull = GetFullName(ctorSymbol);
            if (!SymbolMap.TryGetValue(ctorFull, out var fromIds)) continue;

            foreach (var invoke in ctorDecl.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
            {
                var invoked = model.GetSymbolInfo(invoke).Symbol;
                if (invoked is null) continue;
                var invokedFull = GetFullName(invoked);
                if (SymbolMap.TryGetValue(invokedFull, out var invokedIds))
                    edges.Add(new EdgeRow { FromSymbolId = fromIds.SymbolId, ToSymbolId = invokedIds.SymbolId, Kind = "calls" });
            }
        }

        return (refs, edges);
    }

    public static Dictionary<string, FileSymbolId> LoadSymbolMap(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var map = new Dictionary<string, FileSymbolId>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, full_name, file_id FROM symbols WHERE full_name IS NOT NULL";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var name = reader.GetString(1);
            var fid = reader.GetInt64(2);
            map[name] = new FileSymbolId(fid, id);
        }
        return map;
    }
}
           
