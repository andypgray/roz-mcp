using Microsoft.CodeAnalysis;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.DiagnosticToolTests;

/// <summary>
///     Tests for the project filter parameter on get_diagnostics.
///     Uses the DiagnosticFixture solution which has two projects:
///     DiagnosticFixture (main) and DiagnosticFixture.Tests (test).
/// </summary>
public class GetDiagnosticsProjectFilterTests(DiagnosticWorkspaceFixture fixture)
{
    private readonly DiagnosticTools tools = TestFileHelper.CreateDiagnosticTools(fixture);

    [Fact]
    public async Task GetDiagnostics_ProjectFilter_ReturnsOnlyMatchingProjectDiagnostics()
    {
        // Act — filter to test project only (exact enough to not match main project)
        string result = await tools.GetDiagnostics(severity: DiagnosticSeverity.Warning, project: ".Tests", includeTests: true, ct: TestContext.Current.CancellationToken);

        // Assert — test project diagnostics should be present
        result.ShouldContain("TestWarningExamples.cs");

        // Assert — main project's error file should not appear (ErrorExamples.cs is only in main)
        result.ShouldNotContain("ErrorExamples.cs");
    }

    [Fact]
    public async Task GetDiagnostics_ProjectFilter_TestsProject_ReturnsTestDiagnosticsOnly()
    {
        // Act — filter to test project only using suffix that uniquely identifies it
        string result = await tools.GetDiagnostics(severity: DiagnosticSeverity.Warning, project: "Fixture.Tests", includeTests: true, ct: TestContext.Current.CancellationToken);

        // Assert — test project diagnostics should be present
        result.ShouldContain("TestWarningExamples.cs");

        // Assert — main project's unique files should not appear
        result.ShouldNotContain("ErrorExamples.cs");
        result.ShouldNotContain("ObsoleteApi.cs");
    }

    [Fact]
    public async Task GetDiagnostics_ProjectFilter_CaseInsensitive_MatchesRegardless()
    {
        // Act — lowercase filter
        string result = await tools.GetDiagnostics(project: "diagnosticfixture", ct: TestContext.Current.CancellationToken);

        // Assert — still finds diagnostics (matches both projects case-insensitively)
        result.ShouldContain("CS0219");
        result.ShouldNotContain("No diagnostics");
    }

    [Fact]
    public async Task GetDiagnostics_ProjectFilter_SubstringMatch_MatchesPartialName()
    {
        // Act — partial match that hits both projects
        string result = await tools.GetDiagnostics(severity: DiagnosticSeverity.Warning, project: "Diagnostic", ct: TestContext.Current.CancellationToken);

        // Assert — both projects match the substring, so diagnostics from both appear
        result.ShouldContain("CS0219");
        result.ShouldNotContain("No diagnostics");
    }

    [Fact]
    public async Task GetDiagnostics_ProjectFilter_NoMatch_ThrowsWithAvailableProjects()
    {
        // Act & Assert — nonexistent project should throw with available project names
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.GetDiagnostics(project: "NonExistentProject"));

        ex.Message.ShouldContain("No project matching 'NonExistentProject' found in solution");
        ex.Message.ShouldContain("Available projects:");
        ex.Message.ShouldContain("DiagnosticFixture");
    }

    [Fact]
    public async Task GetDiagnostics_ProjectFilter_NullProject_ReturnsSolutionWideDiagnostics()
    {
        // Act — no project filter (null)
        string result = await tools.GetDiagnostics(ct: TestContext.Current.CancellationToken);

        // Assert — solution-wide, includes diagnostics from all projects
        result.ShouldContain("CS0219");
        result.ShouldContain("CS0246");
    }

    [Fact]
    public async Task GetDiagnostics_ProjectFilter_WithSeverityFilter_BothApply()
    {
        // Act — filter to both projects with error severity only
        string result = await tools.GetDiagnostics(severity: DiagnosticSeverity.Error, project: "DiagnosticFixture", ct: TestContext.Current.CancellationToken);

        // Assert — only errors (CS0246 from main project); no warnings
        result.ShouldContain("CS0246");
        result.ShouldNotContain("CS0219");
        result.ShouldNotContain("CS0618");
    }
}
