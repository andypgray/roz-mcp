using Microsoft.Extensions.Logging.Abstractions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;
using Zphil.Roz.Services;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Services;

public class DiagnosticBaselineManagerTests(WorkspaceFixture fixture)
{
    [Fact]
    public async Task DisposeAsync_ClearsBaseline()
    {
        // Arrange
        var manager = new DiagnosticBaselineManager(fixture.WorkspaceManager, NullLogger<DiagnosticBaselineManager>.Instance);
        await manager.CaptureBaselineIfNeededAsync(TestContext.Current.CancellationToken);
        manager.GetBaseline().ShouldNotBeNull();

        // Act
        await manager.DisposeAsync();

        // Assert
        manager.GetBaseline().ShouldBeNull();
    }

    [Fact]
    public async Task DisposeAsync_WhileCaptureInFlight_DrainsBeforeReturning()
    {
        // The drain contract: DisposeAsync must await an in-flight background capture, not return
        // while it's still running against a workspace about to be torn down. (ClearBaseline, by
        // contrast, cancels and returns immediately.) Park a capture mid-flight via the test hook,
        // begin disposing, and assert dispose stays pending until the capture is released.
        var captureParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var manager = new DiagnosticBaselineManager(
            fixture.WorkspaceManager,
            NullLogger<DiagnosticBaselineManager>.Instance,
            async () =>
            {
                captureParked.TrySetResult();
                await release.Task;
            });

        manager.ScheduleBaselineCaptureIfNeeded();
        await captureParked.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Act — dispose while the capture is parked. It must block on the drain, not complete now.
        Task disposeTask = manager.DisposeAsync().AsTask();
        disposeTask.IsCompleted.ShouldBeFalse();

        // Releasing the capture lets DisposeAsync drain it and complete.
        release.TrySetResult();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        manager.GetBaseline().ShouldBeNull();
    }

    [Fact]
    public async Task ScheduleBaselineCaptureIfNeeded_SolutionNotLoaded_ReturnsWithoutScheduling()
    {
        // Arrange
        var ws = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance, "C:\\nonexistent\\fake.sln");
        var manager = new DiagnosticBaselineManager(ws, NullLogger<DiagnosticBaselineManager>.Instance);

        // Act
        manager.ScheduleBaselineCaptureIfNeeded();

        // Assert
        manager.GetBaseline().ShouldBeNull();

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task ClearBaseline_AfterSchedulingCapture_LeavesBaselineNull()
    {
        // Arrange
        var manager = new DiagnosticBaselineManager(fixture.WorkspaceManager, NullLogger<DiagnosticBaselineManager>.Instance);

        // Act — schedule a capture, then clear before it settles
        manager.ScheduleBaselineCaptureIfNeeded();
        manager.ClearBaseline();

        // Assert — the baseline resolves to the cleared (null) state, not a stale captured value.
        // The real mid-capture cancellation race is covered by ResetBaseline_CancelledCaptureRaces.
        DiagnosticBaseline? baseline = await manager.GetBaselineAsync();
        baseline.ShouldBeNull();

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task ResetBaseline_CancelledCaptureRaces_DoesNotClobberNewPendingHandle()
    {
        // A cancelled-but-still-running capture must not write a stale baseline or null out the
        // pending handle of the capture scheduled after it. Hammer the schedule→clear→reschedule
        // sequence; each round must still resolve to a usable baseline.
        for (var i = 0; i < 20; i++)
        {
            var manager = new DiagnosticBaselineManager(fixture.WorkspaceManager, NullLogger<DiagnosticBaselineManager>.Instance);

            manager.ScheduleBaselineCaptureIfNeeded();
            manager.ClearBaseline();
            manager.ScheduleBaselineCaptureIfNeeded();

            // Throws "Baseline capture produced no baseline" if the race clobbered the live handle.
            DiagnosticBaseline baseline = await manager.GetOrCaptureBaselineAsync(TestContext.Current.CancellationToken);
            baseline.ShouldNotBeNull();

            await manager.DisposeAsync();
        }
    }
}
