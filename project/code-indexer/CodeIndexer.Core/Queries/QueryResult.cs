using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeIndexer.Core.Queries;

public sealed class QueryResponse<T>
{
    public required string Command { get; init; }
    public required string SolutionName { get; init; }
    public required string SolutionPath { get; init; }
    public required string IndexedAt { get; init; }
    public required string IndexedCommit { get; init; }
    public bool Stale { get; init; }
    public required T Data { get; init; }
    public int TotalCount { get; init; }
    public int? Limit { get; init; }
    public bool Truncated { get; init; }
    public List<string>? Warnings { get; init; }

    public string ToJson() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
}

public sealed class SymbolResult
{
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
    public string? ProjectName { get; init; }
}

public sealed class OutlineResult
{
    public required string FilePath { get; init; }
    public int LineCount { get; init; }
    public bool IsGenerated { get; init; }
    public required List<SymbolResult> Symbols { get; init; }
}

public sealed class ShowResult
{
    public required SymbolResult Symbol { get; init; }
    public string? SourceCode { get; init; }
}

public sealed class SearchHit
{
    public required string Name { get; init; }
    public string? FullName { get; init; }
    public required string Kind { get; init; }
    public string? Signature { get; init; }
    public string? FilePath { get; init; }
    public string? DocSummary { get; init; }
    public int? StartLine { get; init; }
    public bool IsGenerated { get; init; }
    public string? ProjectName { get; init; }
    public double Rank { get; init; }
}

public sealed class ReferenceResult
{
    public required string SymbolName { get; init; }
    public string? SymbolFullName { get; init; }
    public required string FilePath { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public required string Kind { get; init; }
    public string? ReferenceName { get; init; }
}

public sealed class CallerCalleeResult
{
    public required long SymbolId { get; init; }
    public required string Name { get; init; }
    public string? FullName { get; init; }
    public required string Kind { get; init; }
    public string? FilePath { get; init; }
    public int Depth { get; init; }
}

public sealed class HierarchyNode
{
    public required long SymbolId { get; init; }
    public required string Name { get; init; }
    public string? FullName { get; init; }
    public required string Kind { get; init; }
    public string? Accessibility { get; init; }
    public string? FilePath { get; init; }
    public required string EdgeKind { get; init; }
    public List<HierarchyNode> Children { get; init; } = new();
}
