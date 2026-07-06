using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.NavigationToolTests;

/// <summary>
///     Tests for go_to_definition when the cursor is on a keyword rather than an identifier.
///     Most keywords fall back to the enclosing declaration; <c>override</c> navigates to the overridden base member.
/// </summary>
public class GoToDefinitionKeywordTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools tools = CreateNavigationTools(fixture);

    [Theory]
    [InlineData("IShape.cs", 6, 1, "IShape")] // 'public' keyword on interface
    [InlineData("Shape.cs", 4, 8, "Shape")] // 'abstract' keyword on class
    [InlineData("Shape.cs", 7, 5, "Area")] // 'public' keyword on member (Area property)
    [InlineData("Circle.cs", 3, 8, "Circle")] // 'class' keyword on Circle
    public async Task GoToDefinition_OnKeyword_ReturnsSymbolInfoWithDeclarationNote(string file, int line, int col,
        string expectedSymbol)
    {
        string filePath = fixture.ShapesFile(file);

        string result = await tools.GoToDefinition(Loc(filePath, line, col), ct: TestContext.Current.CancellationToken);

        result.ShouldContain(expectedSymbol);
        result.ShouldContain("At declaration");
    }

    [Fact]
    public async Task GoToDefinition_OnInterfaceName_ReturnsSymbolInfoWithDeclarationNote()
    {
        // Arrange — "public interface IShape" — IShape starts at col 18
        string filePath = fixture.ShapesFile("IShape.cs");

        string result = await tools.GoToDefinition(Loc(filePath, 6, 18), ct: TestContext.Current.CancellationToken);

        result.ShouldContain("IShape");
        result.ShouldContain("At declaration");
    }

    [Theory]
    [InlineData("Triangle.cs", 16, 12, "Shape", "Describe")] // override method → Shape.Describe
    [InlineData("Circle.cs", 7, 12, "Shape", "Area")] // override property (abstract) → Shape.Area
    [InlineData("Rectangle.cs", 8, 12, "Shape", "Area")] // override property (another class) → Shape.Area
    public async Task GoToDefinition_OnOverrideKeyword_ResolvesToBaseMember(string file, int line, int col,
        string expectedType, string expectedMember)
    {
        string filePath = fixture.ShapesFile(file);

        string result = await tools.GoToDefinition(Loc(filePath, line, col), ct: TestContext.Current.CancellationToken);

        result.ShouldContain(expectedType);
        result.ShouldContain(expectedMember);
        result.ShouldNotContain("At declaration");
    }

    [Fact]
    public async Task GoToDefinition_OnRegionDirective_ThrowsWithDirectiveTriviaMessage()
    {
        // Arrange — ShapeService.cs line 45 is "#region Utilities"
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act & Assert — should reject, not snap to enclosing ShapeService class
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.GoToDefinition(Loc(filePath, 45, 5)));
        ex.Message.ShouldContain("RegionDirectiveTrivia");
    }
}
