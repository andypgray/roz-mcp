using Microsoft.CodeAnalysis;
using Zphil.Roz.Extensions;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Extensions;

public class SolutionExtensionsTests(WorkspaceFixture fixture)
{
    [Fact]
    public async Task EnumerateSourceDocumentPaths_ExcludesBuildOutput()
    {
        // Arrange
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        string dir = await fixture.WorkspaceManager.GetRequiredSolutionDirectoryAsync(TestContext.Current.CancellationToken);

        // Act
        DocumentPathEntry[] entries = solution.EnumerateSourceDocumentPaths(dir).ToArray();

        // Assert — obj/bin excluded, relative paths forward-slashed, distinct by absolute path.
        entries.ShouldNotBeEmpty();
        entries.ShouldAllBe(e => !PathExtensions.ContainsDirectorySegment(e.NormalizedRelativePath, "obj"));
        entries.ShouldAllBe(e => !PathExtensions.ContainsDirectorySegment(e.NormalizedRelativePath, "bin"));
        entries.ShouldAllBe(e => !e.NormalizedRelativePath.Contains('\\'));
        entries.Select(e => e.AbsolutePath).ShouldBeUnique();
    }
}
