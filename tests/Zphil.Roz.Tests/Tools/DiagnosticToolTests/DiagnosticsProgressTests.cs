using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Zphil.Roz.Services;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Tools.DiagnosticToolTests;

public class DiagnosticsProgressTests(WorkspaceFixture fixture)
{
    [Fact]
    public async Task GetSolutionDiagnosticsAsync_ReportsPerProjectProgress()
    {
        // Arrange
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        ConcurrentBag<ProgressNotificationValue> reports = [];
        IProgress<ProgressNotificationValue> progress = new RecordingProgress<ProgressNotificationValue>(reports.Add);
        int projectCount = solution.Projects.Count();

        // Act
        await DiagnosticService.GetSolutionDiagnosticsAsync(solution.Projects, progress, TestContext.Current.CancellationToken);

        // Assert — should get one report per project
        reports.Count.ShouldBeGreaterThanOrEqualTo(1);
        reports.ShouldContain(r => r.Total.HasValue && (int)r.Total.Value == projectCount);
    }

    [Fact]
    public async Task GetSolutionDiagnosticsAsync_FinalProgressEqualsTotal()
    {
        // Arrange
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        ConcurrentBag<ProgressNotificationValue> reports = [];
        IProgress<ProgressNotificationValue> progress = new RecordingProgress<ProgressNotificationValue>(reports.Add);
        int projectCount = solution.Projects.Count();

        // Act
        await DiagnosticService.GetSolutionDiagnosticsAsync(solution.Projects, progress, TestContext.Current.CancellationToken);

        // Assert
        reports.ShouldContain(r => (int)r.Progress == projectCount, $"Should have a report with Progress == {projectCount}");
    }

    [Fact]
    public async Task GetSolutionDiagnosticsAsync_NullProgress_DoesNotThrow()
    {
        // Arrange
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Act & Assert
        await DiagnosticService.GetSolutionDiagnosticsAsync(solution.Projects, ct: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetDiagnosticsAsync_FileScopedSkipsProgress()
    {
        // Arrange
        string iShapeFile = fixture.ShapesFile("IShape.cs");
        await using DiagnosticBaselineManager baselineManager = new(
            fixture.WorkspaceManager, NullLogger<DiagnosticBaselineManager>.Instance);
        DiagnosticService service = new(fixture.WorkspaceManager, baselineManager, TestFileHelper.CreateFixerCatalog(fixture));
        ConcurrentBag<ProgressNotificationValue> reports = [];
        IProgress<ProgressNotificationValue> progress = new RecordingProgress<ProgressNotificationValue>(reports.Add);

        // Act
        await service.GetDiagnosticsAsync([iShapeFile], progress: progress, ct: TestContext.Current.CancellationToken);

        // Assert — file-scoped diagnostics should not report per-project compilation progress
        reports.ShouldBeEmpty();
    }
}
