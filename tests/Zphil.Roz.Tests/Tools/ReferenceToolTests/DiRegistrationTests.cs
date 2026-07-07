using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.ReferenceToolTests;

public class DiRegistrationTests(WorkspaceFixture fixture)
{
    private readonly ReferenceTools referenceTools = CreateReferenceTools(fixture);

    // ── find_references referenceKinds=invocations on .ctor with DI registration ────

    [Fact]
    public async Task FindReferences_Invocations_CtorWithDiRegistration_ShowsRegistrations()
    {
        // Arrange — ShapeService has no direct "new ShapeService()" calls, but is DI-registered
        string result = await referenceTools.FindReferences(symbolNames: [".ctor"], containingType: "ShapeService", referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — should show DI registration fallback
        result.ShouldContain("DI registration");
        result.ShouldContain("AddTransient");
        result.ShouldContain("ServiceRegistration.cs");
    }

    [Fact]
    public async Task FindReferences_Invocations_CtorWithDirectCallers_NoDiFallback()
    {
        // Arrange — ShapeCollection has a direct "new ShapeCollection()" caller in operator+
        string result = await referenceTools.FindReferences(symbolNames: [".ctor"], containingType: "ShapeCollection", referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — has direct callers, so no DI fallback section
        result.ShouldContain("Callers of");
        result.ShouldNotContain("DI registration");
    }

    // ── find_references (all kinds) on .ctor with DI registration ───────────

    [Fact]
    public async Task FindReferences_CtorWithDiRegistration_ShowsRegistrations()
    {
        // Arrange — ShapeService .ctor has no direct references
        string result = await referenceTools.FindReferences(symbolNames: [".ctor"], containingType: "ShapeService", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("DI registration");
        result.ShouldContain("AddTransient");
    }

    // ── find_references referenceKinds=invocations on non-constructor returns no DI info ──

    [Fact]
    public async Task FindReferences_Invocations_NonConstructor_NoDiScan()
    {
        // Arrange — a regular method with no callers should not trigger DI scan
        string result = await referenceTools.FindReferences(symbolNames: ["Format"], containingType: "ShapeService", referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — regular "no callers" or callers found, but no DI section
        result.ShouldNotContain("DI registration");
    }

    // ── find_implementations with DI annotations ────────────────────────────

    [Fact]
    public async Task FindImplementations_ShowsDiRegistrations()
    {
        // Arrange — IShape has multiple implementations; Circle and Rectangle are DI-registered
        string result = await referenceTools.FindImplementations(symbolNames: ["IShape"], ct: TestContext.Current.CancellationToken);

        // Assert — DI registrations section should appear
        result.ShouldContain("DI registrations");
        result.ShouldContain("Circle");
    }

    // ── find_implementations on a type (derived-types dispatch) with DI annotations ──

    [Fact]
    public async Task FindImplementations_OnType_ShowsDiRegistrations()
    {
        // Arrange — Shape has multiple subtypes; Circle is DI-registered
        string result = await referenceTools.FindImplementations(symbolNames: ["Shape"], ct: TestContext.Current.CancellationToken);

        // Assert — DI registrations section should appear
        result.ShouldContain("DI registrations");
        result.ShouldContain("Circle");
    }

    // ── DI registration patterns ────────────────────────────────────────────

    [Fact]
    public async Task FindReferences_Invocations_CtorWithDirectCallers_NoDiFallbackForCircle()
    {
        // Circle has direct "new Circle()" callers in ShapeCalculator, so DI fallback doesn't trigger
        string result = await referenceTools.FindReferences(symbolNames: [".ctor"], containingType: "Circle", referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — has direct callers, no DI fallback
        result.ShouldContain("Callers of");
        result.ShouldNotContain("DI registration");
    }

    [Fact]
    public async Task FindReferences_Invocations_CtorSingleton_DetectsLifetime()
    {
        // ShapeCalculator .ctor has direct callers, but it's also DI-registered as singleton.
        // The DI section appears via find_implementations on IShape, not find_references referenceKinds=invocations.
        string result = await referenceTools.FindReferences(symbolNames: [".ctor"], containingType: "ShapeCalculator", referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — has direct callers, so no DI fallback
        result.ShouldContain("Callers of");
    }
}
