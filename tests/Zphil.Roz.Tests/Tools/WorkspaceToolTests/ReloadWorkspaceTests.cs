using Microsoft.CodeAnalysis;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.WorkspaceToolTests;

public class ReloadWorkspaceTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task GetWorkspaceInfo_Reload_ReturnsSuccessMessage()
    {
        // Arrange
        WorkspaceTools tools = TestFileHelper.CreateWorkspaceTools(Fixture);
        Solution solution = await Fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        int expectedProjects = solution.Projects.Count();

        // Act
        string result = await tools.GetWorkspaceInfo(reload: true, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("reloaded successfully");
        result.ShouldContain($"{expectedProjects} projects");
    }
}
