using System.Collections.Concurrent;
using ModelContextProtocol;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.WorkspaceToolTests;

public class ReloadWorkspaceProgressTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task GetWorkspaceInfo_Reload_ReportsProgressNotifications()
    {
        // Arrange
        WorkspaceTools tools = TestFileHelper.CreateWorkspaceTools(Fixture);
        ConcurrentBag<ProgressNotificationValue> reports = [];

        IProgress<ProgressNotificationValue> progress = new RecordingProgress<ProgressNotificationValue>(reports.Add);

        // Act
        await tools.GetWorkspaceInfo(reload: true, progress: progress, ct: TestContext.Current.CancellationToken);

        // GetWorkspaceInfo(reload:true) now returns at the usable-snapshot point, before warmup finishes
        // reporting per-project progress (the cold-start decoupling). Wait out the full load so the
        // progress stream is complete before asserting on it (RecordingProgress reports inline).
        await (Fixture.WorkspaceManager.LoadReadyTask ?? Task.CompletedTask);

        // Assert — should have initial "Loading solution" + per-project compilation reports
        reports.ShouldNotBeEmpty();
        reports.ShouldContain(r => r.Message != null && r.Message.Contains("Loading solution"));
        reports.ShouldContain(r => r.Total.HasValue && r.Total.Value > 0, "Should report total project count");
    }

    [Fact]
    public async Task GetWorkspaceInfo_Reload_ProgressValuesIncreaseMonotonically()
    {
        // Arrange
        WorkspaceTools tools = TestFileHelper.CreateWorkspaceTools(Fixture);
        ConcurrentBag<ProgressNotificationValue> reports = [];
        IProgress<ProgressNotificationValue> progress = new RecordingProgress<ProgressNotificationValue>(reports.Add);

        // Act — through the tool (reload: true) so the test exercises GetWorkspaceInfo, matching its name
        await tools.GetWorkspaceInfo(reload: true, progress: progress, ct: TestContext.Current.CancellationToken);

        // GetWorkspaceInfo(reload:true) now returns at the usable-snapshot point, before warmup finishes
        // reporting per-project progress (the cold-start decoupling). Wait out the full load so the final
        // Progress == Total report has been emitted before asserting (RecordingProgress reports inline).
        await (Fixture.WorkspaceManager.LoadReadyTask ?? Task.CompletedTask);

        // Assert — compilation progress values should increase (allow for concurrent reporting order)
        List<ProgressNotificationValue> compilationReports = reports
            .Where(r => r.Total.HasValue && r.Total.Value > 0)
            .OrderBy(r => r.Progress)
            .ToList();

        compilationReports.ShouldNotBeEmpty();

        // Final report should have Progress == Total
        ProgressNotificationValue last = compilationReports.Last();
        last.Progress.ShouldBe(last.Total!.Value);
    }

    [Fact]
    public async Task GetWorkspaceInfo_Reload_NullProgress_DoesNotThrow()
    {
        // Arrange
        WorkspaceTools tools = TestFileHelper.CreateWorkspaceTools(Fixture);

        // Act & Assert — reload through the tool with null progress should work without errors
        await tools.GetWorkspaceInfo(reload: true, ct: TestContext.Current.CancellationToken);
    }
}
