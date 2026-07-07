using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.NavigationToolTests;

public class FindOverloadsTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools tools = CreateNavigationTools(fixture);

    // ── Position-based resolution ────────────────────────────────────────

    [Fact]
    public async Task FindOverloads_ByPosition_ReturnsAllOverloads()
    {
        // Arrange — ShapeService.cs line 27: first Format overload
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act
        string result = await tools.FindOverloads([Loc(filePath, 27, 19)], ct: TestContext.Current.CancellationToken);

        // Assert — compact format: parameter list + location
        result.ShouldContain("Overloads of 'Format' in ShapeService (3):");
        result.ShouldContain("(IShape shape)");
        result.ShouldContain("(IShape shape, bool includePerimeter)");
        result.ShouldContain("(IShape shape, bool includePerimeter, string prefix)");
        result.ShouldContain("\u2014"); // em-dash location separator
    }

    [Fact]
    public async Task FindOverloads_ByPositionOnSecondOverload_ReturnsSameResults()
    {
        // Arrange — ShapeService.cs line 30: second Format overload
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act
        string result = await tools.FindOverloads([Loc(filePath, 30, 19)], ct: TestContext.Current.CancellationToken);

        // Assert — should find the same 3 overloads regardless of which one the cursor is on
        result.ShouldContain("Overloads of 'Format' in ShapeService (3):");
    }

    // ── Name-based resolution ────────────────────────────────────────────

    [Fact]
    public async Task FindOverloads_ByName_ReturnsAllOverloads()
    {
        // Act
        string result = await tools.FindOverloads(symbolNames: ["Format"], containingType: "ShapeService", ct: TestContext.Current.CancellationToken);

        // Assert — compact format only (no full signatures without includeBody/includeDocs)
        result.ShouldContain("Overloads of 'Format' in ShapeService (3):");
        result.ShouldContain("(IShape shape)");
        result.ShouldContain("(IShape shape, bool includePerimeter)");
        result.ShouldNotContain("Location:");
    }

    // ── Single method (no overloads) ─────────────────────────────────────

    [Fact]
    public async Task FindOverloads_SingleMethod_ReportsNoOverloads()
    {
        // Arrange — ProcessShape has no overloads
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act
        string result = await tools.FindOverloads([Loc(filePath, 16, 19)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("has no overloads");
        result.ShouldContain("ProcessShape");
    }

    // ── includeBody ──────────────────────────────────────────────────────

    [Fact]
    public async Task FindOverloads_WithIncludeBody_ShowsSource()
    {
        // Act
        string result = await tools.FindOverloads(symbolNames: ["Format"], containingType: "ShapeService", includeBody: true, ct: TestContext.Current.CancellationToken);

        // Assert — body should include the actual overload source, not just the header
        result.ShouldContain("Body:");
        result.ShouldContain("shape.Describe()");
    }

    [Fact]
    public async Task FindOverloads_WithIncludeBody_DedentsBodyInsteadOfStaircasing()
    {
        // Act — the (IShape, bool) overload is a multi-line expression body whose
        // continuation lines sit one indent level under the signature in source.
        string result = await tools.FindOverloads(symbolNames: ["Format"], containingType: "ShapeService",
            includeBody: true, ct: TestContext.Current.CancellationToken);

        // Assert — within the 2-param overload's Body block, the `includePerimeter`
        // ternary-condition continuation line must be exactly one indent level (4 spaces)
        // deeper than the signature line — not 8 (the staircase).
        string[] lines = result.Replace("\r", "").Split('\n');

        int signatureIndex = Array.FindIndex(lines,
            l => l.Contains("Format(IShape shape, bool includePerimeter) =>"));
        signatureIndex.ShouldBeGreaterThanOrEqualTo(0, "2-param overload signature line should be present in the body");

        int continuationIndex = Array.FindIndex(lines, signatureIndex + 1,
            l => l.Trim() == "includePerimeter");
        continuationIndex.ShouldBeGreaterThan(signatureIndex, "ternary-condition continuation line should follow the signature");

        int signatureIndent = LeadingSpaces(lines[signatureIndex]);
        int continuationIndent = LeadingSpaces(lines[continuationIndex]);

        // Pre-fix the delta is 8: SyntaxNode.ToString() strips the signature's leading
        // trivia, Dedent early-returns on minIndent==0, and the formatter's 4-space prefix
        // doubles up on the still-indented continuation lines. Post-fix it is 4.
        (continuationIndent - signatureIndent).ShouldBe(4);

        static int LeadingSpaces(string line)
        {
            return line.Length - line.TrimStart(' ').Length;
        }
    }

    // ── includeDocs ──────────────────────────────────────────────────────

    [Fact]
    public async Task FindOverloads_WithIncludeDocs_ShowsDocumentation()
    {
        // Arrange — ProcessShape has XML docs
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act
        string result = await tools.FindOverloads([Loc(filePath, 16, 19)], includeDocs: true, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Processes a shape");
    }

    // ── Error cases ──────────────────────────────────────────────────────

    [Fact]
    public async Task FindOverloads_NonMethodSymbol_ReturnsInlineError()
    {
        // Arrange — IShape.cs line 6: interface declaration, not a method
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — single-cursor user error renders inline
        string result = await tools.FindOverloads([Loc(filePath, 6, 18)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("not a method");
    }

    [Fact]
    public async Task FindOverloads_NeitherPositionNorName_ThrowsUserError()
    {
        // Act & Assert — neither location nor names is rejected with an actionable message
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.FindOverloads());
        ex.Message.ShouldContain("Provide one of:");
    }

    [Fact]
    public async Task FindOverloads_BothPositionAndBatchNames_ThrowsConflict()
    {
        // Arrange
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act & Assert — combining batch names with position is rejected
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.FindOverloads([Loc(filePath, 27, 19)], ["Format"]));

        ex.Message.ShouldContain("Pass either location");
    }

    [Fact]
    public async Task FindOverloads_NonExistentMethod_ReturnsError()
    {
        // Act — per-name error is captured inline
        string result = await tools.FindOverloads(symbolNames: ["NonExistentMethod123"], containingType: "ShapeService", ct: TestContext.Current.CancellationToken);

        result.ShouldContain("No method found");
    }

    [Fact]
    public async Task FindOverloads_KindFilterExcludesAllMatches_ReportsActualKinds()
    {
        // Act — "Circle" exists as a class; kind=Method excludes it.
        string result = await tools.FindOverloads(symbolNames: ["Circle"], kind: SymbolicKind.Method,
            ct: TestContext.Current.CancellationToken);

        // Assert — the empty result should hint that Circle exists as a Class
        result.ShouldContain("No method found");
        result.ShouldContain("\"Circle\" exists as Class");
        result.ShouldContain("drop the kind filter or use a different kind");
    }

    [Fact]
    public async Task FindOverloads_KindFilterPasses_NoKindBlameHint()
    {
        // Act — kind=Class survives the kind filter (Circle is a class), but OfType<IMethodSymbol>
        // drops it. KindFilterBlame should NOT blame the kind filter here — that's an OfType drop.
        string result = await tools.FindOverloads(symbolNames: ["Circle"], kind: SymbolicKind.Class,
            ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No method found");
        result.ShouldNotContain("exists as");
    }

    // ── Indexer support ───────────────────────────────────────────────────

    [Fact]
    public async Task FindOverloads_IndexerByName_ReturnsIndexer()
    {
        // Act
        string result = await tools.FindOverloads(symbolNames: ["this[]"], containingType: "ShapeCollection", ct: TestContext.Current.CancellationToken);

        // Assert — ShapeCollection has one indexer
        result.ShouldContain("this[]");
        result.ShouldContain("ShapeCollection");
        result.ShouldContain("index");
    }

    [Fact]
    public async Task FindOverloads_IndexerByPosition_ReturnsIndexer()
    {
        // Arrange — ShapeCollection.cs line 10: "    public IShape this[int index] => _shapes[index];"
        string filePath = fixture.ServicesFile("ShapeCollection.cs");

        // Act
        string result = await tools.FindOverloads([Loc(filePath, 10, 25)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("this[]");
        result.ShouldContain("ShapeCollection");
    }

    // ── Special symbol names ──────────────────────────────────────────────

    [Fact]
    public async Task FindOverloads_CtorByName_ReturnsConstructorOverloads()
    {
        // Act — ShapeCalculator has 2 instance constructors
        string result = await tools.FindOverloads(symbolNames: [".ctor"], containingType: "ShapeCalculator", ct: TestContext.Current.CancellationToken);

        // Assert — compact format shows parameter lists only
        result.ShouldContain("(2)");
        result.ShouldContain("(IShape shape)");
        result.ShouldContain("(double radius)");
    }

    [Fact]
    public async Task FindOverloads_CctorByName_ReturnsStaticConstructor()
    {
        // Act — ShapeCalculator has 1 static constructor
        string result = await tools.FindOverloads(symbolNames: [".cctor"], containingType: "ShapeCalculator", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("has no overloads");
    }

    [Fact]
    public async Task FindOverloads_OpAdditionByName_ReturnsOperator()
    {
        // Act — ShapeCollection has operator+
        string result = await tools.FindOverloads(symbolNames: ["op_Addition"], containingType: "ShapeCollection", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("ShapeCollection");
        result.ShouldContain("has no overloads");
    }

    [Fact]
    public async Task FindOverloads_OpImplicitByName_ShowsReturnTypesForConversionOperators()
    {
        // Act — ShapeCollection has two implicit operators: int and string
        string result = await tools.FindOverloads(symbolNames: ["op_Implicit"], containingType: "ShapeCollection", ct: TestContext.Current.CancellationToken);

        // Assert — return types must be visible to distinguish conversion overloads
        result.ShouldContain("-> int");
        result.ShouldContain("-> string");
    }

    [Fact]
    public async Task FindOverloads_SpecialName_NoContainingType_ReturnsError()
    {
        // Act — special names require containingType; per-name error is captured inline
        string result = await tools.FindOverloads(symbolNames: [".ctor"], ct: TestContext.Current.CancellationToken);

        result.ShouldContain("containingType");
    }

    // ── Base type hint ────────────────────────────────────────────────────

    [Fact]
    public async Task FindOverloads_MethodInBaseType_SuggestsBaseType()
    {
        // Act — Describe is declared in Shape, not overridden in Circle
        string result = await tools.FindOverloads(symbolNames: ["Describe"], containingType: "Circle", ct: TestContext.Current.CancellationToken);

        // Assert — should mention the base type
        result.ShouldContain("base type");
        result.ShouldContain("Shape");
    }

    // ── Ambiguous containing type ─────────────────────────────────────────

    [Fact]
    public async Task FindOverloads_CtorByName_AmbiguousContainingType_ReturnsAmbiguous()
    {
        // Act — two classes named 'NameTwin' exist in different namespaces
        string result = await tools.FindOverloads(symbolNames: [".ctor"], containingType: "NameTwin", ct: TestContext.Current.CancellationToken);

        // Assert — matches the error format used by find_references
        result.ShouldContain("Ambiguous");
        result.ShouldContain("NameTwin");
        result.ShouldContain("locations=['path:line:col']");
        result.ShouldContain("NameCollisionExamples.cs");
    }

    [Fact]
    public async Task FindOverloads_MethodByName_AmbiguousContainingType_ReturnsAmbiguous()
    {
        string result = await tools.FindOverloads(symbolNames: ["Execute"], containingType: "NameTwin", ct: TestContext.Current.CancellationToken);

        result.ShouldContain("Ambiguous");
        result.ShouldContain("Execute");
    }

    [Fact]
    public async Task FindOverloads_IndexerByName_AmbiguousContainingType_ReturnsAmbiguous()
    {
        string result = await tools.FindOverloads(symbolNames: ["this[]"], containingType: "NameTwin", ct: TestContext.Current.CancellationToken);

        result.ShouldContain("Ambiguous");
        result.ShouldContain("this[]");
    }

    // ── Batch ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindOverloads_Batch_MultipleNames_ProducesSectionedOutput()
    {
        // Act — batch two members on ShapeService
        string result = await tools.FindOverloads(symbolNames: ["Format", "ProcessShape"], containingType: "ShapeService", ct: TestContext.Current.CancellationToken);

        // Assert — each name gets its own header section
        result.ShouldContain("=== Format ===");
        result.ShouldContain("=== ProcessShape ===");
    }

    [Fact]
    public async Task FindOverloads_Batch_SectionsSeparatedBySingleBlankLine()
    {
        // Act — batch of two members on MultiTfmService (both resolve on the class)
        string result = await tools.FindOverloads(symbolNames: ["Calculate", "GetValue"], containingType: "MultiTfmService", ct: TestContext.Current.CancellationToken);

        // Assert — exactly one blank line between === sections, not two
        string normalized = result.Replace("\r\n", "\n");
        normalized.ShouldContain("=== Calculate ===");
        normalized.ShouldContain("=== GetValue ===");
        normalized.ShouldNotContain("\n\n\n===");
    }

    [Fact]
    public async Task FindOverloads_Batch_SingleName_OmitsHeaderWrapper()
    {
        // Act — single-item batch uses the FormatBatch N=1 short-circuit
        string result = await tools.FindOverloads(symbolNames: ["Format"], containingType: "ShapeService", ct: TestContext.Current.CancellationToken);

        // Assert — no "=== Format ===" wrapper for a single result
        result.ShouldNotContain("=== Format ===");
    }

    [Fact]
    public async Task FindOverloads_Batch_SameTypeNameDifferentNamespace_FallsBackToFullyQualified()
    {
        // Twins.Alpha.NameTwin.Execute vs Twins.Beta.NameTwin.Execute — bare "Execute"
        // AND simple containing-type "NameTwin" both collide → escalate to Tier 2.
        string file = fixture.ServicesFile("NameCollisionExamples.cs");
        string result = await tools.FindOverloads(
            [Loc(file, 9), Loc(file, 20)],
            ct: TestContext.Current.CancellationToken);

        result.ShouldNotContain("=== Execute ===");
        result.ShouldNotContain("=== NameTwin.Execute ==="); // proves Tier-1 was insufficient
        result.ShouldContain("=== TestFixture.Services.Twins.Alpha.NameTwin.Execute ===");
        result.ShouldContain("=== TestFixture.Services.Twins.Beta.NameTwin.Execute ===");
    }

    [Fact]
    public async Task FindOverloads_Batch_RejectsEmptyArray_Throws()
    {
        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.FindOverloads(symbolNames: []));

        ex.Message.ShouldContain("must not be empty");
    }

    // ── Project filter disambiguates cross-project name clashes ─────────

    [Fact]
    public async Task FindOverloads_BySymbolName_AmbiguousAcrossProjects_Errors()
    {
        // Arrange — Greet exists on TestFixture.Legacy.SharedHelper and TestFixture.Minimal.SharedHelper.
        // Without a project filter, find_overloads reports cross-type ambiguity.

        // Act
        string result = await tools.FindOverloads(symbolNames: ["Greet"], containingType: "SharedHelper", ct: TestContext.Current.CancellationToken);

        // Assert — per-name error captured inline by BatchOrSingle
        result.ShouldContain("Ambiguous");
    }

    [Fact]
    public async Task FindOverloads_BySymbolNameWithProject_NarrowsAmbiguity()
    {
        // Act — project filter narrows resolution to one Greet.
        string result = await tools.FindOverloads(symbolNames: ["Greet"], containingType: "SharedHelper", project: "Minimal", ct: TestContext.Current.CancellationToken);

        // Assert — resolves the Minimal Greet cleanly
        result.ShouldContain("Greet");
        result.ShouldNotContain("Ambiguous");
    }

    // ── "Did you mean" suggestion on miss ────────────────────────────────

    [Fact]
    public async Task FindOverloads_UnknownMethod_WithSimilarName_SuggestsDidYouMean()
    {
        // Act — typo 'ProcessShap' should fuzzy-match 'ProcessShape' on ShapeService
        string result = await tools.FindOverloads(symbolNames: ["ProcessShap"], containingType: "ShapeService", ct: TestContext.Current.CancellationToken);

        // Assert — per-name error captured inline by BatchOrSingle, with did-you-mean suffix
        result.ShouldContain("No method found");
        result.ShouldContain("Did you mean:");
        result.ShouldContain("ProcessShape");
    }

    [Fact]
    public async Task FindOverloads_UnknownMethod_NoContainingType_SuggestsDidYouMean()
    {
        // Act — typo 'ProcessShap' with no containingType fuzzy-matches across the solution
        string result = await tools.FindOverloads(symbolNames: ["ProcessShap"], ct: TestContext.Current.CancellationToken);

        // Assert — solution-wide fuzzy suggestion, even without containingType
        result.ShouldContain("No method found");
        result.ShouldContain("Did you mean:");
        result.ShouldContain("ProcessShape");
    }

    [Fact]
    public async Task FindOverloads_IndexerByName_UnknownContainingType_SuggestsDidYouMean()
    {
        // Act — typo 'ShapeCollectio' fuzzy-matches 'ShapeCollection'
        string result = await tools.FindOverloads(symbolNames: ["this[]"], containingType: "ShapeCollectio", ct: TestContext.Current.CancellationToken);

        // Assert — per-name error captured inline with did-you-mean suffix
        result.ShouldContain("No type found");
        result.ShouldContain("Did you mean:");
        result.ShouldContain("ShapeCollection");
    }

    // ── includeGenerated ─────────────────────────────────────────────────

    [Fact]
    public async Task FindOverloads_ByName_IncludeGeneratedFalse_ExcludesGeneratedOverload()
    {
        // PartialShapeProcessor has two GeneratedMethod overloads:
        //   - .g.cs:    GeneratedMethod()
        //   - .Extra.cs: GeneratedMethod(string label)
        // Default includeGenerated=false keeps only the non-generated overload.
        string result = await tools.FindOverloads(symbolNames: ["GeneratedMethod"], containingType: "PartialShapeProcessor", ct: TestContext.Current.CancellationToken);

        result.ShouldContain("(string label)");
        result.ShouldNotContain("PartialShapeProcessor.g.cs");
    }

    [Fact]
    public async Task FindOverloads_ByName_IncludeGeneratedTrue_IncludesGeneratedOverload()
    {
        string result = await tools.FindOverloads(symbolNames: ["GeneratedMethod"], containingType: "PartialShapeProcessor", includeGenerated: true, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("(string label)");
        result.ShouldContain("PartialShapeProcessor.g.cs");
    }

    // ── includeTests ─────────────────────────────────────────────────────

    [Fact]
    public async Task FindOverloads_ByName_DefaultExcludesTests_NoMatchInTestProject()
    {
        // ShapeTestHelper.DescribeAll lives only in TestFixture.Tests
        string result = await tools.FindOverloads(symbolNames: ["DescribeAll"], containingType: "ShapeTestHelper", ct: TestContext.Current.CancellationToken);

        result.ShouldContain("No method found");
    }

    [Fact]
    public async Task FindOverloads_ByName_IncludeTestsTrue_ResolvesTestProjectMethod()
    {
        string result = await tools.FindOverloads(symbolNames: ["DescribeAll"], containingType: "ShapeTestHelper", includeTests: true, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("DescribeAll");
        result.ShouldContain("params IShape[]");
    }
}
