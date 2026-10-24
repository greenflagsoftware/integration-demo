namespace CodeIndexer.Core.Models;

/// <summary>
/// Represents a single project loaded from a solution.
/// </summary>
public sealed class IndexedProject
{
    public required string Name { get; init; }
    public required string FilePath { get; init; }
    public required string Language { get; init; }
    public required string TargetFramework { get; init; }

    /// <summary>
    /// All target frameworks declared in the project (before TFM selection).
    /// </summary>
    public IReadOnlyList<string> AllTargetFrameworks { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The Roslyn Project once the workspace is loaded.
    /// </summary>
    public Microsoft.CodeAnalysis.Project? RoslynProject { get; set; }

    /// <summary>
    /// Whether this project should be included in indexing.
    /// </summary>
    public bool IncludeInIndex { get; set; } = true;

    /// <summary>
    /// Load diagnostics specific to this project.
    /// </summary>
    public List<DiagnosticEntry> Diagnostics { get; init; } = new();
}
