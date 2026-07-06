using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.ReferenceToolTests;

public class ConstructorReferenceTests(WorkspaceFixture fixture)
{
    private readonly ReferenceTools tools = CreateReferenceTools(fixture);

    // ── find_references on constructors ──────────────────────────────────────

    [Fact]
    public async Task FindReferences_OnExplicitConstructor_FindsCallSite()
    {
        // Arrange — "    public ShapeCalculator(IShape shape)" — ShapeCalculator at line 15, col 12
        // ShapeCalculatorConsumer.CreateFromShape calls this constructor
        string filePath = fixture.ServicesFile("ShapeCalculator.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 15, 12)], ct: TestContext.Current.CancellationToken);

        // Assert — the constructor call in ShapeCalculatorConsumer is found
        result.ShouldContain("ShapeCalculatorConsumer.cs");
    }

    [Fact]
    public async Task FindReferences_OnStaticConstructor_ReturnsResult()
    {
        // Arrange — "    static ShapeCalculator()" — line 10, col 12 (on "ShapeCalculator")
        string filePath = fixture.ServicesFile("ShapeCalculator.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 10, 12)], ct: TestContext.Current.CancellationToken);

        // Assert — should not throw; static constructors have no explicit call sites
        result.ShouldNotBeNullOrWhiteSpace();
    }

    // ── find_references referenceKinds=invocations on implicit constructors ──────────

    [Fact]
    public async Task FindReferences_Invocations_ImplicitConstructor_ByName_FindsNewInvocations()
    {
        // ShapeCollection has no explicit constructor, but "new ShapeCollection()" is called in operator+
        string result = await tools.FindReferences(symbolNames: [".ctor"], containingType: "ShapeCollection", referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — should find the call site
        result.ShouldContain("new ShapeCollection()");
    }

    // ── find_references with containingType ──────────────────────────────────

    [Fact]
    public async Task FindReferences_CtorByContainingType_FindsReferences()
    {
        // Act — .ctor resolved via containingType
        string result = await tools.FindReferences(symbolNames: [".ctor"], containingType: "ShapeCalculator", ct: TestContext.Current.CancellationToken);

        // Assert — should find the constructor call site
        result.ShouldContain("ShapeCalculator");
    }

    [Fact]
    public async Task FindReferences_OpAdditionByContainingType_FindsReferences()
    {
        // Act — op_Addition resolved via containingType
        string result = await tools.FindReferences(symbolNames: ["op_Addition"], containingType: "ShapeCollection", ct: TestContext.Current.CancellationToken);

        // Assert — resolved the operator (may have no call-site references)
        result.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task FindReferences_SpecialMember_NoContainingType_ReturnsError()
    {
        // Act — .ctor with no containingType cannot be resolved; per-name error is captured inline
        string result = await tools.FindReferences(symbolNames: [".ctor"], ct: TestContext.Current.CancellationToken);

        result.ShouldContain("containingType");
    }

    // ── find_references referenceKinds=invocations on constructors ────────────────────

    [Fact]
    public async Task FindReferences_Invocations_OnConstructorDeclaration_ReturnsResult()
    {
        // Arrange — "    public ShapeCalculator(double radius)" — line 20, col 12
        string filePath = fixture.ServicesFile("ShapeCalculator.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 20, 12)], referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — should not throw; returns caller information or "no callers"
        result.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task FindReferences_Invocations_OnConstructor_ShowsCallSiteContext()
    {
        // Arrange — "    public ShapeCalculator(IShape shape)" — line 15, col 12
        string filePath = fixture.ServicesFile("ShapeCalculator.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 15, 12)], referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — the call site is ShapeCalculatorConsumer.CreateFromShape: `new ShapeCalculator(shape)`
        result.ShouldContain("ShapeCalculatorConsumer");
        result.ShouldContain("new ShapeCalculator(shape)");
    }
}
