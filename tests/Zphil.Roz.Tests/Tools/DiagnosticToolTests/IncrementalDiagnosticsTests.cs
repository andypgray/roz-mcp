using Microsoft.CodeAnalysis;
using Zphil.Roz.Models;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.DiagnosticToolTests;

public class IncrementalDiagnosticsTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    private DiagnosticTools CreateDiagnosticTools() => TestFileHelper.CreateDiagnosticTools(Fixture);

    [Fact]
    public async Task GetDiagnostics_Incremental_NoBaseline_CapturesSilentlyAndReportsZeroNew()
    {
        // Arrange
        DiagnosticTools tools = CreateDiagnosticTools();

        // Act — no prior edits or reset; the call should implicitly capture a baseline
        // and report no new diagnostics (current state == baseline), with no extra messaging.
        string result = await tools.GetDiagnostics(incremental: true, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("0 new,");
        result.ShouldNotContain("Note:");
    }

    [Fact]
    public async Task GetDiagnostics_Incremental_AfterImplicitCapture_NextCallDiffsAgainstAutoBaseline()
    {
        // Arrange
        DiagnosticTools diagnosticTools = CreateDiagnosticTools();
        CodeEditTools codeEditTools = TestFileHelper.CreateEditTools(Fixture);
        string circleFile = TestFileHelper.CircleFile(Fixture);

        // Act — first call triggers implicit baseline capture
        await diagnosticTools.GetDiagnostics(incremental: true, ct: TestContext.Current.CancellationToken);

        // Introduce a new error after the implicit baseline was captured
        await codeEditTools.ReplaceContent([new ReplaceContentRequest(circleFile, "Math.PI * Radius * Radius", "undefinedVar * Radius")], ct: TestContext.Current.CancellationToken);

        string result = await diagnosticTools.GetDiagnostics([circleFile], DiagnosticSeverity.Error, incremental: true, ct: TestContext.Current.CancellationToken);

        // Assert — second call should diff against the auto-captured baseline
        result.ShouldContain("NEW");
        result.ShouldContain("undefinedVar");
    }

    [Fact]
    public async Task GetDiagnostics_Incremental_AfterIntroducingError_ShowsOnlyNewDiagnostic()
    {
        // Arrange
        DiagnosticTools diagnosticTools = CreateDiagnosticTools();
        CodeEditTools codeEditTools = TestFileHelper.CreateEditTools(Fixture);
        string circleFile = TestFileHelper.CircleFile(Fixture);

        // Act — introduce a compile error by referencing an undefined variable
        await codeEditTools.ReplaceContent([new ReplaceContentRequest(circleFile, "Math.PI * Radius * Radius", "undefinedVar * Radius")], ct: TestContext.Current.CancellationToken);

        string result = await diagnosticTools.GetDiagnostics([circleFile], DiagnosticSeverity.Error, incremental: true, ct: TestContext.Current.CancellationToken);

        // Assert — should show new error(s) but label them as "NEW"
        result.ShouldContain("NEW");
        result.ShouldContain("new,");
    }

    [Fact]
    public async Task GetDiagnostics_Incremental_NoNewDiagnostics_ReportsNoneNew()
    {
        // Arrange
        DiagnosticTools diagnosticTools = CreateDiagnosticTools();
        CodeEditTools codeEditTools = TestFileHelper.CreateEditTools(Fixture);
        string circleFile = TestFileHelper.CircleFile(Fixture);

        // Act — make a harmless edit (rename a local concept, keeping it valid)
        await codeEditTools.ReplaceContent([new ReplaceContentRequest(circleFile, "2 * Math.PI * Radius", "Math.PI * Radius * 2")], ct: TestContext.Current.CancellationToken);

        string result = await diagnosticTools.GetDiagnostics([circleFile], incremental: true, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No new diagnostics introduced since baseline.");
        result.ShouldContain("0 new,");
    }

    [Fact]
    public async Task ResetBaseline_CapturesCurrentState()
    {
        // Arrange
        DiagnosticTools tools = CreateDiagnosticTools();

        // Act
        string result = await tools.GetDiagnostics(resetBaseline: true, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Diagnostic baseline reset.");
    }

    [Fact]
    public async Task ResetBaseline_ThenIntroduceError_ShowsNew()
    {
        // Arrange
        DiagnosticTools diagnosticTools = CreateDiagnosticTools();
        CodeEditTools codeEditTools = TestFileHelper.CreateEditTools(Fixture);
        string circleFile = TestFileHelper.CircleFile(Fixture);

        // Set baseline explicitly
        await diagnosticTools.GetDiagnostics(resetBaseline: true, ct: TestContext.Current.CancellationToken);

        // Act — introduce a compile error
        await codeEditTools.ReplaceContent([new ReplaceContentRequest(circleFile, "Math.PI * Radius * Radius", "undefinedVar * Radius")], ct: TestContext.Current.CancellationToken);

        string result = await diagnosticTools.GetDiagnostics([circleFile], DiagnosticSeverity.Error, incremental: true, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("NEW");
        result.ShouldContain("undefinedVar");
    }

    [Fact]
    public async Task GetDiagnostics_Incremental_FilePathFilter_ResolvedCountScopedToFilteredFiles()
    {
        // Arrange — introduce errors in TWO files, capture baseline, then fix only one
        DiagnosticTools diagnosticTools = CreateDiagnosticTools();
        CodeEditTools codeEditTools = TestFileHelper.CreateEditTools(Fixture);
        string circleFile = TestFileHelper.CircleFile(Fixture);
        string triangleFile = TestFileHelper.TriangleFile(Fixture);

        // Introduce errors in both files
        await codeEditTools.ReplaceContent([new ReplaceContentRequest(circleFile, "Math.PI * Radius * Radius", "undefinedCircleVar")], ct: TestContext.Current.CancellationToken);
        await codeEditTools.ReplaceContent([new ReplaceContentRequest(triangleFile, "(a + b + c) / 2", "undefinedTriangleVar")], ct: TestContext.Current.CancellationToken);

        // Capture baseline with errors in both files
        await diagnosticTools.GetDiagnostics(resetBaseline: true, ct: TestContext.Current.CancellationToken);

        // Fix Circle.cs only — Triangle.cs still has the error
        await codeEditTools.ReplaceContent([new ReplaceContentRequest(circleFile, "undefinedCircleVar", "Math.PI * Radius * Radius")], ct: TestContext.Current.CancellationToken);

        // Act — query incremental scoped to Circle.cs only
        string result = await diagnosticTools.GetDiagnostics([circleFile], DiagnosticSeverity.Error, incremental: true, ct: TestContext.Current.CancellationToken);

        // Assert — "resolved" should only count the Circle.cs error we fixed,
        // not the Triangle.cs error (which is still present but out of scope)
        result.ShouldContain("0 new,");
        result.ShouldNotContain("2 resolved");
        result.ShouldContain("1 resolved");
    }

    [Fact]
    public async Task GetDiagnostics_Incremental_FilePathFilterRedundantPrefix_ScopesResolvedCount()
    {
        // Arrange — two errors in baseline, fix one, then query incremental with a
        // redundant-prefix path. The scope must resolve through FilePathResolver so it
        // agrees with the main query — otherwise the Triangle.cs error (out of scope)
        // would look "resolved."
        DiagnosticTools diagnosticTools = CreateDiagnosticTools();
        CodeEditTools codeEditTools = TestFileHelper.CreateEditTools(Fixture);
        string circleFile = TestFileHelper.CircleFile(Fixture);
        string triangleFile = TestFileHelper.TriangleFile(Fixture);

        await codeEditTools.ReplaceContent([new ReplaceContentRequest(circleFile, "Math.PI * Radius * Radius", "undefinedCircleVar")], ct: TestContext.Current.CancellationToken);
        await codeEditTools.ReplaceContent([new ReplaceContentRequest(triangleFile, "(a + b + c) / 2", "undefinedTriangleVar")], ct: TestContext.Current.CancellationToken);

        await diagnosticTools.GetDiagnostics(resetBaseline: true, ct: TestContext.Current.CancellationToken);

        await codeEditTools.ReplaceContent([new ReplaceContentRequest(circleFile, "undefinedCircleVar", "Math.PI * Radius * Radius")], ct: TestContext.Current.CancellationToken);

        // Act — redundant-prefix form of the Circle.cs path
        const string redundantPrefixPath = "TestFixture/TestFixture/Shapes/Circle.cs";
        string result = await diagnosticTools.GetDiagnostics([redundantPrefixPath], DiagnosticSeverity.Error, incremental: true, ct: TestContext.Current.CancellationToken);

        // Assert — scope resolves correctly, so only the Circle.cs fix counts as resolved
        result.ShouldContain("0 new,");
        result.ShouldContain("1 resolved");
        result.ShouldNotContain("2 resolved");
    }

    [Fact]
    public async Task GetDiagnostics_ResetBaselineWithIncremental_Throws()
    {
        // Arrange
        DiagnosticTools tools = CreateDiagnosticTools();

        // Act & Assert
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.GetDiagnostics(incremental: true, resetBaseline: true));
        ex.Message.ShouldContain("Cannot combine resetBaseline=true with incremental=true");
    }

    [Fact]
    public async Task GetDiagnostics_Incremental_RaisedSeverityFloor_DoesNotCountBelowFloorBaselineAsResolved()
    {
        // Arrange — inject a deterministic Warning (CS1030 via #warning) and capture it into the
        // baseline. Circle.cs is all expression-bodied, so a #warning directive is the cleanest
        // way to plant a stable below-floor diagnostic.
        DiagnosticTools diagnosticTools = CreateDiagnosticTools();
        CodeEditTools codeEditTools = TestFileHelper.CreateEditTools(Fixture);
        string circleFile = TestFileHelper.CircleFile(Fixture);

        await codeEditTools.ReplaceContent([new ReplaceContentRequest(circleFile, "namespace TestFixture.Shapes;", "#warning BaselineSeverityProbe\r\nnamespace TestFixture.Shapes;")], ct: TestContext.Current.CancellationToken);

        // Capture baseline at default severity — includes the Warning.
        await diagnosticTools.GetDiagnostics(resetBaseline: true, ct: TestContext.Current.CancellationToken);

        // Act — query at the Error floor without removing the Warning. The Warning is still present
        // but below the floor, so it must NOT be reported as "resolved."
        string result = await diagnosticTools.GetDiagnostics([circleFile], DiagnosticSeverity.Error, incremental: true, ct: TestContext.Current.CancellationToken);

        // Assert — below-floor baseline keys are not phantom-resolved.
        result.ShouldContain("0 new,");
        result.ShouldContain("0 resolved");
    }

    [Fact]
    public async Task GetDiagnostics_Incremental_ProjectFilter_ResolvedCountScopedToProject()
    {
        // Arrange — introduce errors in TWO projects, capture baseline, then fix only the Legacy one.
        DiagnosticTools diagnosticTools = CreateDiagnosticTools();
        CodeEditTools codeEditTools = TestFileHelper.CreateEditTools(Fixture);
        string circleFile = TestFileHelper.CircleFile(Fixture);
        string legacyFile = TestFileHelper.LegacyClassFile(Fixture);

        await codeEditTools.ReplaceContent([new ReplaceContentRequest(circleFile, "Math.PI * Radius * Radius", "undefinedCircleVar")], ct: TestContext.Current.CancellationToken);
        await codeEditTools.ReplaceContent([new ReplaceContentRequest(legacyFile, "Console.WriteLine(Name)", "Console.WriteLine(undefinedLegacyVar)")], ct: TestContext.Current.CancellationToken);

        // Capture baseline with errors in both projects.
        await diagnosticTools.GetDiagnostics(resetBaseline: true, ct: TestContext.Current.CancellationToken);

        // Fix the Legacy error only — TestFixture (Circle.cs) still has its error.
        await codeEditTools.ReplaceContent([new ReplaceContentRequest(legacyFile, "Console.WriteLine(undefinedLegacyVar)", "Console.WriteLine(Name)")], ct: TestContext.Current.CancellationToken);

        // Act — scope to the Legacy project only ("Legacy" matches only TestFixture.Legacy).
        string result = await diagnosticTools.GetDiagnostics(severity: DiagnosticSeverity.Error, incremental: true, project: "Legacy", ct: TestContext.Current.CancellationToken);

        // Assert — only the Legacy error we fixed counts as resolved; the still-present,
        // out-of-scope Circle.cs error must NOT be phantom-counted.
        result.ShouldContain("0 new,");
        result.ShouldContain("1 resolved");
        result.ShouldNotContain("2 resolved");
    }
}
