using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Symbols;

public class SymbolResolverTests(WorkspaceFixture fixture)
{
    private readonly ReferenceTools referenceTools = CreateReferenceTools(fixture);
    private readonly TypeHierarchyTools typeHierarchyTools = CreateTypeTools(fixture);

    [Fact]
    public async Task FindReferences_Invocations_CctorByName_ResolvesStaticConstructor()
    {
        // Act — ShapeCalculator has a single static constructor
        string result = await referenceTools.FindReferences(symbolNames: [".cctor"], containingType: "ShapeCalculator", referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — should resolve without error
        result.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task FindReferences_CctorByName_ResolvesStaticConstructor()
    {
        // Act — ShapeCalculator has exactly one .cctor
        string result = await referenceTools.FindReferences(symbolNames: [".cctor"], containingType: "ShapeCalculator", ct: TestContext.Current.CancellationToken);

        // Assert — should resolve without error
        result.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task FindImplementations_CctorByName_ResolvesStaticConstructor()
    {
        // Act
        string result = await referenceTools.FindImplementations(symbolNames: [".cctor"], containingType: "ShapeCalculator", ct: TestContext.Current.CancellationToken);

        // Assert — should resolve without error (no implementations expected for a static constructor)
        result.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task FindReferences_Invocations_CtorByNameMultipleOverloads_MergesResults()
    {
        // Arrange — ShapeCalculator has 2 overloaded constructors (.ctor(IShape) and .ctor(double))
        // ShapeCalculatorConsumer calls both overloads

        // Act — should merge callers across both overloads instead of throwing ambiguity
        string result = await referenceTools.FindReferences(symbolNames: [".ctor"], containingType: "ShapeCalculator", referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — both overloads' callers are present
        result.ShouldContain("CreateFromShape");
        result.ShouldContain("CreateFromRadius");
    }

    [Fact]
    public async Task FindReferences_CtorByNameMultipleOverloads_MergesResults()
    {
        // Arrange — ShapeCalculator has 2 overloaded constructors

        // Act — should merge references across both overloads
        string result = await referenceTools.FindReferences(symbolNames: [".ctor"], containingType: "ShapeCalculator", ct: TestContext.Current.CancellationToken);

        // Assert — references from both overloads are present
        result.ShouldContain("ShapeCalculatorConsumer.cs");
    }

    [Fact]
    public async Task FindReferences_Invocations_CtorByNameNonexistentType_ReturnsError()
    {
        // Act — .ctor with a non-existent containingType; per-name error is captured inline
        string result = await referenceTools.FindReferences(symbolNames: [".ctor"], containingType: "NonExistentType", referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("No symbol found");
    }

    [Fact]
    public async Task ResolveSymbolAsync_DefaultExcludesTests_NarrowsProjectSearch()
    {
        // Arrange — Verify that the default includeTests=false flows through to SymbolResolver
        // by checking that resolution doesn't search test projects.
        // We use FindReferences referenceKinds=invocations with symbolName as this goes through the name-based resolution path.

        // Act — Describe on IShape should resolve without considering test projects
        string result = await referenceTools.FindReferences(symbolNames: ["Describe"], containingType: "IShape", referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — default excludeTests narrows to production: the test-project caller is absent
        result.ShouldContain("Describe");
        result.ShouldNotContain("ShapeTests.cs");
    }

    [Fact]
    public async Task ResolveSymbolAsync_IncludeTests_SearchesAllProjects()
    {
        // Act — With includeTests=true, all projects are searched
        string result = await referenceTools.FindReferences(symbolNames: ["Describe"], containingType: "IShape", includeTests: true, referenceKinds: Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — includeTests widens to all projects: the test-project caller is present
        result.ShouldContain("Describe");
        result.ShouldContain("ShapeTests.cs");
    }

    [Fact]
    public async Task FindImplementations_OnTypeBySymbolName_DefaultExcludesTests_ResolvesFromProductionOnly()
    {
        // Act — Shape exists only in production (TestShape is a different type name).
        // Default includeTests=false means resolution only searches non-test projects.
        string result = await referenceTools.FindImplementations(symbolNames: ["Shape"], ct: TestContext.Current.CancellationToken);

        // Assert — resolution should succeed and include derived classes from production
        result.ShouldContain("Circle");
        result.ShouldContain("Rectangle");
    }

    // ── symbolNames + position: batch/position conflict ─────────────────

    [Fact]
    public async Task FindReferences_SymbolNamesWithFilePath_ThrowsConflict()
    {
        // Arrange
        string filePath = fixture.ShapesFile("Circle.cs");

        // Act & Assert — combining batch names with location is rejected
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => referenceTools.FindReferences(symbolNames: ["Circle"], locations: [filePath]));

        ex.Message.ShouldContain("Pass either location");
    }

    [Fact]
    public async Task FindReferences_SymbolNamesWithPosition_ThrowsConflict()
    {
        // Arrange
        string filePath = fixture.ShapesFile("Circle.cs");

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => referenceTools.FindReferences([Loc(filePath, 3)], ["Circle"]));

        ex.Message.ShouldContain("Pass either location");
    }

    [Fact]
    public async Task GetTypeHierarchy_SymbolNamesWithFilePath_ThrowsConflict()
    {
        // Arrange
        string filePath = fixture.ShapesFile("Circle.cs");

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => typeHierarchyTools.GetTypeHierarchy(symbolNames: ["Circle"], locations: [filePath]));

        ex.Message.ShouldContain("Pass either location");
    }
}
