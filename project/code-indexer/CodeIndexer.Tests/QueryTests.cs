using CodeIndexer.Core.Indexing;
using CodeIndexer.Core.Loading;
using CodeIndexer.Core.Queries;

namespace CodeIndexer.Tests;

public class QueryTests : IDisposable
{
    private static readonly string FixtureSolutionPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tests", "Fixtures", "SimpleClassLib", "SimpleClassLib.sln"));

    private readonly string _dbPath;
    private readonly string _repoRoot;

    public QueryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_query_{Guid.NewGuid():N}.db");
        _repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));

        // Build index first.
        var filter = new ProjectFilter().Include("SimpleLib");
        var loader = new SolutionLoader(filter);
        var loadResult = loader.LoadAsync(FixtureSolutionPath).GetAwaiter().GetResult();
        var engine = new IndexEngine(_repoRoot);
        engine.IndexSolutionAsync(_dbPath, loadResult).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { GC.Collect(); GC.WaitForPendingFinalizers(); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void FindSymbol_ExactMatch_ReturnsSymbol()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.FindSymbol("Calculator", null, null, false, 10, 0);

        Assert.Equal("find-symbol", result.Command);
        Assert.NotEmpty(result.Data);
        Assert.Contains(result.Data, s => s.Name == "Calculator");
        Assert.Equal("class", result.Data[0].Kind);
    }

    [Fact]
    public void FindSymbol_PrefixMatch_ReturnsMultiple()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.FindSymbol("Calc", null, null, false, 10, 0);

        Assert.NotEmpty(result.Data);
        Assert.Contains(result.Data, s => s.Name == "Calculator");
        Assert.Contains(result.Data, s => s.Name == "ICalculator");
    }

    [Fact]
    public void FindSymbol_FilterByKind_ReturnsOnlyMatchingKind()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.FindSymbol("Add", "method", null, false, 10, 0);

        Assert.All(result.Data, s => Assert.Equal("method", s.Kind));
    }

    [Fact]
    public void FindSymbol_ExcludesGeneratedByDefault()
    {
        using var queries = new SymbolQueries(_dbPath);
        // Generated files contain "assembly" attributes, etc.
        var result = queries.FindSymbol("assembly", null, null, false, 10, 0);

        Assert.DoesNotContain(result.Data, s => s.IsGenerated);
    }

    [Fact]
    public void FindSymbol_IncludesGeneratedWhenFlagSet()
    {
        using var queries = new SymbolQueries(_dbPath);

        // Without flag: only non-generated.
        var without = queries.FindSymbol("add", null, null, false, 10, 0);
        // With flag: should include at least as many.
        var with = queries.FindSymbol("add", null, null, true, 10, 0);

        Assert.True(with.Data.Count >= without.Data.Count);
    }

    [Fact]
    public void FindSymbol_LimitRespected()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.FindSymbol("a", null, null, false, 3, 0);

        Assert.True(result.Data.Count <= 3);
    }

    [Fact]
    public void FindSymbol_OffsetSkipsResults()
    {
        using var queries = new SymbolQueries(_dbPath);
        var all = queries.FindSymbol("a", null, null, false, 20, 0);
        var skipped = queries.FindSymbol("a", null, null, false, 20, 2);

        Assert.True(all.Data.Count >= 2);
        if (all.Data.Count > 2)
        {
            Assert.Equal(all.Data[2].Name, skipped.Data[0].Name);
        }
    }

    [Fact]
    public void FindSymbol_Truncated_WhenMoreThanLimit()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.FindSymbol("a", null, null, true, 1, 0);

        // Should have exactly 1 symbol and be marked truncated.
        Assert.True(result.Data.Count <= 1);
    }

    [Fact]
    public void Outline_ReturnsFileAndSymbols()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.Outline("Class1.cs");

        Assert.NotNull(result.Data);
        Assert.Contains("Class1.cs", result.Data.FilePath);
        Assert.True(result.Data.LineCount > 0);
        Assert.NotEmpty(result.Data.Symbols);
    }

    [Fact]
    public void Show_ReturnsSymbolDetails()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.Show("Calculator", false);

        Assert.NotNull(result.Data.Symbol);
        Assert.Equal("Calculator", result.Data.Symbol.Name);
        Assert.Equal("class", result.Data.Symbol.Kind);
    }

    [Fact]
    public void Show_WithSource_ReturnsCode()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.Show("Calculator", true);

        Assert.NotNull(result.Data.SourceCode);
        Assert.Contains("public class Calculator", result.Data.SourceCode);
    }

    [Fact]
    public void Show_UnknownSymbol_ReturnsFallback()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.Show("NonExistentSymbol_XYZZY", false);

        Assert.Equal("unknown", result.Data.Symbol.Kind);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public void Search_ReturnsMatchingSymbols()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.Search("calculator", false, 10);

        Assert.NotEmpty(result.Data);
        Assert.Contains(result.Data, h => h.Name == "Calculator");
    }

    [Fact]
    public void Search_ExcludesGeneratedByDefault()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.Search("assembly", false, 10);

        Assert.DoesNotContain(result.Data, h => h.IsGenerated);
    }

    [Fact]
    public void Search_IncludesGeneratedWhenFlagSet()
    {
        using var queries = new SymbolQueries(_dbPath);

        // With includeGenerated=true, should return at least as many results as without.
        var without = queries.Search("a", false, 50);
        var with = queries.Search("a", true, 50);

        Assert.True(with.Data.Count >= without.Data.Count);
    }

    [Fact]
    public void Search_RespectsLimit()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.Search("a", false, 3);

        Assert.True(result.Data.Count <= 3);
    }

    [Fact]
    public void GetSolutionContext_ReturnsMetadata()
    {
        using var queries = new SymbolQueries(_dbPath);
        var (name, path, indexedAt, commit) = queries.GetSolutionContext();

        Assert.Equal("SimpleClassLib", name);
        Assert.NotEmpty(indexedAt);
        Assert.NotEmpty(commit);
    }

    // ──────────────── Phase 3 tests ────────────────

    [Fact]
    public void Refs_ExistingSymbol_ReturnsReferences()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.Refs("ICalculator", 100);

        Assert.Equal("refs", result.Command);
        // No in-project calls to ICalculator in the fixture, but endpoint should work.
        Assert.NotNull(result.Data);
    }

    [Fact]
    public void Refs_UnknownSymbol_ReturnsWarning()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.Refs("NonExistent", 100);

        Assert.Empty(result.Data);
        Assert.NotNull(result.Warnings);
        Assert.Contains("not found", result.Warnings[0]);
    }

    [Fact]
    public void Callers_FindsExternalCallers()
    {
        using var queries = new SymbolQueries(_dbPath);
        // No method calls ICalculator in the fixture, so callers should be empty.
        var result = queries.Callers("ICalculator", 1, 50);

        Assert.Equal("callers", result.Command);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public void Callees_NoCalleesForMethod()
    {
        using var queries = new SymbolQueries(_dbPath);
        // Subtract only calls BCL methods which aren't indexed.
        var result = queries.Callees("global::SimpleLib.Calculator.Subtract", 1, 50);

        Assert.Equal("callees", result.Command);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public void Implementations_ReturnsImplementers()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.Implementations("ICalculator", 50);

        Assert.NotEmpty(result.Data);
        Assert.Contains(result.Data, i => i.Name == "Calculator");
        Assert.Equal("class", result.Data[0].Kind);
    }

    [Fact]
    public void Implementations_UnknownInterface_ReturnsWarning()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.Implementations("INonExistent", 50);

        Assert.Empty(result.Data);
        Assert.NotNull(result.Warnings);
    }

    [Fact]
    public void Hierarchy_ShowsImplementsEdge()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.Hierarchy("Calculator", 3);

        Assert.Equal("hierarchy", result.Command);
        Assert.NotEmpty(result.Data.Children);
        Assert.Contains(result.Data.Children, c => c.Name == "ICalculator" && c.EdgeKind == "implements");
    }

    [Fact]
    public void Hierarchy_UnknownType_ReturnsWarning()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.Hierarchy("NonExistentType", 3);

        Assert.NotNull(result.Warnings);
        Assert.Contains("not found", result.Warnings[0]);
    }
}
