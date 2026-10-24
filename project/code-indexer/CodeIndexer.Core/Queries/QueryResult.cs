using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeIndexer.Core.Queries;

public interface IQueryResponse
{
    string ToJson();
    string ToTable();
}

/// <summary>
/// The envelope every query returns. It carries the solution context, staleness, truncation, and, for commands that
/// take a symbol selector, the symbol that was resolved (or the candidates when the selector was ambiguous).
/// </summary>
public sealed class QueryResponse<T> : IQueryResponse
{
    public required string Command { get; init; }
    public required string SolutionName { get; init; }
    public required string SolutionPath { get; init; }
    public required string IndexedAt { get; init; }
    public required string IndexedCommit { get; init; }
    public bool Stale { get; init; }
    /// <summary>Indexed files that changed or disappeared since indexing (first 20).</summary>
    public List<string>? StaleFiles { get; init; }
    /// <summary>The symbol a selector resolved to.</summary>
    public SymbolResult? Target { get; init; }
    /// <summary>Set instead of <see cref="Target"/> when a selector matched several symbols.</summary>
    public List<SymbolResult>? Candidates { get; init; }
    public required T Data { get; init; }
    public int TotalCount { get; init; }
    /// <summary>How many items matched before limit/offset were applied, when that is known.</summary>
    public int? TotalMatches { get; init; }
    public int? Limit { get; init; }
    public bool Truncated { get; init; }
    public List<string>? Warnings { get; init; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public string ToTable() => TableFormatter.Render(JsonSerializer.SerializeToElement(this, JsonOptions));
}

public class SymbolResult
{
    /// <summary>Row id within this database; stable until the next index run. Pass it back as <c>--id</c>.</summary>
    public long Id { get; init; }
    /// <summary>Stable identity: "&lt;assembly&gt;|&lt;documentation id&gt;". Pass it back as <c>--key</c>.</summary>
    public string Key { get; init; } = "";
    public required string Name { get; init; }
    public string? FullName { get; init; }
    public required string Kind { get; init; }
    public string? Accessibility { get; init; }
    public string? Signature { get; init; }
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }
    public string? FilePath { get; init; }
    public string? DocSummary { get; init; }
    public bool IsGenerated { get; init; }
    /// <summary>True for symbols declared outside the indexed projects (framework, NuGet, excluded projects).</summary>
    public bool IsExternal { get; init; }
    public string? ProjectName { get; init; }
}

/// <summary>A symbol with the symbols declared inside it (a type's members, a namespace's types).</summary>
public sealed class OutlineNode : SymbolResult
{
    public List<OutlineNode> Children { get; init; } = new();
}

public sealed class LocationResult
{
    public required string FilePath { get; init; }
    public int StartLine { get; init; }
    public int StartColumn { get; init; }
    public int EndLine { get; init; }
    public int EndColumn { get; init; }
    public bool IsPrimary { get; init; }
    public bool IsGenerated { get; init; }
}

public sealed class OutlineResult
{
    /// <summary>The file outlined; null when a type was outlined.</summary>
    public string? FilePath { get; init; }
    public int? LineCount { get; init; }
    public bool IsGenerated { get; init; }
    public required List<OutlineNode> Symbols { get; init; }
}

public sealed class DefinitionResult
{
    public required SymbolResult Symbol { get; init; }
    /// <summary>One entry per declaration (several for a partial type). Empty for external symbols.</summary>
    public required List<LocationResult> Locations { get; init; }
}

public sealed class ShowResult
{
    public required SymbolResult Symbol { get; init; }
    public required List<LocationResult> Locations { get; init; }
    /// <summary>Source of the primary declaration, only with <c>--source</c>.</summary>
    public string? SourceCode { get; init; }
    public bool SourceTruncated { get; init; }
}

public sealed class SearchHit
{
    public long Id { get; init; }
    public string Key { get; init; } = "";
    public required string Name { get; init; }
    public string? FullName { get; init; }
    public required string Kind { get; init; }
    public string? Signature { get; init; }
    public string? FilePath { get; init; }
    public string? DocSummary { get; init; }
    public int? StartLine { get; init; }
    public bool IsGenerated { get; init; }
    public bool IsExternal { get; init; }
    public string? ProjectName { get; init; }
    public double Rank { get; init; }
}

/// <summary>A compact reference to a symbol, used where the full payload would be noise.</summary>
public sealed class SymbolRef
{
    public long Id { get; init; }
    public string Key { get; init; } = "";
    public required string Name { get; init; }
    public string? FullName { get; init; }
    public required string Kind { get; init; }
}

public sealed class ReferenceResult
{
    public required string FilePath { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    /// <summary>call, use, read, write, type-use, create, inherit, implement, attribute, typeof, nameof.</summary>
    public required string Kind { get; init; }
    public bool IsGenerated { get; init; }
    /// <summary>The member or type that contains the reference.</summary>
    public SymbolRef? Enclosing { get; init; }
    /// <summary>The source line, only with <c>--context</c>.</summary>
    public string? Context { get; init; }
}

public sealed class RefGroup
{
    public required string Group { get; init; }
    public int Count { get; init; }
    public required List<ReferenceResult> References { get; init; }
}

public sealed class RefsResult
{
    /// <summary>Flat results; empty when grouped.</summary>
    public required List<ReferenceResult> References { get; init; }
    /// <summary>Set instead of <see cref="References"/> with <c>--group-by</c>.</summary>
    public List<RefGroup>? Groups { get; init; }
}

public sealed class CallSite
{
    public required string FilePath { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
}

public sealed class CallNode
{
    public required SymbolResult Symbol { get; init; }
    public int Depth { get; init; }
    /// <summary>
    /// With <c>--through-dispatch</c>: the interface/abstract/base member that was actually named at the call site,
    /// when it differs from the symbol asked about (callers) or from this node (callees).
    /// </summary>
    public SymbolRef? Via { get; init; }
    public required List<CallSite> CallSites { get; init; }
    public int CallSiteCount { get; init; }
}

public sealed class ImplementationResult
{
    public required SymbolResult Symbol { get; init; }
    /// <summary>implements, inherits, or overrides.</summary>
    public required string Relation { get; init; }
    public int Depth { get; init; }
}

public sealed class HierarchyNode
{
    public required SymbolResult Symbol { get; init; }
    /// <summary>inherits or implements, relative to the node it hangs under.</summary>
    public required string Relation { get; init; }
    public List<HierarchyNode> Children { get; init; } = new();
}

public sealed class HierarchyResult
{
    public required List<HierarchyNode> Supertypes { get; init; }
    public required List<HierarchyNode> Subtypes { get; init; }
}

public sealed class AtResult
{
    /// <summary>
    /// reference: the position is on a use of <see cref="Symbol"/>.
    /// declaration: it is on the first line of <see cref="Symbol"/>'s declaration.
    /// containing: it is inside <see cref="Symbol"/>'s declaration.
    /// </summary>
    public required string Match { get; init; }
    public required SymbolResult Symbol { get; init; }
    public string? ReferenceKind { get; init; }
    /// <summary>For a reference: the member that contains it.</summary>
    public SymbolRef? Enclosing { get; init; }
    public required string FilePath { get; init; }
    public int Line { get; init; }
    public int? Column { get; init; }
}
