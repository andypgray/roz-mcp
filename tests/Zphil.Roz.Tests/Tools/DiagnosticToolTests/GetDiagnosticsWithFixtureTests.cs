using Microsoft.CodeAnalysis;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.DiagnosticToolTests;

/// <summary>
///     Tests for the get_diagnostics tool using a fixture solution that intentionally
///     contains compiler warnings (CS0219, CS0618) and errors (CS0246).
/// </summary>
public class GetDiagnosticsWithFixtureTests(DiagnosticWorkspaceFixture fixture)
{
    private readonly DiagnosticTools tools = TestFileHelper.CreateDiagnosticTools(fixture);

    // ── Solution-wide: severity filtering ────────────────────────────────────

    [Fact]
    public async Task GetDiagnostics_DefaultSeverity_ReturnsWarningsAndErrors()
    {
        // Act
        string result = await tools.GetDiagnostics(ct: TestContext.Current.CancellationToken);

        // Assert — both warnings (CS0219) and errors (CS0246) should appear
        result.ShouldContain("CS0219");
        result.ShouldContain("CS0246");
        result.ShouldNotContain("No diagnostics");
    }

    [Fact]
    public async Task GetDiagnostics_WarningSeverity_IncludesBothWarningsAndErrors()
    {
        // Act
        string result = await tools.GetDiagnostics(severity: DiagnosticSeverity.Warning, ct: TestContext.Current.CancellationToken);

        // Assert — warning filter includes errors too (Error >= Warning)
        result.ShouldContain("error");
        result.ShouldContain("warning");
    }

    [Fact]
    public async Task GetDiagnostics_ErrorSeverity_ExcludesWarnings()
    {
        // Act
        string result = await tools.GetDiagnostics(severity: DiagnosticSeverity.Error, ct: TestContext.Current.CancellationToken);

        // Assert — only errors; no CS0219 (unused var) or CS0618 (obsolete)
        result.ShouldContain("CS0246");
        result.ShouldNotContain("CS0219");
        result.ShouldNotContain("CS0618");
    }

    [Fact]
    public async Task GetDiagnostics_InfoSeverity_IncludesWarningsAndErrors()
    {
        // Act — info is the lowest filter; should return everything
        string result = await tools.GetDiagnostics(severity: DiagnosticSeverity.Info, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("CS0219");
        result.ShouldContain("CS0246");
    }

    [Fact]
    public async Task GetDiagnostics_HiddenSeverity_DoesNotThrow()
    {
        // Act — Hidden is a valid enum value; should not throw (BUG-003)
        string result = await tools.GetDiagnostics(severity: DiagnosticSeverity.Hidden, ct: TestContext.Current.CancellationToken);

        // Assert — Hidden is the lowest filter, so all diagnostics should appear
        result.ShouldContain("CS0219");
        result.ShouldContain("CS0246");
    }

    // ── Specific diagnostic IDs ──────────────────────────────────────────────

    [Fact]
    public async Task GetDiagnostics_DefaultSeverity_ReportsCS0219UnusedVariable()
    {
        // Act
        string result = await tools.GetDiagnostics(ct: TestContext.Current.CancellationToken);

        // Assert — CS0219: variable assigned but its value is never used
        result.ShouldContain("CS0219");
    }

    [Fact]
    public async Task GetDiagnostics_DefaultSeverity_ReportsCS0618ObsoleteUsage()
    {
        // Act
        string result = await tools.GetDiagnostics(ct: TestContext.Current.CancellationToken);

        // Assert — CS0618: call to [Obsolete("...")] member
        result.ShouldContain("CS0618");
    }

    [Fact]
    public async Task GetDiagnostics_SolutionWide_IncludesSummaryLine()
    {
        // Act
        string result = await tools.GetDiagnostics(ct: TestContext.Current.CancellationToken);

        // Assert — FormatDiagnostics always appends a summary line
        result.ShouldContain("Summary:");
    }

    // ── Per-file scoping ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetDiagnostics_ScopedToWarningFile_ReturnsOnlyWarnings()
    {
        // Arrange
        string warningFile = fixture.ProjectFile("WarningExamples.cs");

        // Act
        string result = await tools.GetDiagnostics([warningFile], ct: TestContext.Current.CancellationToken);

        // Assert — WarningExamples.cs has CS0219 and CS0618 but no CS0246
        result.ShouldContain("CS0219");
        result.ShouldContain("CS0618");
        result.ShouldNotContain("CS0246");
    }

    [Fact]
    public async Task GetDiagnostics_RedundantPrefix_Resolves()
    {
        // Arrange — agent-typed path with a redundant project prefix.
        // Real relative path is "DiagnosticFixture/WarningExamples.cs".
        const string redundantPath = "DiagnosticFixture/DiagnosticFixture/WarningExamples.cs";

        // Act
        string result = await tools.GetDiagnostics([redundantPath], ct: TestContext.Current.CancellationToken);

        // Assert — resolver found WarningExamples.cs despite the redundant prefix
        result.ShouldContain("CS0219");
        result.ShouldNotContain("not found");
    }

    [Fact]
    public async Task GetDiagnostics_ScopedToErrorFile_ReturnsErrors()
    {
        // Arrange
        string errorFile = fixture.ProjectFile("ErrorExamples.cs");

        // Act
        string result = await tools.GetDiagnostics([errorFile], DiagnosticSeverity.Error, ct: TestContext.Current.CancellationToken);

        // Assert — ErrorExamples.cs has CS0246; no CS0219 from WarningExamples
        result.ShouldContain("CS0246");
        result.ShouldNotContain("CS0219");
    }

    [Fact]
    public async Task GetDiagnostics_ScopedToCleanFile_ReturnsNoDiagnostics()
    {
        // Arrange — ObsoleteApi.cs defines obsolete members but doesn't call them
        string cleanFile = fixture.ProjectFile("ObsoleteApi.cs");

        // Act
        string result = await tools.GetDiagnostics([cleanFile], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No diagnostics");
    }

    [Fact]
    public async Task GetDiagnostics_ScopedToWarningFile_ErrorFilter_ReturnsNoDiagnostics()
    {
        // Arrange — WarningExamples.cs has only warnings, not errors
        string warningFile = fixture.ProjectFile("WarningExamples.cs");

        // Act
        string result = await tools.GetDiagnostics([warningFile], DiagnosticSeverity.Error, ct: TestContext.Current.CancellationToken);

        // Assert — filtering for errors only on a warnings-only file yields nothing
        result.ShouldContain("No diagnostics");
    }

    [Fact]
    public async Task GetDiagnostics_NonExistentFile_ReturnsError()
    {
        // Act
        string result = await tools.GetDiagnostics(["NonExistent.cs"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Error");
        result.ShouldContain("not found");
        result.ShouldNotContain("No diagnostics");
    }

    // ── Batch tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDiagnostics_MultipleFiles_ReturnsMergedDiagnostics()
    {
        // Arrange
        string warningFile = fixture.ProjectFile("WarningExamples.cs");
        string errorFile = fixture.ProjectFile("ErrorExamples.cs");

        // Act
        string result = await tools.GetDiagnostics([warningFile, errorFile], ct: TestContext.Current.CancellationToken);

        // Assert — diagnostics from both files merged
        result.ShouldContain("CS0219");
        result.ShouldContain("CS0246");
    }

    [Fact]
    public async Task GetDiagnostics_MultipleFiles_OneInvalid_ReturnsResultsAndError()
    {
        // Arrange
        string validFile = fixture.ProjectFile("WarningExamples.cs");

        // Act
        string result = await tools.GetDiagnostics([validFile, "NonExistent.cs"], ct: TestContext.Current.CancellationToken);

        // Assert — valid file diagnostics still present
        result.ShouldContain("CS0219");

        // Assert — error for invalid file present
        result.ShouldContain("Error");
        result.ShouldContain("NonExistent.cs");
        result.ShouldContain("not found");
    }

    // ── diagnosticIds filtering ─────────────────────────────────────────────

    [Fact]
    public async Task GetDiagnostics_SingleDiagnosticId_ReturnsOnlyThatCode()
    {
        // Act
        string result = await tools.GetDiagnostics(diagnosticIds: ["CS0246"], ct: TestContext.Current.CancellationToken);

        // Assert — only CS0246 errors, no warnings
        result.ShouldContain("CS0246");
        result.ShouldNotContain("CS0219");
        result.ShouldNotContain("CS0618");
    }

    [Fact]
    public async Task GetDiagnostics_MultipleDiagnosticIds_ReturnsBothCodes()
    {
        // Act
        string result = await tools.GetDiagnostics(diagnosticIds: ["CS0219", "CS0618"], ct: TestContext.Current.CancellationToken);

        // Assert — both warning codes present, error code absent
        result.ShouldContain("CS0219");
        result.ShouldContain("CS0618");
        result.ShouldNotContain("CS0246");
    }

    [Fact]
    public async Task GetDiagnostics_NonExistentDiagnosticId_ReturnsNoDiagnostics()
    {
        // Act
        string result = await tools.GetDiagnostics(diagnosticIds: ["CS9999"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No diagnostics");
        result.ShouldContain("CS9999");
    }

    [Fact]
    public async Task GetDiagnostics_DiagnosticIdCaseInsensitive_MatchesRegardless()
    {
        // Act
        string result = await tools.GetDiagnostics(diagnosticIds: ["cs0246"], ct: TestContext.Current.CancellationToken);

        // Assert — lowercase input still matches CS0246
        result.ShouldContain("CS0246");
        result.ShouldNotContain("No diagnostics");
    }

    [Fact]
    public async Task GetDiagnostics_DiagnosticIdWithFilePaths_BothFiltersApply()
    {
        // Arrange — WarningExamples.cs has CS0219 and CS0618, but not CS0246
        string warningFile = fixture.ProjectFile("WarningExamples.cs");

        // Act — filter to CS0219 within that file
        string result = await tools.GetDiagnostics([warningFile], diagnosticIds: ["CS0219"], ct: TestContext.Current.CancellationToken);

        // Assert — only CS0219 from that file
        result.ShouldContain("CS0219");
        result.ShouldNotContain("CS0618");
        result.ShouldNotContain("CS0246");
    }

    // ── includeTests filtering ────────────────────────────────────────────────

    [Fact]
    public async Task GetDiagnostics_IncludeTests_IncludesTestProjectDiagnostics()
    {
        // Act — includeTests=true opts in to test project diagnostics
        string result = await tools.GetDiagnostics(severity: DiagnosticSeverity.Warning, includeTests: true, ct: TestContext.Current.CancellationToken);

        // Assert — the test project's diagnostics should be present
        result.ShouldContain("DiagnosticFixture.Tests");
    }

    [Fact]
    public async Task GetDiagnostics_DefaultExcludesTests_OmitsTestProjectDiagnostics()
    {
        // Act — default includeTests is false
        string result = await tools.GetDiagnostics(severity: DiagnosticSeverity.Warning, ct: TestContext.Current.CancellationToken);

        // Assert — test project's diagnostics should be absent
        result.ShouldNotContain("DiagnosticFixture.Tests");
    }

    [Fact]
    public async Task GetDiagnostics_DefaultExcludesTests_StillReportsMainProjectDiagnostics()
    {
        // Act — default includeTests is false
        string result = await tools.GetDiagnostics(severity: DiagnosticSeverity.Warning, ct: TestContext.Current.CancellationToken);

        // Assert — non-test project diagnostics remain
        result.ShouldContain("CS0219");
        result.ShouldContain("CS0618");
    }

    // ── Fixer summary annotation ──────────────────────────────────────────────

    [Fact]
    public async Task GetDiagnostics_OnlyCompilerWarnings_OmitsFixerSummary()
    {
        // Arrange — DiagnosticFixture emits CS0219/CS0618/CS0246, none of which have
        // CodeFixProviders loaded via AnalyzerReferences (built-in C# fixers ship inside
        // Roslyn workspaces, not as analyzer packages).

        // Act
        string result = await tools.GetDiagnostics(ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("CS0219");
        result.ShouldNotContain("Available analyzer fixes");
    }

    [Fact]
    public async Task GetDiagnostics_WithXunit1004FixableInfo_AppendsFixerSummary()
    {
        // Arrange — SkippedFactExample.cs has [Fact(Skip = "...")] which trips xUnit1004
        // (a fixer-bearing analyzer rule). xUnit1004's default severity is Info, so the
        // test opts into Info severity to surface it. End-to-end proof that analyzers
        // flow through CompilationWithAnalyzers in the file-scoped path.
        string skippedFile = Path.Combine(
            fixture.WorkspaceManager.SolutionDirectory!,
            "DiagnosticFixture.Tests", "SkippedFactExample.cs");

        // Act
        string result = await tools.GetDiagnostics([skippedFile], DiagnosticSeverity.Info,
            /* includeTests: */ true, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("xUnit1004");
        result.ShouldContain("Available analyzer fixes");
        result.ShouldContain("dotnet format");
    }
}
