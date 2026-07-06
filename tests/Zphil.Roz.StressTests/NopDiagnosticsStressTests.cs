using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

/// <summary>
///     Functional analyzer + incremental-diagnostic coverage at scale. The baseline-concurrency
///     tests covered race conditions, but not that analyzer IDs actually surface alongside compiler
///     codes, that the id filter narrows output, or that an incremental diff isolates a single new
///     diagnostic — all on nopCommerce's large, diagnostic-heavy projects.
/// </summary>
[Trait("Category", "Stress")]
[Collection("NopReadOnlyWorkspace")]
public class NopDiagnosticsStressTests(NopWorkspaceFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task GetDiagnostics_AnalyzerIds_AppearAlongsideCompiler()
    {
        // Arrange — Nop.Core is a large SDK project with the .NET analyzers enabled by default, so
        // get_diagnostics runs CompilationWithAnalyzers and non-CS analyzer IDs appear next to CS*.
        // Query at Info severity: most default CA rules (e.g. CA1822 "mark members as static")
        // surface as suggestions, so Nop.Core is clean at Warning but shows analyzer IDs at Info.
        DiagnosticTools diag = CreateDiagnosticTools(fixture);

        // Act — bounded; analyzer execution over a whole project is expensive
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        string result = await TimingHelper.TimeAsync("Nop_GetDiagnostics_AnalyzerIds_NopCore",
            () => diag.GetDiagnostics(project: "Nop.Core", severity: DiagnosticSeverity.Info, ct: cts.Token), output);

        // Assert — at least one analyzer (non-CS) diagnostic ID is present
        output.WriteLine(result[..Math.Min(3000, result.Length)]);
        List<string> ids = Regex.Matches(result, @"(?:warning|error|info)\s+([A-Za-z]{2,}\d{3,}):")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        output.WriteLine($"Diagnostic IDs seen: {String.Join(", ", ids)}");
        output.WriteLine($"Analyzer-fixes hint present: {result.Contains("Available analyzer fixes", StringComparison.Ordinal)}");

        ids.ShouldNotBeEmpty();
        ids.Any(id => !id.StartsWith("CS", StringComparison.Ordinal))
            .ShouldBeTrue("at least one non-CS analyzer ID (e.g. CA/IDE) should appear alongside compiler codes");
    }

    [Fact]
    public async Task GetDiagnostics_DiagnosticIdsFilter_Narrows()
    {
        // Arrange — filtering to a single analyzer ID must echo the filter and suppress every other
        // code, including compiler CS codes.
        DiagnosticTools diag = CreateDiagnosticTools(fixture);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        string result = await TimingHelper.TimeAsync("Nop_GetDiagnostics_IdFilter_NopCore",
            () => diag.GetDiagnostics(project: "Nop.Core", diagnosticIds: ["CA1822"], ct: cts.Token), output);

        // Assert — the filter is applied (echoed) and no CS codes leak through
        output.WriteLine(result[..Math.Min(2000, result.Length)]);
        result.ShouldContain("CA1822");
        Regex.IsMatch(result, @"\bCS\d{4}\b").ShouldBeFalse("a CA-only filter must exclude compiler CS codes");
    }

    [Fact]
    public async Task GetDiagnostics_Incremental_CleanDiff_AfterEdit()
    {
        // Arrange — fresh workspace; capture a baseline, then introduce exactly one new compiler
        // warning (an unused local → CS0219) and confirm the incremental diff isolates it. The
        // baseline is captured solution-wide with analyzers (expensive on nopCommerce's 35 projects),
        // so this rides the test's own cancellation token rather than a short timeout — matching the
        // existing baseline-capture stress tests.
        await using TempWorkspace temp = await TempWorkspaceFactory.CreateAsync(TestSolutionConfig.NopCommerce, TestContext.Current.CancellationToken);
        DiagnosticTools diag = CreateDiagnosticTools(temp);
        CodeEditTools codeEdit = CreateEditTools(temp);
        string productServiceFile = ProductServiceFile(temp.WorkspaceManager);

        string baselineResult = await TimingHelper.TimeAsync("Nop_GetDiagnostics_Incremental_Baseline",
            () => diag.GetDiagnostics(resetBaseline: true, ct: TestContext.Current.CancellationToken), output);
        baselineResult.ShouldContain("baseline");

        // Introduce one new diagnostic: an assigned-but-never-used local
        await codeEdit.InsertSymbol(productServiceFile, "GetProductByIdAsync",
            "public void StressDiagMarker() { int zzStressUnused = 42; }", ct: TestContext.Current.CancellationToken);

        // Act — incremental, scoped to the edited file for a clean, fast diff
        string result = await TimingHelper.TimeAsync("Nop_GetDiagnostics_Incremental_AfterEdit",
            () => diag.GetDiagnostics([productServiceFile], incremental: true, ct: TestContext.Current.CancellationToken), output);

        // Assert — the diff reports the newly introduced diagnostic and little else
        output.WriteLine(result);
        result.ShouldContain("zzStressUnused");
        result.ShouldContain("CS0219");

        Match newCount = Regex.Match(result, @"Summary:\s*(\d+) new");
        newCount.Success.ShouldBeTrue("incremental output should summarize the new-diagnostic count");
        var newDiagnostics = Int32.Parse(newCount.Groups[1].Value);
        output.WriteLine($"New diagnostics since baseline: {newDiagnostics}");
        newDiagnostics.ShouldBeGreaterThanOrEqualTo(1);
        newDiagnostics.ShouldBeLessThanOrEqualTo(3, "the diff should isolate the introduced diagnostic, not replay the baseline");
    }
}
