using CodeIndexer.Core;
using CodeIndexer.Core.Indexing;
using CodeIndexer.Core.Loading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;

namespace CodeIndexer.Tests;

public class ProjectFilterTests
{
    [Fact]
    public void Include_IsExactMatch_NotSubstring()
    {
        var filter = new ProjectFilter().Include("SimpleLib");

        Assert.True(filter.ShouldInclude("SimpleLib"));
        Assert.False(filter.ShouldInclude("SimpleLib.Tests"));
        Assert.False(filter.ShouldInclude("MySimpleLib"));
    }

    [Fact]
    public void Include_SupportsGlobs()
    {
        var filter = new ProjectFilter().Include("Google.Cloud.Storage.*");

        Assert.True(filter.ShouldInclude("Google.Cloud.Storage.V1"));
        Assert.False(filter.ShouldInclude("Google.Cloud.Speech.V1"));
    }

    [Theory]
    [InlineData("Google.Cloud.Storage.V1.Tests")]
    [InlineData("Google.Cloud.Storage.V1.IntegrationTests")]
    [InlineData("Google.Cloud.Storage.V1.Snippets")]
    [InlineData("Google.Cloud.Storage.V1.BenchmarkingTool")]
    [InlineData("Google.Cloud.Storage.V1.CleanTestData")]
    [InlineData("tests")]
    public void Default_ExcludesTestLikeProjects(string name) =>
        Assert.False(new ProjectFilter().ShouldInclude(name));

    [Theory]
    [InlineData("Contest.Core")]
    [InlineData("Latest.Client")]
    [InlineData("Google.Cloud.Storage.V1")]
    [InlineData("Testimony")]
    public void Default_DoesNotExcludeNamesThatMerelyContainTest(string name) =>
        Assert.True(new ProjectFilter().ShouldInclude(name));

    [Fact]
    public void Exclude_AlwaysWins()
    {
        var filter = new ProjectFilter().Include("MyLib.*").Exclude("*.Legacy");

        Assert.False(filter.ShouldInclude("MyLib.Legacy"));
        Assert.True(filter.ShouldInclude("MyLib.New"));
    }
}

public class SdkSelectorTests
{
    private static readonly Version[] Installed =
    {
        new(8, 0, 132), new(8, 0, 426), new(9, 0, 205), new(10, 0, 301),
    };

    [Fact]
    public void NoPin_PicksHighest() =>
        Assert.Equal(new Version(10, 0, 301), SdkSelector.Select(Installed, null));

    [Fact]
    public void Pin_DefaultRollForward_IsLatestPatchInFeatureBand()
    {
        var pin = SdkSelector.TryParse("""{ "sdk": { "version": "8.0.426", "rollForward": "latestMinor" } }""");
        Assert.NotNull(pin);
        Assert.Equal(new Version(8, 0, 426), SdkSelector.Select(Installed, pin));
    }

    [Fact]
    public void Pin_Disable_RequiresExactVersion()
    {
        var present = new GlobalJsonPin("8.0.426", "disable", false);
        var missing = new GlobalJsonPin("8.0.427", "disable", false);

        Assert.Equal(new Version(8, 0, 426), SdkSelector.Select(Installed, present));
        Assert.Null(SdkSelector.Select(Installed, missing));
    }

    [Fact]
    public void Pin_LatestMinor_StaysInMajorVersion()
    {
        var pin = new GlobalJsonPin("8.0.100", "latestMinor", false);
        Assert.Equal(new Version(8, 0, 426), SdkSelector.Select(Installed, pin));
    }

    [Fact]
    public void Pin_Unsatisfied_ReturnsNull()
    {
        var pin = new GlobalJsonPin("7.0.100", "latestPatch", false);
        Assert.Null(SdkSelector.Select(Installed, pin));
    }

    [Fact]
    public void Pin_LatestMajor_AllowsNewerMajor()
    {
        var pin = new GlobalJsonPin("8.0.426", "latestMajor", false);
        Assert.Equal(new Version(10, 0, 301), SdkSelector.Select(Installed, pin));
    }

    [Fact]
    public void TryParse_IgnoresFilesWithoutSdkVersion()
    {
        Assert.Null(SdkSelector.TryParse("""{ "msbuild-sdks": { } }"""));
        Assert.Null(SdkSelector.TryParse("not json"));
    }
}

public class PrimaryTfmTests
{
    [Theory]
    [InlineData("net8.0;net462", "net8.0")]
    [InlineData("netstandard2.0;net462", "netstandard2.0")]
    [InlineData("net8.0;net10.0", "net10.0")]
    [InlineData("net10.0;netstandard2.0", "net10.0")]
    [InlineData("net462", "net462")]
    [InlineData("netcoreapp3.1;net462", "netcoreapp3.1")]
    public void SelectsExpectedTfm(string declared, string expected) =>
        Assert.Equal(expected, SolutionLoader.SelectPrimaryTfm(declared.Split(';')));

    [Fact]
    public void NoTfms_ReturnsNull() =>
        Assert.Null(SolutionLoader.SelectPrimaryTfm(Array.Empty<string>()));
}

public class PathUtilTests
{
    [Fact]
    public void ToRepoRelative_UsesForwardSlashes()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");
        var path = Path.Combine(root, "apis", "Foo", "Foo.sln");

        Assert.Equal("apis/Foo/Foo.sln", PathUtil.ToRepoRelative(root, path));
    }

    [Fact]
    public void IsUnderDirectory_IsSeparatorAware()
    {
        var dir = Path.Combine(Path.GetTempPath(), "apis", "Foo");

        Assert.True(PathUtil.IsUnderDirectory(Path.Combine(dir, "src", "A.csproj"), dir));
        Assert.False(PathUtil.IsUnderDirectory(Path.Combine(Path.GetTempPath(), "apis", "FooBar", "A.csproj"), dir));
    }
}

public class SymbolIdentityTests
{
    private static (Document Doc1, Document Doc2, Compilation Compilation) Compile(string source1, string source2 = "")
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp)
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddMetadataReference(MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var d1 = project.AddDocument("A.cs", source1);
        var d2 = d1.Project.AddDocument("B.cs", source2);
        var compilation = d2.Project.GetCompilationAsync().GetAwaiter().GetResult()!;
        return (d1.Project.GetDocument(d1.Id)!, d2.Project.GetDocument(d2.Id)!, compilation);
    }

    [Fact]
    public void Key_DistinguishesOverloads()
    {
        var (_, _, compilation) = Compile("""
            public class C { public int Add(int a) => a; public int Add(string a) => 0; }
            """);
        var adds = compilation.GetTypeByMetadataName("C")!.GetMembers("Add").ToList();

        Assert.Equal(2, adds.Count);
        Assert.NotEqual(SymbolKey.For(adds[0]), SymbolKey.For(adds[1]));
    }

    [Fact]
    public void Key_OfConstructedGenericMethod_EqualsItsDefinition()
    {
        var (_, _, compilation) = Compile("""
            public class C { public T Id<T>(T x) => x; }
            """);
        var open = (IMethodSymbol)compilation.GetTypeByMetadataName("C")!.GetMembers("Id").Single();
        var closed = open.Construct(compilation.GetSpecialType(SpecialType.System_Int32));

        Assert.Equal(SymbolKey.For(open), SymbolKey.For(closed));
    }

    private static SymbolExtraction Extract(Document docA, Document? docB, Compilation compilation)
    {
        var files = new Dictionary<SyntaxTree, FileRef> { [docA.GetSyntaxTreeAsync().Result!] = new FileRef(1, false) };
        if (docB is not null)
            files[docB.GetSyntaxTreeAsync().Result!] = new FileRef(2, false);
        return new SymbolExtractor(compilation).Extract(files);
    }

    [Fact]
    public void Extractor_DoesNotEmitNestedTypesTwice()
    {
        var (doc, _, compilation) = Compile("""
            public class Outer { public class Inner { public int X; } }
            """);
        var rows = Extract(doc, null, compilation).Symbols;

        Assert.Single(rows, r => r.Name == "Inner" && r.Kind == "class");
        Assert.Equal(rows.Count, rows.Select(r => r.Key).Distinct().Count());
    }

    [Fact]
    public void Extractor_EmitsContainersBeforeMembers_AndLinksThem()
    {
        var (doc, _, compilation) = Compile("""
            namespace N { public class Outer { public class Inner { public int X; } public void M() { } } }
            """);
        var rows = Extract(doc, null, compilation).Symbols;

        var keys = rows.Select(r => r.Key).ToList();
        foreach (var row in rows.Where(r => r.ContainerKey is not null))
            Assert.True(keys.IndexOf(row.ContainerKey!) < keys.IndexOf(row.Key), $"{row.Name} precedes its container");
        Assert.EndsWith("|T:N.Outer.Inner", rows.Single(r => r.Name == "X").ContainerKey);
        Assert.EndsWith("|N:N", rows.Single(r => r.Name == "Outer").ContainerKey);
    }

    [Fact]
    public void Extractor_PartialType_IsOneSymbolWithALocationPerPart()
    {
        var (docA, docB, compilation) = Compile(
            "public partial class P { public void FromA() { } }",
            "public partial class P { public void FromB() { } }");
        var rows = Extract(docA, docB, compilation).Symbols;

        var type = Assert.Single(rows, r => r.Name == "P" && r.Kind == "class");
        Assert.Equal(new long[] { 1, 2 }, type.Locations.Select(l => l.FileId).OrderBy(x => x));
        Assert.Single(type.Locations, l => l.IsPrimary);
        Assert.Equal(1, rows.Single(r => r.Name == "FromA").Primary.FileId);
        Assert.Equal(2, rows.Single(r => r.Name == "FromB").Primary.FileId);
    }

    [Fact]
    public void Extractor_PartialType_PrefersHandWrittenPartAsPrimary()
    {
        var (docA, docB, compilation) = Compile(
            "public partial class P { }",
            "public partial class P { }");
        var files = new Dictionary<SyntaxTree, FileRef>
        {
            [docA.GetSyntaxTreeAsync().Result!] = new FileRef(1, true),   // generated
            [docB.GetSyntaxTreeAsync().Result!] = new FileRef(2, false),  // hand-written
        };
        var type = new SymbolExtractor(compilation).Extract(files).Symbols.Single(r => r.Name == "P");

        Assert.Equal(2, type.Primary.FileId);
        Assert.False(type.IsGenerated);
    }

    [Fact]
    public void Extractor_IndexesNamespacesAndDelegates_ButNotAccessorsOrImplicitMembers()
    {
        var (doc, _, compilation) = Compile("""
            namespace N { public delegate int D(int x); public record R(int A); public class C { public int P { get; set; } public event System.EventHandler? E; } }
            """);
        var rows = Extract(doc, null, compilation).Symbols;

        Assert.Contains(rows, r => r.Kind == "namespace" && r.Name == "N");
        Assert.Contains(rows, r => r.Kind == "delegate" && r.Name == "D");
        Assert.DoesNotContain(rows, r => r.Name is "get_P" or "set_P" or "add_E" or "remove_E" or "Invoke" or "EqualityContract");
        Assert.Contains(rows, r => r.Kind == "property" && r.Name == "P");
        Assert.Contains(rows, r => r.Kind == "event" && r.Name == "E");
    }
}

public class GeneratedFileTests
{
    [Theory]
    [InlineData("SpeechGrpc.cs")]
    [InlineData("Foo.Grpc.cs")]
    [InlineData("Foo.g.cs")]
    [InlineData("Foo.g.i.cs")]
    [InlineData("Foo.generated.cs")]
    [InlineData("Widget.Generated.cs")]
    [InlineData("Form1.Designer.cs")]
    [InlineData("SimpleLib.AssemblyInfo.cs")]
    [InlineData("SimpleLib.GlobalUsings.g.cs")]
    public void RecognizesGeneratedFileNames(string name) =>
        Assert.True(SymbolExtractor.IsGeneratedFilePath("some/dir/" + name));

    [Theory]
    [InlineData("Calculator.cs")]
    [InlineData("GrpcClientFactory.cs")]
    [InlineData("Generator.cs")]
    [InlineData("Designer.cs")]
    public void LeavesHandWrittenFileNamesAlone(string name) =>
        Assert.False(SymbolExtractor.IsGeneratedFilePath("some/dir/" + name));
}

public class IndexRebuildTests
{
    private static async Task<IndexResult> IndexAsync(string solution, string dbPath, ProjectFilter? filter = null)
    {
        var load = await new SolutionLoader(filter).LoadAsync(solution);
        return await new IndexEngine(Fixtures.RepoRoot).IndexSolutionAsync(dbPath, load);
    }

    private static long Scalar(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Reindexing_ReplacesTheDatabase_InsteadOfDuplicatingRows()
    {
        var db = Fixtures.TempDbPath();
        try
        {
            var first = await IndexAsync(Fixtures.SimpleClassLib, db);
            long symbols = Scalar(db, "SELECT COUNT(*) FROM symbols");
            var second = await IndexAsync(Fixtures.SimpleClassLib, db);

            Assert.Equal(first.SymbolsIndexed, second.SymbolsIndexed);
            Assert.Equal(symbols, Scalar(db, "SELECT COUNT(*) FROM symbols"));
            Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM solutions"));
            Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM projects"));
            Assert.Equal(0, Scalar(db, "SELECT COUNT(*) - COUNT(DISTINCT key) FROM symbols"));
            Assert.False(File.Exists(db + ".tmp"));
        }
        finally { Fixtures.DeleteDb(db); }
    }

    [Fact]
    public async Task Index_StoresRepoRelativePaths_WithForwardSlashes()
    {
        var db = Fixtures.TempDbPath();
        try
        {
            await IndexAsync(Fixtures.SimpleClassLib, db);

            Assert.Equal(0, Scalar(db, "SELECT COUNT(*) FROM files WHERE instr(path, char(92)) > 0"));
            Assert.Equal(0, Scalar(db, "SELECT COUNT(*) FROM projects WHERE instr(path, char(92)) > 0"));
            Assert.Equal(0, Scalar(db, "SELECT COUNT(*) FROM solutions WHERE instr(path, char(92)) > 0"));
            Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM files WHERE path LIKE 'project/code-indexer/tests/Fixtures/SimpleClassLib/SimpleLib/Class1.cs'"));
        }
        finally { Fixtures.DeleteDb(db); }
    }

    [Fact]
    public async Task Index_FilterIsExact_SoTestProjectIsNotPulledIn()
    {
        var db = Fixtures.TempDbPath();
        try
        {
            await IndexAsync(Fixtures.SimpleClassLib, db, new ProjectFilter().Include("SimpleLib"));

            Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM projects"));
        }
        finally { Fixtures.DeleteDb(db); }
    }

    [Fact]
    public async Task MultiTargetedProject_IsIndexedOnce_UsingThePrimaryTfm()
    {
        var load = await new SolutionLoader().LoadAsync(Fixtures.MultiTarget);

        var project = Assert.Single(load.Projects);
        Assert.Equal("MultiLib", project.Name);
        Assert.Equal("net10.0", project.TargetFramework);
        Assert.Equal(new[] { "net8.0", "net10.0" }, project.AllTargetFrameworks);
        Assert.Contains("net10.0", project.RoslynProject!.Name);
    }

    [Fact]
    public async Task MultiTargetedProject_DoesNotDuplicateSymbols_AndRecordsADiagnostic()
    {
        var db = Fixtures.TempDbPath();
        try
        {
            await IndexAsync(Fixtures.MultiTarget, db);

            Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM projects"));
            Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM symbols WHERE name = 'Greet'"));
            // GreetModern only exists under #if NET10_0, so it proves net10.0 was the TFM analyzed.
            Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM symbols WHERE name = 'GreetModern'"));
            Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM diagnostics WHERE severity = 'Info' AND message LIKE 'Multi-targeted%'"));
        }
        finally { Fixtures.DeleteDb(db); }
    }

    [Fact]
    public async Task Index_PersistsLoadDiagnostics()
    {
        var db = Fixtures.TempDbPath();
        try
        {
            var load = await new SolutionLoader().LoadAsync(Fixtures.SimpleClassLib);
            var withExtra = new CodeIndexer.Core.Models.SolutionLoadResult
            {
                SolutionPath = load.SolutionPath,
                Projects = load.Projects,
                Diagnostics = load.Diagnostics.Append(new CodeIndexer.Core.Models.DiagnosticEntry
                {
                    Severity = CodeIndexer.Core.Models.DiagnosticSeverity.Warning,
                    Message = "synthetic load warning",
                }).ToList(),
            };
            await new IndexEngine(Fixtures.RepoRoot).IndexSolutionAsync(db, withExtra);

            Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM diagnostics WHERE severity = 'Warning' AND message = 'synthetic load warning' AND project_id IS NULL"));
        }
        finally { Fixtures.DeleteDb(db); }
    }

    [Fact]
    public async Task Index_RecordsExistingGitCommit()
    {
        var db = Fixtures.TempDbPath();
        try
        {
            await IndexAsync(Fixtures.SimpleClassLib, db);

            using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = 'git_commit'";
            var commit = (string)cmd.ExecuteScalar()!;

            Assert.Matches("^[0-9a-f]{40}$", commit);
        }
        finally { Fixtures.DeleteDb(db); }
    }

    [Fact]
    public void GetGitCommit_OutsideARepository_IsUnknown()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"codeindex_nogit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal("unknown", IndexEngine.GetGitCommit(dir));
        }
        finally { Directory.Delete(dir); }
    }
}
