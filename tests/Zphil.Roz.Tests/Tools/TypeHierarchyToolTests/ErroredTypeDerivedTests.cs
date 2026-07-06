using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.TypeHierarchyToolTests;

/// <summary>
///     Tests that find_implementations (type dispatch) includes types with compilation errors.
///     Hexagon.cs extends Shape but has an unresolved IEquatable&lt;NonExistentType&gt;
///     that causes SymbolFinder.FindDerivedClassesAsync to skip it.
/// </summary>
public class ErroredTypeDerivedTests(WorkspaceFixture fixture)
{
    private readonly ReferenceTools referenceTools = CreateReferenceTools(fixture);

    [Fact]
    public async Task FindImplementations_OnClass_IncludesTypesWithCompilationErrors()
    {
        // Arrange — "public abstract class Shape : IShape" at line 4, col 23
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await referenceTools.FindImplementations([Loc(filePath, 4, 23)], ct: TestContext.Current.CancellationToken);

        // Assert — Hexagon extends Shape but has a compilation error
        result.ShouldContain("Hexagon");
    }

    [Fact]
    public async Task FindImplementations_OnInterface_IncludesTypesWithCompilationErrors()
    {
        // Act — IShape is implemented by Shape, which Hexagon extends
        string result = await referenceTools.FindImplementations(symbolNames: ["IShape"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Hexagon");
    }

    [Fact]
    public async Task FindImplementations_BySymbolName_Class_IncludesErroredTypes()
    {
        // Act
        string result = await referenceTools.FindImplementations(symbolNames: ["Shape"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Hexagon");
    }

    [Fact]
    public async Task FindImplementations_ErroredType_HasSourceLocation()
    {
        // Act
        string result = await referenceTools.FindImplementations(symbolNames: ["Shape"], ct: TestContext.Current.CancellationToken);

        // Assert — Hexagon should show its source file, not be treated as metadata-only
        result.ShouldContain("Hexagon.cs");
    }
}
