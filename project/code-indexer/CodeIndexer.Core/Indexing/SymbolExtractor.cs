using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeIndexer.Core.Indexing;

public sealed class SymbolExtractor
{
    private readonly Compilation _compilation;
    public SymbolExtractor(Compilation compilation) => _compilation = compilation;

    public List<SymbolRow> Extract(IEnumerable<Document> documents)
    {
        var rows = new List<SymbolRow>();
        foreach (var doc in documents)
        {
            var tree = doc.GetSyntaxTreeAsync().Result;
            if (tree is null) continue;
            var model = _compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            var isGenerated = DetectGenerated(root);

            var typeDecls = root.DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>()
                .Concat<MemberDeclarationSyntax>(root.DescendantNodes().OfType<NamespaceDeclarationSyntax>())
                .Concat<MemberDeclarationSyntax>(root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>());

            foreach (var decl in typeDecls)
                ExtractFromDeclaration(decl, model, null, rows, isGenerated);
        }
        return rows;
    }

    private void ExtractFromDeclaration(MemberDeclarationSyntax decl, SemanticModel model, long? containerId, List<SymbolRow> rows, bool fileGenerated)
    {
        var symbol = model.GetDeclaredSymbol(decl);
        if (symbol is null || symbol.IsImplicitlyDeclared) return;
        var kind = GetSymbolKind(symbol);
        if (kind == "namespace") return;
        // Skip property/event accessor methods (they have their own symbols already).
        if (symbol is IMethodSymbol method && method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove)
            return;
        var isGenerated = fileGenerated || HasGeneratedAttribute(symbol);
        var fullName = GetFullName(symbol);

        rows.Add(new SymbolRow
        {
            Kind = kind,
            Name = symbol.Name,
            FullName = fullName,
            ContainerId = containerId,
            Signature = GetSignature(symbol),
            Accessibility = symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
            StartLine = decl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            EndLine = decl.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
            DocSummary = ExtractSummary(symbol.GetDocumentationCommentXml()),
            IsGenerated = isGenerated,
        });

        if (symbol is INamedTypeSymbol namedType)
        {
            foreach (var member in namedType.GetMembers())
            {
                if (member.IsImplicitlyDeclared || member is INamespaceSymbol) continue;
                var mkind = GetSymbolKind(member);
                if (mkind == "namespace") continue;
                // Skip property/event accessor methods.
                if (member is IMethodSymbol m && m.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove)
                    continue;
                var mg = isGenerated || HasGeneratedAttribute(member);
                var memberFullName = GetFullName(member);
                int sl = 0, el = 0;
                var syn = member.DeclaringSyntaxReferences.FirstOrDefault();
                if (syn is not null)
                {
                    sl = syn.GetSyntax().GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    el = syn.GetSyntax().GetLocation().GetLineSpan().EndLinePosition.Line + 1;
                }
                rows.Add(new SymbolRow
                {
                    Kind = mkind,
                    Name = member.Name,
                    FullName = memberFullName,
                    Signature = GetSignature(member),
                    Accessibility = member.DeclaredAccessibility.ToString().ToLowerInvariant(),
                    StartLine = sl,
                    EndLine = el,
                    DocSummary = ExtractSummary(member.GetDocumentationCommentXml()),
                    IsGenerated = mg,
                });
            }
        }
    }

    /// <summary>
    /// Build a fully qualified name that always includes the containing type, e.g.
    /// "global::SimpleLib.Calculator.Add" for a method.
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

    private static string GetSymbolKind(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => "namespace",
        INamedTypeSymbol t when t.TypeKind == TypeKind.Class => "class",
        INamedTypeSymbol t when t.TypeKind == TypeKind.Struct => "struct",
        INamedTypeSymbol t when t.TypeKind == TypeKind.Interface => "interface",
        INamedTypeSymbol t when t.TypeKind == TypeKind.Enum => "enum",
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => "other",
    };

    private static string? GetSignature(ISymbol symbol)
    {
        try { return symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat); }
        catch { return null; }
    }

    private static string? ExtractSummary(string? docXml)
    {
        if (string.IsNullOrEmpty(docXml)) return null;
        try { return XDocument.Parse(docXml).Descendants("summary").FirstOrDefault()?.Value?.Trim(); }
        catch { return null; }
    }

    private static bool HasGeneratedAttribute(ISymbol symbol) =>
        symbol.GetAttributes().Any(a =>
            a.AttributeClass?.Name is "GeneratedCodeAttribute" or "CompilerGeneratedCodeAttribute");

    public static bool DetectGenerated(SyntaxNode root)
    {
        foreach (var trivia in root.GetLeadingTrivia())
        {
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                var t = trivia.ToString();
                if (t.Contains("<auto-generated>") || t.Contains("auto-generated") ||
                    t.Contains("Autogenerated") || t.Contains("auto_generated") ||
                    t.Contains("<autogenerated") || t.Contains("<auto-generated"))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Check if a file path indicates generated code (by convention).
    /// </summary>
    public static bool IsGeneratedFilePath(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var name = Path.GetFileName(filePath);
        return name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".Grpc.cs", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("AssemblyAttributes", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("AssemblyInfo", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("GlobalUsings", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SymbolRow
{
    public long? Id { get; set; }
    public long? FileId { get; set; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public long? ContainerId { get; set; }
    public string? Signature { get; init; }
    public string? Accessibility { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string? DocSummary { get; init; }
    public bool IsGenerated { get; init; }
}
