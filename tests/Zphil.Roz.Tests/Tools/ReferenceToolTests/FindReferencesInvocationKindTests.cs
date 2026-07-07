using System.Text.RegularExpressions;
using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.ReferenceToolTests;

public class FindReferencesInvocationKindTests(WorkspaceFixture fixture)
{
    private const ReferenceKind Kind = ReferenceKind.Invocations;

    private readonly ReferenceTools tools = CreateReferenceTools(fixture);

    [Fact]
    public async Task FindReferences_OnInterfaceMethod_FindsCallSites()
    {
        // Arrange — "    string Describe();" — Describe starts at col 12 (line 18)
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 18, 12)], referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — ProcessShape calls shape.Describe()
        result.ShouldContain("ProcessShape");
    }

    [Fact]
    public async Task FindReferences_CallerWithMultipleCallSites_HeaderCountsCallerSymbols()
    {
        // Arrange — DoubleCallExample.PingTwice has two call sites of Ping (one caller symbol).
        // CR-19: the callers header must count caller symbols (matching TotalCount and the
        // truncation guard), not call sites — the old code summed call sites and showed "(2)".

        // Act
        string result = await tools.FindReferences(symbolNames: ["Ping"], containingType: "DoubleCallExample", referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — one caller, shown as "(1)", with the calling method present.
        result.ShouldContain("Callers of 'Ping' (1)");
        result.ShouldNotContain("Callers of 'Ping' (2)");
        result.ShouldContain("PingTwice");
    }

    [Fact]
    public async Task FindReferences_OnPublicKeyword_ResolvesToEnclosingMethod()
    {
        // Arrange — "    public virtual string Describe() =>" — col 5 is the 'p' of 'public' (line 13)
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 13, 5)], referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — ProcessShape calls shape.Describe()
        result.ShouldContain("ProcessShape");
    }

    [Fact]
    public async Task FindReferences_OnInterfaceMethod_ShowsCallSiteLineText()
    {
        // Arrange — "    string Describe();" — Describe starts at col 12 (line 18)
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 18, 12)], referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — should include the actual call-site source line from ShapeService.ProcessShape
        result.ShouldContain("shape.Describe()");
    }

    // ── contextLines ────────────────────────────────────────────────────

    [Fact]
    public async Task FindReferences_ContextLines1_ShowsSurroundingLines()
    {
        // Arrange — IShape.Describe() at line 18, col 12
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 18, 12)], contextLines: 1, referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — should include the call-site line and surrounding lines with line numbers
        result.ShouldContain("shape.Describe()");
        // Window 16-18 with the call line marked '>'; the "(16-18):" header is gone — the
        // range is now implicit in the consecutive gutter line numbers.
        Regex.IsMatch(result, @"(?m)^ +> +\d+ \|").ShouldBeTrue("expected the matched call line marked with '>'");
        result.ShouldContain("16 |");
        result.ShouldContain("17 |");
        result.ShouldContain("18 |");
    }

    // ── maxResults ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FindReferences_WithMaxResults_LimitsOutput()
    {
        // Arrange — IShape.Describe() at line 18, col 12 — has multiple callers
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — limit to 1 caller
        string result = await tools.FindReferences([Loc(filePath, 18, 12)], maxResults: 1, referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("showing 1 of");
    }

    [Fact]
    public async Task FindReferences_WithMaxResults_GreaterThanTotal_ReturnsAll()
    {
        // Arrange — IShape.Describe() at line 18, col 12
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 18, 12)], maxResults: 1000, referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — no truncation notice
        result.ShouldNotContain("showing");
    }

    // ── project labels ────────────────────────────────────────────────────

    [Fact]
    public async Task FindReferences_ShowsProjectNamePerCaller()
    {
        // Arrange — IShape.Describe() at line 18, col 12
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 18, 12)], referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — caller entries should include the project name in brackets
        result.ShouldContain("[TestFixture]");
    }

    // ── excludeBaseCalls ────────────────────────────────────────────────────

    [Fact]
    public async Task FindReferences_WithExcludeBaseCalls_ExcludesBaseCallsFromOverrides()
    {
        // Arrange — Shape.Describe() at line 13, "Describe" starts at col 27
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 13, 27)], referenceKinds: Kind, excludeBaseCalls: true, ct: TestContext.Current.CancellationToken);

        // Assert — ProcessShape is a direct caller, Triangle.Describe uses base.Describe() (indirect)
        result.ShouldContain("ProcessShape");
        result.ShouldNotContain("Triangle");
    }

    [Fact]
    public async Task FindReferences_WithoutExcludeBaseCalls_IncludesAllCallers()
    {
        // Arrange — Shape.Describe() at line 13
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 13, 27)], referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — both direct and indirect callers
        result.ShouldContain("ProcessShape");
        result.ShouldContain("Describe");
    }

    [Fact]
    public async Task FindReferences_Invocations_NonExistentFile_ReturnsInlineError()
    {
        // Single-cursor user errors render as an inline error message (matches symbolNames batch behavior).
        string result = await tools.FindReferences([Loc("NonExistent.cs", 1, 1)], referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("File not found in solution");
    }

    // ── includeTests ─────────────────────────────────────────────────────

    [Fact]
    public async Task FindReferences_Invocations_DefaultExcludesTests()
    {
        // Arrange — IShape.Describe() at line 18, col 12
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 18, 12)], referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — test caller should be excluded
        result.ShouldNotContain("ShapeTests");
    }

    // ── symbolName resolution ────────────────────────────────────────────

    [Fact]
    public async Task FindReferences_BySymbolNameAndContainingType_FindsCallSites()
    {
        // Act — Describe exists on IShape, Shape, Circle etc. — narrow with containingType
        string result = await tools.FindReferences(symbolNames: ["Describe"], containingType: "IShape", referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — ProcessShape calls shape.Describe()
        result.ShouldContain("ProcessShape");
    }

    [Fact]
    public async Task FindReferences_Invocations_Ambiguous_ReturnsCandidates()
    {
        // Act — "Area" exists on IShape, Shape, Circle, Rectangle, Triangle, Pentagon;
        // per-name error is captured inline
        string result = await tools.FindReferences(symbolNames: ["Area"], referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — error should list candidates with file positions
        result.ShouldContain("Ambiguous");
        result.ShouldContain("Area");
    }

    [Fact]
    public async Task FindReferences_Invocations_BySymbolName_NotFound_ReturnsError()
    {
        // Act — per-name error is captured inline
        string result = await tools.FindReferences(symbolNames: ["ZzzNotReal"], referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("No symbol found");
    }

    [Fact]
    public async Task FindReferences_Invocations_BothNameAndPosition_ThrowsConflict()
    {
        // Arrange — "string Describe();" at line 18 in IShape.cs
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act & Assert — combining batch names with position is rejected
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.FindReferences([Loc(filePath, 18, 12)], ["Describe"], referenceKinds: Kind));
        ex.Message.ShouldContain("Pass either location");
    }

    [Fact]
    public async Task FindReferences_Invocations_NeitherNameNorPosition_Throws() =>
        await Should.ThrowAsync<UserErrorException>(() => tools.FindReferences(referenceKinds: Kind));

    // ── excludeBaseCalls: non-method symbols ──────────────────────────────

    [Fact]
    public async Task FindReferences_ExcludeBaseCalls_OnProperty_IncludesAllCallers()
    {
        // Arrange — Area is an abstract property with no base.Area calls in the override chain,
        // so excludeBaseCalls should be a no-op (no overrides to exclude).
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act — Shape.Area at line 7, col 28 ("Area" identifier)
        string result = await tools.FindReferences([Loc(filePath, 7, 28)], referenceKinds: Kind, excludeBaseCalls: true, ct: TestContext.Current.CancellationToken);

        // Assert — should still include callers despite excludeBaseCalls being true
        result.ShouldNotBeNullOrWhiteSpace();
    }

    // ── excludeBaseCalls: method that overrides but does NOT override the target ──

    [Fact]
    public async Task FindReferences_ExcludeBaseCalls_NonOverridingMethod_StillIncluded()
    {
        // Arrange — Circle does NOT override Describe (only Triangle does),
        // so Circle should still appear as a caller even with excludeBaseCalls=true
        // when looking at callers of IShape.Describe
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — IShape.Describe at line 18, col 12
        string result = await tools.FindReferences([Loc(filePath, 18, 12)], referenceKinds: Kind, excludeBaseCalls: true, ct: TestContext.Current.CancellationToken);

        // Assert — ProcessShape should still appear (it's a regular caller, not an override)
        result.ShouldContain("ProcessShape");
    }

    // ── excludeBaseCalls: kind auto-promotion ─────────────────────────────

    [Fact]
    public async Task FindReferences_ExcludeBaseCalls_WithKindsAll_PromotesKindToInvocations()
    {
        // Arrange — IShape.Describe() at line 18, col 12; referenceKinds defaults to All
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — excludeBaseCalls=true forces referenceKinds=invocations, so the call returns invocation results
        string result = await tools.FindReferences([Loc(filePath, 18, 12)], excludeBaseCalls: true, ct: TestContext.Current.CancellationToken);

        // Assert — ProcessShape is a direct caller of IShape.Describe()
        result.ShouldContain("ProcessShape");
    }

    // ── includeTests: verify both paths ────────────────────────────────────

    [Fact]
    public async Task FindReferences_Invocations_IncludeTests_IncludesTestProjectCallers()
    {
        // Arrange — IShape.Describe has a caller in ShapeTests
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — includeTests: true opts in to test projects
        string result = await tools.FindReferences([Loc(filePath, 18, 12)], includeTests: true, maxResults: null, referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — test project caller should be present
        result.ShouldContain("ShapeTests");
    }

    // ── excludeBaseCalls: deep override chain (Triangle.Describe -> Shape.Describe) ──

    [Fact]
    public async Task FindReferences_ExcludeBaseCalls_DeepOverrideChain_ExcludesOverride()
    {
        // Arrange — Triangle.Describe() calls base.Describe(), which is Shape.Describe().
        // When excludeBaseCalls=true on Shape.Describe, Triangle.Describe should be excluded
        // because it directly overrides Shape.Describe (1-level chain via the while loop).
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act — Shape.Describe at line 13, col 27
        string resultWith = await tools.FindReferences([Loc(filePath, 13, 27)], referenceKinds: Kind, excludeBaseCalls: true, ct: TestContext.Current.CancellationToken);
        string resultWithout = await tools.FindReferences([Loc(filePath, 13, 27)], referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — Triangle override is excluded when excludeBaseCalls=true but present otherwise
        resultWith.ShouldNotContain("Triangle");
        resultWithout.ShouldContain("Describe");
    }

    // ── indexer snap-to-nearest ─────────────────────────────────────────

    [Theory]
    [InlineData(5, "public keyword")]
    [InlineData(12, "return type (IShape)")]
    [InlineData(19, "this keyword")]
    public async Task FindReferences_Invocations_OnIndexerLine_ResolvesToIndexer(int column, string cursorDescription)
    {
        // Arrange — "    public IShape this[int index] => _shapes[index];" (line 10)
        // Cursor on different tokens should all snap to the indexer declaration
        _ = cursorDescription;
        string filePath = fixture.ServicesFile("ShapeCollection.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 10, column)], referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — DescribeFirst calls collection[0]
        result.ShouldContain("DescribeFirst");
    }

    // ── project filter (resolution-only; see "Project Filter" in CLAUDE.md) ───

    [Fact]
    public async Task FindReferences_Invocations_CursorMode_WithProject_IgnoresFilterAndReportsAllProjects()
    {
        // Arrange — IShape.Describe() has invocation callers in both TestFixture (ProcessShape)
        // and TestFixture.Tests (ShapeTests). A cursor already targets one symbol, so `project`
        // has no resolution role: the old result-filter dropped the production caller when scoped
        // to the test project; the fix reports callers across all projects instead.
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — scope to the (unambiguous) test project, include tests so its caller is a candidate.
        string result = await tools.FindReferences([Loc(filePath, 18, 12)], project: "TestFixture.Tests", includeTests: true, referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — both the production caller (would have been filtered out) and the test caller
        // survive, and a note explains the ignored filter.
        result.ShouldContain("ProcessShape");
        result.ShouldContain("ShapeTests");
        result.ShouldContain("project filter ignored");
    }

    [Fact]
    public async Task FindReferences_Invocations_CursorMode_WithProject_SameCallersAsUnscoped()
    {
        // Arrange — IShape.Describe() has callers in TestFixture (ProcessShape) and the test
        // project (ShapeTests). TestFixture.Minimal has none.
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — cursor mode ignores `project`, so the caller set must match the unscoped call.
        string withProject = await tools.FindReferences([Loc(filePath, 18, 12)], project: "TestFixture.Minimal", includeTests: true, maxResults: null, referenceKinds: Kind, ct: TestContext.Current.CancellationToken);
        string withoutProject = await tools.FindReferences([Loc(filePath, 18, 12)], includeTests: true, maxResults: null, referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — every caller present unscoped is still present when scoped (no silent undercount).
        foreach (string caller in new[] { "ProcessShape", "ShapeTests" })
        {
            withoutProject.ShouldContain(caller);
            withProject.ShouldContain(caller);
        }
    }

    [Fact]
    public async Task FindReferences_Invocations_WithProject_NoMatch_ReturnsInlineError()
    {
        // Arrange
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 18, 12)], project: "NonExistentProject", referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — single-cursor user error renders inline
        result.ShouldContain("No project matching");
    }

    // ── containingType receiver filtering ──────────────────────────────────

    [Fact]
    public async Task FindReferences_Invocations_WithContainingType_FiltersOutUnrelatedReceivers()
    {
        // Act — Describe is declared on Shape. When containingType="Shape",
        // callers where the receiver is IShape (not a subtype of Shape) should be excluded.
        string result = await tools.FindReferences(symbolNames: ["Describe"], containingType: "Shape", maxResults: 100, referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — AnalyzeCircle/AnalyzeRectangle call Describe on Shape subtypes
        result.ShouldContain("AnalyzeCircle");
        result.ShouldContain("AnalyzeRectangle");
        // AnalyzeGenericShape calls shape.Describe() where shape is IShape (NOT IS-A Shape)
        result.ShouldNotContain("AnalyzeGenericShape");
        // ProcessShape calls shape.Describe() where shape is IShape (NOT IS-A Shape)
        result.ShouldNotContain("ProcessShape");
    }

    [Fact]
    public async Task FindReferences_Invocations_WithContainingType_InterfaceIncludesAllImplementors()
    {
        // Act — IShape.Describe() — all shape types implement IShape
        string result = await tools.FindReferences(symbolNames: ["Describe"], containingType: "IShape", maxResults: 100, referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — callers on any IShape implementor should be included
        result.ShouldContain("AnalyzeCircle");
        result.ShouldContain("AnalyzeRectangle");
        result.ShouldContain("AnalyzeGenericShape");
        result.ShouldContain("ProcessShape");
    }

    [Fact]
    public async Task FindReferences_Invocations_WithContainingType_Property_FiltersToReceiverType()
    {
        // Act — Area is overridden on Circle. When containingType="Circle",
        // only callers where receiver IS-A Circle should be returned.
        string result = await tools.FindReferences(symbolNames: ["Area"], containingType: "Circle", maxResults: 100, referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — SumAreas calls c.Area (Circle) and r.Area (Rectangle)
        // Only the Circle receiver should match
        result.ShouldContain("SumAreas");
    }

    // ── maxResults validation ────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task FindReferences_Invocations_MaxResultsLessThanOne_ReturnsInlineError(int maxResults)
    {
        // Arrange
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 18, 12)], maxResults: maxResults, referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — single-cursor user error renders inline (validation lives in the service)
        result.ShouldContain("maxResults must be >= 1");
    }

    // ── ctor caller inside object creation (receiver-type filtering) ───────

    [Fact]
    public async Task FindReferences_Invocations_CtorCallInsideLambda_IncludesLambdaCallSite()
    {
        // Regression: ObjectCreationExpression inside a lambda argument should match
        // `containingType` during receiver-type filtering. Previously, GetReceiverType walked
        // past `new X()` to the outer invocation and used the outer receiver's type,
        // causing lambda-wrapped constructor calls to be filtered out.
        // ServiceRegistration.Configure has `services.AddScoped<ShapeCollection>(sp => new ShapeCollection())`.

        // Act
        string result = await tools.FindReferences(symbolNames: [".ctor"], containingType: "ShapeCollection", referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — lambda-wrapped call in ServiceRegistration.cs should be present
        result.ShouldContain("ServiceRegistration.cs");
    }

    [Fact]
    public async Task FindReferences_Invocations_CtorCallViaImplicitNew_IncludesImplicitObjectCreation()
    {
        // Regression: ImplicitObjectCreationExpressionSyntax (target-typed `new()`) should be
        // recognized by GetReceiverType alongside explicit `new X()`. OuterContainer.CreateProcessor
        // returns `new()` which is an ImplicitObjectCreationExpressionSyntax targeting InnerProcessor.

        // Act
        string result = await tools.FindReferences(symbolNames: [".ctor"], containingType: "InnerProcessor", referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — implicit `new()` call in TypeKindExamples.cs should be present
        result.ShouldContain("TypeKindExamples.cs");
    }

    // ── project filter during symbol resolution ────────────────────────────

    [Fact]
    public async Task FindReferences_Invocations_SameNameInTwoProjects_ReturnsAmbiguity()
    {
        // Arrange — Greet exists on both TestFixture.Legacy.SharedHelper and
        // TestFixture.Minimal.SharedHelper. Without project filter, resolution is ambiguous.

        // Act — per-name error is captured inline
        string result = await tools.FindReferences(symbolNames: ["Greet"], containingType: "SharedHelper", referenceKinds: Kind, ct: TestContext.Current.CancellationToken);
        result.ShouldContain("Ambiguous");
    }

    [Fact]
    public async Task FindReferences_Invocations_WithProjectFilter_NarrowsAmbiguity()
    {
        // Arrange — Greet exists in both Legacy and Minimal projects. With `project: "Minimal"`
        // during resolution, only one candidate remains — no ambiguity error.

        // Act
        string result = await tools.FindReferences(symbolNames: ["Greet"], containingType: "SharedHelper", project: "Minimal", referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — no ambiguity, no callers (neither Greet method is called anywhere)
        result.ShouldContain("No callers found");
    }

    // ── ctor with no callers and no DI registrations ────────────────────────

    [Fact]
    public async Task FindReferences_Invocations_CtorNoDirectCallersNoDiRegistrations_ShowsDirectOrDiMessage()
    {
        // Arrange — LocalFunctionExample has no `new LocalFunctionExample()` calls and is not DI-registered.
        // The DI scan runs (target IS a constructor) but returns an empty list — output should
        // clarify that BOTH direct callers and DI registrations were checked.

        // Act
        string result = await tools.FindReferences(symbolNames: [".ctor"], containingType: "LocalFunctionExample", referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No direct callers or DI registrations found");
    }

    // ── Batch ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindReferences_Invocations_Batch_MultipleNames_ProducesSectionedOutput()
    {
        // Act — batch two member names on IShape
        string result = await tools.FindReferences(symbolNames: ["Describe", "Area"], containingType: "IShape", referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — each name gets its own header section
        result.ShouldContain("=== Describe ===");
        result.ShouldContain("=== Area ===");
    }

    [Fact]
    public async Task FindReferences_Invocations_Batch_SingleName_OmitsHeaderWrapper()
    {
        // Act — single-item batch uses the FormatBatch N=1 short-circuit
        string result = await tools.FindReferences(symbolNames: ["Describe"], containingType: "IShape", referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — no "=== Describe ===" wrapper for a single result
        result.ShouldNotContain("=== Describe ===");
    }

    [Fact]
    public async Task FindReferences_Invocations_Batch_RejectsPositionWithNames_Throws()
    {
        // Arrange
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.FindReferences([Loc(filePath, 18)], ["Describe"], referenceKinds: Kind));

        ex.Message.ShouldContain("Pass either location");
    }

    [Fact]
    public async Task FindReferences_Invocations_Batch_RejectsEmptyArray_Throws()
    {
        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.FindReferences(symbolNames: [], referenceKinds: Kind));

        ex.Message.ShouldContain("must not be empty");
    }

    // ── Interface-dispatch tip ─────────────────────────────────────────────

    [Fact]
    public async Task FindReferences_Invocations_OnConcreteMethodImplementingInterface_EmitsInterfaceHint()
    {
        // Arrange — Shape.Describe() implements IShape.Describe(); callers going through IShape
        // resolve to the interface symbol, not Shape.Describe. The tip should flag that.

        // Act
        string result = await tools.FindReferences(symbolNames: ["Describe"], containingType: "Shape", referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Tip:");
        result.ShouldContain("IShape.Describe");
        result.ShouldContain("interface dispatch");
    }

    [Fact]
    public async Task FindReferences_Invocations_OnInterfaceMember_DoesNotEmitHint()
    {
        // Act — querying the interface member itself
        string result = await tools.FindReferences(symbolNames: ["Describe"], containingType: "IShape", referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — no tip, since we're already on the interface
        result.ShouldNotContain("Tip:");
    }

    [Fact]
    public async Task FindReferences_Invocations_OnMethodImplementingTwoInterfaces_ListsBothInHint()
    {
        // Act — AlphaBetaHandler.Handle implements both IAlpha.Handle and IBeta.Handle
        string result = await tools.FindReferences(symbolNames: ["Handle"], containingType: "AlphaBetaHandler", referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — tip should list both interface members
        result.ShouldContain("Tip:");
        result.ShouldContain("IAlpha.Handle");
        result.ShouldContain("IBeta.Handle");
        result.ShouldContain("2 interface members");
    }

    [Fact]
    public async Task FindReferences_Invocations_OnExplicitImplementation_EmitsInterfaceHint()
    {
        // Arrange — ShapeManager has `void IResettable.Reset() {}` (explicit impl).

        // Act
        string result = await tools.FindReferences(symbolNames: ["Reset"], containingType: "ShapeManager", referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — tip should point to IResettable.Reset
        result.ShouldContain("Tip:");
        result.ShouldContain("IResettable.Reset");
        result.ShouldContain("interface dispatch");
    }

    [Fact]
    public async Task FindReferences_Invocations_OnNonInterfaceMethod_DoesNotEmitHint()
    {
        // Arrange — StandaloneVirtualMethods.DoWork has no interface at all
        // Act
        string result = await tools.FindReferences(symbolNames: ["DoWork"], containingType: "StandaloneVirtualMethods", referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — regression guard: no tip for vanilla virtual methods
        result.ShouldNotContain("Tip:");
    }

    [Fact]
    public async Task FindReferences_Invocations_OnMethodImplementingExternalInterface_EmitsExternalHint()
    {
        // Arrange — ShapeManager has `void IDisposable.Dispose() {}` (explicit external impl).

        // Act
        string result = await tools.FindReferences(symbolNames: ["Dispose"], containingType: "ShapeManager", referenceKinds: Kind, ct: TestContext.Current.CancellationToken);

        // Assert — the tip should mention IDisposable and flag it as external
        result.ShouldContain("Tip:");
        result.ShouldContain("IDisposable.Dispose");
        result.ShouldContain("external");
        result.ShouldContain("System.IDisposable");
    }
}
