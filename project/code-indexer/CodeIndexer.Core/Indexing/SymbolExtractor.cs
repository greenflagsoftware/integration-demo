using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeIndexer.Core.Indexing;

/// <summary>A source file known to the database, as seen by the extractor.</summary>
public sealed record FileRef(long FileId, bool IsGenerated);

public sealed class SymbolLocation
{
    public required long FileId { get; init; }
    public required int StartLine { get; init; }
    public required int StartColumn { get; init; }
    public required int EndLine { get; init; }
    public required int EndColumn { get; init; }
    public bool IsPrimary { get; set; }
}

public sealed class SymbolRow
{
    public required string Key { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required string FullName { get; init; }
    /// <summary>Key of the containing namespace or type; always emitted before this row.</summary>
    public string? ContainerKey { get; init; }
    public string? Signature { get; init; }
    public string? Accessibility { get; init; }
    public string? DocSummary { get; init; }
    public bool IsGenerated { get; init; }
    public required List<SymbolLocation> Locations { get; init; }

    public SymbolLocation Primary => Locations.First(l => l.IsPrimary);
}

/// <summary>A relationship between two symbols that follows from declarations rather than from uses.</summary>
public sealed record SymbolEdge(ISymbol From, ISymbol To, string Kind);

public sealed class SymbolExtraction
{
    /// <summary>Symbols in declaration order, every container before its members.</summary>
    public List<SymbolRow> Symbols { get; } = new();
    /// <summary>inherits, implements, and overrides edges.</summary>
    public List<SymbolEdge> Edges { get; } = new();
}

/// <summary>
/// Walks the symbols declared by a compilation's assembly (namespaces, types, members), producing one row per
/// symbol with a location for every declaration, so a partial type is one symbol with several locations.
/// </summary>
public sealed class SymbolExtractor
{
    private readonly Compilation _compilation;
    private IReadOnlyDictionary<SyntaxTree, FileRef> _files = new Dictionary<SyntaxTree, FileRef>();
    private Dictionary<long, bool> _generatedByFile = new();
    private SymbolExtraction _result = new();

    public SymbolExtractor(Compilation compilation) => _compilation = compilation;

    /// <param name="files">The syntax trees that belong to indexed files. Declarations elsewhere are ignored.</param>
    public SymbolExtraction Extract(IReadOnlyDictionary<SyntaxTree, FileRef> files)
    {
        _files = files;
        _generatedByFile = files.Values.GroupBy(f => f.FileId).ToDictionary(g => g.Key, g => g.First().IsGenerated);
        _result = new SymbolExtraction();
        VisitNamespace(_compilation.Assembly.GlobalNamespace, null);
        return _result;
    }

    private void VisitNamespace(INamespaceSymbol ns, string? containerKey)
    {
        foreach (var child in ns.GetNamespaceMembers())
        {
            string? key = containerKey;
            var row = TryCreateRow(child, containerKey);
            if (row is not null)
            {
                _result.Symbols.Add(row);
                key = row.Key;
            }
            VisitNamespace(child, key);
        }

        foreach (var type in ns.GetTypeMembers())
            VisitType(type, containerKey);
    }

    private void VisitType(INamedTypeSymbol type, string? containerKey)
    {
        if (type.IsImplicitlyDeclared) return;
        var row = TryCreateRow(type, containerKey);
        if (row is null) return;
        _result.Symbols.Add(row);
        AddTypeEdges(type);

        foreach (var member in type.GetMembers())
        {
            if (member is INamedTypeSymbol nested)
            {
                VisitType(nested, row.Key);
                continue;
            }
            if (member.IsImplicitlyDeclared) continue;
            if (member is IMethodSymbol method && SymbolFacts.IsAccessor(method)) continue;

            var memberRow = TryCreateRow(member, row.Key);
            if (memberRow is null) continue;
            _result.Symbols.Add(memberRow);
            AddMemberEdges(member);
        }

        AddInterfaceImplementationEdges(type);
    }

    private SymbolRow? TryCreateRow(ISymbol symbol, string? containerKey)
    {
        var locations = new List<SymbolLocation>();
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            if (!_files.TryGetValue(reference.SyntaxTree, out var file)) continue;
            var span = reference.GetSyntax().GetLocation().GetLineSpan();
            locations.Add(new SymbolLocation
            {
                FileId = file.FileId,
                StartLine = span.StartLinePosition.Line + 1,
                StartColumn = span.StartLinePosition.Character + 1,
                EndLine = span.EndLinePosition.Line + 1,
                EndColumn = span.EndLinePosition.Character + 1,
            });
        }
        if (locations.Count == 0) return null;

        // Prefer a hand-written declaration as the primary one, then the earliest file.
        var primary = locations
            .OrderBy(l => _generatedByFile[l.FileId])
            .ThenBy(l => l.FileId)
            .ThenBy(l => l.StartLine)
            .First();
        primary.IsPrimary = true;

        return new SymbolRow
        {
            Key = SymbolKey.For(symbol),
            Kind = SymbolFacts.Kind(symbol),
            Name = symbol.Name,
            FullName = SymbolFacts.FullName(symbol),
            ContainerKey = containerKey,
            Signature = SymbolFacts.Signature(symbol),
            Accessibility = SymbolFacts.Accessibility(symbol),
            DocSummary = SymbolFacts.DocSummary(symbol),
            IsGenerated = _generatedByFile[primary.FileId] || HasGeneratedAttribute(symbol),
            Locations = locations,
        };
    }

    // ---- declaration-level relationships -------------------------------------------------------

    /// <summary>Every class, struct, enum, and delegate has one of these as an implicit base; an edge to it says nothing.</summary>
    private static bool IsImplicitBase(INamedTypeSymbol type) => type.SpecialType is
        SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum
        or SpecialType.System_MulticastDelegate or SpecialType.System_Delegate;

    private void AddTypeEdges(INamedTypeSymbol type)
    {
        var baseType = type.BaseType;
        if (baseType is not null && !IsImplicitBase(baseType))
            _result.Edges.Add(new SymbolEdge(type, baseType.OriginalDefinition, "inherits"));

        foreach (var iface in type.Interfaces)
            _result.Edges.Add(new SymbolEdge(type, iface.OriginalDefinition, "implements"));
    }

    private void AddMemberEdges(ISymbol member)
    {
        ISymbol? overridden = member switch
        {
            IMethodSymbol m => m.OverriddenMethod,
            IPropertySymbol p => p.OverriddenProperty,
            IEventSymbol e => e.OverriddenEvent,
            _ => null,
        };
        if (overridden is not null)
            _result.Edges.Add(new SymbolEdge(member, overridden.OriginalDefinition, "overrides"));

        IEnumerable<ISymbol> explicitImpls = member switch
        {
            IMethodSymbol m => m.ExplicitInterfaceImplementations,
            IPropertySymbol p => p.ExplicitInterfaceImplementations,
            IEventSymbol e => e.ExplicitInterfaceImplementations,
            _ => Array.Empty<ISymbol>(),
        };
        foreach (var impl in explicitImpls)
            _result.Edges.Add(new SymbolEdge(member, impl.OriginalDefinition, "implements"));
    }

    /// <summary>
    /// For every interface the type declares (and the interfaces those extend), links each interface member to the
    /// member that implements it, wherever that member is declared (the type or a base class).
    /// </summary>
    private void AddInterfaceImplementationEdges(INamedTypeSymbol type)
    {
        foreach (var declared in type.Interfaces)
        {
            foreach (var iface in new[] { declared }.Concat(declared.AllInterfaces))
            {
                foreach (var ifaceMember in iface.GetMembers())
                {
                    if (ifaceMember is IMethodSymbol m && SymbolFacts.IsAccessor(m)) continue;
                    if (ifaceMember is not (IMethodSymbol or IPropertySymbol or IEventSymbol)) continue;

                    var impl = type.FindImplementationForInterfaceMember(ifaceMember);
                    if (impl is null || impl.IsImplicitlyDeclared) continue;
                    // Explicit implementations are linked from the member itself (AddMemberEdges).
                    if (impl is IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 }
                        or IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 }
                        or IEventSymbol { ExplicitInterfaceImplementations.Length: > 0 })
                        continue;

                    _result.Edges.Add(new SymbolEdge(impl.OriginalDefinition, ifaceMember.OriginalDefinition, "implements"));
                }
            }
        }
    }

    // ---- generated code ------------------------------------------------------------------------

    private static bool HasGeneratedAttribute(ISymbol symbol) =>
        symbol.GetAttributes().Any(a =>
            a.AttributeClass?.Name is "GeneratedCodeAttribute" or "CompilerGeneratedAttribute");

    public static bool DetectGenerated(SyntaxNode root)
    {
        foreach (var trivia in root.GetLeadingTrivia())
        {
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                var t = trivia.ToString();
                if (t.Contains("auto-generated", StringComparison.OrdinalIgnoreCase) ||
                    t.Contains("autogenerated", StringComparison.OrdinalIgnoreCase) ||
                    t.Contains("auto_generated", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether a file name indicates generated code by convention: "*.g.cs", "*.g.i.cs", "*.generated.cs",
    /// "*.Designer.cs", gRPC output ("*Grpc.cs"), and the build's own AssemblyInfo/AssemblyAttributes/GlobalUsings.
    /// </summary>
    public static bool IsGeneratedFilePath(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var name = Path.GetFileName(filePath);
        return name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("Grpc.cs", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("AssemblyAttributes", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("AssemblyInfo", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("GlobalUsings", StringComparison.OrdinalIgnoreCase);
    }
}
