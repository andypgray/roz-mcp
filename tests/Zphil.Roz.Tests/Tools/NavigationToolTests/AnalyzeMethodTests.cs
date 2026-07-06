using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.NavigationToolTests;

public class AnalyzeMethodTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools tools = CreateNavigationTools(fixture);

    // ── Signature section ─────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeMethod_ByName_RendersSignature()
    {
        // Act
        string result = await tools.AnalyzeMethod(symbolNames: ["ProcessShape"], containingType: "ShapeService",
            ct: TestContext.Current.CancellationToken);

        // Assert — signature line carries the method name and its parameter type
        result.ShouldContain("ProcessShape");
        result.ShouldContain("IShape");
    }

    [Fact]
    public async Task AnalyzeMethod_ByLocation_ResolvesMethod()
    {
        // Arrange — ShapeService.cs line 16: ProcessShape
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act
        string result = await tools.AnalyzeMethod([Loc(filePath, 16, 19)], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("ProcessShape");
    }

    // ── Inbound section (reused find_references referenceKinds=invocations) ─────────

    [Fact]
    public async Task AnalyzeMethod_UncalledMethod_RendersNoCallersInbound()
    {
        // Act — ProcessShape has no callers in the fixture
        string result = await tools.AnalyzeMethod(symbolNames: ["ProcessShape"], containingType: "ShapeService",
            ct: TestContext.Current.CancellationToken);

        // Assert — inbound renders the reused no-callers message rather than throwing
        result.ShouldContain("No callers found for 'ProcessShape'");
    }

    [Fact]
    public async Task AnalyzeMethod_Constructor_ShowsDiRegistrationFallbackInInbound()
    {
        // Act — ShapeService is DI-registered (AddTransient) with no direct `new` callers, so the
        // reused invocation path's DI fallback fires in the inbound section.
        string result = await tools.AnalyzeMethod(symbolNames: [".ctor"], containingType: "ShapeService",
            ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("DI registration");
        result.ShouldContain("transient");
    }

    // ── Outbound section ──────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeMethod_RendersInSolutionOutboundCall()
    {
        // Act — ProcessShape calls IShape.Describe(), an in-solution member
        string result = await tools.AnalyzeMethod(symbolNames: ["ProcessShape"], containingType: "ShapeService",
            ct: TestContext.Current.CancellationToken);

        // Assert — one in-solution callee shown under the outbound header
        result.ShouldContain("Outbound calls (1 in-solution");
        result.ShouldContain("Describe");
    }

    [Fact]
    public async Task AnalyzeMethod_ExternalCallsSuppressedByDefault()
    {
        // Act — GetLargest calls Enumerable.MaxBy (external) and IShape.Area (in-solution)
        string result = await tools.AnalyzeMethod(symbolNames: ["GetLargest"], containingType: "ShapeService",
            ct: TestContext.Current.CancellationToken);

        // Assert — external callee collapses to a count + type-name summary, not a full callee row.
        // ("MaxBy(" still appears inside the IShape.Area call-site snippet, but "MaxBy<...>" — the
        // rendered generic-method header — must not, which is what proves it stayed collapsed.)
        result.ShouldContain("Outbound calls (1 in-solution, 1 external)");
        result.ShouldContain("(+1 external: Enumerable)");
        result.ShouldNotContain("MaxBy<");
    }

    [Fact]
    public async Task AnalyzeMethod_IncludeExternalCalls_WidensToShowExternalCallee()
    {
        // Act
        string result = await tools.AnalyzeMethod(symbolNames: ["GetLargest"], containingType: "ShapeService",
            includeExternalCalls: true, ct: TestContext.Current.CancellationToken);

        // Assert — the previously-suppressed external callee now renders as a full callee row, and
        // the collapsed "+N external" summary line is gone.
        result.ShouldContain("IEnumerable<IShape>.MaxBy");
        result.ShouldNotContain("(+1 external");
    }

    [Fact]
    public async Task AnalyzeMethod_CountsCallsInsideLocalFunctions()
    {
        // Act — CalculateTotal calls local function GetArea, whose body reads IShape.Area. The walk
        // deliberately descends into the local function, so IShape.Area is an outbound call of
        // CalculateTotal even though it is only reachable through GetArea's body.
        string result = await tools.AnalyzeMethod(symbolNames: ["CalculateTotal"], containingType: "LocalFunctionExample",
            ct: TestContext.Current.CancellationToken);

        // Assert — IShape.Area appears as a distinct callee (only possible via descent), alongside the
        // local-function call itself, for two in-solution targets total.
        result.ShouldContain("Outbound calls (2 in-solution");
        result.ShouldContain("IShape.Area");
        result.ShouldContain("GetArea");
    }

    [Fact]
    public async Task AnalyzeMethod_ObjectCreation_RendersConstructorAsOutboundCall()
    {
        // Act — ShapeCalculator's (double) constructor does `new Circle(radius)`, an in-solution
        // object-creation callee (the other ctor only assigns a field, so Circle is the lone callee).
        string result = await tools.AnalyzeMethod(symbolNames: [".ctor"], containingType: "ShapeCalculator",
            ct: TestContext.Current.CancellationToken);

        // Assert — the constructed type's ctor is resolved and grouped as one in-solution outbound call
        result.ShouldContain("Outbound calls (1 in-solution");
        result.ShouldContain("Circle");
    }

    [Fact]
    public async Task AnalyzeMethod_ImplicitObjectCreation_RendersConstructorAsOutboundCall()
    {
        // Act — OuterContainer.CreateProcessor's body is `=> new()`, a target-typed object creation of
        // the in-solution nested InnerProcessor. Only an in-solution count of 1 proves the implicit
        // `new()` resolved (InnerProcessor also appears in the signature as the return type).
        string result = await tools.AnalyzeMethod(symbolNames: ["CreateProcessor"], containingType: "OuterContainer",
            ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Outbound calls (1 in-solution");
    }

    [Fact]
    public async Task AnalyzeMethod_LowMaxResults_DoesNotTruncateOutbound()
    {
        // Act — CalculateTotal has two in-solution callees; maxResults=1 caps INBOUND callers only.
        // Outbound is body-bounded, so both callees must still render — the analyze_method A/B's task-05
        // outbound-recall regression was a low maxResults silently truncating a god-method's collaborators.
        string result = await tools.AnalyzeMethod(symbolNames: ["CalculateTotal"], containingType: "LocalFunctionExample",
            maxResults: 1, ct: TestContext.Current.CancellationToken);

        // Assert — both callees render despite maxResults=1, and no outbound truncation hint appears
        result.ShouldContain("Outbound calls (2 in-solution");
        result.ShouldNotContain("total outbound");
    }

    [Fact]
    public async Task AnalyzeMethod_OutboundIncludesImplicitThisAndConditionalPropertyAccess()
    {
        // Act — Read() touches a bare property (this.Size) and a null-conditional one
        // (collection?.Count); both are property accesses the outbound walk must count, while the
        // nameof(Untouched) operand stays a compile-time reference and must NOT be counted.
        string result = await tools.AnalyzeMethod(symbolNames: ["Read"], containingType: "OutboundAccessExample",
            ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Outbound calls (2 in-solution"); // fails today: shows "0 in-solution"
        result.ShouldContain("Size");
        result.ShouldContain("Count");
        result.ShouldNotContain("Untouched"); // nameof operand stays dropped
    }

    [Fact]
    public async Task AnalyzeMethod_ConditionalInvocation_CountedOnceViaInvocationNotBinding()
    {
        // Act — ConditionalCall() makes one null-conditional call (collection?.Dispose()); the walk
        // visits both the invocation and its member binding, and must count the call exactly once.
        string result = await tools.AnalyzeMethod(symbolNames: ["ConditionalCall"], containingType: "OutboundAccessExample",
            ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Outbound calls (1 in-solution");
        result.ShouldContain("Dispose");
    }

    [Fact]
    public async Task AnalyzeMethod_PartialMethod_AnalyzesImplementingPartBody()
    {
        // Act — PartialMethodExample.Summarize is a partial method: the defining declaration is
        // bodyless, so the extractor must select the implementing part (which calls IShape.Describe).
        string result = await tools.AnalyzeMethod(symbolNames: ["Summarize"], containingType: "PartialMethodExample",
            ct: TestContext.Current.CancellationToken);

        // Assert — the implementing part's body was walked, surfacing its in-solution callee
        result.ShouldContain("Summarize");
        result.ShouldContain("Outbound calls (1 in-solution");
        result.ShouldContain("Describe");
    }

    // ── includeOverloads aggregate ────────────────────────────────────────

    [Fact]
    public async Task AnalyzeMethod_IncludeOverloads_ShowsOverloadListAndAggregatesCallees()
    {
        // Act — Format has 3 overloads; IShape.Describe is called from two of their bodies
        string result = await tools.AnalyzeMethod(symbolNames: ["Format"], containingType: "ShapeService",
            includeOverloads: true, ct: TestContext.Current.CancellationToken);

        // Assert — overload signature list appended, and callees aggregated across the overload set
        result.ShouldContain("Overloads of 'Format' in ShapeService (3)");
        result.ShouldContain("Describe");
    }

    [Fact]
    public async Task AnalyzeMethod_IncludeOverloadsByLocation_ExpandsSingleResolvedSymbolToOverloadSet()
    {
        // Arrange — ShapeService.cs line 27 is the first Format overload. A cursor resolves a single
        // symbol, so includeOverloads must expand it to the whole overload set (the name-based path
        // already returns every overload; only the positional path exercises that expansion).
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act
        string result = await tools.AnalyzeMethod([Loc(filePath, 27, 19)], includeOverloads: true,
            ct: TestContext.Current.CancellationToken);

        // Assert — the expansion produced the same aggregate as the name-based call
        result.ShouldContain("Overloads of 'Format' in ShapeService (3)");
        result.ShouldContain("Describe");
    }

    // ── Error cases ───────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeMethod_NonMethodTarget_ReturnsInlineError()
    {
        // Act — IShape is an interface, not a method; single-item user error renders inline
        string result = await tools.AnalyzeMethod(symbolNames: ["IShape"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("not a method");
    }

    [Fact]
    public async Task AnalyzeMethod_NeitherLocationNorName_ThrowsUserError()
    {
        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.AnalyzeMethod());
        ex.Message.ShouldContain("Provide one of:");
    }

    [Fact]
    public async Task AnalyzeMethod_BothLocationAndBatchNames_ThrowsConflict()
    {
        // Arrange
        string filePath = fixture.ServicesFile("ShapeService.cs");

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.AnalyzeMethod([Loc(filePath, 16, 19)], ["ProcessShape"]));
        ex.Message.ShouldContain("Pass either location");
    }

    [Fact]
    public async Task AnalyzeMethod_EmptyArray_Throws()
    {
        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.AnalyzeMethod(symbolNames: []));
        ex.Message.ShouldContain("must not be empty");
    }

    // ── Batch ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeMethod_Batch_MultipleNames_ProducesSectionedOutput()
    {
        // Act
        string result = await tools.AnalyzeMethod(symbolNames: ["ProcessShape", "GetLargest"],
            containingType: "ShapeService", ct: TestContext.Current.CancellationToken);

        // Assert — each name gets its own header section
        result.ShouldContain("=== ProcessShape ===");
        result.ShouldContain("=== GetLargest ===");
    }
}
