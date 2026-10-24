using CodeIndexer.Core.Indexing;
using CodeIndexer.Core.Loading;
using CodeIndexer.Core.Models;
using CodeIndexer.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit.Sdk;

namespace CodeIndexer.Tests;

/// <summary>
/// The guarantee of incremental indexing: after any sequence of edits, the updated index is exactly what a clean rebuild of
/// the files would produce, while only the projects the dependency rule names are redone.
/// The fixture (tests\Fixtures\Incremental) has Base, Mid (uses Base), Leaf (uses Mid), Solo (uses nothing), and Tests
/// (uses Base, excluded by the default filter). One long scenario shares one restored copy of it.
/// </summary>
public class IncrementalScenarioTests
{
    private static readonly string[] AllFour = { "Inc.Base", "Inc.Leaf", "Inc.Mid", "Inc.Solo" };

    private static void Same(string step, string what, IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var e = expected.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var a = actual.OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (!e.SequenceEqual(a))
            throw new XunitException($"Step '{step}': {what} expected [{string.Join(", ", e)}] but was [{string.Join(", ", a)}].");
    }

    [Fact]
    public async Task UpdatedIndex_EqualsACleanRebuild_AfterEveryKindOfEdit()
    {
        using var ws = new IncrementalWorkspace();

        async Task<IndexRunResult> Step(string step, IndexMode mode, string[]? redone = null, string[]? kept = null, bool compare = true)
        {
            var run = await ws.Index();
            if (run.Mode != mode)
                throw new XunitException($"Step '{step}': expected {mode} but was {run.Mode} ({run.Reason}). Changed: {string.Join("; ", run.ChangedInputs)}");
            if (redone is not null) Same(step, "projects redone", redone, run.Reindexed.Select(r => r.Name));
            if (kept is not null) Same(step, "projects kept", kept, run.Unchanged);
            if (compare) IndexDump.AssertSame(step, await ws.CleanDump(), ws.Dump());
            return run;
        }

        // 1. No index yet: a full build.
        var first = await Step("first index", IndexMode.Full);
        Assert.Equal("no existing index", first.Reason);

        // 2. Nothing changed: nothing is loaded, nothing is written.
        var idle = await ws.Index();
        Assert.Equal(IndexMode.UpToDate, idle.Mode);
        Assert.Null(idle.Result);
        Assert.Null(idle.LoadResult);

        // 3. A body edit in the project nothing else uses: only that project is redone, and the others keep their ids.
        var baseIds = ws.SymbolIds("Inc.Base|");
        var midIds = ws.SymbolIds("Inc.Mid|");
        ws.Edit("Inc.Solo/Solo.cs", t => t.Replace("\"solo\".ToUpper()", "\"solo\".Trim().ToUpper()"));
        await Step("edit Solo", IndexMode.Incremental, redone: new[] { "Inc.Solo" }, kept: new[] { "Inc.Base", "Inc.Mid", "Inc.Leaf" });
        Assert.Equal(baseIds, ws.SymbolIds("Inc.Base|"));
        Assert.Equal(midIds, ws.SymbolIds("Inc.Mid|"));

        // 4. An edit at the end of the chain redoes only the end.
        ws.Edit("Inc.Leaf/App.cs", t => t.Replace("_service.Calc(3)", "_service.Calc(4)"));
        await Step("edit Leaf", IndexMode.Incremental, redone: new[] { "Inc.Leaf" }, kept: new[] { "Inc.Base", "Inc.Mid", "Inc.Solo" });

        // 5. An API change at the start of the chain redoes everything that depends on it, and not the unrelated project.
        ws.Edit("Inc.Base/MathUtil.cs", t => t.Replace("public static int Triple", "public static int Square(int x) => x * x;\n    public static int Triple"));
        await Step("add a method to Base", IndexMode.Incremental, redone: new[] { "Inc.Base", "Inc.Mid", "Inc.Leaf" }, kept: new[] { "Inc.Solo" });

        // 6. Saving a file without changing it is not a change.
        ws.Write("Inc.Base/MathUtil.cs", File.ReadAllText(ws.Path_("Inc.Base/MathUtil.cs")));
        Assert.Equal(IndexMode.UpToDate, (await ws.Index()).Mode);

        // 7. A new file, then its deletion.
        ws.Write("Inc.Mid/Extra.cs", "namespace Inc.Mid;\n\npublic class Extra\n{\n    public int Value() => Inc.Base.MathUtil.Triple(1);\n}\n");
        await Step("add a file to Mid", IndexMode.Incremental, redone: new[] { "Inc.Mid", "Inc.Leaf" }, kept: new[] { "Inc.Base", "Inc.Solo" });
        ws.Delete("Inc.Mid/Extra.cs");
        await Step("delete that file", IndexMode.Incremental, redone: new[] { "Inc.Mid", "Inc.Leaf" }, kept: new[] { "Inc.Base", "Inc.Solo" });

        // 8. A rename is a deletion plus an addition; the partial class loses and regains a part.
        ws.Rename("Inc.Solo/Solo.More.cs", "Inc.Solo/Solo.Length.cs");
        await Step("rename a file in Solo", IndexMode.Incremental, redone: new[] { "Inc.Solo" }, kept: new[] { "Inc.Base", "Inc.Mid", "Inc.Leaf" });

        // 9. A project the filter excludes: its inputs are tracked, but there is nothing to redo in the index.
        ws.Edit("Inc.Tests/BaseTests.cs", t => t.Replace("Twice(2)", "Twice(5)"));
        await Step("edit the excluded Tests project", IndexMode.Incremental, redone: Array.Empty<string>(), kept: AllFour);

        // 10. The project file itself.
        ws.Edit("Inc.Mid/Inc.Mid.csproj", t => t.Replace("<Nullable>enable</Nullable>", "<Nullable>enable</Nullable>\n    <NoWarn>CS1591</NoWarn>"));
        await Step("edit Mid.csproj", IndexMode.Incremental, redone: new[] { "Inc.Mid", "Inc.Leaf" }, kept: new[] { "Inc.Base", "Inc.Solo" });

        // 11. A dry run reports the plan and changes nothing.
        ws.Edit("Inc.Solo/Solo.cs", t => t.Replace("Trim()", "TrimEnd()"));
        var before = ws.Dump();
        var dry = await ws.Index(dryRun: true);
        Assert.True(dry.WasDryRun);
        Same("dry run", "projects it would redo", new[] { "Inc.Solo" }, dry.Reindexed.Select(r => r.Name));
        IndexDump.AssertSame("dry run leaves the index alone", before, ws.Dump());
        await Step("apply after the dry run", IndexMode.Incremental, redone: new[] { "Inc.Solo" });

        // 12. A project added to the solution, then removed.
        ws.Write("Inc.Extra/Inc.Extra.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n" +
            "  <ItemGroup>\n    <ProjectReference Include=\"..\\Inc.Solo\\Inc.Solo.csproj\" />\n  </ItemGroup>\n</Project>\n");
        ws.Write("Inc.Extra/Extra.cs", "namespace Inc.Extra;\n\npublic class Extra\n{\n    public int Size() => new Inc.Solo.Solo().Length();\n}\n");
        ws.Dotnet("sln Inc.sln add Inc.Extra/Inc.Extra.csproj");
        ws.Dotnet("restore Inc.sln --disable-build-servers");
        var added = await Step("add a project", IndexMode.Incremental, redone: new[] { "Inc.Extra" }, kept: AllFour);
        Assert.Equal("added to the solution", added.Reindexed.Single().Reason);

        ws.Dotnet("sln Inc.sln remove Inc.Extra/Inc.Extra.csproj");
        Directory.Delete(ws.Path_("Inc.Extra"), recursive: true);
        var removed = await Step("remove the project", IndexMode.Incremental, redone: Array.Empty<string>(), kept: AllFour);
        Assert.Equal(new[] { "Inc.Extra/Inc.Extra.csproj" }, removed.Removed);

        // 13. A change to a build-wide file rebuilds everything, whether added or removed.
        ws.Write("Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <LangVersion>latest</LangVersion>\n  </PropertyGroup>\n</Project>\n");
        var env = await Step("add Directory.Build.props", IndexMode.Full);
        Assert.Contains("Directory.Build.props", env.Reason);
        ws.Delete("Directory.Build.props");
        Assert.Contains("Directory.Build.props", (await Step("remove Directory.Build.props", IndexMode.Full)).Reason);

        // 14. A different project filter, then back.
        var filter = new ProjectFilter().Exclude("Inc.Solo");
        var filtered = await ws.Index(filter);
        Assert.Equal(IndexMode.Full, filtered.Mode);
        Assert.Contains("filter", filtered.Reason);
        IndexDump.AssertSame("filtered rebuild", await ws.CleanDump(filter), ws.Dump());
        await Step("filter restored", IndexMode.Full);

        // 15. An index from another tool version cannot be updated.
        ws.Execute("UPDATE meta SET value = '0.0.0' WHERE key = 'tool_version'");
        Assert.Contains("tool version", (await Step("other tool version", IndexMode.Full)).Reason);

        // 16. --full rebuilds even when nothing changed.
        var forced = await ws.Index(full: true);
        Assert.Equal(IndexMode.Full, forced.Mode);
        Assert.Equal("--full was given", forced.Reason);
    }

    [Fact]
    public async Task Update_OfAnIndex_KeepsQueriesWorking_AndCrossProjectLinks()
    {
        using var ws = new IncrementalWorkspace();
        await ws.Index();

        // Redo only Leaf; its calls into Mid and Base (kept) must still resolve to the kept symbols.
        ws.Edit("Inc.Leaf/App.cs", t => t.Replace("_service.Calc(3)", "_service.Calc(7)"));
        var run = await ws.Index();
        Assert.Equal(new[] { "Inc.Leaf" }, run.Reindexed.Select(r => r.Name));

        using var q = new CodeIndexer.Core.Queries.SymbolQueries(ws.Db);
        var callers = q.Callers(new CodeIndexer.Core.Queries.SymbolSelector(Text: "Service.Calc"));
        Assert.Equal(new[] { "Run" }, callers.Data.Select(c => c.Symbol.Name));
        Assert.False(callers.Stale);

        var twiceCallers = q.Callers(new CodeIndexer.Core.Queries.SymbolSelector(Text: "MathUtil.Twice"), new CodeIndexer.Core.Queries.CallOptions(Depth: 0));
        Assert.Equal(new[] { "Calc", "Run" }, twiceCallers.Data.Select(c => c.Symbol.Name).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task EditingAFileAfterIndexing_MakesQueriesStale_UntilTheUpdateRuns()
    {
        using var ws = new IncrementalWorkspace();
        await ws.Index();

        ws.Write("Inc.Solo/Brand.cs", "namespace Inc.Solo;\n\npublic class Brand { }\n");
        using (var q = new CodeIndexer.Core.Queries.SymbolQueries(ws.Db))
        {
            var stale = q.FindSymbol("Solo");
            Assert.True(stale.Stale);
            Assert.Contains(stale.StaleFiles!, f => f.Contains("Inc.Solo/") && f.Contains("files added, deleted, or renamed"));
        }

        await ws.Index();
        using var after = new CodeIndexer.Core.Queries.SymbolQueries(ws.Db);
        Assert.False(after.FindSymbol("Solo").Stale);
        Assert.Contains(after.FindSymbol("Brand").Data, s => s.Kind == "class");
    }
}

public class IncrementalAtomicityTests
{
    [Fact]
    public async Task AFailedUpdate_LeavesTheIndexExactlyAsItWas_AndTheNextRunSucceeds()
    {
        using var ws = new IncrementalWorkspace();
        await ws.Index();
        var before = ws.Dump();

        ws.Edit("Inc.Solo/Solo.cs", t => t.Replace("\"solo\".ToUpper()", "\"solo\".Trim().ToUpper()"));
        // Make the update fail after it has already deleted the old rows, while it is writing the new ones.
        ws.Execute("CREATE TRIGGER sabotage BEFORE INSERT ON symbol_locations BEGIN SELECT RAISE(ABORT, 'sabotaged'); END");

        await Assert.ThrowsAnyAsync<Exception>(() => ws.Index());
        // Take the trigger out of the comparison: it is not part of the index's content.
        ws.Execute("DROP TRIGGER sabotage");
        IndexDump.AssertSame("after the failed update", before, ws.Dump());

        // The failed run changed nothing, so the edit is still pending and the next run applies it.
        var retry = await ws.Index();
        Assert.Equal(IndexMode.Incremental, retry.Mode);
        Assert.Equal(new[] { "Inc.Solo" }, retry.Reindexed.Select(r => r.Name));
        IndexDump.AssertSame("retry", await ws.CleanDump(), ws.Dump());
    }
}

public class IncrementalCompatibilityTests
{
    [Fact]
    public async Task AnIndexFromAnOlderSchema_IsReplacedByAFullRebuild()
    {
        using var ws = new IncrementalWorkspace();
        await ws.Index();
        ws.Execute("UPDATE meta SET value = '5' WHERE key = 'schema_version'");

        var run = await ws.Index();

        Assert.Equal(IndexMode.Full, run.Mode);
        Assert.Contains("incompatible version", run.Reason);
        IndexDump.AssertSame("rebuilt over the old schema", await ws.CleanDump(), ws.Dump());
        Assert.Equal(IndexMode.UpToDate, (await ws.Index()).Mode);
    }

    [Fact]
    public async Task AnUnreadableIndexFile_IsReplacedByAFullRebuild()
    {
        using var ws = new IncrementalWorkspace();
        await ws.Index();
        SqliteConnection.ClearAllPools();
        File.WriteAllText(ws.Db, "this is not a database");
        foreach (var sidecar in new[] { ws.Db + "-wal", ws.Db + "-shm" }) if (File.Exists(sidecar)) File.Delete(sidecar);

        var run = await ws.Index();

        Assert.Equal(IndexMode.Full, run.Mode);
        IndexDump.AssertSame("rebuilt over a corrupt file", await ws.CleanDump(), ws.Dump());
    }
}

public class IndexDumpTests : IClassFixture<SemanticsIndex>
{
    private readonly SemanticsIndex _ix;
    public IndexDumpTests(SemanticsIndex ix) => _ix = ix;

    [Fact]
    public void TheComparison_CanFail_ForEverySectionItCovers()
    {
        // A comparison that cannot fail proves nothing: damage a copy of an index in one place at a time.
        var damage = new Dictionary<string, string>
        {
            ["symbols"] = "UPDATE symbols SET signature = 'changed' WHERE id = (SELECT MIN(id) FROM symbols WHERE is_external = 0 AND kind = 'method')",
            ["locations"] = "UPDATE symbol_locations SET start_line = start_line + 1 WHERE rowid = (SELECT MIN(rowid) FROM symbol_locations)",
            ["references"] = "DELETE FROM symbol_references WHERE id = (SELECT MIN(id) FROM symbol_references)",
            ["edges"] = "DELETE FROM edges WHERE (from_symbol_id, to_symbol_id, kind) = (SELECT from_symbol_id, to_symbol_id, kind FROM edges LIMIT 1)",
            ["files"] = "UPDATE files SET content_hash = 'x' WHERE id = (SELECT MIN(id) FROM files)",
            ["inputs"] = "UPDATE inputs SET hash = 'x' WHERE rowid = (SELECT MIN(rowid) FROM inputs)",
            ["diagnostics"] = "INSERT INTO diagnostics (severity, message) VALUES ('Info', 'extra')",
            ["projects"] = "UPDATE projects SET indexed_tfm = 'net1.0' WHERE id = (SELECT MIN(id) FROM projects)",
        };
        var original = IndexDump.Capture(_ix.DbPath);

        foreach (var (section, sql) in damage)
        {
            var copy = Fixtures.TempDbPath();
            try
            {
                File.Copy(_ix.DbPath, copy);
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copy};Pooling=False"))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    Assert.True(cmd.ExecuteNonQuery() > 0, $"the damage to '{section}' changed nothing");
                }

                var ex = Assert.ThrowsAny<Exception>(() => IndexDump.AssertSame("control", original, IndexDump.Capture(copy)));
                Assert.Contains($"[{section}]", ex.Message);
            }
            finally { Fixtures.DeleteDb(copy); }
        }
    }

    [Fact]
    public void TheDump_IgnoresRowIdsAndTimes()
    {
        var copy = Fixtures.TempDbPath();
        try
        {
            File.Copy(_ix.DbPath, copy);
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copy};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE meta SET value = 'later' WHERE key IN ('indexed_at', 'git_commit'); UPDATE solutions SET indexed_at = 'later'; UPDATE files SET modified_ticks = 1";
                cmd.ExecuteNonQuery();
            }

            IndexDump.AssertSame("times only", IndexDump.Capture(_ix.DbPath), IndexDump.Capture(copy));
        }
        finally { Fixtures.DeleteDb(copy); }
    }
}

public class IndexPlanTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "plan-root");

    private static IndexedProject P(string name) => new()
    {
        Name = name, FilePath = Path.Combine(Root, name, name + ".csproj"), Language = "C#", TargetFramework = "net10.0",
    };

    private static string Rel(string name) => $"{name}/{name}.csproj";

    private static SolutionLoadResult Load(string[] names, params (string Project, string[] Dependents)[] dependents) => new()
    {
        SolutionPath = Path.Combine(Root, "x.sln"),
        Projects = names.Select(P).ToList(),
        Dependents = dependents.ToDictionary(
            d => P(d.Project).FilePath,
            d => (IReadOnlySet<string>)d.Dependents.Select(x => P(x).FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase),
    };

    private static InputRecord Project(string name) => new(Rel(name), InputScanner.Project, Rel(name), true, "h", 1, 1);
    private static ChangedInput Changed(string project, string kind = InputScanner.Source) =>
        new(new InputRecord($"{project}/a.cs", kind, Rel(project), true, "h", 1, 1), "modified");

    [Fact]
    public void ChangedProject_IsRedone_WithItsTransitiveDependents()
    {
        var load = Load(new[] { "A", "B", "C", "D" }, ("A", new[] { "B", "C" }), ("B", new[] { "C" }));
        var stored = new[] { "A", "B", "C", "D" }.Select(Project).ToList();

        var plan = IndexRunner.Plan(load, stored, new[] { Changed("A") }, Root);

        Assert.Equal(new[] { "A/A.csproj", "B/B.csproj", "C/C.csproj" }, plan.Dirty.Keys.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("depends on A", plan.Dirty[Rel("B")]);
        Assert.Empty(plan.Removed);
    }

    [Fact]
    public void ChangeInALeaf_RedoesOnlyTheLeaf()
    {
        var load = Load(new[] { "A", "B" }, ("A", new[] { "B" }));
        var stored = new[] { "A", "B" }.Select(Project).ToList();

        var plan = IndexRunner.Plan(load, stored, new[] { Changed("B") }, Root);

        Assert.Equal(new[] { Rel("B") }, plan.Dirty.Keys);
    }

    [Fact]
    public void AddedAndRemovedProjects_AreRecognizedFromTheStoredProjectList()
    {
        var load = Load(new[] { "A", "New" });
        var stored = new[] { "A", "Gone" }.Select(Project).ToList();

        var plan = IndexRunner.Plan(load, stored, Array.Empty<ChangedInput>(), Root);

        Assert.Equal(new[] { Rel("New") }, plan.Dirty.Keys);
        Assert.Equal("added to the solution", plan.Dirty[Rel("New")]);
        Assert.Equal(new[] { Rel("Gone") }, plan.Removed);
    }

    [Fact]
    public void ChangedExcludedProject_StillRedoesItsIndexedDependents()
    {
        // Excluded projects are in the solution graph; whatever binds against them must be redone.
        var load = Load(new[] { "Excluded.Tests", "Lib" }, ("Excluded.Tests", new[] { "Lib" }));
        var stored = new[] { "Excluded.Tests", "Lib" }.Select(Project).ToList();

        var plan = IndexRunner.Plan(load, stored, new[] { Changed("Excluded.Tests") }, Root);

        Assert.Contains(Rel("Lib"), plan.Dirty.Keys);
    }

    [Fact]
    public void BuildWideChanges_AreNotAttributedToAProject()
    {
        var load = Load(new[] { "A" });
        var stored = new[] { Project("A") };
        var envChange = new ChangedInput(new InputRecord("Directory.Build.props", InputScanner.Env, "", true, "h", 1, 1), "modified");

        var plan = IndexRunner.Plan(load, stored, new[] { envChange }, Root);

        Assert.Empty(plan.Dirty);
    }

    [Fact]
    public void ProjectFilterSignature_ChangesWithPatterns_NotWithTheirOrder()
    {
        var plain = new ProjectFilter().Signature;
        var excluding = new ProjectFilter().Exclude("X").Signature;
        var ab = new ProjectFilter().Include("A").Include("B").Signature;
        var ba = new ProjectFilter().Include("B").Include("A").Signature;

        Assert.NotEqual(plain, excluding);
        Assert.Equal(ab, ba);
        Assert.NotEqual(plain, ab);
    }
}

public class InputScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"scanner_{Guid.NewGuid():N}");

    public InputScannerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "sub"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "obj"));
        File.WriteAllText(Path.Combine(_root, "src", "A.cs"), "class A {}");
        File.WriteAllText(Path.Combine(_root, "src", "sub", "B.cs"), "class B {}");
        File.WriteAllText(Path.Combine(_root, "src", "obj", "Generated.cs"), "class G {}");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Src => Path.Combine(_root, "src");

    [Fact]
    public void Listing_ChangesOnAddDeleteRename_NotOnEdit()
    {
        var original = InputScanner.ListingHash(Src);

        File.WriteAllText(Path.Combine(Src, "A.cs"), "class A { int x; }");
        Assert.Equal(original, InputScanner.ListingHash(Src));

        File.WriteAllText(Path.Combine(Src, "C.cs"), "class C {}");
        var added = InputScanner.ListingHash(Src);
        Assert.NotEqual(original, added);

        File.Move(Path.Combine(Src, "C.cs"), Path.Combine(Src, "D.cs"));
        Assert.NotEqual(added, InputScanner.ListingHash(Src));

        File.Delete(Path.Combine(Src, "D.cs"));
        Assert.Equal(original, InputScanner.ListingHash(Src));
    }

    [Fact]
    public void Listing_IgnoresObjAndBin()
    {
        var original = InputScanner.ListingHash(Src);
        Directory.CreateDirectory(Path.Combine(Src, "bin"));
        File.WriteAllText(Path.Combine(Src, "bin", "X.cs"), "class X {}");
        File.WriteAllText(Path.Combine(Src, "obj", "More.cs"), "class M {}");

        Assert.Equal(original, InputScanner.ListingHash(Src));
    }

    private InputRecord Record(string relative, string kind = InputScanner.Source)
    {
        var full = Path.Combine(_root, relative);
        var info = new FileInfo(full);
        return new InputRecord(relative.Replace('\\', '/'), kind, "src/p.csproj", true,
            IndexDatabase.ComputeContentHash(File.ReadAllBytes(full)), info.Length, info.LastWriteTimeUtc.Ticks);
    }

    [Fact]
    public void FindChanges_ReportsModifiedAndDeleted_NotTouched()
    {
        var a = Record("src/A.cs");
        var b = Record("src/sub/B.cs");
        var stored = new[] { a, b };
        Assert.Empty(InputScanner.FindChanges(stored, _root));

        // Same content, new modification time: hashed, found equal, not a change.
        File.SetLastWriteTimeUtc(Path.Combine(Src, "A.cs"), DateTime.UtcNow.AddMinutes(5));
        Assert.Empty(InputScanner.FindChanges(stored, _root));

        File.WriteAllText(Path.Combine(Src, "A.cs"), "class A { int changed; }");
        File.Delete(Path.Combine(Src, "sub", "B.cs"));
        var changes = InputScanner.FindChanges(stored, _root);

        Assert.Equal(new[] { ("src/A.cs", "modified"), ("src/sub/B.cs", "deleted") },
            changes.Select(c => (c.Stored.Path, c.Reason)).OrderBy(x => x.Path, StringComparer.Ordinal));
    }

    [Fact]
    public void FindChanges_CanIgnoreProjectsTheIndexDoesNotCover()
    {
        var a = Record("src/A.cs") with { Indexed = false };
        File.WriteAllText(Path.Combine(Src, "A.cs"), "class A { int changed; }");

        Assert.Single(InputScanner.FindChanges(new[] { a }, _root));
        Assert.Empty(InputScanner.FindChanges(new[] { a }, _root, indexedOnly: true));
    }

    [Fact]
    public void FindChanges_NoticesABuildWideFileThatAppearedAfterIndexing()
    {
        var listing = new InputRecord("src", InputScanner.Listing, "src/p.csproj", true, InputScanner.ListingHash(Src), 0, 0);
        Assert.Empty(InputScanner.FindChanges(new[] { listing }, _root));

        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        var change = Assert.Single(InputScanner.FindChanges(new[] { listing }, _root));

        Assert.Equal(("Directory.Build.props", InputScanner.Env, "added"), (change.Stored.Path, change.Stored.Kind, change.Reason));
    }
}

public class GarbageCollectionTests
{
    [Fact]
    public void CollectGarbage_RemovesUnusedStubsAndEmptyNamespaces_Iteratively()
    {
        var path = Fixtures.TempDbPath();
        try
        {
            using var db = new IndexDatabase(path, "");
            var ns = db.InsertSymbol(null, "|N:Empty", "namespace", "Empty", "global::Empty", null, null, null, null, null, null, false);
            var usedNs = db.InsertSymbol(null, "|N:Used", "namespace", "Used", "global::Used", null, null, null, null, null, null, false);
            var project = db.InsertProject("P", "P/P.csproj", "net10.0", "net10.0", "C#", false);
            var file = db.InsertFile(project, "P/a.cs", "h", 1, false);
            db.InsertLocation(usedNs, file, 1, 1, 1, 10, false);

            // A stub type containing a stub method, nothing refers to either: both must go (the type only after the method).
            var type = db.InsertSymbol(null, "asm|T:Ext.Type", "class", "Type", "global::Ext.Type", null, null, null, null, null, null, false, true, "asm");
            db.InsertSymbol(null, "asm|M:Ext.Type.M", "method", "M", "global::Ext.Type.M", type, null, null, null, null, null, false, true, "asm");
            // A stub that a reference points at survives, and so does its container.
            var keptType = db.InsertSymbol(null, "asm|T:Ext.Kept", "class", "Kept", "global::Ext.Kept", null, null, null, null, null, null, false, true, "asm");
            var keptMethod = db.InsertSymbol(null, "asm|M:Ext.Kept.M", "method", "M", "global::Ext.Kept.M", keptType, null, null, null, null, null, false, true, "asm");
            var local = db.InsertSymbol(file, "P|M:P.Local", "method", "Local", "global::P.Local", null, null, null, 1, 1, null, false);
            db.InsertReference(keptMethod, file, 1, 1, 1, "call", local, null);

            var removed = db.CollectGarbage();

            Assert.Equal(3, removed);
            var keys = new List<string>();
            using (var cmd = db.Connection.CreateCommand())
            {
                cmd.CommandText = "SELECT key FROM symbols ORDER BY key";
                using var r = cmd.ExecuteReader();
                while (r.Read()) keys.Add(r.GetString(0));
            }
            Assert.Equal(new[] { "P|M:P.Local", "asm|M:Ext.Kept.M", "asm|T:Ext.Kept", "|N:Used" }.OrderBy(x => x, StringComparer.Ordinal), keys.OrderBy(x => x, StringComparer.Ordinal));
        }
        finally { Fixtures.DeleteDb(path); }
    }
}
