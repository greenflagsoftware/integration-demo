using Microsoft.CodeAnalysis;

namespace CodeIndexer.Core.Indexing;

/// <summary>
/// Stable identity for a symbol: "&lt;assembly&gt;|&lt;documentation-comment id&gt;". Unlike a name string it
/// distinguishes overloads and generic arity, and it is the same whichever syntax node it was reached from.
/// </summary>
public static class SymbolKey
{
    public static string For(ISymbol symbol)
    {
        var def = symbol.OriginalDefinition;
        var assembly = def.ContainingAssembly?.Name ?? "";
        var id = def.GetDocumentationCommentId();
        if (id is null)
        {
            // Symbols without a documentation id (rare for declared members): fall back to a display string.
            id = "!" + def.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        }
        return assembly + "|" + id;
    }
}
