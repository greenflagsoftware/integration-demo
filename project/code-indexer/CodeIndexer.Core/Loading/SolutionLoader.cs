using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeIndexer.Core.Models;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeIndexer.Core.Loading;

/// <summary>
/// Loads a .NET solution into Roslyn's Solution, keeping exactly one Roslyn project per project file.
/// For multi-targeted projects the primary TFM is chosen (highest modern net5+, else netstandard,
/// else netcoreapp, else the first declared) and only that TFM's project is indexed.
/// </summary>
public sealed class SolutionLoader
{
    private static readonly object RegisterLock = new();

    /// <summary>Which SDK's MSBuild was registered and why, once <see cref="RegisterMsBuild"/> has run.</summary>
    public static string? RegisteredSdkDescription { get; private set; }

    /// <summary>The registered SDK's version, for example "8.0.426".</summary>
    public static string? RegisteredSdkVersion { get; private set; }

    /// <summary>
    /// Registers an MSBuild instance. If a global.json governs <paramref name="solutionDirectory"/>, the SDK it
    /// pins (subject to its rollForward policy) is used; otherwise the highest installed SDK. Has no effect
    /// once an instance is registered in this process.
    /// </summary>
    public static void RegisterMsBuild(string? solutionDirectory = null)
    {
        lock (RegisterLock)
        {
            if (MSBuildLocator.IsRegistered)
                return;

            var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
            if (instances.Length == 0)
                throw new InvalidOperationException(
                    "No MSBuild instance was found. Ensure the .NET SDK is installed.");

            var pinned = solutionDirectory is null ? null : SdkSelector.FindGlobalJson(solutionDirectory);
            var chosenVersion = SdkSelector.Select(instances.Select(i => i.Version).ToList(), pinned?.Pin);
            if (chosenVersion is null)
            {
                var installed = string.Join(", ", instances.Select(i => i.Version).OrderBy(v => v));
                throw new InvalidOperationException(
                    $"global.json ({pinned!.Value.Path}) requires .NET SDK {pinned.Value.Pin.Version} " +
                    $"(rollForward: {pinned.Value.Pin.RollForward}), but the installed SDKs are: {installed}. " +
                    "Install a matching SDK.");
            }

            MSBuildLocator.RegisterInstance(instances.First(i => i.Version == chosenVersion));
            RegisteredSdkVersion = chosenVersion.ToString();
            RegisteredSdkDescription = pinned is null
                ? $"{chosenVersion} (highest installed; no global.json found)"
                : $"{chosenVersion} (global.json pins {pinned.Value.Pin.Version}, rollForward {pinned.Value.Pin.RollForward}: {pinned.Value.Path})";
        }
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

        if (!File.Exists(solutionPath))
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Severity = Models.DiagnosticSeverity.Error,
                Message = $"Solution file not found: {solutionPath}",
            });
            return new SolutionLoadResult { SolutionPath = solutionPath, Diagnostics = diagnostics, Projects = projects };
        }

        try
        {
            RegisterMsBuild(Path.GetDirectoryName(Path.GetFullPath(solutionPath)));
        }
        catch (InvalidOperationException ex)
        {
            diagnostics.Add(new DiagnosticEntry { Severity = Models.DiagnosticSeverity.Error, Message = ex.Message });
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

        // A multi-targeted project appears once per TFM with the same file path; keep one per file.
        foreach (var group in roslynSolution.Projects.GroupBy(p => p.FilePath ?? p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToList();
            var projectPath = group.Key;
            var projectName = members[0].FilePath is null
                ? StripTfmSuffix(members[0].Name)
                : Path.GetFileNameWithoutExtension(members[0].FilePath!);
            var projectDiagnostics = new List<DiagnosticEntry>();

            var tfms = ReadTargetFrameworks(projectPath);
            if (tfms.Count == 0)
                tfms = members.Select(TfmFromProjectName).OfType<string>().Distinct().ToList();

            var selectedTfm = SelectPrimaryTfm(tfms);
            if (selectedTfm is null)
            {
                projectDiagnostics.Add(new DiagnosticEntry
                {
                    Severity = Models.DiagnosticSeverity.Warning,
                    Message = "Could not determine a target framework for this project.",
                });
            }

            var chosen = members.Count == 1
                ? members[0]
                : members.FirstOrDefault(p => string.Equals(TfmFromProjectName(p), selectedTfm, StringComparison.OrdinalIgnoreCase))
                  ?? members[0];

            if (members.Count > 1)
            {
                projectDiagnostics.Add(new DiagnosticEntry
                {
                    Severity = Models.DiagnosticSeverity.Info,
                    Message = $"Multi-targeted ({string.Join(";", tfms)}); indexing {selectedTfm ?? "first available"} only. " +
                              "Code under conditional compilation for other targets is not indexed.",
                });
            }

            projects.Add(new IndexedProject
            {
                Name = projectName,
                FilePath = projectPath,
                Language = chosen.Language,
                TargetFramework = selectedTfm ?? "unknown",
                AllTargetFrameworks = tfms.AsReadOnly(),
                RoslynProject = chosen,
                IncludeInIndex = _filter.ShouldInclude(projectName),
                Diagnostics = projectDiagnostics,
            });
        }

        return new SolutionLoadResult
        {
            SolutionPath = solutionPath,
            Projects = projects,
            Diagnostics = diagnostics,
            FilterSignature = _filter.Signature,
            SdkVersion = RegisteredSdkVersion ?? "",
            Dependents = ComputeDependents(roslynSolution),
        };
    }

    /// <summary>
    /// Maps each project file to the project files that depend on it transitively. Multi-targeted projects appear once per
    /// target framework in Roslyn's graph; they are merged by file path.
    /// </summary>
    private static Dictionary<string, IReadOnlySet<string>> ComputeDependents(Solution solution)
    {
        var graph = solution.GetProjectDependencyGraph();
        var pathOf = solution.Projects.ToDictionary(p => p.Id, p => p.FilePath ?? p.Name);
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            var path = pathOf[project.Id];
            if (!result.TryGetValue(path, out var set))
                result[path] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dependentId in graph.GetProjectsThatTransitivelyDependOnThisProject(project.Id))
            {
                var dependentPath = pathOf[dependentId];
                if (!string.Equals(dependentPath, path, StringComparison.OrdinalIgnoreCase))
                    set.Add(dependentPath);
            }
        }
        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Regex TfmSuffix = new(@"\s*\(([^()]+)\)\s*$", RegexOptions.Compiled);

    /// <summary>Roslyn names the per-TFM projects of a multi-targeted project like "Foo(net8.0)".</summary>
    private static string? TfmFromProjectName(Project project)
    {
        var m = TfmSuffix.Match(project.Name);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string StripTfmSuffix(string name) => TfmSuffix.Replace(name, "");

    private static List<string> ReadTargetFrameworks(string projectPath)
    {
        try
        {
            if (!File.Exists(projectPath))
                return new List<string>();

            var doc = XDocument.Load(projectPath);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

            var tfmElement = doc.Descendants(ns + "TargetFrameworks").FirstOrDefault();
            string? tfmValue = null;

            if (tfmElement is not null && !string.IsNullOrWhiteSpace(tfmElement.Value))
                tfmValue = tfmElement.Value.Trim();
            else
            {
                var singleTfm = doc.Descendants(ns + "TargetFramework").FirstOrDefault();
                if (singleTfm is not null && !string.IsNullOrWhiteSpace(singleTfm.Value))
                    tfmValue = singleTfm.Value.Trim();
            }

            return tfmValue is null
                ? new List<string>()
                : tfmValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static readonly Regex ModernTfm = new(@"^net(\d+)\.(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StandardTfm = new(@"^netstandard(\d+)\.(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CoreAppTfm = new(@"^netcoreapp(\d+)\.(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Highest modern .NET (net5.0+) wins, then the highest netstandard, then the highest netcoreapp,
    /// then the first declared (for example net462 only).
    /// </summary>
    public static string? SelectPrimaryTfm(IReadOnlyList<string> tfms)
    {
        if (tfms.Count == 0)
            return null;

        static string? Highest(IReadOnlyList<string> list, Regex re, int minMajor = 0) =>
            list.Select(t => (t, m: re.Match(t)))
                .Where(x => x.m.Success && int.Parse(x.m.Groups[1].Value) >= minMajor)
                .OrderByDescending(x => int.Parse(x.m.Groups[1].Value))
                .ThenByDescending(x => int.Parse(x.m.Groups[2].Value))
                .Select(x => x.t)
                .FirstOrDefault();

        return Highest(tfms, ModernTfm, 5)
               ?? Highest(tfms, StandardTfm)
               ?? Highest(tfms, CoreAppTfm)
               ?? tfms[0];
    }

    public static void PrintSummary(SolutionLoadResult result)
    {
        Console.WriteLine($"Solution: {result.SolutionPath}");
        if (RegisteredSdkDescription is not null)
            Console.WriteLine($".NET SDK: {RegisteredSdkDescription}");
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
