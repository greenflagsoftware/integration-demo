namespace CodeIndexer.Core.Models;

/// <summary>
/// Represents a diagnostic message from loading or indexing.
/// </summary>
public sealed class DiagnosticEntry
{
    public DiagnosticSeverity Severity { get; init; }
    public required string Message { get; init; }
    public string? ProjectPath { get; init; }
    public string? FilePath { get; init; }

    public override string ToString() =>
        $"[{Severity}] {Message}" +
        (ProjectPath is not null ? $" (project: {ProjectPath})" : "") +
        (FilePath is not null ? $" (file: {FilePath})" : "");
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}
