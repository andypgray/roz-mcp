using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.NavigationToolTests;

public class FindSymbolFilePathsFilterTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools tools = TestFileHelper.CreateNavigationTools(fixture);

    [Fact]
    public async Task FindSymbol_WithFilePaths_RestrictsToSpecifiedFile()
    {
        // Arrange
        string circlePath = fixture.ShapesFile("Circle.cs");

        // Act — search for "Area" but only in Circle.cs
        string result = await tools.FindSymbol(["Area"], filePaths: [circlePath], ct: TestContext.Current.CancellationToken);

        // Assert — only Circle.Area should appear
        result.ShouldContain("Circle");
        result.ShouldNotContain("Rectangle");
        result.ShouldNotContain("Triangle");
    }

    [Fact]
    public async Task FindSymbol_WithFilePaths_MultipleFiles_RestrictsToThoseFiles()
    {
        // Arrange
        string circlePath = fixture.ShapesFile("Circle.cs");
        string rectanglePath = fixture.ShapesFile("Rectangle.cs");

        // Act — search for "Area" in Circle.cs and Rectangle.cs
        string result = await tools.FindSymbol(["Area"], filePaths: [circlePath, rectanglePath], ct: TestContext.Current.CancellationToken);

        // Assert — both Circle.Area and Rectangle.Area should appear, but not Triangle.Area
        result.ShouldContain("Circle");
        result.ShouldContain("Rectangle");
        result.ShouldNotContain("Triangle");
    }

    [Fact]
    public async Task FindSymbol_WithFilePaths_NoMatchInFile_ReturnsNoResults()
    {
        // Arrange — IShape.cs doesn't define "Circle"
        string ishapePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindSymbol(["Circle"], filePaths: [ishapePath], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No symbols found");
        result.ShouldContain("in files");
    }

    [Fact]
    public async Task FindSymbol_WithRelativeFilePaths_Works()
    {
        // Act — use a relative path
        string result = await tools.FindSymbol(["Circle"], filePaths: ["TestFixture/Shapes/Circle.cs"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Circle");
        result.ShouldNotContain("No symbols found");
    }

    [Fact]
    public async Task FindSymbol_WithFilePathsRedundantPrefix_RestrictsToSpecifiedFile()
    {
        // Arrange — redundant project prefix: "TestFixture/TestFixture/Shapes/Circle.cs"
        // must suffix-match "TestFixture/Shapes/Circle.cs".
        const string redundantPrefixPath = "TestFixture/TestFixture/Shapes/Circle.cs";

        // Act
        string result = await tools.FindSymbol(["Area"], filePaths: [redundantPrefixPath], ct: TestContext.Current.CancellationToken);

        // Assert — Circle.Area appears, other shapes' Area members do not
        result.ShouldContain("Circle");
        result.ShouldNotContain("Rectangle");
        result.ShouldNotContain("Triangle");
    }

    [Fact]
    public async Task FindSymbol_WithFilePaths_CombinesWithOtherFilters()
    {
        // Arrange
        string shapePath = fixture.ShapesFile("Shape.cs");

        // Act — search for "Shape" with kind=Class in Shape.cs only (should exclude ShapeService)
        string result = await tools.FindSymbol(["Shape"], SymbolicKind.Class, filePaths: [shapePath], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Shape");
        result.ShouldNotContain("ShapeService");
        result.ShouldNotContain("ShapeHelper");
    }

    [Fact]
    public async Task FindSymbol_WithoutFilePaths_SearchesAllFiles()
    {
        // Act — no filePaths filter, should find symbols across all files
        string result = await tools.FindSymbol(["Area"], maxResults: 20, ct: TestContext.Current.CancellationToken);

        // Assert — Area exists in multiple files
        result.ShouldContain("Found");
        result.ShouldNotContain("No symbols found");
    }

    [Fact]
    public async Task FindSymbol_WithGlobPattern_ExpandsAndFilters()
    {
        // Act — glob matching only C*.cs in Shapes folder
        string result = await tools.FindSymbol(["Area"], filePaths: ["TestFixture/Shapes/C*.cs"], ct: TestContext.Current.CancellationToken);

        // Assert — only Circle.Area should appear
        result.ShouldContain("Circle");
        result.ShouldNotContain("Rectangle");
        result.ShouldNotContain("Triangle");
    }

    [Fact]
    public async Task FindSymbol_WithDoubleStarGlob_MatchesAcrossDirectories()
    {
        // Act — ** glob finds Circle.cs regardless of directory
        string result = await tools.FindSymbol(["Circle"], filePaths: ["**/Circle.cs"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Circle");
        result.ShouldNotContain("No symbols found");
    }

    [Fact]
    public async Task FindSymbol_WithMixedLiteralAndGlob_Works()
    {
        // Arrange
        string trianglePath = fixture.ShapesFile("Triangle.cs");

        // Act — one literal path + one glob
        string result = await tools.FindSymbol(["Area"], filePaths: [trianglePath, "TestFixture/Shapes/C*.cs"], ct: TestContext.Current.CancellationToken);

        // Assert — Triangle (literal) and Circle (glob) but not Rectangle
        result.ShouldContain("Circle");
        result.ShouldContain("Triangle");
        result.ShouldNotContain("Rectangle");
    }

    [Fact]
    public async Task FindSymbol_WithGlobMatchingNoFiles_ReturnsError()
    {
        // Act — ExpandGlobPatternsAsync error is captured inline per name
        string result = await tools.FindSymbol(["Circle"], filePaths: ["**/Nonexistent*.cs"], ct: TestContext.Current.CancellationToken);
        result.ShouldContain("matched no files");
    }
}
