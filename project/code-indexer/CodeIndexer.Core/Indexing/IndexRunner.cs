using CodeIndexer.Core.Loading;
using CodeIndexer.Core.Models;
using CodeIndexer.Core.Storage;

namespace CodeIndexer.Core.Indexing;

public enum IndexMode
{
    /// <summary>Nothing the index depends on changed; nothing was loaded or written.</summary>
    UpToDate,
    /// <summary>Only the projects affected by a change were indexed again.</summary>
    Incremental,
    /// <summary>The whole index was rebuilt.</summary>
    Full,
}

public sealed record IndexRunOptions(
    string SolutionPath, string DbPath, string RepoRoot, ProjectFilter Filter, bool Full = false, bool DryRun = false);

/// <summary>A project an incremental run redid, and why.</summary>
public sealed record ReindexedProject(string Name, string Path, string Reason);

public sealed class IndexRunResult
{
    public required IndexMode Mode { get; init; }
    /// <summary>Why this mode: for Full, what made an update impossible; for Incremental, what changed.</summary>
    public required string Reason { get; init; }
    public List<string> ChangedInputs { get; init; } = new();
    public List<ReindexedProject> Reindexed { get; init; } = new();
    public List<string> Unchanged { get; init; } = new();
    public List<string> Removed { get; init; } = new();
    public bool WasDryRun { get; init; }
    public IndexResult? Result { get; init; }
    public SolutionLoadResult? LoadResult { get; init; }
}

/// <summary>
/// Brings an index up to date with the least work that is still correct. The result of an update always equals what a
/// full rebuild of the same files would produce.
///
/// The dependency rule: the unit of work is the project. A project is redone if one of its inputs changed (a source file,
/// the project file, its restore output, or files added/deleted/renamed under its directory), if it was added to the
/// solution, or if any project it references, directly or transitively, is redone, because what it binds to may have
/// changed. A change to a build-wide file (Directory.Build.props, global.json, nuget.config), to the project filter, to the
/// SDK, or to the tool version redoes everything. Projects the filter excludes still count as dependencies.
/// </summary>
public static class IndexRunner
{
    public static async Task<IndexRunResult> RunAsync(IndexRunOptions options)
    {
        var dbPath = Path.GetFullPath(options.DbPath);
        var repoRoot = options.RepoRoot;
        var solutionRel = PathUtil.ToRepoRelative(repoRoot, options.SolutionPath);
        var engine = new IndexEngine(repoRoot);

        // 1. Can the existing index be updated at all?
        string? fullReason = options.Full ? "--full was given" : !File.Exists(dbPath) ? "no existing index" : null;
        List<InputRecord> stored = new();
        List<ChangedInput> changes = new();
        string? storedSdk = null;

        if (fullReason is null)
        {
            try
            {
                using var db = new IndexDatabase(dbPath, repoRoot);
                fullReason = Incompatibility(db, solutionRel, options.Filter.Signature);
                if (fullReason is null)
                {
                    storedSdk = db.GetMeta("sdk_version");
                    stored = db.GetInputs();
                    changes = InputScanner.FindChanges(stored, repoRoot);
                }
            }
            catch (InvalidOperationException)
            {
                fullReason = "the existing index was built by an incompatible version";
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                fullReason = "the existing index file is unreadable";
            }
        }

        // 2. Without loading anything: did the build environment change, or did nothing change at all?
        if (fullReason is null)
        {
            if (changes.FirstOrDefault(c => c.Stored.Kind == InputScanner.Env) is { } env)
                fullReason = $"build environment changed: {env.Stored.Path} ({env.Reason})";
            else if (stored.Count == 0)
                fullReason = "the existing index records no inputs";
            else if (changes.Count == 0)
                return new IndexRunResult { Mode = IndexMode.UpToDate, Reason = "no input changed since the last run", WasDryRun = options.DryRun };
        }

        // 3. Load the solution (needed to rebuild, and to find dependents).
        var load = await new SolutionLoader(options.Filter).LoadAsync(options.SolutionPath);
        var indexable = load.IndexableProjects.ToList();
        if (fullReason is null && load.SdkVersion != (storedSdk ?? ""))
            fullReason = $"the .NET SDK changed ({(string.IsNullOrEmpty(storedSdk) ? "unknown" : storedSdk)} -> {load.SdkVersion})";

        List<string> Describe(IEnumerable<ChangedInput> c) => c.Select(x => $"{x.Stored.Path} ({x.Reason})").ToList();

        IndexPlan? plan = null;
        if (fullReason is null)
        {
            plan = Plan(load, stored, changes, repoRoot);
            if (indexable.Count > 0 && indexable.All(p => plan.Dirty.ContainsKey(PathUtil.ToRepoRelative(repoRoot, p.FilePath))))
                fullReason = "every indexed project is affected";
        }

        // 4. Do it.
        if (fullReason is not null)
        {
            if (options.DryRun)
                return new IndexRunResult { Mode = IndexMode.Full, Reason = fullReason, ChangedInputs = Describe(changes), LoadResult = load, WasDryRun = true };
            var full = await engine.IndexSolutionAsync(dbPath, load);
            return new IndexRunResult { Mode = IndexMode.Full, Reason = fullReason, ChangedInputs = Describe(changes), Result = full, LoadResult = load };
        }

        var reindexed = plan!.Dirty
            .Where(kv => indexable.Any(p => PathUtil.ToRepoRelative(repoRoot, p.FilePath) == kv.Key))
            .Select(kv => new ReindexedProject(load.Projects.First(p => PathUtil.ToRepoRelative(repoRoot, p.FilePath) == kv.Key).Name, kv.Key, kv.Value))
            .OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        var unchanged = indexable
            .Where(p => !plan.Dirty.ContainsKey(PathUtil.ToRepoRelative(repoRoot, p.FilePath)))
            .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        var summary = new IndexRunResult
        {
            Mode = IndexMode.Incremental,
            Reason = $"{changes.Count} input(s) changed",
            ChangedInputs = Describe(changes),
            Reindexed = reindexed,
            Unchanged = unchanged,
            Removed = plan.Removed.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            LoadResult = load,
            WasDryRun = options.DryRun,
        };
        if (options.DryRun) return summary;

        var result = await engine.UpdateAsync(dbPath, load, new HashSet<string>(plan.Dirty.Keys), new HashSet<string>(plan.Removed));
        return new IndexRunResult
        {
            Mode = summary.Mode, Reason = summary.Reason, ChangedInputs = summary.ChangedInputs, Reindexed = summary.Reindexed,
            Unchanged = summary.Unchanged, Removed = summary.Removed, LoadResult = load, Result = result,
        };
    }

    /// <summary>Why the existing database cannot be updated, or null if it can.</summary>
    private static string? Incompatibility(IndexDatabase db, string solutionRel, string filterSignature)
    {
        if (db.GetMeta("tool_version") != IndexEngine.ToolVersion)
            return $"the index was built by tool version {db.GetMeta("tool_version") ?? "unknown"}, this is {IndexEngine.ToolVersion}";
        if (db.GetMeta("solution_path") != solutionRel)
            return "the index was built from a different solution";
        if (db.GetMeta("filter") != filterSignature)
            return "the project filter (--include/--exclude) changed";
        return null;
    }

    public sealed class IndexPlan
    {
        /// <summary>Repo-relative project file to the reason it is redone.</summary>
        public Dictionary<string, string> Dirty { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Removed { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Applies the dependency rule to the changed inputs and the current solution.</summary>
    public static IndexPlan Plan(SolutionLoadResult load, IReadOnlyCollection<InputRecord> stored, IReadOnlyCollection<ChangedInput> changes, string repoRoot)
    {
        var plan = new IndexPlan();
        var current = load.Projects.ToDictionary(p => PathUtil.ToRepoRelative(repoRoot, p.FilePath), StringComparer.OrdinalIgnoreCase);
        var storedProjects = stored.Where(i => i.Kind == InputScanner.Project).Select(i => i.Owner).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var removed in storedProjects.Where(p => !current.ContainsKey(p)))
            plan.Removed.Add(removed);

        foreach (var added in current.Keys.Where(p => !storedProjects.Contains(p)))
            plan.Dirty[added] = "added to the solution";

        foreach (var change in changes.Where(c => c.Stored.Owner.Length > 0 && current.ContainsKey(c.Stored.Owner)))
            plan.Dirty.TryAdd(change.Stored.Owner, $"{System.IO.Path.GetFileName(change.Stored.Path)} {change.Reason}");

        // The dependency rule: anything that references a redone project is redone too.
        foreach (var (path, _) in plan.Dirty.ToList())
        {
            if (!current.TryGetValue(path, out var project)) continue;
            if (!load.Dependents.TryGetValue(project.FilePath, out var dependents)) continue;
            foreach (var dependent in dependents)
            {
                var rel = PathUtil.ToRepoRelative(repoRoot, dependent);
                if (current.ContainsKey(rel))
                    plan.Dirty.TryAdd(rel, $"depends on {project.Name}");
            }
        }
        return plan;
    }
}
