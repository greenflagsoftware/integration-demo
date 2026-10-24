using System.Security.Cryptography;
using System.Text;
using CodeIndexer.Core.Models;
using CodeIndexer.Core.Storage;

namespace CodeIndexer.Core.Indexing;

/// <summary>
/// One thing the index depends on. <see cref="Owner"/> is the repo-relative path of the project it belongs to, or
/// empty for the solution and build-wide files.
/// </summary>
/// <param name="Kind">
/// source (a document, additional file, or analyzer config), project (the project file), assets (obj/project.assets.json,
/// i.e. restore output), listing (a fingerprint of the *.cs files under the project directory, so additions, deletions
/// and renames are noticed), solution, env (Directory.Build.props and friends, global.json, nuget.config).
/// </param>
public sealed record InputRecord(string Path, string Kind, string Owner, bool Indexed, string Hash, long Size, long ModifiedTicks);

public sealed record ChangedInput(InputRecord Stored, string Reason);

/// <summary>
/// Records and re-checks the inputs of an index. Used to skip indexing entirely when nothing changed, to decide which
/// projects an incremental update must redo, and by the query-time staleness check.
/// </summary>
public static class InputScanner
{
    public const string Source = "source", Project = "project", Assets = "assets", Listing = "listing", Solution = "solution", Env = "env";

    private static readonly string[] EnvironmentFileNames =
    {
        "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "Directory.Build.rsp",
        "global.json", "nuget.config", "NuGet.Config", "NuGet.config",
    };

    // ---- recording -----------------------------------------------------------------------------

    /// <summary>Everything one project depends on: its documents, project file, restore output, and directory listing.</summary>
    public static List<InputRecord> CollectProject(IndexedProject project, string repoRoot)
    {
        var owner = PathUtil.ToRepoRelative(repoRoot, project.FilePath);
        bool indexed = project.IncludeInIndex;
        var records = new List<InputRecord>();

        void AddFile(string? fullPath, string kind)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return;
            records.Add(FileRecord(fullPath, kind, owner, indexed, repoRoot));
        }

        AddFile(project.FilePath, Project);
        var projectDir = Path.GetDirectoryName(project.FilePath);
        if (projectDir is not null)
            AddFile(Path.Combine(projectDir, "obj", "project.assets.json"), Assets);

        if (project.RoslynProject is { } roslyn)
        {
            foreach (var doc in roslyn.Documents) AddFile(doc.FilePath, Source);
            foreach (var doc in roslyn.AdditionalDocuments) AddFile(doc.FilePath, Source);
            foreach (var doc in roslyn.AnalyzerConfigDocuments) AddFile(doc.FilePath, Source);
        }

        if (projectDir is not null && Directory.Exists(projectDir))
            records.Add(new InputRecord(PathUtil.ToRepoRelative(repoRoot, projectDir), Listing, owner, indexed, ListingHash(projectDir), 0, 0));

        // A file can be shared between projects; keep one record per (path, kind) for this owner.
        return records.GroupBy(r => (r.Path, r.Kind)).Select(g => g.First()).ToList();
    }

    /// <summary>The solution file and the build-wide files that apply to the projects (found by walking up from each project).</summary>
    public static List<InputRecord> CollectEnvironment(string solutionPath, IEnumerable<string> projectDirectories, string repoRoot)
    {
        var records = new List<InputRecord>();
        if (File.Exists(solutionPath))
            records.Add(FileRecord(solutionPath, Solution, "", true, repoRoot));
        foreach (var file in EnvironmentFiles(projectDirectories, repoRoot))
            records.Add(FileRecord(file, Env, "", true, repoRoot));
        return records;
    }

    /// <summary>Build-wide files that exist now, from each directory up to the repository root.</summary>
    public static IEnumerable<string> EnvironmentFiles(IEnumerable<string> projectDirectories, string repoRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(repoRoot).TrimEnd('\\', '/');
        foreach (var start in projectDirectories)
        {
            var dir = new DirectoryInfo(Path.GetFullPath(start));
            while (dir is not null)
            {
                foreach (var name in EnvironmentFileNames)
                {
                    var candidate = Path.Combine(dir.FullName, name);
                    if (File.Exists(candidate) && seen.Add(candidate)) yield return candidate;
                }
                if (string.Equals(dir.FullName.TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase)) break;
                dir = dir.Parent;
            }
        }
    }

    private static InputRecord FileRecord(string fullPath, string kind, string owner, bool indexed, string repoRoot)
    {
        var info = new FileInfo(fullPath);
        return new InputRecord(PathUtil.ToRepoRelative(repoRoot, fullPath), kind, owner, indexed,
            IndexDatabase.ComputeContentHash(File.ReadAllBytes(fullPath)), info.Length, info.LastWriteTimeUtc.Ticks);
    }

    /// <summary>
    /// A fingerprint of the *.cs files under a directory (excluding obj and bin): their relative paths, not their content.
    /// It changes when a file is added, deleted, or renamed.
    /// </summary>
    public static string ListingHash(string directory)
    {
        var root = Path.GetFullPath(directory);
        var names = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> subdirectories, files;
            try
            {
                subdirectories = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir, "*.cs");
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var sub in subdirectories)
            {
                var name = Path.GetFileName(sub);
                if (name.Equals("obj", StringComparison.OrdinalIgnoreCase) || name.Equals("bin", StringComparison.OrdinalIgnoreCase)) continue;
                pending.Push(sub);
            }
            foreach (var file in files)
                names.Add(PathUtil.Normalize(Path.GetRelativePath(root, file)));
        }
        names.Sort(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', names))));
    }

    // ---- checking ------------------------------------------------------------------------------

    /// <summary>
    /// Compares recorded inputs with the disk. A file whose size and modification time are unchanged is not read; one that
    /// differs is hashed and counts as changed only if its content differs. Also notices a build-wide file that appeared
    /// since indexing. Pass <paramref name="indexedOnly"/> to ignore projects the index does not cover.
    /// </summary>
    public static List<ChangedInput> FindChanges(IReadOnlyCollection<InputRecord> stored, string repoRoot, bool indexedOnly = false)
    {
        var changes = new List<ChangedInput>();
        foreach (var input in stored)
        {
            if (indexedOnly && !input.Indexed) continue;
            var full = Path.Combine(repoRoot, input.Path);

            if (input.Kind == Listing)
            {
                if (!Directory.Exists(full)) changes.Add(new ChangedInput(input, "directory deleted"));
                else if (ListingHash(full) != input.Hash) changes.Add(new ChangedInput(input, "files added, deleted, or renamed"));
                continue;
            }

            var info = new FileInfo(full);
            if (!info.Exists) { changes.Add(new ChangedInput(input, "deleted")); continue; }
            if (info.Length == input.Size && info.LastWriteTimeUtc.Ticks == input.ModifiedTicks) continue;
            if (IndexDatabase.ComputeContentHash(File.ReadAllBytes(full)) != input.Hash)
                changes.Add(new ChangedInput(input, "modified"));
        }

        // A build-wide file that did not exist at index time (a new Directory.Build.props) affects every project below it.
        var storedEnv = new HashSet<string>(stored.Where(i => i.Kind == Env).Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        var projectDirs = stored.Where(i => i.Kind == Listing).Select(i => Path.Combine(repoRoot, i.Path)).Where(Directory.Exists).ToList();
        foreach (var file in EnvironmentFiles(projectDirs, repoRoot))
        {
            var relative = PathUtil.ToRepoRelative(repoRoot, file);
            if (!storedEnv.Contains(relative))
                changes.Add(new ChangedInput(new InputRecord(relative, Env, "", true, "", 0, 0), "added"));
        }
        return changes;
    }
}
