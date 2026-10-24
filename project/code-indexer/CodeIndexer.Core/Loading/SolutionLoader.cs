using System.Xml.Linq;
using CodeIndexer.Core.Models;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeIndexer.Core.Loading;

/// <summary>
/// Loads a .NET solution into Roslyn's Solution,
/// selecting the primary TFM per project (prefer net10.0, else net8.0, else netstandard2.0).
/// </summary>
public sealed class SolutionLoader
{
    private static bool _msBuildRegistered;

    public static void RegisterMsBuild()
    {
        if (_msBuildRegistered)
            return;

        if (!MSBuildLocator.IsRegistered)
        {
            var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
            if (instances.Length == 0)
                throw new InvalidOperationException(
                    "No MSBuild instance was found. Ensure the .NET SDK is installed.");
            MSBuildLocator.RegisterInstance(instances.OrderByDescending(i => i.Version).First());
        }
        _msBuildRegistered = true;
    }

    private readonly ProjectFilter _filter;

    public SolutionLoader(ProjectFilter? filter = null)
    {
        _filter = filter ?? new ProjectFilter();
    }

    public async Task<SolutionLoadResult> LoadAsync(string solutionPath)
    {
        var diagnostics = new List<DiagnosticEntry>();
        var projects = new List<IndexedProject>();

        RegisterMsBuild();

        if (!File.Exists(solutionPath))
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Severity = Models.DiagnosticSeverity.Error,
                Message = $"Solution file not found: {solutionPath}",
            });
            return new SolutionLoadResult { SolutionPath = solutionPath, Diagnostics = diagnostics, Projects = projects };
        }

        var workspace = MSBuildWorkspace.Create();
#pragma warning disable CS0618
        workspace.WorkspaceFailed += (_, args) =>
#pragma warning restore CS0618
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Severity = args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure
                    ? Models.DiagnosticSeverity.Error
                    : Models.DiagnosticSeverity.Warning,
                Message = args.Diagnostic.Message,
            });
        };

        Solution roslynSolution;
        try
        {
            roslynSolution = await workspace.OpenSolutionAsync(solutionPath);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Severity = Models.DiagnosticSeverity.Error,
                Message = $"Failed to open solution: {ex.Message}",
            });
            return new SolutionLoadResult { SolutionPath = solutionPath, Diagnostics = diagnostics, Projects = projects };
        }

        foreach (var roslynProject in roslynSolution.Projects)
        {
            var projectName = roslynProject.Name;
            var projectPath = roslynProject.FilePath ?? projectName;
            var projectDiagnostics = new List<DiagnosticEntry>();

            var (tfms, selectedTfm) = ReadTargetFrameworks(projectPath);

            if (selectedTfm is null && tfms.Count > 0)
                selectedTfm = tfms[0];

            if (selectedTfm is null)
            {
                projectDiagnostics.Add(new DiagnosticEntry
                {
                    Severity = Models.DiagnosticSeverity.Warning,
                    Message = "Could not determine a target framework for this project.",
                });
            }

            var include = _filter.ShouldInclude(projectName);

            var indexedProject = new IndexedProject
            {
                Name = projectName,
                FilePath = projectPath,
                Language = roslynProject.Language,
                TargetFramework = selectedTfm ?? "unknown",
                AllTargetFrameworks = tfms.AsReadOnly(),
                RoslynProject = roslynProject,
                IncludeInIndex = include,
                Diagnostics = projectDiagnostics,
            };

            projects.Add(indexedProject);
        }

        return new SolutionLoadResult
        {
            SolutionPath = solutionPath,
            Projects = projects,
            Diagnostics = diagnostics,
        };
    }

    private static (List<string> allTfms, string? selected) ReadTargetFrameworks(string projectPath)
    {
        try
        {
            if (!File.Exists(projectPath))
                return (new List<string>(), null);

            var doc = XDocument.Load(projectPath);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

            var tfmElement = doc.Descendants(ns + "TargetFrameworks").FirstOrDefault();
            string? tfmValue = null;

            if (tfmElement is not null && !string.IsNullOrWhiteSpace(tfmElement.Value))
            {
                tfmValue = tfmElement.Value.Trim();
            }
            else
            {
                var singleTfm = doc.Descendants(ns + "TargetFramework").FirstOrDefault();
                if (singleTfm is not null && !string.IsNullOrWhiteSpace(singleTfm.Value))
                    tfmValue = singleTfm.Value.Trim();
            }

            if (tfmValue is null)
                return (new List<string>(), null);

            var tfms = tfmValue
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            var selected = SelectPrimaryTfm(tfms);
            return (tfms, selected);
        }
        catch
        {
            return (new List<string>(), null);
        }
    }

    private static string? SelectPrimaryTfm(IReadOnlyList<string> tfms)
    {
        if (tfms.Count == 0)
            return null;

        foreach (var preferred in new[] { "net10.0", "net8.0", "netstandard2.0" })
        {
            var match = tfms.FirstOrDefault(t =>
                t.Equals(preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return tfms[0];
    }

    public static void PrintSummary(SolutionLoadResult result)
    {
        Console.WriteLine($"Solution: {result.SolutionPath}");
        Console.WriteLine($"Projects: {result.Projects.Count} total, " +
                          $"{result.LoadedProjectCount} loaded, " +
                          $"{result.ExcludedProjectCount} excluded");
        Console.WriteLine();

        foreach (var project in result.Projects)
        {
            var status = project.RoslynProject is not null ? "loaded" : "missing";
            var included = project.IncludeInIndex ? "include" : "excluded";
            var docCount = project.RoslynProject?.DocumentIds.Count ?? 0;
            Console.WriteLine($"  [{included}] {project.Name} ({status}, {project.TargetFramework}, {docCount} documents)");

            foreach (var d in project.Diagnostics)
                Console.WriteLine($"    {d}");
        }

        foreach (var d in result.Diagnostics)
            Console.WriteLine($"  [solution] {d}");
    }
}
