using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.NavigationToolTests;

/// <summary>
///     Tests that types with compilation errors (e.g. unresolved base types) are still visible
///     to find_symbol and get_symbols_overview. Hexagon.cs has an intentional CS0246 error
///     (IEquatable&lt;NonExistentType&gt;) that causes SymbolFinder to skip it.
/// </summary>
public class ErroredTypeTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools tools = TestFileHelper.CreateNavigationTools(fixture);

    [Fact]
    public async Task FindSymbol_TypeWithCompilationError_FindsType()
    {
        // Act
        string result = await tools.FindSymbol(["Hexagon"], SymbolicKind.Class, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Hexagon");
    }

    [Fact]
    public async Task FindSymbol_TypeWithCompilationError_NoKindFilter_FindsType()
    {
        // Act
        string result = await tools.FindSymbol(["Hexagon"], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Hexagon");
    }

    [Fact]
    public async Task FindSymbol_MemberOfErroredType_FindsMember()
    {
        // Act
        string result = await tools.FindSymbol(["Side"], SymbolicKind.Property, containingType: "Hexagon", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Side");
    }

    [Fact]
    public async Task FindSymbol_ConstructorOfErroredType_FindsConstructor()
    {
        // Act
        string result = await tools.FindSymbol([".ctor"], containingType: "Hexagon", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Hexagon");
    }

    [Fact]
    public async Task FindSymbol_FqnOfErroredType_FindsType()
    {
        // Act
        string result = await tools.FindSymbol(["TestFixture.Shapes.Hexagon"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Hexagon");
    }

    [Fact]
    public async Task GetSymbolsOverview_ErroredType_FindsType()
    {
        // Arrange — baseline: syntax-tree-based tool already sees errored types
        string filePath = fixture.ShapesFile("Hexagon.cs");

        // Act
        string result = await tools.GetSymbolsOverview([filePath], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Hexagon");
    }
}
