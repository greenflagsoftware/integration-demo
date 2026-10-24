using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeIndexer.Core.Indexing;

public sealed class ReferenceRow
{
    public long SymbolId { get; set; }
    public long FileId { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    /// <summary>Width of the referenced token in characters, so a position can be matched to the reference.</summary>
    public int Length { get; set; } = 1;
    public string Kind { get; set; } = "use";
    /// <summary>The member (or type) whose body or declaration contains the reference, when it is an indexed symbol.</summary>
    public long? EnclosingSymbolId { get; set; }
    public string? ReferenceName { get; set; }
}

public sealed class EdgeRow
{
    public long FromSymbolId { get; set; }
    public long ToSymbolId { get; set; }
    public string Kind { get; set; } = "";
}

/// <summary>
/// Resolves every name in a syntax tree to the symbol it denotes and records where it is used, how (call, read,
/// write, type-use, ...), and by which member. Also derives the call and creation edges that follow from uses.
/// </summary>
public sealed class ReferenceExtractor
{
    /// <summary>Row id for a symbol, creating an external stub row when it is not indexed. Null when not representable.</summary>
    public delegate long? Resolve(ISymbol symbol);

    /// <summary>Row id of an indexed symbol, or null. Never creates rows.</summary>
    public delegate long? Lookup(ISymbol symbol);

    private readonly Compilation _compilation;
    private readonly Resolve _resolve;
    private readonly Lookup _lookup;

    private SemanticModel _model = null!;
    private long _fileId;
    private List<ReferenceRow> _refs = null!;
    private List<EdgeRow> _edges = null!;

    public ReferenceExtractor(Compilation compilation, Resolve resolve, Lookup lookup)
    {
        _compilation = compilation;
        _resolve = resolve;
        _lookup = lookup;
    }

    public (List<ReferenceRow> References, List<EdgeRow> Edges) Extract(SyntaxTree tree, long fileId)
    {
        _model = _compilation.GetSemanticModel(tree);
        _fileId = fileId;
        _refs = new List<ReferenceRow>();
        _edges = new List<EdgeRow>();

        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            switch (node)
            {
                case SimpleNameSyntax name:
                    VisitName(name);
                    break;
                case ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax:
                    VisitCreation((ExpressionSyntax)node);
                    break;
                case ConstructorInitializerSyntax initializer:
                    VisitInitializer(initializer);
                    break;
                case ElementAccessExpressionSyntax elementAccess:
                    VisitElementAccess(elementAccess);
                    break;
            }
        }
        return (_refs, _edges);
    }

    // ---- names ---------------------------------------------------------------------------------

    private void VisitName(SimpleNameSyntax name)
    {
        if (name is IdentifierNameSyntax { IsVar: true }) return;

        var info = _model.GetSymbolInfo(name);
        var symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
        if (symbol is null) return;

        symbol = Normalize(symbol, name);
        if (symbol is null) return;

        var id = _resolve(symbol);
        if (id is null) return;

        var enclosing = Enclosing(name);
        foreach (var kind in Classify(name, symbol))
        {
            Add(name.GetLocation(), name.Identifier.Span.Length, id.Value, kind, enclosing, symbol);
            if (kind == "call" && symbol is IMethodSymbol && enclosing is not null)
                _edges.Add(new EdgeRow { FromSymbolId = enclosing.Value, ToSymbolId = id.Value, Kind = "calls" });
        }
    }

    /// <summary>
    /// Maps what the semantic model returned to the declaration that is stored: the declared extension method for a
    /// reduced call, the generic definition for a constructed one. Returns null for symbols that are not indexed
    /// (locals, parameters, namespaces, type parameters, lambdas, ...).
    /// </summary>
    private static ISymbol? Normalize(ISymbol symbol, SimpleNameSyntax name)
    {
        switch (symbol)
        {
            case IMethodSymbol method:
                if (method.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction
                    or MethodKind.BuiltinOperator or MethodKind.DelegateInvoke or MethodKind.FunctionPointerSignature)
                    return null;
                if (method.ReducedFrom is not null) method = method.ReducedFrom;
                // `[Foo]` binds to the attribute's constructor; the use that matters is the attribute type.
                if (method.MethodKind == MethodKind.Constructor && name.Ancestors().OfType<AttributeSyntax>().Any())
                    return method.ContainingType.OriginalDefinition;
                return method.OriginalDefinition;
            case IPropertySymbol or IFieldSymbol or IEventSymbol:
                return symbol.OriginalDefinition;
            case INamedTypeSymbol type:
                if (type.TypeKind == TypeKind.Error || type.IsAnonymousType) return null;
                return type.OriginalDefinition;
            default:
                return null;
        }
    }

    // ---- reference kinds -----------------------------------------------------------------------

    private IReadOnlyList<string> Classify(SimpleNameSyntax name, ISymbol target)
    {
        // Climb to the whole expression the name is the last part of (A.B.C, a?.B, global::A).
        ExpressionSyntax top = name;
        while (true)
        {
            if (top.Parent is QualifiedNameSyntax q && q.Right == top) top = q;
            else if (top.Parent is AliasQualifiedNameSyntax aq && aq.Name == top) top = aq;
            else if (top.Parent is MemberAccessExpressionSyntax m && m.Name == top) top = m;
            else if (top.Parent is MemberBindingExpressionSyntax mb && mb.Name == top) top = mb;
            else break;
        }

        if (IsNameofArgument(top)) return new[] { "nameof" };

        if (target is INamedTypeSymbol)
        {
            return top.Parent switch
            {
                TypeOfExpressionSyntax => new[] { "typeof" },
                AttributeSyntax attribute when attribute.Name == top => new[] { "attribute" },
                BaseTypeSyntax => new[] { target.TypeKindIsInterface() ? "implement" : "inherit" },
                _ => new[] { "type-use" },
            };
        }

        if (target is IMethodSymbol)
            return top.Parent is InvocationExpressionSyntax invocation && invocation.Expression == top
                ? new[] { "call" }
                : new[] { "use" };

        return AccessKinds(top, target);
    }

    private static IReadOnlyList<string> AccessKinds(ExpressionSyntax top, ISymbol target)
    {
        switch (top.Parent)
        {
            case AssignmentExpressionSyntax assignment when assignment.Left == top:
                if (target is IEventSymbol || assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                    return new[] { "write" };
                return new[] { "write", "read" };
            case PrefixUnaryExpressionSyntax prefix when prefix.Operand == top
                                                         && (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)):
                return new[] { "write", "read" };
            case PostfixUnaryExpressionSyntax postfix when postfix.Operand == top
                                                           && (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression)):
                return new[] { "write", "read" };
            case ArgumentSyntax argument when argument.Expression == top:
                if (argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)) return new[] { "write" };
                if (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)) return new[] { "write", "read" };
                break;
        }
        return new[] { "read" };
    }

    private bool IsNameofArgument(ExpressionSyntax top) =>
        top.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } }
        && invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }
        && _model.GetSymbolInfo(invocation).Symbol is null;

    // ---- creation, constructor initializers, indexers ------------------------------------------

    private void VisitCreation(ExpressionSyntax creation)
    {
        var constructor = _model.GetSymbolInfo(creation).Symbol as IMethodSymbol;
        var createdType = (constructor?.ContainingType ?? _model.GetTypeInfo(creation).Type as INamedTypeSymbol)?.OriginalDefinition;
        if (createdType is null || createdType.TypeKind == TypeKind.Error) return;

        // A type with no declared constructor has an implicit one that is not stored; link to the type instead.
        bool explicitConstructor = constructor is not null && !constructor.IsImplicitlyDeclared;
        ISymbol target = explicitConstructor ? constructor!.OriginalDefinition : createdType;
        var id = _resolve(target);
        if (id is null) return;

        var enclosing = Enclosing(creation);
        // `new Foo()` already records the type name as a use; only add a row where no type name exists.
        if (explicitConstructor || creation is ImplicitObjectCreationExpressionSyntax)
            Add(creation.GetLocation(), creation.GetFirstToken().Span.Length, id.Value, "create", enclosing, target);
        if (enclosing is not null)
            _edges.Add(new EdgeRow { FromSymbolId = enclosing.Value, ToSymbolId = id.Value, Kind = "creates" });
    }

    private void VisitInitializer(ConstructorInitializerSyntax initializer)
    {
        if (_model.GetSymbolInfo(initializer).Symbol is not IMethodSymbol constructor || constructor.IsImplicitlyDeclared)
            return;
        var id = _resolve(constructor.OriginalDefinition);
        if (id is null) return;

        var enclosing = Enclosing(initializer);
        Add(initializer.ThisOrBaseKeyword.GetLocation(), initializer.ThisOrBaseKeyword.Span.Length, id.Value, "call", enclosing, constructor.OriginalDefinition);
        if (enclosing is not null)
            _edges.Add(new EdgeRow { FromSymbolId = enclosing.Value, ToSymbolId = id.Value, Kind = "calls" });
    }

    private void VisitElementAccess(ElementAccessExpressionSyntax access)
    {
        if (_model.GetSymbolInfo(access).Symbol is not IPropertySymbol indexer) return;
        var id = _resolve(indexer.OriginalDefinition);
        if (id is null) return;

        var enclosing = Enclosing(access);
        foreach (var kind in AccessKinds(access, indexer))
            Add(access.ArgumentList.OpenBracketToken.GetLocation(), 1, id.Value, kind, enclosing, indexer.OriginalDefinition);
    }

    // ---- the enclosing member ------------------------------------------------------------------

    /// <summary>
    /// The nearest enclosing declaration that is an indexed symbol: a method, constructor, property (its accessors
    /// and initializer included), event, field, or, failing those, the type that contains the node.
    /// Lambdas and local functions belong to the member that contains them.
    /// </summary>
    private long? Enclosing(SyntaxNode node)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            ISymbol? declared = n switch
            {
                MethodDeclarationSyntax or ConstructorDeclarationSyntax or DestructorDeclarationSyntax
                    or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax
                    or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax
                    or EnumMemberDeclarationSyntax or BaseTypeDeclarationSyntax or DelegateDeclarationSyntax
                    => _model.GetDeclaredSymbol(n),
                VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax }
                    => _model.GetDeclaredSymbol(n),
                _ => null,
            };
            if (declared is not null)
                return _lookup(declared.OriginalDefinition);
        }
        return null;
    }

    private void Add(Location location, int length, long symbolId, string kind, long? enclosing, ISymbol target)
    {
        var start = location.GetLineSpan().StartLinePosition;
        _refs.Add(new ReferenceRow
        {
            SymbolId = symbolId,
            FileId = _fileId,
            Line = start.Line + 1,
            Column = start.Character + 1,
            Length = Math.Max(1, length),
            Kind = kind,
            EnclosingSymbolId = enclosing,
            ReferenceName = SymbolFacts.FullName(target),
        });
    }
}

internal static class SymbolExtensions
{
    public static bool TypeKindIsInterface(this ISymbol symbol) => symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface };
}
