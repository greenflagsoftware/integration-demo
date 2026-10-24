using Microsoft.Data.Sqlite;

namespace CodeIndexer.Core.Storage;

/// <summary>Finds which index database a command should use: an explicit path, a named solution from the catalog, or the only one.</summary>
public static class IndexLocator
{
    /// <summary>Looks for ".codeindex/catalog.db" in the current directory, the user profile, and their parents.</summary>
    public static string? FindCatalogPath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), profile, Path.Combine(profile, ".codeindex") })
        {
            var probe = start;
            while (probe is not null)
            {
                var candidate = Path.Combine(probe, ".codeindex", "catalog.db");
                if (File.Exists(candidate)) return candidate;
                var parent = Path.GetDirectoryName(probe);
                if (parent == probe) break;
                probe = parent;
            }
        }
        return null;
    }

    public static List<CatalogEntry>? LoadCatalog(out string? catalogPath)
    {
        catalogPath = FindCatalogPath();
        if (catalogPath is null) return null;
        using var conn = new SqliteConnection($"Data Source={catalogPath};Pooling=False");
        conn.Open();
        return CatalogDatabase.List(conn);
    }

    /// <summary>
    /// <paramref name="db"/> wins; then <paramref name="solution"/> by name via the catalog; with neither, the only indexed
    /// solution if there is exactly one. On failure returns null and says why in <paramref name="problem"/>.
    /// </summary>
    public static string? ResolveDbPath(string? db, string? solution, out string? problem)
    {
        problem = null;
        if (!string.IsNullOrEmpty(db))
        {
            if (File.Exists(db)) return db;
            problem = $"No index database at '{db}'. Run 'codeindex index <solution>' first.";
            return null;
        }

        var entries = LoadCatalog(out _);
        if (entries is null || entries.Count == 0)
        {
            problem = "No index database found. Run 'codeindex index <solution>' first, or pass --db.";
            return null;
        }

        if (!string.IsNullOrEmpty(solution))
        {
            var match = entries.FirstOrDefault(e => e.SolutionName.Equals(solution, StringComparison.OrdinalIgnoreCase))
                        ?? entries.FirstOrDefault(e => e.SolutionName.Contains(solution, StringComparison.OrdinalIgnoreCase));
            if (match is not null && File.Exists(match.DatabasePath)) return match.DatabasePath;
            problem = $"No indexed solution matches '{solution}'. Indexed: {string.Join(", ", entries.Select(e => e.SolutionName))}.";
            return null;
        }

        if (entries.Count == 1 && File.Exists(entries[0].DatabasePath)) return entries[0].DatabasePath;
        problem = $"Several solutions are indexed ({string.Join(", ", entries.Select(e => e.SolutionName))}); pass --solution <name> or --db <path>.";
        return null;
    }
}
