namespace CodeIndexer.Core.Models;

/// <summary>
/// The result of loading a .NET solution into Roslyn.
/// </summary>
public sealed class SolutionLoadResult
{
    public required string SolutionPath { get; init; }
    public IReadOnlyList<IndexedProject> Projects { get; init; } = Array.Empty<IndexedProject>();
    public IReadOnlyList<DiagnosticEntry> Diagnostics { get; init; } = Array.Empty<DiagnosticEntry>();

    /// <summary>
    /// How many projects were successfully loaded.
    /// </summary>
    public int LoadedProjectCount => Projects.Count(p => p.RoslynProject is not null);

    /// <summary>
    /// How many projects were excluded via filters.
    /// </summary>
    public int ExcludedProjectCount => Projects.Count(p => !p.IncludeInIndex);

    /// <summary>
    /// Projects that are included and have a loaded Roslyn project.
    /// </summary>
    public IEnumerable<IndexedProject> IndexableProjects =>
        Projects.Where(p => p.IncludeInIndex && p.RoslynProject is not null);
}
