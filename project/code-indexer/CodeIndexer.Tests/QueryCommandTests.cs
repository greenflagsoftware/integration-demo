using System.Text.Json;
using CodeIndexer.Core.Queries;
using Microsoft.Data.Sqlite;

namespace CodeIndexer.Tests;

/// <summary>
/// Behavior of every query command against the Semantics fixture. Expected answers are worked out by hand from the
/// fixture source; locations come from "// @marker" comments.
/// </summary>
public abstract class QueryCommandTestBase : IClassFixture<SemanticsIndex>, IDisposable
{
    protected readonly SemanticsIndex Ix;
    protected readonly SymbolQueries Q;

    protected QueryCommandTestBase(SemanticsIndex ix)
    {
        Ix = ix;
        Q = ix.Queries();
    }

    public void Dispose() => Q.Dispose();

    protected const string AddInt = "M:Semantics.Core.Calculator.Add(System.Int32,System.Int32)";
    protected const string CircleArea = "M:Semantics.Core.Circle.Area";

    protected static SymbolSelector Name(string text, string? kind = null) => new(Text: text, Kind: kind);
    protected static SymbolSelector Doc(string docId) => new(Key: docId);

    protected static IEnumerable<OutlineNode> Flatten(IEnumerable<OutlineNode> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    protected static List<string> Docs(IEnumerable<string> keys) => keys.Select(k => k[(k.IndexOf('|') + 1)..]).ToList();
}

public class SelectorTests : QueryCommandTestBase
{
    public SelectorTests(SemanticsIndex ix) : base(ix) { }

    [Fact]
    public void UniqueName_Resolves()
    {
        var r = Q.Resolve(Name("ShapeBase"));

        Assert.True(r.IsResolved);
        Assert.Equal("class", r.Symbol!.Kind);
    }

    [Fact]
    public void AmbiguousName_ReturnsCandidates_InsteadOfGuessing()
    {
        var r = Q.Resolve(Name("Add"));

        Assert.False(r.IsResolved);
        Assert.Equal(4, r.Candidates.Count);
        Assert.Equal(4, r.Candidates.Select(c => c.Id).Distinct().Count());
        Assert.Equal(4, r.Candidates.Select(c => c.Key).Distinct().Count());
        Assert.Contains("matches 4 symbols", r.Warning);
        Assert.Contains("--id", r.Warning);
    }

    [Fact]
    public void Container_DotMember_NarrowsTheCandidates()
    {
        var overloads = Q.Resolve(Name("Calculator.Add"));
        Assert.False(overloads.IsResolved);
        Assert.Equal(3, overloads.Candidates.Count);

        var repo = Q.Resolve(Name("Repository.Add"));
        Assert.True(repo.IsResolved);
        Assert.Equal("M:Semantics.Core.Repository`1.Add(`0)", Docs(new[] { repo.Symbol!.Key }).Single());
    }

    [Fact]
    public void ExplicitImplementation_IsNotConfusedWithTheInterfaceMember()
    {
        var r = Q.Resolve(Name("IShape.Area"));

        Assert.True(r.IsResolved);
        Assert.Equal("M:Semantics.Core.IShape.Area", Docs(new[] { r.Symbol!.Key }).Single());
    }

    [Fact]
    public void GenericArguments_AreIgnoredInTheName()
    {
        var r = Q.Resolve(Name("Repository<T>"));

        Assert.True(r.IsResolved);
        Assert.Equal("class", r.Symbol!.Kind);
    }

    [Fact]
    public void ById_ByKey_AndByDocumentationId_AllResolveToTheSameSymbol()
    {
        var byDoc = Q.Resolve(Doc(AddInt)).Symbol!;
        var byId = Q.Resolve(new SymbolSelector(Id: byDoc.Id)).Symbol!;
        var byKey = Q.Resolve(new SymbolSelector(Key: byDoc.Key)).Symbol!;
        var byPasted = Q.Resolve(Name(AddInt)).Symbol!;

        Assert.Equal(byDoc.Id, byId.Id);
        Assert.Equal(byDoc.Id, byKey.Id);
        Assert.Equal(byDoc.Id, byPasted.Id);
        Assert.Equal("Semantics.Core|" + AddInt, byDoc.Key);
    }

    [Fact]
    public void ByPosition_ResolvesTheSymbolUsedThere()
    {
        var line = Ix.Line("Consumer.cs", "app-add-int");
        var column = Ix.Column("Consumer.cs", "app-add-int", "Add(");

        var r = Q.Resolve(Name($"Consumer.cs:{line}:{column}"));

        Assert.True(r.IsResolved);
        Assert.Equal(Q.Resolve(Doc(AddInt)).Symbol!.Id, r.Symbol!.Id);
    }

    [Fact]
    public void UnknownName_IsNotFound()
    {
        var r = Q.Resolve(Name("NoSuchSymbol_XYZZY"));

        Assert.False(r.IsResolved);
        Assert.Empty(r.Candidates);
        Assert.Contains("not found", r.Warning);
    }

    [Fact]
    public void UnknownId_IsNotFound() =>
        Assert.Contains("No symbol with id", Q.Resolve(new SymbolSelector(Id: 99999)).Warning);

    [Fact]
    public void EmptySelector_SaysWhatToPass() =>
        Assert.Contains("--id", Q.Resolve(new SymbolSelector()).Warning);

    [Fact]
    public void Kind_NarrowsTheMatches()
    {
        Assert.True(Q.Resolve(Name("Changed", "event")).IsResolved);
        Assert.False(Q.Resolve(Name("Changed", "field")).IsResolved);
    }

    [Fact]
    public void ExternalSymbols_AreFoundOnlyWhenNothingIndexedMatches()
    {
        var r = Q.Resolve(Name("HttpClient.GetStringAsync"));

        Assert.True(r.IsResolved);
        Assert.True(r.Symbol!.IsExternal);
        Assert.Equal("method", r.Symbol.Kind);
    }

    [Fact]
    public void Namespace_ResolvesWhenNothingElseMatches()
    {
        var r = Q.Resolve(Name("Semantics.Core"));

        Assert.True(r.IsResolved);
        Assert.Equal("namespace", r.Symbol!.Kind);
    }

    [Fact]
    public void GeneratedOnlyMatch_StillResolves() =>
        Assert.True(Q.Resolve(Name("FromGenerated")).IsResolved);

    [Theory]
    [InlineData("src/File.cs:12", "src/File.cs", 12, null)]
    [InlineData("src/File.cs:12:5", "src/File.cs", 12, 5)]
    [InlineData(@"E:\Dev\x\File.cs:7:3", "E:/Dev/x/File.cs", 7, 3)]
    public void PositionSelectors_AreParsed(string text, string file, int line, int? column)
    {
        Assert.True(new SymbolSelector(Text: text).TryGetPosition(out var f, out var l, out var c));
        Assert.Equal((file, line, column), (f, l, c));
    }

    [Theory]
    [InlineData("Calculator")]
    [InlineData("Calculator.Add")]
    [InlineData("E:Dev")]
    public void NamesAreNotPositions(string text) =>
        Assert.False(new SymbolSelector(Text: text).TryGetPosition(out _, out _, out _));
}

public class FindAndSearchTests : QueryCommandTestBase
{
    public FindAndSearchTests(SemanticsIndex ix) : base(ix) { }

    [Fact]
    public void Results_CarryIdAndKey()
    {
        var hit = Q.FindSymbol("ShapeBase").Data[0];

        Assert.True(hit.Id > 0);
        Assert.Equal("Semantics.Core|T:Semantics.Core.ShapeBase", hit.Key);
    }

    [Fact]
    public void GeneratedAndExternalSymbols_AreHiddenUnlessAsked()
    {
        Assert.Empty(Q.FindSymbol("FromGenerated").Data);
        var generated = Q.FindSymbol("FromGenerated", new FindOptions(IncludeGenerated: true)).Data;
        Assert.True(Assert.Single(generated).IsGenerated);

        Assert.Empty(Q.FindSymbol("GetStringAsync").Data);
        var external = Q.FindSymbol("GetStringAsync", new FindOptions(IncludeExternal: true)).Data;
        Assert.True(Assert.Single(external).IsExternal);
    }

    [Fact]
    public void LimitOffsetAndTotalMatches_AreConsistent()
    {
        var all = Q.FindSymbol("Add", new FindOptions(Kind: "method", Limit: 10));
        var firstPage = Q.FindSymbol("Add", new FindOptions(Kind: "method", Limit: 2));
        var secondPage = Q.FindSymbol("Add", new FindOptions(Kind: "method", Limit: 2, Offset: 2));

        Assert.Equal(4, all.TotalMatches);
        Assert.False(all.Truncated);
        Assert.Equal(2, firstPage.Data.Count);
        Assert.True(firstPage.Truncated);
        Assert.Equal(4, firstPage.TotalMatches);
        Assert.False(secondPage.Truncated);
        Assert.Equal(all.Data.Select(d => d.Id), firstPage.Data.Concat(secondPage.Data).Select(d => d.Id));
    }

    [Fact]
    public void ProjectFilter_RestrictsToThatProject()
    {
        var app = Q.FindSymbol("Run", new FindOptions(Project: "Semantics.App")).Data;

        Assert.NotEmpty(app);
        Assert.All(app, s => Assert.Equal("Semantics.App", s.ProjectName));
    }

    [Fact]
    public void InvalidFullTextQuery_IsAWarning_NotAnException()
    {
        var r = Q.FindSymbol("\"unterminated phrase");

        Assert.Empty(r.Data);
        Assert.Contains("Invalid full-text query", r.Warnings![0]);
    }

    [Fact]
    public void Search_RanksAndExcludesExternalByDefault()
    {
        var r = Q.Search("calculator");

        Assert.Contains(r.Data, h => h.Name == "Calculator");
        Assert.All(r.Data, h => Assert.False(h.IsExternal));
        Assert.True(r.Data.Select(h => h.Rank).SequenceEqual(r.Data.Select(h => h.Rank).OrderBy(x => x)));
    }
}

public class DefinitionAndShowTests : QueryCommandTestBase
{
    public DefinitionAndShowTests(SemanticsIndex ix) : base(ix) { }

    [Fact]
    public void Definition_OfPartialType_HasOneLocationPerPart_HandWrittenFirst()
    {
        var r = Q.Definition(Name("Widget"));

        Assert.Equal(2, r.Data.Locations.Count);
        Assert.EndsWith("/Widget.cs", r.Data.Locations[0].FilePath);
        Assert.True(r.Data.Locations[0].IsPrimary);
        Assert.EndsWith("/Widget.Generated.cs", r.Data.Locations[1].FilePath);
        Assert.True(r.Data.Locations[1].IsGenerated);
        Assert.Equal(r.Data.Symbol.Id, r.Target!.Id);
    }

    [Fact]
    public void Definition_OfExternalSymbol_ExplainsThereIsNoSource()
    {
        var r = Q.Definition(Name("HttpClient.GetStringAsync"));

        Assert.Empty(r.Data.Locations);
        Assert.Contains("outside the indexed projects", r.Warnings![0]);
    }

    [Fact]
    public void Definition_Ambiguous_ReturnsCandidates()
    {
        var r = Q.Definition(Name("Add"));

        Assert.Equal(4, r.Candidates!.Count);
        Assert.Null(r.Target);
    }

    [Fact]
    public void Show_WithSource_ReturnsTheDeclaration()
    {
        var r = Q.Show(Doc(AddInt), includeSource: true);

        Assert.Equal("public int Add(int a, int b) => a + b; // @add-int", r.Data.SourceCode!.Trim());
        Assert.False(r.Data.SourceTruncated);
    }

    [Fact]
    public void Show_TruncatesLongSource()
    {
        var r = Q.Show(Name("ShapeUser"), includeSource: true, maxSourceLines: 3);

        Assert.Equal(3, r.Data.SourceCode!.Split('\n').Length);
        Assert.True(r.Data.SourceTruncated);
        Assert.Contains(r.Warnings!, w => w.Contains("truncated"));
    }
}

public class OutlineTests : QueryCommandTestBase
{
    public OutlineTests(SemanticsIndex ix) : base(ix) { }

    [Fact]
    public void FileOutline_NestsMembersUnderTypesUnderNamespaces()
    {
        var r = Q.Outline(Name("Calculator.cs"));

        Assert.EndsWith("/Calculator.cs", r.Data.FilePath);
        var calculator = Flatten(r.Data.Symbols).Single(n => n.Name == "Calculator");
        Assert.Equal(new[] { "Add", "Add", "Add", "Echo", "Total" }, calculator.Children.Select(c => c.Name));
        var namespaces = Flatten(r.Data.Symbols).Where(n => n.Kind == "namespace").Select(n => n.FullName).ToList();
        Assert.Contains("global::Semantics.Core", namespaces);
    }

    [Fact]
    public void FileOutline_OfAPartialPart_ListsOnlyThatPartsMembers()
    {
        var r = Q.Outline(Name("Widget.Generated.cs"));

        var widget = Flatten(r.Data.Symbols).Single(n => n.Name == "Widget");
        Assert.Equal(new[] { "FromGenerated" }, widget.Children.Select(c => c.Name));
        Assert.True(r.Data.IsGenerated);
    }

    [Fact]
    public void TypeOutline_ListsMembersAndNestedTypes()
    {
        var r = Q.Outline(Name("Repository"));

        var root = Assert.Single(r.Data.Symbols);
        Assert.Equal(new[] { "_items", "Add", "Find", "Cursor" }, root.Children.Select(c => c.Name));
        Assert.Equal(new[] { "Position" }, root.Children.Single(c => c.Name == "Cursor").Children.Select(c => c.Name));
        Assert.Equal(root.Id, r.Target!.Id);
    }

    [Fact]
    public void AFileNameSharedByTwoFiles_ListsTheMatches()
    {
        // Both projects have an obj\...\.NETCoreApp,Version=v10.0.AssemblyAttributes.cs.
        var r = Q.Outline(Name(".NETCoreApp,Version=v10.0.AssemblyAttributes.cs"));

        Assert.Empty(r.Data.Symbols);
        Assert.Contains("matches 2 files", r.Warnings![0]);
    }
}

public class AtTests : QueryCommandTestBase
{
    public AtTests(SemanticsIndex ix) : base(ix) { }

    [Fact]
    public void OnAUse_ReturnsTheSymbolUsed_WithKindAndEnclosingMember()
    {
        var line = Ix.Line("Consumer.cs", "app-add-int");
        var column = Ix.Column("Consumer.cs", "app-add-int", "Add(");

        var r = Q.At("Consumer.cs", line, column);

        Assert.Equal("reference", r.Data.Match);
        Assert.Equal("Semantics.Core|" + AddInt, r.Data.Symbol.Key);
        Assert.Equal("call", r.Data.ReferenceKind);
        Assert.Equal("Run", r.Data.Enclosing!.Name);
        Assert.Equal(r.Data.Symbol.Id, r.Target!.Id);
    }

    [Fact]
    public void OnTheReceiverOfThatSameCall_ReturnsTheField()
    {
        var line = Ix.Line("Consumer.cs", "app-add-int");
        var column = Ix.Column("Consumer.cs", "app-add-int", "_calc");

        var r = Q.At("Consumer.cs", line, column);

        Assert.Equal("_calc", r.Data.Symbol.Name);
        Assert.Equal("read", r.Data.ReferenceKind);
    }

    [Fact]
    public void LineWithSeveralUses_WithoutAColumn_ReturnsCandidates()
    {
        var r = Q.At("Consumer.cs", Ix.Line("Consumer.cs", "app-add-int"));

        Assert.Null(r.Target);
        Assert.Equal(new[] { "_calc", "Add" }, r.Candidates!.Select(c => c.Name));
        Assert.Contains("add a column", r.Warnings![0]);
    }

    [Fact]
    public void OnADeclarationLine_ReturnsThatDeclaration()
    {
        var line = Ix.Line("Calculator.cs", "add-double");

        var r = Q.At("Calculator.cs", line, 25);

        Assert.Equal("declaration", r.Data.Match);
        Assert.Equal("M:Semantics.Core.Calculator.Add(System.Double,System.Double)", Docs(new[] { r.Data.Symbol.Key }).Single());
    }

    [Fact]
    public void InsideABody_ReturnsTheContainingMember()
    {
        var line = Ix.Line("Calculator.cs", "total") + 2; // "int sum = 0;" inside Total

        var r = Q.At("Calculator.cs", line);

        Assert.Equal("containing", r.Data.Match);
        Assert.Equal("Total", r.Data.Symbol.Name);
    }

    [Fact]
    public void UnknownFile_SaysSo()
    {
        var r = Q.At("Nope.cs", 1);

        Assert.Null(r.Target);
        Assert.Contains("File not indexed", r.Warnings![0]);
    }

    [Fact]
    public void LineWithNothing_IsNotFound()
    {
        var r = Q.At("Calculator.cs", 1);

        Assert.Null(r.Target);
        Assert.Contains("Nothing indexed", r.Warnings![0]);
    }
}

public class RefsTests : QueryCommandTestBase
{
    public RefsTests(SemanticsIndex ix) : base(ix) { }

    [Fact]
    public void AddInt_HasTheFiveMarkedCallSites_WithEnclosingMembers()
    {
        var r = Q.Refs(Doc(AddInt));

        Assert.Equal(5, r.Data.References.Count);
        Assert.Equal(5, r.TotalMatches);
        Assert.All(r.Data.References, x => Assert.Equal("call", x.Kind));
        var lines = r.Data.References.Select(x => $"{Path.GetFileName(x.FilePath)}:{x.Line}").ToHashSet();
        Assert.Contains(Ix.At("Calculator.cs", "twice"), lines);
        Assert.Contains(Ix.At("Misc.cs", "local-add-int"), lines);
        Assert.Contains(r.Data.References, x => x.Enclosing!.Name == "Run");
    }

    [Fact]
    public void ReferenceKindFilter_NarrowsToWrites()
    {
        var r = Q.Refs(Name("Account.Balance"), new RefsOptions(Kinds: new[] { "write" }));

        Assert.Equal(2, r.Data.References.Count);
        Assert.All(r.Data.References, x => Assert.Equal("write", x.Kind));
    }

    [Fact]
    public void FileFilter_NarrowsToThatFile()
    {
        var r = Q.Refs(Doc(AddInt), new RefsOptions(File: "Misc.cs"));

        Assert.Equal(2, r.Data.References.Count);
        Assert.All(r.Data.References, x => Assert.EndsWith("Misc.cs", x.FilePath));
    }

    [Fact]
    public void Context_ReturnsTheSourceLine()
    {
        var r = Q.Refs(Doc(AddInt), new RefsOptions(Context: true, File: "Consumer.cs"));

        var only = Assert.Single(r.Data.References);
        Assert.Contains("_calc.Add(1, 2)", only.Context);
    }

    [Fact]
    public void GroupByFile_AndByMember_PartitionTheSameReferences()
    {
        var flat = Q.Refs(Doc(AddInt)).Data.References.Count;
        var byFile = Q.Refs(Doc(AddInt), new RefsOptions(GroupBy: "file")).Data;
        var byMember = Q.Refs(Doc(AddInt), new RefsOptions(GroupBy: "member")).Data;

        Assert.Empty(byFile.References);
        Assert.Equal(flat, byFile.Groups!.Sum(g => g.Count));
        Assert.Equal(flat, byMember.Groups!.Sum(g => g.References.Count));
        Assert.Contains(byMember.Groups!, g => g.Group.EndsWith("Misc.UseLambda") && g.Count == 2);
        Assert.Equal(3, byFile.Groups!.Count); // Calculator.cs, Misc.cs, Consumer.cs
    }

    [Fact]
    public void LimitAndOffset_PageThroughTheReferences()
    {
        var page1 = Q.Refs(Doc(AddInt), new RefsOptions(Limit: 2));
        var page2 = Q.Refs(Doc(AddInt), new RefsOptions(Limit: 2, Offset: 2));

        Assert.True(page1.Truncated);
        Assert.Equal(5, page1.TotalMatches);
        Assert.Equal(2, page2.Data.References.Count);
        Assert.DoesNotContain(page2.Data.References, x => page1.Data.References.Any(y => y.Line == x.Line && y.FilePath == x.FilePath && y.Column == x.Column));
    }

    [Fact]
    public void ReferencesInGeneratedFiles_AreHiddenWithACount()
    {
        var attribute = Doc("T:System.Runtime.Versioning.TargetFrameworkAttribute");

        var hidden = Q.Refs(attribute);
        Assert.Empty(hidden.Data.References);
        Assert.Contains(hidden.Warnings!, w => w.Contains("generated files were excluded"));

        var shown = Q.Refs(attribute, new RefsOptions(IncludeGenerated: true));
        Assert.NotEmpty(shown.Data.References);
        Assert.All(shown.Data.References, x => Assert.True(x.IsGenerated));
    }

    [Fact]
    public void AmbiguousSymbol_ReturnsCandidates_AndNoReferences()
    {
        var r = Q.Refs(Name("Add"));

        Assert.Empty(r.Data.References);
        Assert.Equal(4, r.Candidates!.Count);
    }

    [Fact]
    public void TypeReferences_IncludeEveryKindOfUse()
    {
        var r = Q.Refs(Doc("T:Semantics.Core.Calculator"));

        var kinds = r.Data.References.Select(x => x.Kind).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(new[] { "create", "nameof", "type-use" }, kinds);
    }
}

public class CallHierarchyTests : QueryCommandTestBase
{
    public CallHierarchyTests(SemanticsIndex ix) : base(ix) { }

    private static List<string> Names(QueryResponse<List<CallNode>> r) => r.Data.Select(n => n.Symbol.Name).ToList();

    [Fact]
    public void Callers_AreTheFourMembers_WithCallSites()
    {
        var r = Q.Callers(Doc(AddInt));

        Assert.Equal(new[] { "Run", "Total", "Twice", "UseLambda" }, Names(r).OrderBy(x => x));
        Assert.All(r.Data, n => Assert.Equal(1, n.Depth));
        var lambda = r.Data.Single(n => n.Symbol.Name == "UseLambda");
        Assert.Equal(2, lambda.CallSiteCount);
        Assert.Equal(
            new[] { Ix.Line("Misc.cs", "lambda-add-int"), Ix.Line("Misc.cs", "local-add-int") },
            lambda.CallSites.Select(s => s.Line).OrderBy(x => x));
        var twice = r.Data.Single(n => n.Symbol.Name == "Twice");
        Assert.Equal(Ix.Line("Calculator.cs", "twice"), Assert.Single(twice.CallSites).Line);
    }

    [Fact]
    public void Callers_WithDepth_FollowTheChainUpward()
    {
        var r = Q.Callers(Doc(AddInt), new CallOptions(Depth: 2));

        var runTwice = r.Data.Single(n => n.Symbol.Name == "RunTwice");
        Assert.Equal(2, runTwice.Depth);
        Assert.Equal(4, r.Data.Count(n => n.Depth == 1));
    }

    [Fact]
    public void DepthZero_MeansUnlimited_AndTerminates()
    {
        var unlimited = Q.Callers(Doc(AddInt), new CallOptions(Depth: 0));
        var deep = Q.Callers(Doc(AddInt), new CallOptions(Depth: 5));

        Assert.Equal(Names(deep).OrderBy(x => x), Names(unlimited).OrderBy(x => x));
        Assert.Contains("RunTwice", Names(unlimited));
    }

    [Fact]
    public void Callers_AreTruncatedAtTheLimit()
    {
        var r = Q.Callers(Doc(AddInt), new CallOptions(Limit: 2));

        Assert.Equal(2, r.Data.Count);
        Assert.True(r.Truncated);
    }

    [Fact]
    public void WithoutDispatch_AnOverrideSeesOnlyDirectCalls()
    {
        var r = Q.Callers(Doc(CircleArea));

        Assert.Equal(new[] { "CircleArea" }, Names(r));
    }

    [Fact]
    public void ThroughDispatch_AnOverrideAlsoSeesCallsMadeViaTheBaseAndInterface()
    {
        var r = Q.Callers(Doc(CircleArea), new CallOptions(Depth: 3, ThroughDispatch: true));

        var byName = r.Data.ToDictionary(n => n.Symbol.Name);
        Assert.Equal(new[] { "CircleArea", "Describe", "Label", "SumAreas" }, byName.Keys.OrderBy(x => x));
        Assert.Null(byName["CircleArea"].Via);
        Assert.Equal("M:Semantics.Core.ShapeBase.Area", Docs(new[] { byName["Describe"].Via!.Key }).Single());
        Assert.Equal("M:Semantics.Core.IShape.Area", Docs(new[] { byName["SumAreas"].Via!.Key }).Single());
        Assert.Equal(1, byName["Describe"].Depth);
        Assert.Equal(2, byName["Label"].Depth);
        Assert.Equal(Ix.Line("Shapes.cs", "call-ishape-area"), Assert.Single(byName["SumAreas"].CallSites).Line);
    }

    [Fact]
    public void Callees_ListWhatTheMemberCalls()
    {
        var r = Q.Callees(Name("Describe"));

        var only = Assert.Single(r.Data);
        Assert.Equal("Area", only.Symbol.Name);
        Assert.Equal("ShapeBase", Q.Resolve(new SymbolSelector(Id: only.Symbol.Id)).Symbol!.FullName!.Split('.')[^2]);
    }

    [Fact]
    public void Callees_ThroughDispatch_ListEveryImplementation()
    {
        var r = Q.Callees(Name("SumAreas"), new CallOptions(ThroughDispatch: true));

        Assert.Equal(5, r.Data.Count);
        Assert.Null(r.Data.First(n => n.Symbol.Key.EndsWith("M:Semantics.Core.IShape.Area")).Via);
        var impls = r.Data.Where(n => n.Via is not null).ToList();
        Assert.Equal(4, impls.Count);
        Assert.All(impls, n => Assert.EndsWith("M:Semantics.Core.IShape.Area", n.Via!.Key));
        Assert.Contains(impls, n => n.Symbol.Key.EndsWith(CircleArea));
    }

    [Fact]
    public void ExternalCallees_AreHiddenUnlessAsked()
    {
        Assert.Empty(Q.Callees(Name("Fetch")).Data);

        var r = Q.Callees(Name("Fetch"), new CallOptions(IncludeExternal: true));
        var call = Assert.Single(r.Data);
        Assert.True(call.Symbol.IsExternal);
        Assert.Equal("GetStringAsync", call.Symbol.Name);
        Assert.Equal(Ix.Line("Consumer.cs", "app-http"), Assert.Single(call.CallSites).Line);
    }

    [Fact]
    public void CallersOfAnExternalSymbol_AreIndexedMembers()
    {
        var r = Q.Callers(Name("HttpClient.GetStringAsync"));

        Assert.Equal(new[] { "Fetch" }, Names(r));
    }

    [Fact]
    public void ConstructorChain_IsACall()
    {
        var r = Q.Callers(Doc("M:Semantics.Core.Account.#ctor(System.Decimal)"));

        Assert.Equal(new[] { ".ctor" }, Names(r));
        Assert.Equal(Ix.Line("Account.cs", "ctor-default"), Assert.Single(r.Data[0].CallSites).Line);
    }

    [Fact]
    public void CreationCallers_NeedIncludeCreates()
    {
        var without = Q.Callers(Name("Calculator", "class"));
        Assert.Empty(without.Data);
        Assert.Contains("--include-creates", without.Warnings![0]);

        var with = Q.Callers(Name("Calculator", "class"), new CallOptions(IncludeCreates: true));
        Assert.Equal(new[] { "Make", "_calc" }, Names(with).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void PropertyCallers_PointToRefs()
    {
        var r = Q.Callers(Name("Account.Balance"));

        Assert.Empty(r.Data);
        Assert.Contains("refs", r.Warnings![0]);
    }

    [Fact]
    public void AmbiguousSelector_ReturnsCandidates()
    {
        var r = Q.Callers(Name("Add"));

        Assert.Empty(r.Data);
        Assert.Equal(4, r.Candidates!.Count);
    }
}

public class ImplementationAndHierarchyTests : QueryCommandTestBase
{
    public ImplementationAndHierarchyTests(SemanticsIndex ix) : base(ix) { }

    [Fact]
    public void InterfaceImplementations_AreDirectByDefault_AndTransitiveOnRequest()
    {
        var direct = Q.Implementations(Name("IShape"));
        var all = Q.Implementations(Name("IShape"), transitive: true);

        Assert.Equal(new[] { "ShapeBase", "Triangle" }, direct.Data.Select(i => i.Symbol.Name).OrderBy(x => x));
        Assert.All(direct.Data, i => Assert.Equal("implements", i.Relation));
        var transitive = all.Data.Where(i => i.Depth == 2).ToList();
        Assert.Equal(new[] { "Circle", "Square" }, transitive.Select(i => i.Symbol.Name).OrderBy(x => x));
        Assert.All(transitive, i => Assert.Equal("inherits", i.Relation));
    }

    [Fact]
    public void MemberImplementations_FollowOverridesTransitively()
    {
        var direct = Q.Implementations(Name("IShape.Area"));
        var all = Q.Implementations(Name("IShape.Area"), transitive: true);

        Assert.Equal(2, direct.Data.Count);
        Assert.All(direct.Data, i => Assert.Equal("implements", i.Relation));
        Assert.Equal(4, all.Data.Count);
        Assert.Equal(new[] { "overrides", "overrides" }, all.Data.Where(i => i.Depth == 2).Select(i => i.Relation));
    }

    [Fact]
    public void Overrides_AreFoundFromTheOverriddenMember()
    {
        var r = Q.Implementations(Doc("M:Semantics.Core.ShapeBase.Area"));

        Assert.Equal(new[] { "Circle.Area", "Square.Area" },
            r.Data.Select(i => i.Symbol.FullName!.Replace("global::Semantics.Core.", "")).OrderBy(x => x));
        Assert.All(r.Data, i => Assert.Equal("overrides", i.Relation));
    }

    [Fact]
    public void ImplementationsOfAField_IsAWarning()
    {
        var r = Q.Implementations(Name("_items"));

        Assert.Empty(r.Data);
        Assert.Contains("applies to types", r.Warnings![0]);
    }

    [Fact]
    public void Hierarchy_OfADerivedClass_WalksUpThroughTheInterface()
    {
        var r = Q.Hierarchy(Name("Circle"));

        var baseClass = Assert.Single(r.Data.Supertypes);
        Assert.Equal(("ShapeBase", "inherits"), (baseClass.Symbol.Name, baseClass.Relation));
        var iface = Assert.Single(baseClass.Children);
        Assert.Equal(("IShape", "implements"), (iface.Symbol.Name, iface.Relation));
        Assert.Empty(r.Data.Subtypes);
    }

    [Fact]
    public void Hierarchy_OfAnInterface_WalksDown()
    {
        var r = Q.Hierarchy(Name("IShape"), direction: "down");

        Assert.Empty(r.Data.Supertypes);
        Assert.Equal(new[] { "ShapeBase", "Triangle" }, r.Data.Subtypes.Select(n => n.Symbol.Name).OrderBy(x => x));
        var shapeBase = r.Data.Subtypes.Single(n => n.Symbol.Name == "ShapeBase");
        Assert.Equal(new[] { "Circle", "Square" }, shapeBase.Children.Select(n => n.Symbol.Name).OrderBy(x => x));
    }

    [Fact]
    public void Hierarchy_DepthLimitsTheWalk()
    {
        var r = Q.Hierarchy(Name("IShape"), direction: "down", depth: 1);

        Assert.All(r.Data.Subtypes, n => Assert.Empty(n.Children));
    }

    [Fact]
    public void Hierarchy_ShowsExternalSupertypes_FlaggedAsExternal()
    {
        var r = Q.Hierarchy(Name("Person"), direction: "up");

        Assert.Contains(r.Data.Supertypes, n => n.Symbol.Name == "IEquatable" && n.Symbol.IsExternal);
    }

    [Fact]
    public void Hierarchy_OfAMethod_IsAWarning()
    {
        var r = Q.Hierarchy(Doc(AddInt));

        Assert.Contains("applies to types", r.Warnings![0]);
    }
}

public class StalenessTests : QueryCommandTestBase
{
    public StalenessTests(SemanticsIndex ix) : base(ix) { }

    private string CopyDatabase()
    {
        var copy = Fixtures.TempDbPath();
        File.Copy(Ix.DbPath, copy);
        return copy;
    }

    private static void Execute(string db, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void FreshIndex_IsNotStale()
    {
        var r = Q.FindSymbol("Calculator");

        Assert.False(r.Stale);
        Assert.Null(r.StaleFiles);
        Assert.Null(r.Warnings);
    }

    [Fact]
    public void ChangedFile_MarksEveryResponseStale_AndNamesTheFile()
    {
        var db = CopyDatabase();
        try
        {
            Execute(db, "UPDATE files SET content_hash = 'deadbeef', size_bytes = size_bytes + 1 WHERE path LIKE '%/Calculator.cs'");
            using var q = new SymbolQueries(db);

            var r = q.FindSymbol("Calculator");

            Assert.True(r.Stale);
            Assert.EndsWith("/Calculator.cs", Assert.Single(r.StaleFiles!));
            Assert.StartsWith("Index is stale", r.Warnings![0]);
            Assert.True(q.Refs(Name("Calculator")).Stale);
        }
        finally { Fixtures.DeleteDb(db); }
    }

    [Fact]
    public void TouchedButUnchangedFile_IsNotStale()
    {
        var db = CopyDatabase();
        try
        {
            // A different modification time with the same content must be confirmed by hash and found fresh.
            Execute(db, "UPDATE files SET modified_ticks = 1");
            using var q = new SymbolQueries(db);

            Assert.False(q.FindSymbol("Calculator").Stale);
        }
        finally { Fixtures.DeleteDb(db); }
    }

    [Fact]
    public void MissingFile_MarksTheIndexStale()
    {
        var db = CopyDatabase();
        try
        {
            Execute(db, "UPDATE files SET path = 'gone/Misc.cs' WHERE path LIKE '%/Misc.cs'");
            using var q = new SymbolQueries(db);

            var r = q.FindSymbol("Calculator");

            Assert.True(r.Stale);
            Assert.Contains("gone/Misc.cs", r.StaleFiles!);
        }
        finally { Fixtures.DeleteDb(db); }
    }
}

public class OutputFormatTests : QueryCommandTestBase
{
    public OutputFormatTests(SemanticsIndex ix) : base(ix) { }

    [Fact]
    public void Json_IsSnakeCase_WithTargetAndCandidatesOnlyWhenRelevant()
    {
        using var resolved = JsonDocument.Parse(Q.Refs(Doc(AddInt)).ToJson());
        var root = resolved.RootElement;

        Assert.Equal("refs", root.GetProperty("command").GetString());
        Assert.True(root.TryGetProperty("target", out var target));
        Assert.Equal("Add", target.GetProperty("name").GetString());
        Assert.True(root.TryGetProperty("total_matches", out _));
        Assert.False(root.TryGetProperty("candidates", out _));
        Assert.False(root.GetProperty("stale").GetBoolean());

        using var ambiguous = JsonDocument.Parse(Q.Refs(Name("Add")).ToJson());
        Assert.False(ambiguous.RootElement.TryGetProperty("target", out _));
        Assert.Equal(4, ambiguous.RootElement.GetProperty("candidates").GetArrayLength());
    }

    [Fact]
    public void Table_ShowsAHeaderTargetAndRows()
    {
        var table = Q.Refs(Doc(AddInt)).ToTable();

        Assert.StartsWith("refs - Semantics @ ", table);
        Assert.Contains("target: method Semantics.Core.Calculator.Add", table);
        Assert.Contains("call", table);
        Assert.Contains("Consumer.Run", table);
    }

    [Fact]
    public void Table_ForAnAmbiguousSelector_ListsCandidatesAndHowToPick()
    {
        var table = Q.Definition(Name("Add")).ToTable();

        Assert.Contains("candidates (re-run with --id or --key)", table);
        Assert.Contains("global::Semantics.Core.Repository<T>.Add", table);
        Assert.DoesNotContain("symbol: unknown", table);
    }

    [Fact]
    public void Table_ForOutline_IsAnIndentedTree()
    {
        var table = Q.Outline(Name("Calculator.cs")).ToTable();

        var calculatorLine = table.Split('\n').Single(l => l.Contains("class Semantics.Core.Calculator #"));
        var addLine = table.Split('\n').First(l => l.Contains("method Semantics.Core.Calculator.Add #"));
        Assert.True(addLine.Length - addLine.TrimStart().Length > calculatorLine.Length - calculatorLine.TrimStart().Length);
    }

    [Fact]
    public void Table_ForHierarchy_ShowsRelations()
    {
        var table = Q.Hierarchy(Name("IShape"), direction: "down").ToTable();

        Assert.Contains("[implements] class Semantics.Core.ShapeBase", table);
        Assert.Contains("[inherits] class Semantics.Core.Circle", table);
    }
}
