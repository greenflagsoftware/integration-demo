namespace CodeIndexer.Tests;

/// <summary>
/// Golden-result tests against the Semantics fixture (tests\Fixtures\Semantics). Expected answers were
/// worked out by hand from the fixture source and describe what a correct index contains. Tests marked
/// [PendingFact] describe behavior the plan schedules for a later phase.
/// </summary>
public class SemanticsDeclarationTests : IClassFixture<SemanticsIndex>
{
    private readonly SemanticsIndex _ix;
    public SemanticsDeclarationTests(SemanticsIndex ix) => _ix = ix;

    private const string AddInt = "M:Semantics.Core.Calculator.Add(System.Int32,System.Int32)";
    private const string AddDouble = "M:Semantics.Core.Calculator.Add(System.Double,System.Double)";
    private const string AddString = "M:Semantics.Core.Calculator.Add(System.String,System.String)";

    [Fact]
    public void BothProjectsAreIndexed_AndNeitherIsExternal()
    {
        using var cmd = _ix.Conn.CreateCommand();
        cmd.CommandText = "SELECT name, is_external FROM projects ORDER BY name";
        using var r = cmd.ExecuteReader();
        var rows = new List<(string, int)>();
        while (r.Read()) rows.Add((r.GetString(0), r.GetInt32(1)));

        Assert.Equal(new[] { ("Semantics.App", 0), ("Semantics.Core", 0) }, rows);
    }

    [Fact]
    public void CalculatorFile_DeclaresExactlyTheseSymbols_InOrder()
    {
        Assert.Equal(
            new[] { "Calculator", "Add", "Add", "Add", "Echo", "Total", "CalculatorExtensions", "Twice" },
            _ix.NamesInFile("Calculator.cs"));
    }

    [Fact]
    public void ShapesFile_DeclaresExactlyTheseSymbols_InOrder()
    {
        Assert.Equal(
            new[]
            {
                "IShape", "Area", "Name",
                "ShapeBase", "Area", "Name", "Describe",
                "Circle", "Radius", "Area", "Name",
                "Square", "Side", "Area",
                "Triangle", "Semantics.Core.IShape.Area", "Name",
                "ShapeUser", "SumAreas", "Label", "CircleArea",
            },
            _ix.NamesInFile("Shapes.cs"));
    }

    [Fact]
    public void Overloads_AreThreeDistinctSymbols_AtTheirOwnLines()
    {
        var ids = new[] { AddInt, AddDouble, AddString }.Select(_ix.Id).ToList();

        Assert.Equal(3, ids.Distinct().Count());
        Assert.Equal(_ix.Line("Calculator.cs", "add-int"), _ix.Row(ids[0]).StartLine);
        Assert.Equal(_ix.Line("Calculator.cs", "add-double"), _ix.Row(ids[1]).StartLine);
        Assert.Equal(_ix.Line("Calculator.cs", "add-string"), _ix.Row(ids[2]).StartLine);
    }

    [Fact]
    public void RepositoryAdd_IsADifferentSymbolFromCalculatorAdd()
    {
        var repoAdd = _ix.Id("M:Semantics.Core.Repository`1.Add(`0)");

        Assert.NotEqual(_ix.Id(AddInt), repoAdd);
        Assert.Equal(_ix.Line("Containers.cs", "repo-add"), _ix.Row(repoAdd).StartLine);
    }

    [Theory]
    [InlineData("Calculator.cs", "echo", "M:Semantics.Core.Calculator.Echo``1(``0)", "method")]
    [InlineData("Calculator.cs", "total", "M:Semantics.Core.Calculator.Total(System.Int32[])", "method")]
    [InlineData("Calculator.cs", "twice", "M:Semantics.Core.CalculatorExtensions.Twice(Semantics.Core.Calculator,System.Int32)", "method")]
    [InlineData("Shapes.cs", "ishape-area", "M:Semantics.Core.IShape.Area", "method")]
    [InlineData("Shapes.cs", "shapebase-area", "M:Semantics.Core.ShapeBase.Area", "method")]
    [InlineData("Shapes.cs", "describe", "M:Semantics.Core.ShapeBase.Describe", "method")]
    [InlineData("Shapes.cs", "circle-area", "M:Semantics.Core.Circle.Area", "method")]
    [InlineData("Shapes.cs", "circle-radius", "P:Semantics.Core.Circle.Radius", "property")]
    [InlineData("Containers.cs", "field-items", "F:Semantics.Core.Repository`1._items", "field")]
    [InlineData("Containers.cs", "cursor", "T:Semantics.Core.Repository`1.Cursor", "class")]
    [InlineData("Containers.cs", "cursor-position", "F:Semantics.Core.Repository`1.Cursor.Position", "field")]
    [InlineData("Account.cs", "balance", "P:Semantics.Core.Account.Balance", "property")]
    [InlineData("Account.cs", "ctor-start", "M:Semantics.Core.Account.#ctor(System.Decimal)", "method")]
    [InlineData("Account.cs", "changed", "E:Semantics.Core.Account.Changed", "event")]
    [InlineData("Account.cs", "person", "T:Semantics.Core.Person", "class")]
    [InlineData("Account.cs", "point", "T:Semantics.Core.Point", "struct")]
    [InlineData("Account.cs", "color", "T:Semantics.Core.Color", "enum")]
    [InlineData("Misc.cs", "old", "M:Semantics.Core.Misc.Old", "method")]
    public void Symbol_HasExpectedKind_AndStartLine(string file, string marker, string docId, string kind)
    {
        var row = _ix.Row(_ix.Id(docId));

        Assert.Equal(kind, row.Kind);
        Assert.Equal(_ix.Line(file, marker), row.StartLine);
    }

    [Fact]
    public void NestedType_IsIndexedOnce()
    {
        using var cmd = _ix.Conn.CreateCommand();
        cmd.CommandText = "SELECT full_name FROM symbols WHERE name = 'Cursor'";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Contains("Repository", r.GetString(0));
        Assert.False(r.Read());
    }

    [Fact]
    public void PartialClass_HasOneTypeRow_AndEachMemberIsInItsOwnFile()
    {
        using var cmd = _ix.Conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM symbols WHERE full_name = 'global::Semantics.Core.Widget'";
        Assert.Equal(1L, cmd.ExecuteScalar());

        var hand = _ix.Row(_ix.Id("M:Semantics.Core.Widget.FromHandWritten"));
        Assert.EndsWith("/Widget.cs", hand.File);
        Assert.False(hand.IsGenerated);
        Assert.Equal(_ix.Line("Widget.cs", "widget-handwritten"), hand.StartLine);

        var gen = _ix.Row(_ix.Id("M:Semantics.Core.Widget.FromGenerated"));
        Assert.EndsWith("/Widget.Generated.cs", gen.File);
        Assert.True(gen.IsGenerated);
        Assert.Equal(_ix.Line("Widget.Generated.cs", "widget-generated"), gen.StartLine);
    }

    [Fact]
    public void GeneratedFlag_IsSetOnlyForTheGeneratedFixtureFile()
    {
        using var cmd = _ix.Conn.CreateCommand();
        cmd.CommandText = "SELECT path FROM files WHERE is_generated = 0 AND path LIKE '%Widget%'";
        using var r = cmd.ExecuteReader();
        var handWritten = new List<string>();
        while (r.Read()) handWritten.Add(r.GetString(0));

        Assert.Single(handWritten);
        Assert.EndsWith("/Widget.cs", handWritten[0]);
    }

    [PendingFact("C", "partial classes: one symbol with a location in each file (symbol_locations)")]
    public void PartialClass_TypeHasALocationInEachPart()
    {
        using var cmd = _ix.Conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM symbol_locations l JOIN symbols s ON s.id = l.symbol_id WHERE s.full_name = 'global::Semantics.Core.Widget'";
        Assert.Equal(2L, cmd.ExecuteScalar());
    }

    [PendingFact("C", "container_id links members to their types, including nested types")]
    public void Members_AreLinkedToTheirContainers()
    {
        using var cmd = _ix.Conn.CreateCommand();
        cmd.CommandText = "SELECT container_id FROM symbols WHERE id = @i";
        cmd.Parameters.AddWithValue("@i", _ix.Id(AddInt));
        Assert.Equal(_ix.Id("T:Semantics.Core.Calculator"), cmd.ExecuteScalar());

        cmd.Parameters["@i"].Value = _ix.Id("T:Semantics.Core.Repository`1.Cursor");
        Assert.Equal(_ix.Id("T:Semantics.Core.Repository`1"), cmd.ExecuteScalar());
    }

    [PendingFact("C", "delegates are indexed (currently not matched by the symbol walk)")]
    public void Delegate_IsIndexed()
    {
        Assert.Equal(_ix.Line("Account.cs", "transformer"), _ix.Row(_ix.Id("T:Semantics.Core.Transformer")).StartLine);
    }

    [PendingFact("C", "namespaces are indexed as containers")]
    public void Namespace_IsIndexed()
    {
        Assert.True(_ix.Exists("N:Semantics.Core"));
    }
}

public class SemanticsRelationshipTests : IClassFixture<SemanticsIndex>
{
    private readonly SemanticsIndex _ix;
    public SemanticsRelationshipTests(SemanticsIndex ix) => _ix = ix;

    private const string AddInt = "M:Semantics.Core.Calculator.Add(System.Int32,System.Int32)";
    private const string AddDouble = "M:Semantics.Core.Calculator.Add(System.Double,System.Double)";
    private const string AddString = "M:Semantics.Core.Calculator.Add(System.String,System.String)";
    private const string IShapeArea = "M:Semantics.Core.IShape.Area";
    private const string ShapeBaseArea = "M:Semantics.Core.ShapeBase.Area";
    private const string CircleArea = "M:Semantics.Core.Circle.Area";

    // ---- who calls each overload ---------------------------------------------------------------

    [Fact]
    public void CallersOf_AddInt_AreExactlyTheFourCallingMembers()
    {
        var callers = _ix.EdgeSources(_ix.Id(AddInt), "calls");

        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal)
            {
                "M:Semantics.Core.Calculator.Total(System.Int32[])",
                "M:Semantics.Core.CalculatorExtensions.Twice(Semantics.Core.Calculator,System.Int32)",
                "M:Semantics.Core.Misc.UseLambda(Semantics.Core.Calculator)",
                "M:Semantics.App.Consumer.Run",
            },
            callers);
    }

    [Fact]
    public void CallSiteLines_OfAddInt_AreExactlyTheMarkedLines()
    {
        var lines = _ix.RefLines(_ix.Id(AddInt), "call");

        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal)
            {
                _ix.At("Calculator.cs", "total-calls-add-int"),
                _ix.At("Calculator.cs", "twice"),
                _ix.At("Misc.cs", "lambda-add-int"),
                _ix.At("Misc.cs", "local-add-int"),
                _ix.At("Consumer.cs", "app-add-int"),
            },
            lines);
    }

    [Fact]
    public void CallersOf_AddDouble_IsOnlyRunDouble() =>
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "M:Semantics.App.Consumer.RunDouble" },
            _ix.EdgeSources(_ix.Id(AddDouble), "calls"));

    [Fact]
    public void CallersOf_AddString_IsEmpty() =>
        Assert.Empty(_ix.EdgeSources(_ix.Id(AddString), "calls"));

    [Fact]
    public void CallersOf_GenericMethod_IncludesTheClosedCall() =>
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "M:Semantics.App.Consumer.RunEcho" },
            _ix.EdgeSources(_ix.Id("M:Semantics.Core.Calculator.Echo``1(``0)"), "calls"));

    [PendingFact("C", "calls to extension methods resolve to the declared static method (reduced form is not mapped)")]
    public void CallersOf_ExtensionMethod_IncludesTheDotCall() =>
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "M:Semantics.App.Consumer.RunTwice" },
            _ix.EdgeSources(_ix.Id("M:Semantics.Core.CalculatorExtensions.Twice(Semantics.Core.Calculator,System.Int32)"), "calls"));

    // ---- type hierarchy and members ------------------------------------------------------------

    [Fact]
    public void TypeEdges_InheritsAndImplements()
    {
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "T:Semantics.Core.Circle", "T:Semantics.Core.Square" },
            _ix.EdgeSources(_ix.Id("T:Semantics.Core.ShapeBase"), "inherits"));
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "T:Semantics.Core.ShapeBase", "T:Semantics.Core.Triangle" },
            _ix.EdgeSources(_ix.Id("T:Semantics.Core.IShape"), "implements"));
    }

    [Fact]
    public void Overrides_AreRecordedForMethods() =>
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { CircleArea, "M:Semantics.Core.Square.Area" },
            _ix.EdgeSources(_ix.Id(ShapeBaseArea), "overrides"));

    [PendingFact("C", "overrides edges for properties (Circle.Name overrides ShapeBase.Name)")]
    public void Overrides_AreRecordedForProperties() =>
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "P:Semantics.Core.Circle.Name" },
            _ix.EdgeSources(_ix.Id("P:Semantics.Core.ShapeBase.Name"), "overrides"));

    [PendingFact("C", "member-level implements edges (ShapeBase.Area and the explicit Triangle member implement IShape.Area)")]
    public void MemberImplementations_OfIShapeArea()
    {
        var sources = _ix.EdgeSources(_ix.Id(IShapeArea), "implements");

        Assert.Contains(ShapeBaseArea, sources);
        Assert.Equal(2, sources.Count);
        Assert.Contains(sources, s => s.StartsWith("M:Semantics.Core.Triangle."));
    }

    // ---- calls through abstractions ------------------------------------------------------------

    [Fact]
    public void DirectCallers_AreRecordedAgainstTheMemberTheCodeNames()
    {
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "M:Semantics.Core.ShapeUser.SumAreas(System.Collections.Generic.IEnumerable{Semantics.Core.IShape})" },
            _ix.EdgeSources(_ix.Id(IShapeArea), "calls"));
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "M:Semantics.Core.ShapeBase.Describe" },
            _ix.EdgeSources(_ix.Id(ShapeBaseArea), "calls"));
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "M:Semantics.Core.ShapeUser.CircleArea" },
            _ix.EdgeSources(_ix.Id(CircleArea), "calls"));
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "M:Semantics.Core.ShapeUser.Label(Semantics.Core.ShapeBase)" },
            _ix.EdgeSources(_ix.Id("M:Semantics.Core.ShapeBase.Describe"), "calls"));
    }

    [Fact]
    public void CallSiteLine_OfInterfaceCall_IsTheMarkedLine() =>
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { _ix.At("Shapes.cs", "call-ishape-area") },
            _ix.RefLines(_ix.Id(IShapeArea), "call"));

    // ---- object creation -----------------------------------------------------------------------

    [Fact]
    public void ObjectCreation_IsRecordedFromTheCreatingMethod()
    {
        var targets = _ix.EdgeTargets(_ix.Id("M:Semantics.Core.ShapeUser.CircleArea"), "creates");

        Assert.Single(targets);
        Assert.StartsWith("T:Semantics.Core.Circle", targets.Single());
    }

    [PendingFact("C", "target-typed new() and field-initializer creation are recorded")]
    public void ObjectCreation_TargetTypedAndFieldInitializer()
    {
        Assert.Contains("T:Semantics.Core.Calculator", _ix.EdgeTargets(_ix.Id("M:Semantics.Core.Misc.Make"), "creates"));
        Assert.Contains("T:Semantics.Core.Calculator", _ix.EdgeTargets(_ix.Id("F:Semantics.App.Consumer._calc"), "creates"));
    }

    // ---- constructors, initializers, members ---------------------------------------------------

    [PendingFact("C", "constructor initializers (: this(...)) are call edges; calls in property/field initializers belong to the member")]
    public void ConstructorChain_AndInitializerCalls()
    {
        var ctorStart = _ix.Id("M:Semantics.Core.Account.#ctor(System.Decimal)");
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "M:Semantics.Core.Account.#ctor" },
            _ix.EdgeSources(ctorStart, "calls"));

        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "M:Semantics.Core.Account.#ctor", "P:Semantics.Core.Account.Balance" },
            _ix.EdgeSources(_ix.Id("M:Semantics.Core.Account.DefaultBalance"), "calls"));
    }

    [PendingFact("C", "reads and writes of properties are recorded with their kind")]
    public void PropertyAccess_IsRecordedWithKinds()
    {
        var balance = _ix.Id("P:Semantics.Core.Account.Balance");

        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { _ix.At("Account.cs", "write-balance"), _ix.At("Account.cs", "deposit-balance") },
            _ix.RefLines(balance, "write"));
        Assert.Contains(_ix.At("Account.cs", "deposit-balance"), _ix.RefLines(balance, "read"));
    }

    [PendingFact("C", "event raises are recorded as references to the event")]
    public void EventUse_IsRecorded() =>
        Assert.Contains(_ix.At("Account.cs", "raise-changed"), _ix.RefLines(_ix.Id("E:Semantics.Core.Account.Changed")));

    // ---- type usage ----------------------------------------------------------------------------

    [PendingFact("C", "type usages (parameters, fields, return types, nameof, creation) are references")]
    public void ReferencesToType_Calculator_AreExactlyTheMarkedLines() =>
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal)
            {
                _ix.At("Calculator.cs", "twice"),
                _ix.At("Misc.cs", "uselambda"),
                _ix.At("Misc.cs", "typenames"),
                _ix.At("Misc.cs", "target-typed-new"),
                _ix.At("Consumer.cs", "app-new-calc"),
            },
            _ix.RefLines(_ix.Id("T:Semantics.Core.Calculator")));

    [PendingFact("C", "typeof(...) is a reference to the type")]
    public void ReferencesToType_Account_IsTheTypeofLine() =>
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { _ix.At("Misc.cs", "typenames") },
            _ix.RefLines(_ix.Id("T:Semantics.Core.Account")));

    // ---- external symbols ----------------------------------------------------------------------

    [PendingFact("C", "calls into the base class library are stored against external symbol stubs")]
    public void ExternalCalls_AreQueryable()
    {
        var getString = _ix.Id("M:System.Net.Http.HttpClient.GetStringAsync(System.String)");
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "M:Semantics.App.Consumer.Fetch(System.Net.Http.HttpClient)" },
            _ix.EdgeSources(getString, "calls"));
        Assert.Equal(1L, ScalarExternal(getString));

        var listAdd = _ix.Id("M:System.Collections.Generic.List`1.Add(`0)");
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal) { "M:Semantics.Core.Repository`1.Add(`0)" },
            _ix.EdgeSources(listAdd, "calls"));
    }

    private long ScalarExternal(long id)
    {
        using var cmd = _ix.Conn.CreateCommand();
        cmd.CommandText = "SELECT is_external FROM symbols WHERE id = @i";
        cmd.Parameters.AddWithValue("@i", id);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
