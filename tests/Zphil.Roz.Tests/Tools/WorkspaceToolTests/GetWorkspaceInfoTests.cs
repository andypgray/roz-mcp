using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.WorkspaceToolTests;

public class GetWorkspaceInfoTests(WorkspaceFixture fixture)
{
    private readonly WorkspaceTools tools = TestFileHelper.CreateWorkspaceTools(fixture);

    [Fact]
    public async Task GetWorkspaceInfo_ReturnsExpectedSolutionName()
    {
        // Act
        string result = await tools.GetWorkspaceInfo(ct: TestContext.Current.CancellationToken);

        // Assert — pin the solution-name line specifically ("TestFixture" alone permeates the output)
        result.ShouldContain("Solution: TestFixture");
    }

    [Fact]
    public async Task GetWorkspaceInfo_ReturnsCorrectProjectCount()
    {
        // Arrange
        (int logical, int total) = await fixture.GetExpectedProjectCountsAsync(TestContext.Current.CancellationToken);

        // Act
        string result = await tools.GetWorkspaceInfo(ct: TestContext.Current.CancellationToken);

        // Assert — format + semantic check that multi-TFM grouping happens
        result.ShouldContain($"{logical} logical, {total} total");
        logical.ShouldBeLessThan(total, "fixture invariant: TestFixture.MultiTfm produces two TFM variants that must merge into one logical project");
    }

    [Fact]
    public async Task GetWorkspaceInfo_DocumentCountExcludesGeneratedFiles()
    {
        // Arrange — fixture invariant: the loaded solution must have at least one
        // generated file (obj/ build artifacts are always present after a build)
        int expectedDocs = await fixture.GetExpectedDocCountAsync(ct: TestContext.Current.CancellationToken);
        int generatedDocs = await fixture.GetGeneratedDocCountAsync(TestContext.Current.CancellationToken);
        generatedDocs.ShouldBeGreaterThan(0, "fixture must contain generated files (e.g. obj/ build artifacts) to test exclusion");

        // Act
        string result = await tools.GetWorkspaceInfo(ct: TestContext.Current.CancellationToken);

        // Assert — formatter displays the service-computed count, and that count
        // excludes the known-generated docs
        result.ShouldContain($"{expectedDocs} documents");
        result.ShouldNotContain($"{expectedDocs + generatedDocs} documents");
    }

    [Fact]
    public async Task GetWorkspaceInfo_IncludesTargetFramework()
    {
        // Act
        string result = await tools.GetWorkspaceInfo(ct: TestContext.Current.CancellationToken);

        // Assert — TestFixture targets net10.0 (or similar)
        result.ShouldMatch(@"net\d+\.\d+");
    }

    [Fact]
    public async Task GetWorkspaceInfo_NoNuGetPackages_OmitsPackageSection()
    {
        // Act
        string result = await tools.GetWorkspaceInfo(ct: TestContext.Current.CancellationToken);

        // Assert — TestFixture has no NuGet packages, so the section should be absent
        result.ShouldNotContain("NuGet packages:");
    }

    [Fact]
    public async Task GetWorkspaceInfo_IncludesLanguageVersion()
    {
        // Act
        string result = await tools.GetWorkspaceInfo(ct: TestContext.Current.CancellationToken);

        // Assert — should show C# language version (e.g. "C# 14.0" or similar)
        result.ShouldMatch(@"C# \d+(\.\d+)?");
    }

    [Fact]
    public async Task GetWorkspaceInfo_IncludesNullableContext()
    {
        // Act
        string result = await tools.GetWorkspaceInfo(ct: TestContext.Current.CancellationToken);

        // Assert — TestFixture has <Nullable>enable</Nullable>
        result.ShouldContain("nullable=enable");
    }

    [Fact]
    public async Task GetWorkspaceInfo_ShowsProjectDependencies()
    {
        // Act
        string result = await tools.GetWorkspaceInfo(ct: TestContext.Current.CancellationToken);

        // Assert — dependency section shows projects with their direct references
        result.ShouldContain("Project Dependencies:");
    }

    [Fact]
    public async Task GetWorkspaceInfo_DependencyUsesArrowFormat()
    {
        // Act
        string result = await tools.GetWorkspaceInfo(ct: TestContext.Current.CancellationToken);

        // Assert — should use arrow format for dependency references
        result.ShouldContain("\u2192 TestFixture");
    }

    [Fact]
    public async Task GetWorkspaceInfo_NullableDisableProject_ShowsNullableDisable()
    {
        // Act
        string result = await tools.GetWorkspaceInfo(ct: TestContext.Current.CancellationToken);

        // Assert — TestFixture.Minimal has <Nullable>disable</Nullable>
        result.ShouldContain("nullable=disable");
    }

    [Fact]
    public async Task GetWorkspaceInfo_IncludesAllProjectNames()
    {
        // Act
        string result = await tools.GetWorkspaceInfo(ct: TestContext.Current.CancellationToken);

        // Assert — selected projects appear in the output with type tags
        result.ShouldContain("TestFixture [classlib] (C#");
        result.ShouldContain("TestFixture.Minimal [classlib]");
        result.ShouldContain("TestFixture.Razor [razor]");
        result.ShouldContain("TestFixture.Tests [test]");
        result.ShouldContain("TestFixture.TopLevel [console]");
    }

    [Fact]
    public async Task GetWorkspaceInfo_DependencyShowsMinimalDependsOnTestFixture()
    {
        // Act
        string result = await tools.GetWorkspaceInfo(ct: TestContext.Current.CancellationToken);

        // Assert — TestFixture.Minimal references TestFixture
        result.ShouldContain("TestFixture.Minimal \u2192 TestFixture");
    }

    // Project filter tests

    [Fact]
    public async Task GetWorkspaceInfo_ProjectFilter_ReturnsOnlyMatchingProjects()
    {
        // Act
        string result = await tools.GetWorkspaceInfo("Minimal", ct: TestContext.Current.CancellationToken);

        // Assert — only Minimal project appears
        result.ShouldContain("TestFixture.Minimal");
        result.ShouldNotContain("TestFixture.Tests");
        result.ShouldNotContain("TestFixture.TopLevel");
    }

    [Fact]
    public async Task GetWorkspaceInfo_ProjectFilter_IsCaseInsensitive()
    {
        // Act
        string result = await tools.GetWorkspaceInfo("minimal", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("TestFixture.Minimal");
    }

    [Fact]
    public async Task GetWorkspaceInfo_ProjectFilter_NoMatch_ReturnsMessage()
    {
        // Act
        string result = await tools.GetWorkspaceInfo("NonExistentProject", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No projects match");
        result.ShouldContain("NonExistentProject");
    }

    [Fact]
    public async Task GetWorkspaceInfo_ProjectFilter_DocumentCountReflectsFilteredProjects()
    {
        // Arrange
        int filteredDocs = await fixture.GetExpectedDocCountAsync("Minimal", TestContext.Current.CancellationToken);
        int unfilteredDocs = await fixture.GetExpectedDocCountAsync(ct: TestContext.Current.CancellationToken);

        // Act
        string result = await tools.GetWorkspaceInfo("Minimal", ct: TestContext.Current.CancellationToken);

        // Assert — filtered count is shown, and filtering actually reduced the count
        result.ShouldContain($"{filteredDocs} documents");
        filteredDocs.ShouldBeLessThan(unfilteredDocs, "filtering to Minimal must reduce the document count below the solution-wide total");
        result.ShouldNotContain($"{unfilteredDocs} documents");
    }

    [Fact]
    public async Task GetWorkspaceInfo_ProjectFilter_FiltersDependenciesToo()
    {
        // Act — filter to TestFixture.Minimal which depends on TestFixture
        string result = await tools.GetWorkspaceInfo("Minimal", ct: TestContext.Current.CancellationToken);

        // Assert — dependency for Minimal shown, but not for other projects
        result.ShouldContain("TestFixture.Minimal");
        // Dependency graph should only contain filtered projects' entries
        result.ShouldNotContain("TestFixture.Tests \u2192");
    }

    // Reverse dependency tests

    [Fact]
    public async Task GetWorkspaceInfo_FullSolution_ShowsReverseDependencies()
    {
        // Act
        string result = await tools.GetWorkspaceInfo(ct: TestContext.Current.CancellationToken);

        // Assert — TestFixture is depended on by Minimal, Razor, and Tests
        result.ShouldContain("Depended on by:");
        result.ShouldContain("TestFixture \u2190");
    }

    [Fact]
    public async Task GetWorkspaceInfo_ProjectFilter_ShowsReverseDependencies()
    {
        // Act — filter to TestFixture (the root lib that others depend on)
        string result = await tools.GetWorkspaceInfo("TestFixture", ct: TestContext.Current.CancellationToken);

        // Assert — reverse deps show which projects depend on TestFixture
        result.ShouldContain("Depended on by:");
        result.ShouldContain("TestFixture.Minimal");
        result.ShouldContain("TestFixture.Razor");
        result.ShouldContain("TestFixture.Tests");
    }

    [Fact]
    public async Task GetWorkspaceInfo_ProjectFilter_NoDependents_OmitsReverseDepsSection()
    {
        // Act — filter to TopLevel (nothing depends on it)
        string result = await tools.GetWorkspaceInfo("TopLevel", ct: TestContext.Current.CancellationToken);

        // Assert — no reverse dependency section
        result.ShouldNotContain("Depended on by:");
    }

    [Fact]
    public async Task GetWorkspaceInfo_ReloadWithProjectFilter_Throws()
    {
        // Act & Assert
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.GetWorkspaceInfo("Minimal", true));
        ex.Message.ShouldContain("Cannot combine reload=true with a project filter");
    }
}
