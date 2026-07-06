using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.TypeHierarchyToolTests;

public class GetTypeHierarchyTests(WorkspaceFixture fixture)
{
    private readonly TypeHierarchyTools hierarchyTools = CreateTypeTools(fixture);

    [Fact]
    public async Task GetTypeHierarchy_ConcreteClass_ShowsBaseChainAndInterfaces()
    {
        // Arrange — "public class Circle(...) : Shape" — Circle at line 3, col 14
        string filePath = fixture.ShapesFile("Circle.cs");

        // Act
        string result = await hierarchyTools.GetTypeHierarchy([Loc(filePath, 3, 14)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Circle");
        result.ShouldContain("Shape");
        result.ShouldContain("IShape");
    }

    [Fact]
    public async Task GetTypeHierarchy_Interface_ShowsNoBaseTypes()
    {
        // Arrange — IShape at line 6, col 18
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await hierarchyTools.GetTypeHierarchy([Loc(filePath, 6, 18)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("IShape");
        result.ShouldContain("(none beyond System.Object)");
    }

    // ── Forgiving position (keyword) regression tests ───────────────────────

    [Fact]
    public async Task GetTypeHierarchy_OnPublicKeyword_ResolvesToEnclosingType()
    {
        // Arrange — "public class Circle(double radius) : Shape" — col 1 is the 'p' of 'public'
        string filePath = fixture.ShapesFile("Circle.cs");

        // Act
        string result = await hierarchyTools.GetTypeHierarchy([Loc(filePath, 3, 1)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Circle");
        result.ShouldContain("Shape");
        result.ShouldContain("IShape");
    }

    [Fact]
    public async Task GetTypeHierarchy_OnAbstractKeyword_ResolvesToEnclosingType()
    {
        // Arrange — "public abstract class Shape : IShape" — col 8 is the 'a' of 'abstract' (line 4)
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await hierarchyTools.GetTypeHierarchy([Loc(filePath, 4, 8)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Shape");
        result.ShouldContain("IShape");
    }

    [Fact]
    public async Task GetTypeHierarchy_NonExistentFile_ReturnsInlineError()
    {
        // Single-cursor user errors render as an inline error message (matches symbolNames batch behavior).
        string result = await hierarchyTools.GetTypeHierarchy([Loc("NonExistent.cs", 1, 1)], ct: TestContext.Current.CancellationToken);

        result.ShouldContain("File not found in solution");
    }

    // ── Name-based lookup tests ──────────────────────────────────────────────

    [Fact]
    public async Task GetTypeHierarchy_BySymbolName_ConcreteClass()
    {
        // Act
        string result = await hierarchyTools.GetTypeHierarchy(symbolNames: ["Circle"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Circle");
        result.ShouldContain("Shape");
        result.ShouldContain("IShape");
    }

    [Fact]
    public async Task GetTypeHierarchy_BySymbolName_Interface()
    {
        // Act
        string result = await hierarchyTools.GetTypeHierarchy(symbolNames: ["IShape"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("IShape");
        result.ShouldContain("(none beyond System.Object)");
    }

    [Fact]
    public async Task GetTypeHierarchy_BothPositionAndName_ThrowsConflict()
    {
        // Arrange — Circle class at line 3 in Circle.cs
        string filePath = fixture.ShapesFile("Circle.cs");

        // Act & Assert — combining batch names with position is rejected
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => hierarchyTools.GetTypeHierarchy([Loc(filePath, 3, 14)], ["Circle"]));

        ex.Message.ShouldContain("Pass either location");
    }

    // ── Auto-correct containingType == symbolName ─────────────────────────────

    [Fact]
    public async Task GetTypeHierarchy_ContainingTypeMatchesSymbolName_AutoCorrected()
    {
        // Arrange — passing containingType equal to symbolName is a common mistake;
        // the tool should silently drop containingType and resolve the type itself
        string result = await hierarchyTools.GetTypeHierarchy(symbolNames: ["Circle"], containingType: "Circle", ct: TestContext.Current.CancellationToken);

        // Assert — should succeed, not error
        result.ShouldContain("Circle");
        result.ShouldContain("Shape");
        result.ShouldContain("IShape");
    }

    // ── External symbol location ─────────────────────────────────────────────

    [Fact]
    public async Task GetTypeHierarchy_TypeImplementingExternalInterface_ShowsExternalMarker()
    {
        // Arrange — ShapeCollection implements IDisposable (from BCL, not in solution)
        string filePath = fixture.ServicesFile("ShapeCollection.cs");

        // Act
        string result = await hierarchyTools.GetTypeHierarchy([Loc(filePath, 5, 14)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("IDisposable");
        result.ShouldContain("(external)");
    }

    [Fact]
    public async Task GetTypeHierarchy_TypeWithNoInterfaces_ShowsNone()
    {
        // Arrange — ShapeRequest has no interfaces
        string result = await hierarchyTools.GetTypeHierarchy(symbolNames: ["ShapeRequest"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("(none)");
        result.ShouldContain("(none beyond System.Object)");
    }

    // ── Header tag (accessibility + modifiers + kind) ────────────────────────

    [Fact]
    public async Task GetTypeHierarchy_RecordClass_HeaderShowsRecordKeyword()
    {
        string result = await hierarchyTools.GetTypeHierarchy(symbolNames: ["ShapeSnapshot"], ct: TestContext.Current.CancellationToken);

        result.ShouldContain("(public record class)");
    }

    [Fact]
    public async Task GetTypeHierarchy_ReadonlyRecordStruct_HeaderShowsReadonly()
    {
        string result = await hierarchyTools.GetTypeHierarchy(symbolNames: ["ReadonlyShapeId"], ct: TestContext.Current.CancellationToken);

        result.ShouldContain("(public readonly record struct)");
    }

    // ── Batch ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTypeHierarchy_Batch_MultipleNames_ProducesSectionedOutput()
    {
        // Act — batch two type names
        string result = await hierarchyTools.GetTypeHierarchy(symbolNames: ["Circle", "Rectangle"], ct: TestContext.Current.CancellationToken);

        // Assert — each name gets its own header section
        result.ShouldContain("=== Circle ===");
        result.ShouldContain("=== Rectangle ===");
    }

    [Fact]
    public async Task GetTypeHierarchy_Batch_SingleName_OmitsHeaderWrapper()
    {
        // Act — single-item batch uses the FormatBatch N=1 short-circuit
        string result = await hierarchyTools.GetTypeHierarchy(symbolNames: ["Circle"], ct: TestContext.Current.CancellationToken);

        // Assert — no "=== Circle ===" wrapper for a single result
        result.ShouldNotContain("=== Circle ===");
    }

    [Fact]
    public async Task GetTypeHierarchy_Batch_SameTypeNameDifferentNamespace_FallsBackToFullyQualified()
    {
        // Two classes both named 'NameTwin' in different namespaces — bare "NameTwin"
        // and minimally-qualified type name both collide → escalate to Tier 2.
        string file = fixture.ServicesFile("NameCollisionExamples.cs");
        string result = await hierarchyTools.GetTypeHierarchy(
            [Loc(file, 5), Loc(file, 16)],
            ct: TestContext.Current.CancellationToken);

        result.ShouldNotContain("=== NameTwin ===");
        result.ShouldContain("=== TestFixture.Services.Twins.Alpha.NameTwin ===");
        result.ShouldContain("=== TestFixture.Services.Twins.Beta.NameTwin ===");
    }

    [Fact]
    public async Task GetTypeHierarchy_Batch_RejectsEmptyArray_Throws()
    {
        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => hierarchyTools.GetTypeHierarchy(symbolNames: []));

        ex.Message.ShouldContain("must not be empty");
    }

    // ── Per-name containingType null-out ─────────────────────────────────

    [Fact]
    public async Task GetTypeHierarchy_Batch_NullsContainingTypeWhenMatchesSymbolName_CaseInsensitive()
    {
        // Single-name batch where the (lowercase) name matches containingType case-insensitively.
        // Without per-name null-out, the service would fail to resolve 'ishape' inside type 'IShape'.
        string result = await hierarchyTools.GetTypeHierarchy(symbolNames: ["ishape"], containingType: "IShape", ct: TestContext.Current.CancellationToken);

        // Assert — resolves the type itself (not as a member of itself)
        result.ShouldContain("IShape");
    }

    // ── Project filter disambiguates cross-project name clashes ─────────

    [Fact]
    public async Task GetTypeHierarchy_BySymbolName_AmbiguousAcrossProjects_Errors()
    {
        // Arrange — SharedHelper exists in both TestFixture.Legacy and TestFixture.Minimal.
        // Without a project filter, resolution is ambiguous.

        // Act
        string result = await hierarchyTools.GetTypeHierarchy(symbolNames: ["SharedHelper"], ct: TestContext.Current.CancellationToken);

        // Assert — per-name error captured inline by BatchOrSingle
        result.ShouldContain("Ambiguous");
    }

    [Fact]
    public async Task GetTypeHierarchy_BySymbolNameWithProject_NarrowsAmbiguity()
    {
        // Act — SharedHelper exists in two projects; project filter picks one during resolution.
        string result = await hierarchyTools.GetTypeHierarchy(symbolNames: ["SharedHelper"], project: "Minimal", ct: TestContext.Current.CancellationToken);

        // Assert — resolves without ambiguity; the static class has only System.Object as base
        result.ShouldContain("SharedHelper");
        result.ShouldNotContain("Ambiguous");
    }

    // ── includeTests ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetTypeHierarchy_ByName_DefaultExcludesTests_DoesNotResolveTestType()
    {
        // TestShape lives only in TestFixture.Tests; default includeTests=false should miss it.
        string result = await hierarchyTools.GetTypeHierarchy(symbolNames: ["TestShape"], ct: TestContext.Current.CancellationToken);

        result.ShouldContain("No symbol found");
    }

    [Fact]
    public async Task GetTypeHierarchy_ByName_IncludeTestsTrue_ResolvesTestType()
    {
        string result = await hierarchyTools.GetTypeHierarchy(symbolNames: ["TestShape"], includeTests: true, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("TestShape");
        result.ShouldContain("Shape");
    }
}
