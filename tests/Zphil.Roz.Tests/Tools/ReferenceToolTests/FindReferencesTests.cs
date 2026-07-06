using System.Text.RegularExpressions;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.ReferenceToolTests;

public class FindReferencesTests(WorkspaceFixture fixture)
{
    private readonly ReferenceTools tools = CreateReferenceTools(fixture);

    [Fact]
    public async Task FindReferences_OnInterfaceType_FindsUsagesAcrossFiles()
    {
        // Arrange — "public interface IShape" — IShape at line 6, col 18
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — maxResults: null so cross-file references aren't truncated away
        string result = await tools.FindReferences([Loc(filePath, 6, 18)], maxResults: null, ct: TestContext.Current.CancellationToken);

        // Assert — IShape is referenced across multiple files (Shape.cs and ShapeService.cs)
        result.ShouldContain("References to 'IShape'");
        result.ShouldContain("Shape.cs");
        result.ShouldContain("ShapeService.cs");
    }

    [Fact]
    public async Task FindReferences_OnInterfaceType_ExcludesRazorGeneratedLocations()
    {
        // Arrange — IShape at line 6, col 18
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 6, 18)], ct: TestContext.Current.CancellationToken);

        // Assert — references from Razor-generated code (obj/ or .g.cs) should not appear
        result.ShouldNotContain(".g.cs");
        result.ShouldNotContain("obj\\");
    }

    [Fact]
    public async Task FindReferences_OnPublicKeyword_ResolvesToEnclosingSymbol()
    {
        // Arrange — "public interface IShape" — col 1 is the 'p' of 'public'
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 6, 1)], ct: TestContext.Current.CancellationToken);

        // Assert — the 'public' keyword resolves up to IShape (header names the resolved symbol)
        result.ShouldContain("References to 'IShape'");
    }

    [Fact]
    public async Task FindReferences_OnInterfaceType_ShowsSourceLineText()
    {
        // Arrange — IShape at line 6, col 18
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — use maxResults: null to avoid truncation hiding specific references
        string result = await tools.FindReferences([Loc(filePath, 6, 18)], maxResults: null, ct: TestContext.Current.CancellationToken);

        // Assert — reference lines should include source text snippets with | format
        result.ShouldContain("Shape : IShape");
        result.ShouldContain("|");
    }

    // ── contextLines ────────────────────────────────────────────────────

    [Fact]
    public async Task FindReferences_ContextLines1_ShowsSurroundingLines()
    {
        // Arrange — IShape at line 6, col 18
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — use maxResults: null to avoid truncation hiding specific references
        string result = await tools.FindReferences([Loc(filePath, 6, 18)], maxResults: null, contextLines: 1, ct: TestContext.Current.CancellationToken);

        // Assert — should include the reference line and surrounding lines with line numbers
        result.ShouldContain("Shape : IShape");
        result.ShouldContain("|");
        // Multi-line context marks the matched line with '>'; the line range is implicit in
        // the gutter, so there is no longer a "(N-M):" header duplicating the numbers.
        Regex.IsMatch(result, @"(?m)^ +> +\d+ \|").ShouldBeTrue("expected the matched line marked with '>'");
        Regex.IsMatch(result, @"\(\d+-\d+\):").ShouldBeFalse("line-range header should be gone");
    }

    [Fact]
    public async Task FindReferences_DefaultContextLines_ShowsSingleLineWithPipe()
    {
        // Arrange — IShape at line 6, col 18
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — use maxResults: null to avoid truncation hiding specific references
        string result = await tools.FindReferences([Loc(filePath, 6, 18)], maxResults: null, ct: TestContext.Current.CancellationToken);

        // Assert — default contextLines=0 shows match line only with | format
        result.ShouldContain("Shape : IShape");
        result.ShouldContain("|");
        // Single-line context: no parenthesized line number/range, and no '>' hit marker
        // (the marker only appears when surrounding context lines are shown).
        Regex.IsMatch(result, @"\(\d+(-\d+)?\):").ShouldBeFalse("single-line context should not have a parenthesized line number");
        Regex.IsMatch(result, @"(?m)^ +> +\d+ \|").ShouldBeFalse("contextLines=0 should not emit a '>' hit marker");
    }

    // ── implicit references ────────────────────────────────────────────────

    [Fact]
    public async Task FindReferences_OnBaseClass_AnnotatesImplicitReferences()
    {
        // Arrange — "public abstract class Shape : IShape" — Shape at line 4, col 23
        // Circle, Rectangle, Triangle derive from Shape via primary constructors,
        // which implicitly call base() — Roslyn marks those as IsImplicit.
        string filePath = fixture.ShapesFile("Shape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 4, 23)], maxResults: null, ct: TestContext.Current.CancellationToken);

        // Assert — at least one implicit reference should be annotated
        result.ShouldContain("[implicit]");
    }

    [Fact]
    public async Task FindReferences_OnInterface_DoesNotAnnotateExplicitReferences()
    {
        // Arrange — IShape at line 6, col 18
        // All references to IShape are explicit (: IShape, parameter types, etc.)
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 6, 18)], maxResults: null, ct: TestContext.Current.CancellationToken);

        // Assert — no implicit references expected for interface usage
        result.ShouldNotContain("[implicit]");
    }

    // ── maxResults ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FindReferences_WithMaxResults_LimitsOutput()
    {
        // Arrange — IShape at line 6, col 18 — has multiple references
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — limit to 1 reference
        string result = await tools.FindReferences([Loc(filePath, 6, 18)], maxResults: 1, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("showing 1 of");
    }

    [Fact]
    public async Task FindReferences_WithMaxResults_TruncatesDeterministically()
    {
        // Arrange — IShape at line 6, col 18 — has 10+ references
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — run twice with a small maxResults to verify stable ordering
        string result1 = await tools.FindReferences([Loc(filePath, 6, 18)], maxResults: 3, ct: TestContext.Current.CancellationToken);
        string result2 = await tools.FindReferences([Loc(filePath, 6, 18)], maxResults: 3, ct: TestContext.Current.CancellationToken);

        // Assert — truncated results should be identical across runs (deterministic sort)
        result1.ShouldBe(result2);
        result1.ShouldContain("showing 3 of");
    }

    [Fact]
    public async Task FindReferences_WithMaxResults_GreaterThanTotal_ReturnsAll()
    {
        // Arrange — IShape at line 6, col 18
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 6, 18)], maxResults: 1000, ct: TestContext.Current.CancellationToken);

        // Assert — no truncation notice
        result.ShouldNotContain("showing");
    }

    [Fact]
    public async Task FindReferences_NonExistentFile_ReturnsInlineError()
    {
        // Single-cursor user errors render as an inline error message (matches symbolNames batch behavior).
        string result = await tools.FindReferences([Loc("NonExistent.cs", 1, 1)], ct: TestContext.Current.CancellationToken);

        result.ShouldContain("File not found in solution");
    }

    // ── includeTests ─────────────────────────────────────────────────────

    // ── project labels ────────────────────────────────────────────────────

    [Fact]
    public async Task FindReferences_ShowsProjectNameInFileHeaders()
    {
        // Arrange — IShape at line 6, col 18
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 6, 18)], maxResults: null, ct: TestContext.Current.CancellationToken);

        // Assert — file headers should include the project name in brackets
        result.ShouldContain("[TestFixture]");
    }

    // ── includeTests ─────────────────────────────────────────────────────

    [Fact]
    public async Task FindReferences_DefaultExcludesTests_ExcludesTestProjectReferences()
    {
        // Arrange — IShape at line 6, col 18
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 6, 18)], ct: TestContext.Current.CancellationToken);

        // Assert — references from TestFixture.Tests should not appear
        result.ShouldNotContain("ShapeTests");
        result.ShouldNotContain("TestFixture.Tests");
    }

    [Fact]
    public async Task FindReferences_IncludeTests_IncludesTestProjectReferences()
    {
        // Arrange — IShape at line 6, col 18
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 6, 18)], includeTests: true, ct: TestContext.Current.CancellationToken);

        // Assert — ShapeTests.cs references IShape
        result.ShouldContain("ShapeTests");
    }

    // ── symbolName resolution ────────────────────────────────────────────

    [Fact]
    public async Task FindReferences_BySymbolName_FindsUsages()
    {
        // Act — IShape is unique
        string result = await tools.FindReferences(symbolNames: ["IShape"], maxResults: null, ct: TestContext.Current.CancellationToken);

        // Assert — IShape is referenced across Shape.cs and ShapeService.cs
        result.ShouldContain("References to 'IShape'");
        result.ShouldContain("Shape.cs");
        result.ShouldContain("ShapeService.cs");
    }

    [Fact]
    public async Task FindReferences_BySymbolNameAndContainingType_FindsMethodUsages()
    {
        // Act — Describe method on IShape
        string result = await tools.FindReferences(symbolNames: ["Describe"], containingType: "IShape", ct: TestContext.Current.CancellationToken);

        // Assert — resolves IShape.Describe (header names the member; the zero-result path omits it)
        result.ShouldContain("References to 'Describe'");
    }

    [Fact]
    public async Task FindReferences_WithNamespaceAsContainingType_ReturnsNamespaceHint()
    {
        // Act — pass a namespace as containingType; per-name error is captured inline
        string result = await tools.FindReferences(symbolNames: ["Circle"], containingType: "TestFixture.Shapes", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("is a namespace, not a type");
        result.ShouldContain("omit containingType");
    }

    // ── includeTests: count difference ─────────────────────────────────

    [Fact]
    public async Task FindReferences_IncludeTests_HasMoreOrEqualResults()
    {
        // Arrange — IShape is referenced in both production and test projects
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — compare counts with and without includeTests
        string withoutInclude = await tools.FindReferences([Loc(filePath, 6, 18)], maxResults: null, ct: TestContext.Current.CancellationToken);
        string withInclude = await tools.FindReferences([Loc(filePath, 6, 18)], includeTests: true, maxResults: null, ct: TestContext.Current.CancellationToken);

        // Assert — includeTests: true should return more or equal results
        withInclude.Length.ShouldBeGreaterThanOrEqualTo(withoutInclude.Length);
    }

    // ── operator metadata name resolution ────────────────────────────────

    [Theory]
    [InlineData("op_Addition")]
    [InlineData("op_Implicit")]
    public async Task FindReferences_ByOperatorMetadataName_ResolvesOperator(string operatorName)
    {
        // Act — metadata names for operator overloads
        string result = await tools.FindReferences(symbolNames: [operatorName], containingType: "ShapeCollection", ct: TestContext.Current.CancellationToken);

        // Assert — should resolve the operator (even if no external call sites exist)
        result.ShouldNotBeNullOrWhiteSpace();
    }

    // ── project filter (resolution-only; see "Project Filter" in CLAUDE.md) ───

    [Fact]
    public async Task FindReferences_CursorMode_WithProject_IgnoresFilterAndReportsAllProjects()
    {
        // Arrange — IShape at line 6, col 18, declared+used in TestFixture; TestFixture.Minimal
        // has NO IShape references. A cursor already targets one symbol, so `project` has no
        // resolution role: the old result-filter dropped every non-matching reference (here, all
        // of them — TestFixture.Minimal has none), but the fix reports the full set instead.
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 6, 18)], project: "TestFixture.Minimal", maxResults: null, ct: TestContext.Current.CancellationToken);

        // Assert — TestFixture references survive the (ignored) filter, and a note explains why.
        result.ShouldContain("References to 'IShape'");
        result.ShouldContain("[TestFixture]");
        result.ShouldContain("project filter ignored");
    }

    [Fact]
    public async Task FindReferences_CursorMode_WithProject_ReturnsSameCountAsUnscoped()
    {
        // Arrange — IShape, referenced across the solution.
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act — cursor mode ignores `project`, so the reference set must be identical with or
        // without it (the only delta is the appended ignored-note).
        string withProject = await tools.FindReferences([Loc(filePath, 6, 18)], project: "TestFixture.Minimal", maxResults: null, ct: TestContext.Current.CancellationToken);
        string withoutProject = await tools.FindReferences([Loc(filePath, 6, 18)], maxResults: null, ct: TestContext.Current.CancellationToken);

        // Assert — same total location count (regression guard against silent project undercount).
        LocationCount(withProject).ShouldBe(LocationCount(withoutProject));
        return;

        static int LocationCount(string output)
        {
            Match match = Regex.Match(output, @"\((\d+) location");
            match.Success.ShouldBeTrue($"no location count in:\n{output}");
            return Int32.Parse(match.Groups[1].Value);
        }
    }

    [Fact]
    public async Task FindReferences_WithProject_NoMatch_ReturnsInlineError()
    {
        // Arrange
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 6, 18)], project: "NonExistentProject", ct: TestContext.Current.CancellationToken);

        // Assert — single-cursor user error renders inline
        result.ShouldContain("No project matching");
    }

    // ── maxResults validation ────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task FindReferences_MaxResultsLessThanOne_ReturnsInlineError(int maxResults)
    {
        // Arrange
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act
        string result = await tools.FindReferences([Loc(filePath, 6, 18)], maxResults: maxResults, ct: TestContext.Current.CancellationToken);

        // Assert — single-cursor user error renders inline (validation lives in the service)
        result.ShouldContain("maxResults must be >= 1");
    }

    // ── Batch ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindReferences_Batch_MultipleNames_ProducesSectionedOutput()
    {
        // Act — batch two unique type names
        string result = await tools.FindReferences(symbolNames: ["IShape", "Circle"], maxResults: null, ct: TestContext.Current.CancellationToken);

        // Assert — each name gets its own header section
        result.ShouldContain("=== IShape ===");
        result.ShouldContain("=== Circle ===");
    }

    [Fact]
    public async Task FindReferences_Batch_SingleName_OmitsHeaderWrapper()
    {
        // Act — single-item batch uses the FormatBatch N=1 short-circuit
        string result = await tools.FindReferences(symbolNames: ["IShape"], maxResults: null, ct: TestContext.Current.CancellationToken);

        // Assert — no "=== IShape ===" wrapper for a single result
        result.ShouldNotContain("=== IShape ===");
    }

    [Fact]
    public async Task FindReferences_Batch_NameCollisionDifferentTypes_QualifiesToTier1()
    {
        // IAlpha.Handle vs IBeta.Handle — bare "Handle" collides; differently-named
        // containing types → minimally-qualified Type.Member is enough.
        string file = fixture.ServicesFile("MultiInterfaceExamples.cs");
        string result = await tools.FindReferences(
            [Loc(file, 8), Loc(file, 13)],
            maxResults: null, ct: TestContext.Current.CancellationToken);

        result.ShouldNotContain("=== Handle ===");
        result.ShouldContain("=== IAlpha.Handle ===");
        result.ShouldContain("=== IBeta.Handle ===");
        result.ShouldNotContain("=== TestFixture.Services.IAlpha.Handle ==="); // didn't over-escalate
    }

    [Fact]
    public async Task FindReferences_Batch_SameTypeNameDifferentNamespace_FallsBackToFullyQualified()
    {
        // Twins.Alpha.NameTwin.Execute vs Twins.Beta.NameTwin.Execute — bare "Execute"
        // AND simple containing-type "NameTwin" both collide → escalate to Tier 2.
        string file = fixture.ServicesFile("NameCollisionExamples.cs");
        string result = await tools.FindReferences(
            [Loc(file, 9), Loc(file, 20)],
            maxResults: null, ct: TestContext.Current.CancellationToken);

        result.ShouldNotContain("=== Execute ===");
        result.ShouldNotContain("=== NameTwin.Execute ==="); // proves Tier-1 was insufficient
        result.ShouldContain("=== TestFixture.Services.Twins.Alpha.NameTwin.Execute ===");
        result.ShouldContain("=== TestFixture.Services.Twins.Beta.NameTwin.Execute ===");
    }

    [Fact]
    public async Task FindReferences_Batch_DistinctNames_KeepsBareHeaders()
    {
        // Over-qualification guard: distinct simple names stay bare.
        string result = await tools.FindReferences(
            symbolNames: ["IShape", "Circle"], maxResults: null,
            ct: TestContext.Current.CancellationToken);

        result.ShouldContain("=== IShape ===");
        result.ShouldContain("=== Circle ===");
        result.ShouldNotContain("=== TestFixture");
    }

    [Fact]
    public async Task FindReferences_Batch_RejectsPositionWithNames_Throws()
    {
        // Arrange
        string filePath = fixture.ShapesFile("IShape.cs");

        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.FindReferences([Loc(filePath, 6)], ["IShape"]));

        ex.Message.ShouldContain("Pass either location");
    }

    [Fact]
    public async Task FindReferences_Batch_RejectsEmptyArray_Throws()
    {
        // Act & Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => tools.FindReferences(symbolNames: []));

        ex.Message.ShouldContain("must not be empty");
    }
}
