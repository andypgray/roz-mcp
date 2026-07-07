using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.NavigationToolTests;

public class GetSymbolsOverviewTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools tools = TestFileHelper.CreateNavigationTools(fixture);

    [Fact]
    public async Task GetSymbolsOverview_ReturnsTopLevelTypeAndMembers()
    {
        // Arrange
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("IShape");
        result.ShouldContain("Area");
        result.ShouldContain("Perimeter");
        result.ShouldContain("Describe");
    }

    [Fact]
    public async Task GetSymbolsOverview_WithDepthZero_ShowsTypeNameAndMemberCount()
    {
        // Arrange
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], 0, ct: TestContext.Current.CancellationToken);

        // Assert — type name visible with member count summary, but individual members not listed
        result.ShouldContain("IShape");
        result.ShouldContain("3 members (2 properties, 1 method)");
        result.ShouldNotContain("[public");
    }

    [Fact]
    public async Task GetSymbolsOverview_WithDepthZero_ShowsMemberCountForAbstractClass()
    {
        // Arrange
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], 0, ct: TestContext.Current.CancellationToken);

        // Assert — Shape has 2 properties (Area, Perimeter) and 1 method (Describe)
        result.ShouldContain("Shape");
        result.ShouldContain("3 members (2 properties, 1 method)");
    }

    [Fact]
    public async Task GetSymbolsOverview_WithDepthZero_EnumSkipsMemberCount()
    {
        // Arrange
        string filePath = fixture.ServicesFile("TypeKindExamples.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], 0, ct: TestContext.Current.CancellationToken);

        // Assert — enums and delegates should not show member counts
        result.ShouldContain("ShapeColor");
        result.ShouldContain("ShapeMetricFunc");
        string[] lines = result.Split('\n');
        string? shapeColorLine = lines.FirstOrDefault(l => l.Contains("ShapeColor"));
        shapeColorLine.ShouldNotBeNull();
        int shapeColorIndex = Array.IndexOf(lines, shapeColorLine);
        // The line after the enum should NOT be a member count
        if (shapeColorIndex + 1 < lines.Length)
        {
            lines[shapeColorIndex + 1].ShouldNotContain("members");
        }
    }

    [Fact]
    public async Task GetSymbolsOverview_WithDepthZero_MemberKindsFilter_CountsRespectFilter()
    {
        // Arrange
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — filter to methods only
        string result = await tools.GetSymbolsOverview([filePath], 0, memberKinds: [SymbolicKind.Method], ct: TestContext.Current.CancellationToken);

        // Assert — count should reflect only methods
        result.ShouldContain("1 member (1 method)");
        result.ShouldNotContain("propert");
    }

    [Fact]
    public async Task GetSymbolsOverview_NonExistentFile_ReturnsError()
    {
        // Act
        string result = await tools.GetSymbolsOverview(["NonExistent.cs"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Error");
        result.ShouldContain("not found");
    }

    // ── Batch tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSymbolsOverview_MultipleFiles_ReturnsSectionPerFile()
    {
        // Arrange
        string iShapeFile = fixture.ShapesFile("IShape.cs");
        string shapeFile = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await tools.GetSymbolsOverview([iShapeFile, shapeFile], ct: TestContext.Current.CancellationToken);

        // Assert — section headers for each file
        result.ShouldContain("=== ");
        result.ShouldContain("IShape.cs ===");
        result.ShouldContain("Shape.cs ===");

        // Assert — symbols from both files present
        result.ShouldContain("IShape");
        result.ShouldContain("Shape");
    }

    [Fact]
    public async Task GetSymbolsOverview_MultipleFiles_OneInvalid_ReturnsResultsAndError()
    {
        // Arrange
        string validFile = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.GetSymbolsOverview([validFile, "NonExistent.cs"], ct: TestContext.Current.CancellationToken);

        // Assert — valid file results present
        result.ShouldContain("IShape");

        // Assert — error for invalid file present
        result.ShouldContain("Error");
        result.ShouldContain("NonExistent.cs");
        result.ShouldContain("not found");
    }

    // ── Deduplication ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSymbolsOverview_DuplicateFilePaths_DeduplicatesOutput()
    {
        // Arrange
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — same path twice
        string result = await tools.GetSymbolsOverview([filePath, filePath], ct: TestContext.Current.CancellationToken);

        // Assert — should produce a single result, not a batch with duplicate sections
        result.ShouldNotContain("===");
        result.ShouldContain("IShape");
    }

    // ── Top-level statements ────────────────────────────────────────────────────

    [Fact]
    public async Task GetSymbolsOverview_TopLevelStatements_ReturnsGuidanceMessage()
    {
        // Arrange
        string filePath = fixture.TopLevelFile("Program.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert — should detect top-level statements and return guidance
        result.ShouldContain("top-level statements");
        result.ShouldContain("Program.cs");
        result.ShouldNotContain("No type declarations found");
    }

    // ── Member modifiers ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSymbolsOverview_ShowsMemberAccessAndModifiers()
    {
        // Arrange
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert — members show access modifiers and abstract/virtual
        result.ShouldContain("[public abstract property]");
        result.ShouldContain("[public virtual method]");
    }

    // ── memberKinds ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSymbolsOverview_MemberKindsMethod_ShowsOnlyMethods()
    {
        // Arrange
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act — Shape.cs has properties (Area, Perimeter) and a method (Describe)
        string result = await tools.GetSymbolsOverview([filePath], memberKinds: [SymbolicKind.Method], ct: TestContext.Current.CancellationToken);

        // Assert — only the method should appear, not properties
        result.ShouldContain("Describe");
        result.ShouldNotContain("Area");
        result.ShouldNotContain("Perimeter");
    }

    [Fact]
    public async Task GetSymbolsOverview_MemberKindsProperty_ShowsOnlyProperties()
    {
        // Arrange
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], memberKinds: [SymbolicKind.Property], ct: TestContext.Current.CancellationToken);

        // Assert — only properties should appear
        result.ShouldContain("Area");
        result.ShouldContain("Perimeter");
        result.ShouldNotContain("Describe");
    }

    // ── maxTypes ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSymbolsOverview_MaxTypes_TruncatesWithHint()
    {
        // Arrange — TypeKindExamples.cs has 6+ types (Point, ShapeColor, ShapeMetricFunc, etc.)
        string filePath = fixture.ServicesFile("TypeKindExamples.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], maxTypes: 2, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("total");
        result.ShouldContain("increase maxTypes");
    }

    [Fact]
    public async Task GetSymbolsOverview_MaxTypes_NoTruncationWhenUnderLimit()
    {
        // Arrange — IShape.cs has only 1 type
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], maxTypes: 50, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldNotContain("maxTypes");
        result.ShouldContain("IShape");
    }

    [Fact]
    public async Task GetSymbolsOverview_MaxTypes_EnforcedGloballyAcrossFiles()
    {
        // Arrange — 7 shape files, each with 1 type (IShape, Shape, Circle, Rectangle, Triangle, Square, Pentagon)
        string[] filePaths =
        [
            fixture.ShapesFile("IShape.cs"),
            fixture.ShapesFile("Shape.cs"),
            fixture.ShapesFile("Circle.cs"),
            fixture.ShapesFile("Rectangle.cs"),
            fixture.ShapesFile("Triangle.cs"),
            fixture.ShapesFile("Square.cs"),
            fixture.ShapesFile("Pentagon.cs")
        ];

        // Act — maxTypes=3 should return at most 3 types globally, not 3 per file
        string result = await tools.GetSymbolsOverview(filePaths, 0, maxTypes: 3, ct: TestContext.Current.CancellationToken);

        // Assert — global truncation hint with correct counts
        result.ShouldContain("7 total types across 7 files");

        // Only 3 file sections should appear (files with 0 symbols after trimming are removed)
        int fileSections = result.Split("=== ").Length - 1;
        fileSections.ShouldBe(3);
    }

    // ── Project + filePaths ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetSymbolsOverview_ProjectWithFilePaths_FiltersToMatchingFiles()
    {
        // Act — project "TestFixture" has many files, but glob should narrow to Circle.cs only
        string result = await tools.GetSymbolsOverview(["**/Circle.cs"], project: "TestFixture", ct: TestContext.Current.CancellationToken);

        // Assert — only Circle type should appear, not other shapes
        result.ShouldContain("Circle");
        result.ShouldNotContain("IShape");
        result.ShouldNotContain("Rectangle");
        result.ShouldNotContain("Triangle");
        result.ShouldNotContain("ShapeService");
    }

    [Fact]
    public async Task GetSymbolsOverview_ProjectWithNonMatchingFilePaths_ThrowsError()
    {
        // Act & Assert — glob matches nothing in the project
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.GetSymbolsOverview(
            ["**/NonExistent.cs"],
            project: "TestFixture"));

        ex.Message.ShouldContain("matched no files");
    }

    // ── Nested type handling ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetSymbolsOverview_Depth0_IncludesNestedTypes()
    {
        // Arrange — TypeKindExamples.cs has OuterContainer with nested InnerProcessor
        string filePath = fixture.ServicesFile("TypeKindExamples.cs");

        // Act — depth=0 shows a flat list of all types including nested
        string result = await tools.GetSymbolsOverview([filePath], 0, ct: TestContext.Current.CancellationToken);

        // Assert — both the outer type and nested type appear as separate entries
        result.ShouldContain("OuterContainer");
        result.ShouldContain("InnerProcessor");
    }

    [Fact]
    public async Task GetSymbolsOverview_Depth1_ExcludesNestedTypesFromEntries()
    {
        // Arrange — TypeKindExamples.cs has OuterContainer with nested InnerProcessor
        string filePath = fixture.ServicesFile("TypeKindExamples.cs");

        // Act — depth=1 shows members; nested types appear only as members of their parent
        string result = await tools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert — InnerProcessor appears as a member of OuterContainer, not as a separate type entry
        result.ShouldContain("OuterContainer");
        result.ShouldContain("InnerProcessor");

        // The nested type should appear indented under OuterContainer's members, not as a top-level entry
        string[] lines = result.Split('\n');
        string? nestedLine = lines.FirstOrDefault(l => l.Contains("InnerProcessor"));
        nestedLine.ShouldNotBeNull();
        nestedLine.ShouldStartWith("    "); // indented as a member, not a top-level entry
    }

    // ── Empty project ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSymbolsOverview_EmptyProject_ReturnsDescriptiveMessage()
    {
        // Arrange — TestFixture.Empty has 0 source documents

        // Act
        string result = await tools.GetSymbolsOverview(project: "TestFixture.Empty", ct: TestContext.Current.CancellationToken);

        // Assert — should acknowledge the project exists but has no files
        result.ShouldContain("TestFixture.Empty");
        result.ShouldContain("no source documents");
    }

    [Fact]
    public async Task GetSymbolsOverview_NonExistentProject_ThrowsError()
    {
        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() =>
            tools.GetSymbolsOverview(project: "CompletelyBogusProject"));

        ex.Message.ShouldContain("CompletelyBogusProject");
        ex.Message.ShouldContain("No project");
    }

    // ── maxTypes + depth ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSymbolsOverview_MaxTypes_WithDepth1_CountsOnlyTopLevelTypes()
    {
        // Arrange — TypeKindExamples.cs has 11 non-nested types + 1 nested (InnerProcessor)
        string filePath = fixture.ServicesFile("TypeKindExamples.cs");

        // Act — maxTypes=2 with depth=1 should count only non-nested types
        string result = await tools.GetSymbolsOverview([filePath], maxTypes: 2, depth: 1, ct: TestContext.Current.CancellationToken);

        // Assert — truncation hint should reflect non-nested type count (11), not total including nested (12)
        result.ShouldContain("11 total");
    }
}
