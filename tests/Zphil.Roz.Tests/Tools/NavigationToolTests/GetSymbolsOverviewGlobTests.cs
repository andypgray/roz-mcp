using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.NavigationToolTests;

public class GetSymbolsOverviewGlobTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools tools = TestFileHelper.CreateNavigationTools(fixture);

    [Fact]
    public async Task GetSymbolsOverview_GlobPattern_MatchesMultipleFiles()
    {
        // Act — glob for all shape files
        string result = await tools.GetSymbolsOverview(["TestFixture/Shapes/C*.cs"], ct: TestContext.Current.CancellationToken);

        // Assert — Circle.cs matches
        result.ShouldContain("Circle");
    }

    [Fact]
    public async Task GetSymbolsOverview_DoubleStarGlob_MatchesAcrossDirectories()
    {
        // Act — ** glob matches any directory depth
        string result = await tools.GetSymbolsOverview(["**/IShape.cs"], ct: TestContext.Current.CancellationToken);

        // Assert — should find IShape
        result.ShouldContain("IShape");
        result.ShouldContain("Area");
    }

    [Fact]
    public async Task GetSymbolsOverview_GlobPattern_NoMatch_ShowsNearbyFiles()
    {
        // Act — glob under a real directory but matching nothing
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.GetSymbolsOverview(["TestFixture/Shapes/NonExistent*.xyz"]));

        // Assert — error should show nearby files under the prefix
        ex.Message.ShouldContain("matched no files");
        ex.Message.ShouldContain("Files under 'TestFixture/Shapes/'");
        ex.Message.ShouldContain(".cs");
    }

    [Fact]
    public async Task GetSymbolsOverview_GlobPattern_NoMatch_WrongDirectory_ShowsFallback()
    {
        // Act — completely wrong directory name
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.GetSymbolsOverview(["CompletelyWrong/**/*.cs"]));

        // Assert — error should suggest get_workspace_info
        ex.Message.ShouldContain("matched no files");
        ex.Message.ShouldContain("No files found under 'CompletelyWrong/'");
        ex.Message.ShouldContain("get_workspace_info");
    }

    [Fact]
    public async Task GetSymbolsOverview_GlobPattern_ExceedsMaxFiles_ShowsExamples()
    {
        // Act — glob that matches many files with very low maxFiles
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.GetSymbolsOverview(["**/*.cs"], maxFiles: 1));

        // Assert — error should include example paths
        ex.Message.ShouldContain("exceeding maxFiles=1");
        ex.Message.ShouldContain("Example matches");
        ex.Message.ShouldContain(".cs");
    }

    [Fact]
    public async Task GetSymbolsOverview_GlobPattern_ExceedsMaxFiles_DoesNotMentionProjectHint()
    {
        // get_symbols_overview already has a project parameter, so its glob-expansion site
        // deliberately does not emit the "Or use project=<name>" recovery hint.
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.GetSymbolsOverview(["**/*.cs"], maxFiles: 1));

        ex.Message.ShouldNotContain("Or use project=");
    }

    [Fact]
    public async Task GetSymbolsOverview_MixedGlobAndExplicitPaths_Works()
    {
        // Arrange
        string explicitPath = fixture.ShapesFile("IShape.cs");

        // Act — mix explicit path with glob
        string result = await tools.GetSymbolsOverview([explicitPath, "TestFixture/Shapes/Circle.cs"], ct: TestContext.Current.CancellationToken);

        // Assert — both should appear
        result.ShouldContain("IShape");
        result.ShouldContain("Circle");
    }

    [Fact]
    public async Task GetSymbolsOverview_NonGlobPath_PassesThroughUnchanged()
    {
        // Arrange
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — plain path, no globs
        string result = await tools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert — normal behavior
        result.ShouldContain("IShape");
    }

    [Fact]
    public async Task GetSymbolsOverview_QuestionMarkGlob_MatchesSingleChar()
    {
        // Act — Shape.cs has 5 letters, ?hape.cs should match
        string result = await tools.GetSymbolsOverview(["TestFixture/Shapes/?hape.cs"], ct: TestContext.Current.CancellationToken);

        // Assert — Shape.cs should match
        result.ShouldContain("Shape");
    }

    [Fact]
    public async Task GetSymbolsOverview_GlobWithMaxFiles_DefaultAllowsTwenty()
    {
        // Act — default maxFiles=20, TestFixture has ~20 files; use a narrow pattern
        string result = await tools.GetSymbolsOverview(["TestFixture/Shapes/*.cs"], ct: TestContext.Current.CancellationToken);

        // Assert — all shape files should be returned
        result.ShouldContain("IShape");
        result.ShouldContain("Circle");
        result.ShouldContain("Shape");
    }
}
