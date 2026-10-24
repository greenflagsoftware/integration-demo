using CodeIndexer.Core.Indexing;
using CodeIndexer.Core.Loading;
using CodeIndexer.Core.Queries;

namespace CodeIndexer.Tests;

/// <summary>
/// Smoke tests of the query layer against the small SimpleClassLib fixture (one interface, three classes).
/// The behavior of each command is asserted in depth against the Semantics fixture in QueryCommandTests.
/// </summary>
public class QueryTests : IDisposable
{
    private readonly string _dbPath;

    public QueryTests()
    {
        _dbPath = Fixtures.TempDbPath();
        var loader = new SolutionLoader(new ProjectFilter().Include("SimpleLib"));
        var loadResult = loader.LoadAsync(Fixtures.SimpleClassLib).GetAwaiter().GetResult();
        new IndexEngine(Fixtures.RepoRoot).IndexSolutionAsync(_dbPath, loadResult).GetAwaiter().GetResult();
    }

    public void Dispose() => Fixtures.DeleteDb(_dbPath);

    private static SymbolSelector Name(string text) => new(Text: text);

    [Fact]
    public void FindSymbol_ExactMatch_ComesFirst()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.FindSymbol("Calculator", new FindOptions(Limit: 10));

        Assert.Equal("find-symbol", result.Command);
        Assert.Equal("Calculator", result.Data[0].Name);
        Assert.Equal("class", result.Data[0].Kind);
        Assert.Contains(result.Data, s => s.Name == "ICalculator");
    }

    [Fact]
    public void FindSymbol_Prefix_FindsBothTheClassAndTheInterface()
    {
        using var queries = new SymbolQueries(_dbPath);
        var names = queries.FindSymbol("Calc").Data.Select(s => s.Name).ToList();

        Assert.Contains("Calculator", names);
        Assert.Contains("ICalculator", names);
    }

    [Fact]
    public void FindSymbol_FilterByKind_ReturnsOnlyThatKind()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.FindSymbol("Add", new FindOptions(Kind: "method"));

        Assert.Equal(2, result.Data.Count);
        Assert.All(result.Data, s => Assert.Equal("method", s.Kind));
    }

    [Fact]
    public void Outline_File_NestsMembersUnderTheirTypes()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.Outline(Name("Class1.cs"));

        Assert.EndsWith("Class1.cs", result.Data.FilePath);
        Assert.True(result.Data.LineCount > 0);
        var calculator = Flatten(result.Data.Symbols).Single(n => n.Name == "Calculator");
        Assert.Equal(new[] { "Add", "MultiplyAsync", "Subtract" }, calculator.Children.Select(c => c.Name));
    }

    [Fact]
    public void Show_ReturnsTheSymbol_AndItsSourceOnRequest()
    {
        using var queries = new SymbolQueries(_dbPath);

        var plain = queries.Show(Name("Calculator"));
        Assert.Equal("class", plain.Data.Symbol.Kind);
        Assert.Null(plain.Data.SourceCode);

        var withSource = queries.Show(Name("Calculator"), includeSource: true);
        Assert.Contains("public class Calculator", withSource.Data.SourceCode);
    }

    [Fact]
    public void Show_UnknownSymbol_IsAWarning_NotAnException()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.Show(Name("NonExistentSymbol_XYZZY"));

        Assert.Equal(0, result.TotalCount);
        Assert.Contains("not found", result.Warnings![0]);
        Assert.Null(result.Target);
    }

    [Fact]
    public void Search_FindsSymbolsByFullText()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.Search("calculator");

        Assert.Contains(result.Data, h => h.Name == "Calculator");
        Assert.All(result.Data, h => Assert.False(h.IsExternal));
    }

    [Fact]
    public void GetSolutionContext_ReturnsMetadata()
    {
        using var queries = new SymbolQueries(_dbPath);
        var (name, path, indexedAt, commit) = queries.GetSolutionContext();

        Assert.Equal("SimpleClassLib", name);
        Assert.NotEmpty(indexedAt);
        Assert.Matches("^[0-9a-f]{40}$", commit);
    }

    [Fact]
    public void Implementations_OfInterface_IncludeTheClass()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.Implementations(Name("ICalculator"));

        var impl = Assert.Single(result.Data);
        Assert.Equal("Calculator", impl.Symbol.Name);
        Assert.Equal("implements", impl.Relation);
    }

    [Fact]
    public void Hierarchy_OfClass_ShowsTheInterfaceAsASupertype()
    {
        using var queries = new SymbolQueries(_dbPath);
        var result = queries.Hierarchy(Name("Calculator"));

        Assert.Contains(result.Data.Supertypes, n => n.Symbol.Name == "ICalculator" && n.Relation == "implements");
        Assert.Empty(result.Data.Subtypes);
    }

    [Fact]
    public void Refs_Callers_Callees_OnASymbolNothingCalls_AreEmptyWithoutWarnings()
    {
        using var queries = new SymbolQueries(_dbPath);

        var refs = queries.Refs(Name("Subtract"));
        var callers = queries.Callers(Name("Subtract"));
        var callees = queries.Callees(Name("Subtract"));

        Assert.Empty(refs.Data.References);
        Assert.Empty(callers.Data);
        Assert.Empty(callees.Data);
        Assert.Equal("Subtract", callers.Target!.Name);
    }

    [Fact]
    public void Callees_OfDataService_ListsTheExternalCallOnlyWhenAsked()
    {
        using var queries = new SymbolQueries(_dbPath);

        Assert.Empty(queries.Callees(Name("GetDataAsync")).Data);

        var withExternal = queries.Callees(Name("GetDataAsync"), new CallOptions(IncludeExternal: true));
        Assert.Contains(withExternal.Data, c => c.Symbol.Name == "GetAsync" && c.Symbol.IsExternal);
    }

    private static IEnumerable<OutlineNode> Flatten(IEnumerable<OutlineNode> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
}
