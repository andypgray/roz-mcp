using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.ReferenceToolTests;

public class FindImplementationsTests(WorkspaceFixture fixture)
{
    private readonly ReferenceTools tools = CreateReferenceTools(fixture);

    [Fact]
    public async Task FindImplementations_OnInterface_FindsImplementors()
    {
        // Arrange — IShape at line 6, col 18
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 6, 18)], ct: TestContext.Current.CancellationToken);

        // Assert — Shape directly implements IShape
        result.ShouldContain("Shape");
    }

    [Fact]
    public async Task FindImplementations_OnPublicKeyword_ResolvesToEnclosingInterface()
    {
        // Arrange — "public interface IShape" — col 1 is the 'p' of 'public'
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 6, 1)], ct: TestContext.Current.CancellationToken);

        // Assert — should resolve to IShape and find Shape
        result.ShouldContain("Shape");
    }

    [Fact]
    public async Task FindImplementations_NonExistentFile_ReturnsInlineError()
    {
        // Single-cursor user errors render as an inline error message (matches symbolNames batch behavior).
        string result = await tools.FindImplementations([Loc("NonExistent.cs", 1, 1)], ct: TestContext.Current.CancellationToken);

        result.ShouldContain("File not found in solution");
    }

    // ── maxResults ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FindImplementations_WithMaxResults_LimitsOutput()
    {
        // Arrange — IShape at line 6, col 18 — implemented by Shape, Circle, Rectangle, Triangle, TestShape
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — limit to 1 implementation
        string result = await tools.FindImplementations([Loc(filePath, 6, 18)], maxResults: 1, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("showing 1 of");
    }

    [Fact]
    public async Task FindImplementations_WithMaxResults_ShowsDistribution()
    {
        // Arrange — IShape has multiple implementations, truncation triggers distribution
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 6, 18)], maxResults: 1, ct: TestContext.Current.CancellationToken);

        // Assert — distribution block appears when truncated
        result.ShouldContain("Distribution:");
        result.ShouldContain("Total:");
    }

    [Fact]
    public async Task FindImplementations_WithoutTruncation_NoDistribution()
    {
        // Arrange
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — maxResults high enough to include all
        string result = await tools.FindImplementations([Loc(filePath, 6, 18)], maxResults: 1000, ct: TestContext.Current.CancellationToken);

        // Assert — no distribution when not truncated
        result.ShouldNotContain("Distribution:");
    }

    [Fact]
    public async Task FindImplementations_WithMaxResults_GreaterThanTotal_ReturnsAll()
    {
        // Arrange — IShape at line 6, col 18
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 6, 18)], maxResults: 1000, ct: TestContext.Current.CancellationToken);

        // Assert — no truncation notice
        result.ShouldNotContain("showing");
    }

    // ── includeTests ─────────────────────────────────────────────────────

    [Fact]
    public async Task FindImplementations_DefaultExcludesTests_ExcludesTestProjectImplementations()
    {
        // Arrange — IShape at line 6, col 18
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 6, 18)], ct: TestContext.Current.CancellationToken);

        // Assert — TestShape (in test project) should not appear, but Shape should
        result.ShouldNotContain("TestShape");
        result.ShouldContain("Shape");
    }

    // ── symbolName resolution ────────────────────────────────────────────

    [Fact]
    public async Task FindImplementations_BySymbolName_FindsImplementations()
    {
        // Act — Describe method on IShape — implemented by Shape (which overrides it)
        string result = await tools.FindImplementations(symbolNames: ["Describe"], containingType: "IShape", ct: TestContext.Current.CancellationToken);

        // Assert — Shape.Describe implements IShape.Describe
        result.ShouldContain("Shape");
    }

    [Fact]
    public async Task FindImplementations_BySymbolName_Interface_FindsImplementors()
    {
        // Act — IShape interface itself
        string result = await tools.FindImplementations(symbolNames: ["IShape"], ct: TestContext.Current.CancellationToken);

        // Assert — Shape implements IShape
        result.ShouldContain("Shape");
    }

    // ── abstract/virtual class members ──────────────────────────────────────

    [Fact]
    public async Task FindImplementations_AbstractPropertyOnClass_FindsOverrides()
    {
        // Arrange — Shape.Area (abstract property) at line 7
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 7, 28)], ct: TestContext.Current.CancellationToken);

        // Assert — Circle, Rectangle, Triangle, Pentagon all override Area
        result.ShouldContain("Circle");
        result.ShouldContain("Rectangle");
        result.ShouldContain("Triangle");
        result.ShouldContain("Pentagon");
    }

    [Fact]
    public async Task FindImplementations_VirtualMethodOnClass_FindsOverrides()
    {
        // Arrange — Shape.Describe (virtual method) at line 13
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 13, 27)], ct: TestContext.Current.CancellationToken);

        // Assert — only Triangle overrides Describe
        result.ShouldContain("Triangle");
        result.ShouldNotContain("Circle");
        result.ShouldNotContain("Rectangle");
    }

    [Fact]
    public async Task FindImplementations_InterfaceMember_StillFindsImplementations()
    {
        // Arrange — IShape.Area (interface property) at line 9
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 9, 12)], ct: TestContext.Current.CancellationToken);

        // Assert — Shape.Area implements IShape.Area
        result.ShouldContain("Shape");
    }

    [Fact]
    public async Task FindImplementations_BySymbolName_AbstractOnClass_FindsOverrides()
    {
        // Act — Area on Shape (abstract class member)
        string result = await tools.FindImplementations(symbolNames: ["Area"], containingType: "Shape", ct: TestContext.Current.CancellationToken);

        // Assert — all concrete subclasses override Area
        result.ShouldContain("Circle");
        result.ShouldContain("Rectangle");
        result.ShouldContain("Triangle");
    }

    // ── includeBody ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FindImplementations_WithIncludeBody_ShowsSourceBodies()
    {
        // Arrange — IShape.Describe() at line 18, col 12
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 18, 12)], includeBody: true, ct: TestContext.Current.CancellationToken);

        // Assert — Shape.Describe has a body with GetType().Name
        result.ShouldContain("GetType().Name");
    }

    [Fact]
    public async Task FindImplementations_WithoutIncludeBody_OmitsSourceBodies()
    {
        // Arrange — IShape.Describe() at line 18, col 12
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 18, 12)], ct: TestContext.Current.CancellationToken);

        // Assert — should show only signatures, not implementation details
        result.ShouldNotContain("GetType().Name");
    }

    // ── External (metadata) interface members ─────────────────────────────

    [Fact]
    public async Task FindImplementations_ExternalInterface_BySimpleName_FindsInSolutionImplementations()
    {
        // Act — Dispose method on IDisposable (BCL interface, not in solution source)
        // Use high maxResults because BCL types also implement IDisposable
        string result = await tools.FindImplementations(symbolNames: ["Dispose"], containingType: "IDisposable", maxResults: 500, ct: TestContext.Current.CancellationToken);

        // Assert — ShapeManager and ShapeCollection both implement IDisposable.Dispose
        result.ShouldContain("ShapeManager");
        result.ShouldContain("ShapeCollection");
    }

    [Fact]
    public async Task FindImplementations_ExternalInterface_ByFqn_FindsInSolutionImplementations()
    {
        // Act — fully-qualified name, high maxResults to include test fixture types
        string result = await tools.FindImplementations(symbolNames: ["System.IDisposable.Dispose"], maxResults: 500, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("ShapeManager");
        result.ShouldContain("ShapeCollection");
    }

    [Fact]
    public async Task FindImplementations_ExternalInterface_ShowsContractHeader()
    {
        // Act
        string result = await tools.FindImplementations(symbolNames: ["Dispose"], containingType: "IDisposable", maxResults: 1, ct: TestContext.Current.CancellationToken);

        // Assert — should show the external interface contract without a file location
        result.ShouldContain("Contract:");
        result.ShouldContain("IDisposable.Dispose");
    }

    [Fact]
    public async Task FindImplementations_ExternalInterface_ByTypeNameOnly_FindsSolutionImplementors()
    {
        // Act — IDisposable as a type (no containingType), resolved from metadata
        string result = await tools.FindImplementations(symbolNames: ["IDisposable"], maxResults: 500, ct: TestContext.Current.CancellationToken);

        // Assert — ShapeManager and ShapeCollection implement IDisposable
        result.ShouldContain("ShapeManager");
        result.ShouldContain("ShapeCollection");
    }

    // ── Type dispatch (merged from find_derived_types) ──────────────────

    [Fact]
    public async Task FindImplementations_OnAbstractClass_ReturnsAllConcreteSubclasses()
    {
        // Arrange — "public abstract class Shape : IShape" — Shape at line 4, col 23
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 4, 23)], ct: TestContext.Current.CancellationToken);

        // Assert — all concrete subclasses
        result.ShouldContain("Circle");
        result.ShouldContain("Rectangle");
        result.ShouldContain("Triangle");
    }

    [Fact]
    public async Task FindImplementations_OnAbstractClass_UsesTreeFormat()
    {
        // Arrange — Shape at line 4, col 23
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 4, 23)], ct: TestContext.Current.CancellationToken);

        // Assert — class hierarchies use tree format (no numbered list prefix "1.")
        result.ShouldNotMatch(@"^\d+\.", "Class hierarchies should use tree format, not numbered list");
        result.ShouldContain("Circle");
        result.ShouldContain("Triangle");
    }

    [Fact]
    public async Task FindImplementations_OnInterface_AsType_UsesNumberedListFormat()
    {
        // Arrange — IShape at line 6, col 18
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 6, 18)], ct: TestContext.Current.CancellationToken);

        // Assert — interface implementations use numbered list format
        result.ShouldContain("1.");
    }

    [Fact]
    public async Task FindImplementations_OnClass_WithIncludeBody_SwitchesToFlatList()
    {
        // Arrange — Shape at line 4, col 23
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 4, 23)], includeBody: true, ct: TestContext.Current.CancellationToken);

        // Assert — should use numbered list format (not tree) when includeBody=true
        result.ShouldContain("1.");
        result.ShouldContain("Math.PI");
    }

    [Fact]
    public async Task FindImplementations_OnClass_WithoutIncludeBody_UsesTreeFormat()
    {
        // Arrange — Shape at line 4, col 23
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 4, 23)], ct: TestContext.Current.CancellationToken);

        // Assert — without includeBody, class hierarchies use tree format
        result.ShouldNotMatch(@"^\d+\.", "Class hierarchies should use tree format, not numbered list");
        result.ShouldNotContain("Math.PI");
    }

    [Fact]
    public async Task FindImplementations_OnAbstractClass_DefaultExcludesTests()
    {
        // Arrange — Shape at line 4, col 23
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 4, 23)], ct: TestContext.Current.CancellationToken);

        // Assert — TestShape (in test project) should not appear
        result.ShouldNotContain("TestShape");
        result.ShouldContain("Circle");
    }

    [Fact]
    public async Task FindImplementations_OnAbstractClass_IncludeTests_IncludesTestDerivedClasses()
    {
        // Arrange — Shape at line 4, col 23
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 4, 23)], includeTests: true, ct: TestContext.Current.CancellationToken);

        // Assert — TestShape from test project should appear
        result.ShouldContain("TestShape");
    }

    [Fact]
    public async Task FindImplementations_OnDeepHierarchy_ShowsTreeConnectors()
    {
        // Arrange — Shape -> Rectangle -> Square (two-level hierarchy)
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 4, 23)], ct: TestContext.Current.CancellationToken);

        // Assert — Square nested under Rectangle should use box-drawing connectors
        result.ShouldContain("Square");
        result.ShouldContain("\u2514\u2500"); // └─ connector for last child
    }

    [Fact]
    public async Task FindImplementations_OnDeepHierarchy_NestedChildIndentedUnderParent()
    {
        // Arrange — Shape -> Rectangle -> Square
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await tools.FindImplementations([Loc(filePath, 4, 23)], ct: TestContext.Current.CancellationToken);

        // Assert — Square should appear indented under Rectangle
        string[] lines = result.Split('\n');
        string? squareLine = lines.FirstOrDefault(l => l.Contains("Square"));
        squareLine.ShouldNotBeNull();
        squareLine.ShouldContain("\u2514\u2500 Square"); // └─ Square
    }

    [Fact]
    public async Task FindImplementations_OnMarkerInterface_ReturnsImplementingTypes()
    {
        // Arrange — IShapeBuilder is a marker interface with no members and nothing implements it.
        // After the merge, find_implementations on a type resolves derived/implementing types.
        string result = await tools.FindImplementations(symbolNames: ["IShapeBuilder"], ct: TestContext.Current.CancellationToken);

        // Assert — nothing implements IShapeBuilder, so the explicit no-results contract is pinned.
        result.ShouldContain("No implementing types found for 'IShapeBuilder'.");
        result.ShouldNotContain("marker interface");
    }

    [Fact]
    public async Task FindImplementations_OnClass_AsType_ReturnsDerivedClasses()
    {
        // Arrange — Shape is a class; type dispatch returns derived classes
        string result = await tools.FindImplementations(symbolNames: ["Shape"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Circle");
        result.ShouldContain("Rectangle");
        result.ShouldContain("Triangle");
    }

    [Fact]
    public async Task FindImplementations_OnStruct_GuidesToFindReferences()
    {
        // Arrange — Point is a struct
        string result = await tools.FindImplementations(symbolNames: ["Point"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("struct");
        result.ShouldContain("find_references");
    }

    [Fact]
    public async Task FindImplementations_OnEnum_GuidesToFindReferences()
    {
        // Arrange — ShapeColor is an enum
        string result = await tools.FindImplementations(symbolNames: ["ShapeColor"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("enum");
        result.ShouldContain("find_references");
    }

    [Fact]
    public async Task FindImplementations_OnSealedClass_GuidesToFindReferences()
    {
        // Arrange — FinalShape is a sealed class
        string result = await tools.FindImplementations(symbolNames: ["FinalShape"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("sealed");
        result.ShouldContain("find_references");
    }

    [Fact]
    public async Task FindImplementations_OnExternalInterface_AsType_FindsSolutionTypes()
    {
        // Act — IDisposable is a BCL interface, not declared in solution source
        string result = await tools.FindImplementations(symbolNames: ["IDisposable"], maxResults: 500, ct: TestContext.Current.CancellationToken);

        // Assert — ShapeManager and ShapeCollection implement IDisposable
        result.ShouldContain("ShapeManager");
        result.ShouldContain("ShapeCollection");
    }

    [Fact]
    public async Task FindImplementations_OnExternalInterface_AsType_AutoIncludesTargetDocs()
    {
        // Act — IDisposable is metadata-only; docs should auto-enable for the target interface.
        string result = await tools.FindImplementations(symbolNames: ["IDisposable"], maxResults: 500, ct: TestContext.Current.CancellationToken);

        // Assert — IDisposable's XML docs surface without requiring includeDocs=true.
        result.ShouldContain("Documentation:");
    }

    [Fact]
    public async Task FindImplementations_OnExternalInterfaceMember_AutoIncludesTargetDocs()
    {
        // Act — IDisposable.Dispose is a metadata-only interface member.
        string result = await tools.FindImplementations(symbolNames: ["Dispose"], containingType: "IDisposable", maxResults: 500, ct: TestContext.Current.CancellationToken);

        // Assert — Dispose's XML docs surface without requiring includeDocs=true.
        result.ShouldContain("Documentation:");
    }

    // ── Delegate-type guard ───────────────────────────────────────────────

    [Fact]
    public async Task FindImplementations_OnDelegate_ReturnsInlineGuidance()
    {
        // Arrange — ShapeMetricFunc is a delegate defined in TypeKindExamples.cs.
        // Delegates can't be derived or implemented; surface a pointed error instead of misleading "0 derived classes".

        // Act — per-name user error is captured inline by BatchOrSingle (no throw)
        string result = await tools.FindImplementations(symbolNames: ["ShapeMetricFunc"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("delegate");
        result.ShouldContain("find_references");
    }

    // ── Static operators ─────────────────────────────────────────────────

    [Fact]
    public async Task FindImplementations_StaticOperatorByName_ReturnsOperatorGuidance()
    {
        // Arrange — op_Implicit on ShapeCollection has two overloads (int + string)
        // Act
        string result = await tools.FindImplementations(symbolNames: ["op_Implicit"], containingType: "ShapeCollection", ct: TestContext.Current.CancellationToken);

        // Assert — should get operator guidance, not disambiguation error
        result.ShouldContain("static operator");
        result.ShouldContain("find_references referenceKinds=invocations");
    }

    [Fact]
    public async Task FindImplementations_SingleOperatorByName_ReturnsOperatorGuidance()
    {
        // Arrange — op_Addition on ShapeCollection has a single overload
        // Act
        string result = await tools.FindImplementations(symbolNames: ["op_Addition"], containingType: "ShapeCollection", ct: TestContext.Current.CancellationToken);

        // Assert — should get operator guidance, not generic "no implementations"
        result.ShouldContain("static operator");
        result.ShouldContain("find_references referenceKinds=invocations");
    }

    // ── Batch ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindImplementations_Batch_MultipleNames_ProducesSectionedOutput()
    {
        // Act — batch two interface names
        string result = await tools.FindImplementations(symbolNames: ["IShape", "IShapeBuilder"], ct: TestContext.Current.CancellationToken);

        // Assert — each name gets its own header section
        result.ShouldContain("=== IShape ===");
        result.ShouldContain("=== IShapeBuilder ===");
    }

    [Fact]
    public async Task FindImplementations_Batch_SingleName_OmitsHeaderWrapper()
    {
        // Act — single-item batch uses the FormatBatch N=1 short-circuit
        string result = await tools.FindImplementations(symbolNames: ["IShape"], ct: TestContext.Current.CancellationToken);

        // Assert — no "=== IShape ===" wrapper for a single result
        result.ShouldNotContain("=== IShape ===");
    }

    [Fact]
    public async Task FindImplementations_Batch_SameTypeNameDifferentNamespace_FallsBackToFullyQualified()
    {
        string file = fixture.ServicesFile("NameCollisionExamples.cs");
        string result = await tools.FindImplementations(
            [Loc(file, 9), Loc(file, 20)],
            ct: TestContext.Current.CancellationToken);

        result.ShouldNotContain("=== Execute ===");
        result.ShouldNotContain("=== NameTwin.Execute ===");
        result.ShouldContain("=== TestFixture.Services.Twins.Alpha.NameTwin.Execute ===");
        result.ShouldContain("=== TestFixture.Services.Twins.Beta.NameTwin.Execute ===");
    }

    [Fact]
    public async Task FindImplementations_Batch_RejectsPositionWithNames_Throws()
    {
        // Arrange
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.FindImplementations([Loc(filePath, 18)], ["Describe"]));

        ex.Message.ShouldContain("Pass either location");
    }

    [Fact]
    public async Task FindImplementations_Batch_RejectsEmptyArray_Throws()
    {
        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.FindImplementations(symbolNames: []));

        ex.Message.ShouldContain("must not be empty");
    }

    // ── Project filter disambiguates cross-project name clashes ─────────

    [Fact]
    public async Task FindImplementations_BySymbolName_AmbiguousAcrossProjects_Errors()
    {
        // Arrange — Greet exists on TestFixture.Legacy.SharedHelper and TestFixture.Minimal.SharedHelper.
        // Without a project filter, member resolution is ambiguous.

        // Act
        string result = await tools.FindImplementations(symbolNames: ["Greet"], containingType: "SharedHelper", ct: TestContext.Current.CancellationToken);

        // Assert — per-name error captured inline by BatchOrSingle
        result.ShouldContain("Ambiguous");
    }

    [Fact]
    public async Task FindImplementations_BySymbolNameWithProject_NarrowsAmbiguity()
    {
        // Act — project filter narrows to one Greet during resolution.
        string result = await tools.FindImplementations(symbolNames: ["Greet"], containingType: "SharedHelper", project: "Minimal", ct: TestContext.Current.CancellationToken);

        // Assert — resolves without ambiguity (static method has no implementations, but that's fine)
        result.ShouldNotContain("Ambiguous");
    }
}
