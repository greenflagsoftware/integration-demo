using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace CodeIndexer.Core.Indexing;

/// <summary>Display facts about a symbol, shared by declared symbols and external stubs.</summary>
public static class SymbolFacts
{
    /// <summary>
    /// Fully qualified name that always includes the containing type, for example
    /// "global::SimpleLib.Calculator.Add" for a method.
    /// </summary>
    public static string FullName(ISymbol symbol)
    {
        if (symbol.ContainingType is not null)
        {
            var typeFull = symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return typeFull + "." + symbol.Name;
        }
        return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    public static string Kind(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => "namespace",
        INamedTypeSymbol t when t.TypeKind == TypeKind.Class => "class",
        INamedTypeSymbol t when t.TypeKind == TypeKind.Struct => "struct",
        INamedTypeSymbol t when t.TypeKind == TypeKind.Interface => "interface",
        INamedTypeSymbol t when t.TypeKind == TypeKind.Enum => "enum",
        INamedTypeSymbol t when t.TypeKind == TypeKind.Delegate => "delegate",
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => "other",
    };

    public static string? Signature(ISymbol symbol)
    {
        try { return symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat); }
        catch { return null; }
    }

    public static string? Accessibility(ISymbol symbol) => symbol.DeclaredAccessibility.ToString().ToLowerInvariant();

    public static string? DocSummary(ISymbol symbol)
    {
        string? docXml;
        try { docXml = symbol.GetDocumentationCommentXml(); }
        catch { return null; }
        if (string.IsNullOrEmpty(docXml)) return null;
        try { return XDocument.Parse(docXml).Descendants("summary").FirstOrDefault()?.Value?.Trim(); }
        catch { return null; }
    }

    /// <summary>True for members that are part of a property or event (accessors) rather than symbols of their own.</summary>
    public static bool IsAccessor(IMethodSymbol method) =>
        method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet
            or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise;
}
