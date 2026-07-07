using Microsoft.CodeAnalysis;
using Zphil.Roz.Extensions;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Extensions;

// Shares the non-parallel "TestClassifierStatics" collection (defined in TestClassifierTests) because
// TestClassifier.SetOverrides there mutates the process-global statics IsTestProject() reads. (TQ-H4)
[Collection("TestClassifierStatics")]
public class IsTestProjectTests(WorkspaceFixture fixture)
{
    [Theory]
    [InlineData("TestFixture.Tests", true)]
    [InlineData("TestFixture", false)]
    [InlineData("TestFixture.Razor", false)]
    public async Task IsTestProject_Project_ReturnsExpected(string projectName, bool expected)
    {
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Project? project = solution.Projects.FirstOrDefault(p => p.Name == projectName);

        project.ShouldNotBeNull();
        project.IsTestProject().ShouldBe(expected);
    }

    // ── IsInTestProject ─────────────────────────────────────────────────────

    [Fact]
    public async Task IsInTestProject_SymbolFromMainProject_ReturnsFalse()
    {
        // Arrange — Circle is declared in TestFixture (non-test project)
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Project mainProject = solution.Projects.Single(p => p.Name == "TestFixture");
        Compilation? compilation = await mainProject.GetCompilationAsync(TestContext.Current.CancellationToken);
        INamedTypeSymbol? circle = compilation!.GetTypeByMetadataName("TestFixture.Shapes.Circle");

        // Act
        circle.ShouldNotBeNull();
        bool result = circle.IsInTestProject(solution);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task IsInTestProject_MetadataSymbol_ReturnsFalse()
    {
        // Arrange — a BCL type has no source location in the solution
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Project project = solution.Projects.Single(p => p.Name == "TestFixture");
        Compilation? compilation = await project.GetCompilationAsync(TestContext.Current.CancellationToken);
        INamedTypeSymbol? listType = compilation!.GetTypeByMetadataName("System.Collections.Generic.List`1");

        // Act
        listType.ShouldNotBeNull();
        bool result = listType.IsInTestProject(solution);

        // Assert — metadata-only symbols have no source location, should return false
        result.ShouldBeFalse();
    }
}
