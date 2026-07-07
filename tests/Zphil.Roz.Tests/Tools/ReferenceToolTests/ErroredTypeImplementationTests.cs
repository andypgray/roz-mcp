using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.ReferenceToolTests;

/// <summary>
///     Tests that find_implementations includes members from types with compilation errors
///     (e.g. Hexagon, which has an unresolvable IEquatable&lt;NonExistentType&gt; base type).
///     SymbolFinder misses these — the GlobalNamespace fallback supplements them.
/// </summary>
public class ErroredTypeImplementationTests(WorkspaceFixture fixture)
{
    private readonly ReferenceTools tools = TestFileHelper.CreateReferenceTools(fixture);

    [Fact]
    public async Task FindImplementations_AbstractProperty_IncludesErroredTypeOverride()
    {
        // Act — Shape.Area is abstract, Hexagon overrides it
        string result = await tools.FindImplementations(symbolNames: ["Area"], containingType: "Shape", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Hexagon");
    }

    [Fact]
    public async Task FindImplementations_InterfaceProperty_IncludesErroredTypeOverride()
    {
        // Act — IShape.Area, Hexagon ultimately implements it via Shape
        string result = await tools.FindImplementations(symbolNames: ["Area"], containingType: "IShape", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Hexagon");
    }

    [Fact]
    public async Task FindImplementations_SecondAbstractProperty_IncludesErroredTypeOverride()
    {
        // Act — Shape.Perimeter is a second abstract property (distinct from Area), Hexagon overrides it
        string result = await tools.FindImplementations(symbolNames: ["Perimeter"], containingType: "Shape", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Hexagon");
    }
}
