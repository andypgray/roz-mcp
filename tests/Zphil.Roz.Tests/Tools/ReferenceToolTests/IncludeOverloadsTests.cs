using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.ReferenceToolTests;

public class IncludeOverloadsTests(WorkspaceFixture fixture)
{
    private const ReferenceKind Kind = ReferenceKind.Invocations;

    private readonly ReferenceTools tools = CreateReferenceTools(fixture);

    [Fact]
    public async Task FindReferences_IncludeOverloads_FindsCallersOfAllOverloads()
    {
        // Arrange — Format(IShape) at line 27, col 19 (the 1-param overload)
        // Without includeOverloads: only finds callers of Format(IShape) — the 2-param overload calls it on line 33
        // With includeOverloads: also finds the 3-param overload calling Format(IShape, bool) on line 36
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 27, 19)], referenceKinds: Kind, includeOverloads: true, ct: TestContext.Current.CancellationToken);

        // Assert — should find callers from both the 2-param and 3-param overloads
        result.ShouldContain("all 3 overloads");
        result.ShouldContain("Format(shape)");
        result.ShouldContain("Format(shape, includePerimeter)");
    }

    [Fact]
    public async Task FindReferences_WithoutIncludeOverloads_FindsOnlySingleOverloadCallers()
    {
        // Arrange — Format(IShape) at line 27, col 19
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 27, 19)], referenceKinds: Kind, includeOverloads: false, ct: TestContext.Current.CancellationToken);

        // Assert — only the 2-param overload calls Format(IShape) directly
        result.ShouldContain("Format(shape)");
        result.ShouldNotContain("Format(shape, includePerimeter)");
        result.ShouldNotContain("all 3 overloads");
    }

    [Fact]
    public async Task FindReferences_IncludeOverloads_NameBased_AlreadyReturnsAll()
    {
        // Arrange — name-based resolution already returns all overloads via ResolveOverloadsAsync
        // Act
        string withFlag = await tools.FindReferences(symbolNames: ["Format"], containingType: "ShapeService", referenceKinds: Kind, includeOverloads: true, ct: TestContext.Current.CancellationToken);
        string withoutFlag = await tools.FindReferences(symbolNames: ["Format"], containingType: "ShapeService", referenceKinds: Kind, includeOverloads: false, ct: TestContext.Current.CancellationToken);

        // Assert — both should find callers from all overloads since name-based always merges
        withFlag.ShouldContain("Format(shape)");
        withFlag.ShouldContain("Format(shape, includePerimeter)");
        withoutFlag.ShouldContain("Format(shape)");
        withoutFlag.ShouldContain("Format(shape, includePerimeter)");
    }

    [Fact]
    public async Task FindReferences_IncludeOverloads_HeaderShowsOverloadCount()
    {
        // Arrange — Format has 3 overloads in ShapeService
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 27, 19)], referenceKinds: Kind, includeOverloads: true, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("all 3 overloads");
    }
}
