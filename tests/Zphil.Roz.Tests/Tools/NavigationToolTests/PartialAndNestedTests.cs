using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.NavigationToolTests;

public class PartialAndNestedTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools tools = TestFileHelper.CreateNavigationTools(fixture);

    [Fact]
    public async Task FindSymbol_PartialClass_ShowsNonGeneratedLocations()
    {
        // Act
        string result = await tools.FindSymbol(["PartialShapeProcessor"], ct: TestContext.Current.CancellationToken);

        // Assert — generated .g.cs location is suppressed by default
        result.ShouldContain("partial");
        result.ShouldContain("PartialShapeProcessor");
        result.ShouldContain("Locations (2):");
        result.ShouldContain("TypeKindExamples.cs");
        result.ShouldContain("PartialShapeProcessor.Extra.cs");
        result.ShouldNotContain("PartialShapeProcessor.g.cs");
    }

    [Fact]
    public async Task FindSymbol_PartialClass_IncludeGenerated_ShowsAllLocations()
    {
        // Act
        string result = await tools.FindSymbol(["PartialShapeProcessor"], includeGenerated: true, ct: TestContext.Current.CancellationToken);

        // Assert — includeGenerated=true shows all locations including .g.cs
        result.ShouldContain("Locations (3):");
        result.ShouldContain("TypeKindExamples.cs");
        result.ShouldContain("PartialShapeProcessor.Extra.cs");
        result.ShouldContain("PartialShapeProcessor.g.cs");
    }

    [Fact]
    public async Task FindSymbol_NonPartialClass_ShowsSingleLocation()
    {
        // Act
        string result = await tools.FindSymbol(["Circle"], ct: TestContext.Current.CancellationToken);

        // Assert — non-partial type shows singular "Location:" format
        result.ShouldContain("Location:");
        result.ShouldNotContain("Locations (");
    }

    [Fact]
    public async Task FindSymbol_WithDepth2_ShowsNestedTypeMembers()
    {
        // Act
        string result = await tools.FindSymbol(["OuterContainer"], depth: 2, ct: TestContext.Current.CancellationToken);

        // Assert — depth=2 should show nested type AND its members
        result.ShouldContain("InnerProcessor");
        result.ShouldContain("Process");
        result.ShouldContain("Count");
    }

    [Fact]
    public async Task FindSymbol_WithDepth1_ShowsNestedTypeOnly()
    {
        // Act
        string result = await tools.FindSymbol(["OuterContainer"], depth: 1, ct: TestContext.Current.CancellationToken);

        // Assert — depth=1 shows nested type name but not its members
        result.ShouldContain("InnerProcessor");
        result.ShouldNotContain("Process()");
    }

    [Fact]
    public async Task GetSymbolsOverview_FileWithMixedKinds_ShowsAllKinds()
    {
        // Arrange
        string filePath = fixture.ServicesFile("TypeKindExamples.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert — file contains struct, enum, delegate, and class types
        result.ShouldContain("struct");
        result.ShouldContain("enum");
        result.ShouldContain("delegate");
        result.ShouldContain("class");
    }

    [Fact]
    public async Task GetSymbolsOverview_PartialClassMainFile_ShowsOnlyMembersDeclaredInThatFile()
    {
        // Arrange
        string filePath = fixture.ServicesFile("TypeKindExamples.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert — TypeKindExamples.cs declares ProcessName and ProcessCount, but NOT Reset or GeneratedMethod
        result.ShouldContain("ProcessName");
        result.ShouldContain("ProcessCount");
        result.ShouldNotContain("Reset");
        result.ShouldNotContain("GeneratedMethod");
    }

    [Fact]
    public async Task GetSymbolsOverview_PartialClassExtraFile_ShowsOnlyMembersDeclaredInThatFile()
    {
        // Arrange
        string filePath = fixture.ServicesFile("PartialShapeProcessor.Extra.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert — Extra file declares only Reset, not ProcessName or ProcessCount
        result.ShouldContain("Reset");
        result.ShouldNotContain("ProcessName");
        result.ShouldNotContain("ProcessCount");
    }

    [Fact]
    public async Task GetSymbolsOverview_SameFilePartials_DoesNotDuplicateType()
    {
        // Arrange
        string filePath = fixture.ServicesFile("TypeKindExamples.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert — PartialShapeProcessor should appear only once despite two partial declarations
        int count = result.Split("PartialShapeProcessor").Length - 1;
        count.ShouldBe(1);
    }

    [Fact]
    public async Task GetSymbolsOverview_PartialWithGeneratedFile_ShowsLineFromQueriedFile()
    {
        // Arrange — PartialShapeProcessor is declared at line 24 in TypeKindExamples.cs.
        // PartialShapeProcessor.g.cs (generated partial) sorts before TypeKindExamples.cs alphabetically.
        string filePath = fixture.ServicesFile("TypeKindExamples.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert — the inline line number should come from TypeKindExamples.cs (:24), not from the .g.cs file
        string[] lines = result.Split('\n');
        string? partialLine = lines.FirstOrDefault(l => l.Contains("PartialShapeProcessor"));
        partialLine.ShouldNotBeNull();
        partialLine.ShouldContain(":24");
    }

    [Fact]
    public async Task GetSymbolsOverview_GeneratedPartialFile_ShowsMembersFromThatFile()
    {
        // Arrange
        string filePath = fixture.ServicesFile("PartialShapeProcessor.g.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert — generated file shows only GeneratedMethod, not members from other partials
        result.ShouldContain("GeneratedMethod");
        result.ShouldNotContain("ProcessName");
        result.ShouldNotContain("ProcessCount");
        result.ShouldNotContain("Reset");
    }

    [Fact]
    public async Task FindSymbol_GenericWithCustomConstraint_ShowsConstraint()
    {
        // Act
        string result = await tools.FindSymbol(["ShapeProcessor"], ct: TestContext.Current.CancellationToken);

        // Assert — should show the type constraint
        result.ShouldContain("ShapeProcessor");
        result.ShouldContain("where");
        result.ShouldContain("Shape");
    }
}
