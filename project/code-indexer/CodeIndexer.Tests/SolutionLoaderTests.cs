using CodeIndexer.Core.Loading;
using CodeIndexer.Core.Models;

namespace CodeIndexer.Tests;

public class SolutionLoaderTests
{
    private static readonly string FixtureSolutionPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tests", "Fixtures", "SimpleClassLib", "SimpleClassLib.sln"));

    [Fact]
    public async Task LoadFixtureSolution_ReturnsProjects()
    {
        var loader = new SolutionLoader();
        var result = await loader.LoadAsync(FixtureSolutionPath);

        Assert.NotNull(result);
        Assert.Equal(FixtureSolutionPath, result.SolutionPath);
        Assert.NotEmpty(result.Projects);
    }

    [Fact]
    public async Task LoadFixtureSolution_ExcludesTestProjectByDefault()
    {
        var loader = new SolutionLoader();
        var result = await loader.LoadAsync(FixtureSolutionPath);

        // The test project "SimpleLib.Tests" should be excluded by default filter.
        var testProject = result.Projects.FirstOrDefault(p => p.Name.Contains("Tests"));
        Assert.NotNull(testProject);
        Assert.False(testProject.IncludeInIndex);

        // The library project "SimpleLib" should be included.
        var libProject = result.Projects.FirstOrDefault(p => p.Name == "SimpleLib");
        Assert.NotNull(libProject);
        Assert.True(libProject.IncludeInIndex);
    }

    [Fact]
    public async Task LoadFixtureSolution_HasRoslynProjects()
    {
        var loader = new SolutionLoader();
        var result = await loader.LoadAsync(FixtureSolutionPath);

        Assert.True(result.LoadedProjectCount > 0, "Should have at least one loaded Roslyn project");

        foreach (var project in result.IndexableProjects)
        {
            Assert.NotNull(project.RoslynProject);
            Assert.True(project.RoslynProject!.DocumentIds.Count > 0,
                $"Project {project.Name} should have source documents");
        }
    }

    [Fact]
    public async Task LoadFixtureSolution_HasCorrectTargetFramework()
    {
        var loader = new SolutionLoader();
        var result = await loader.LoadAsync(FixtureSolutionPath);

        var libProject = result.Projects.FirstOrDefault(p => p.Name == "SimpleLib");
        Assert.NotNull(libProject);
        Assert.Equal("net10.0", libProject.TargetFramework);
    }

    [Fact]
    public async Task LoadFixtureSolution_WithIncludeFilter_OnlyIncludesMatching()
    {
        var filter = new ProjectFilter().Include("SimpleLib");
        var loader = new SolutionLoader(filter);
        var result = await loader.LoadAsync(FixtureSolutionPath);

        Assert.All(result.IndexableProjects, p => Assert.Contains("SimpleLib", p.Name));
    }

    [Fact]
    public async Task LoadNonExistentSolution_ReturnsError()
    {
        var loader = new SolutionLoader();
        var result = await loader.LoadAsync(@"C:\nonexistent\solution.sln");

        Assert.NotEmpty(result.Diagnostics);
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ProjectFilter_Default_ExcludesTestProjects()
    {
        var filter = new ProjectFilter();

        Assert.False(filter.ShouldInclude("MyProject.Tests"));
        Assert.False(filter.ShouldInclude("tests"));
        Assert.True(filter.ShouldInclude("MyProject.Lib"));
    }

    [Fact]
    public void ProjectFilter_IncludeOverridesDefaultExcludes()
    {
        var filter = new ProjectFilter().Include("*.Tests");

        Assert.True(filter.ShouldInclude("MyProject.Tests"));
    }

    [Fact]
    public void ProjectFilter_ExcludeAlwaysWins()
    {
        var filter = new ProjectFilter()
            .Include("MyLib.*")
            .Exclude("*.Legacy");

        Assert.False(filter.ShouldInclude("MyLib.Legacy"));
        Assert.True(filter.ShouldInclude("MyLib.New"));
    }
}
