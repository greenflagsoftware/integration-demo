using System.Text.RegularExpressions;

namespace CodeIndexer.Core.Loading;

/// <summary>
/// Controls which projects from a solution are included for indexing.
/// Patterns are matched against the whole project name, case-insensitively. A pattern without wildcards
/// must equal the name exactly; '*' and '?' act as wildcards (for example "*.Tests").
/// Default behaviour: skip test, snippet, benchmark, and clean-test-data projects.
/// </summary>
public sealed class ProjectFilter
{
    private static readonly string[] DefaultExcludePatterns =
    {
        "test", "tests", "*.test", "*.tests", "*.integrationtests", "*.unittests",
        "*.snippets", "*.generatedsnippets", "*.benchmarks", "*.benchmarkingtool", "*.cleantestdata",
    };

    private readonly List<Regex> _includePatterns = new();
    private readonly List<Regex> _excludePatterns = new();
    private readonly List<string> _includeText = new();
    private readonly List<string> _excludeText = new();

    /// <summary>
    /// Identifies this filter's behavior (patterns and defaults). An index records it so a later run with a different filter
    /// knows it cannot reuse the old index.
    /// </summary>
    public string Signature =>
        $"include=[{string.Join(",", _includeText.Order(StringComparer.OrdinalIgnoreCase))}];" +
        $"exclude=[{string.Join(",", _excludeText.Order(StringComparer.OrdinalIgnoreCase))}];" +
        $"defaults=[{string.Join(",", DefaultExcludePatterns)}]";
    private readonly List<Regex> _defaultExcludes = DefaultExcludePatterns.Select(ToRegex).ToList();

    /// <summary>
    /// Adds a project name pattern to explicitly include. When any include pattern exists, only matching
    /// projects are included and the default exclusions no longer apply.
    /// </summary>
    public ProjectFilter Include(string pattern)
    {
        _includePatterns.Add(ToRegex(pattern));
        _includeText.Add(pattern);
        return this;
    }

    /// <summary>
    /// Adds a project name pattern to explicitly exclude. Explicit excludes always win.
    /// </summary>
    public ProjectFilter Exclude(string pattern)
    {
        _excludePatterns.Add(ToRegex(pattern));
        _excludeText.Add(pattern);
        return this;
    }

    /// <summary>
    /// Returns true if the project should be indexed.
    /// </summary>
    public bool ShouldInclude(string projectName)
    {
        if (_includePatterns.Count > 0)
        {
            if (!_includePatterns.Any(r => r.IsMatch(projectName)))
                return false;
        }
        else if (_defaultExcludes.Any(r => r.IsMatch(projectName)))
        {
            return false;
        }

        return !_excludePatterns.Any(r => r.IsMatch(projectName));
    }

    private static Regex ToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
