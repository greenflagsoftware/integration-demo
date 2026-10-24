namespace CodeIndexer.Core.Loading;

/// <summary>
/// Controls which projects from a solution are included for indexing.
/// Default behaviour: skip test projects, snippets, benchmarks, and clean-test-data projects.
/// </summary>
public sealed class ProjectFilter
{
    private readonly IReadOnlySet<string> _defaultExcludePatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "snippet", "benchmark", "benchmarkingtool", "cleantestdata",
    };

    private readonly List<string> _includePatterns = new();
    private readonly List<string> _excludePatterns = new();

    public ProjectFilter()
    {
    }

    /// <summary>
    /// Adds a project name pattern to explicitly include.
    /// </summary>
    public ProjectFilter Include(string pattern)
    {
        _includePatterns.Add(pattern);
        return this;
    }

    /// <summary>
    /// Adds a project name pattern to explicitly exclude.
    /// </summary>
    public ProjectFilter Exclude(string pattern)
    {
        _excludePatterns.Add(pattern);
        return this;
    }

    /// <summary>
    /// Returns true if the project should be indexed.
    /// </summary>
    public bool ShouldInclude(string projectName)
    {
        if (_includePatterns.Count > 0)
        {
            if (!_includePatterns.Any(p => projectName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        else
        {
            if (_defaultExcludePatterns.Any(p => projectName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        if (_excludePatterns.Any(p => projectName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }
}
