using System.Diagnostics;

namespace CodeIndexer.Tests;

internal static class Fixtures
{
    private static readonly object RestoreLock = new();

    public static readonly string CodeIndexerRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>Root of the git repository that contains the code indexer.</summary>
    public static readonly string RepoRoot = Path.GetFullPath(Path.Combine(CodeIndexerRoot, "..", ".."));

    public static string SimpleClassLib => Ensure("SimpleClassLib", "SimpleClassLib.sln");
    public static string MultiTarget => Ensure("MultiTarget", "MultiTarget.sln");
    public static string Semantics => Ensure("Semantics", "Semantics.sln");

    private static string Ensure(string folder, string solution)
    {
        var path = Path.Combine(CodeIndexerRoot, "tests", "Fixtures", folder, solution);
        lock (RestoreLock)
        {
            // obj\ is git-ignored, so a fresh clone has nothing restored; restore on first use.
            var dir = Path.GetDirectoryName(path)!;
            var restored = Directory.EnumerateFiles(dir, "project.assets.json", SearchOption.AllDirectories).Any();
            if (!restored)
            {
                using var p = Process.Start(new ProcessStartInfo("dotnet", $"restore \"{path}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                })!;
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                    throw new InvalidOperationException($"dotnet restore failed for fixture {path}");
            }
        }
        return path;
    }

    public static string TempDbPath() => Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}.db");

    public static void DeleteDb(string dbPath)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm", dbPath + ".tmp", Path.Combine(Path.GetDirectoryName(dbPath)!, "catalog.db") })
        {
            // catalog.db lives beside the database; only remove it from the temp folder when we created it.
            if (path.EndsWith("catalog.db")) continue;
            try { File.Delete(path); } catch { }
        }
    }
}
