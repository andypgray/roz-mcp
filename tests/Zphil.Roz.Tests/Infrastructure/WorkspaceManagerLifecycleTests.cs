using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Infrastructure;

/// <summary>
///     Lifecycle invariants on <see cref="WorkspaceManager" />: post-dispose access,
///     multicast reload subscriptions, and deterministic timer shutdown.
/// </summary>
public sealed class WorkspaceManagerLifecycleTests
{
    [Fact]
    public async Task GetSolutionAsync_AfterDispose_ThrowsUserError()
    {
        // Arrange
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: TestContext.Current.CancellationToken);
        await ws.WorkspaceManager.DisposeAsync();

        // Act / Assert — clean UserErrorException, not the NullReferenceException the old workspace! deref produced.
        await Should.ThrowAsync<UserErrorException>(() => ws.WorkspaceManager.GetSolutionAsync());
    }

    [Fact]
    public async Task RegisterBeforeReload_TwoCallbacks_BothFire()
    {
        // Arrange
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: TestContext.Current.CancellationToken);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        var counter1 = 0;
        var counter2 = 0;
        using IDisposable sub1 = ws.WorkspaceManager.RegisterBeforeReload(() => Interlocked.Increment(ref counter1));
        using IDisposable sub2 = ws.WorkspaceManager.RegisterBeforeReload(() => Interlocked.Increment(ref counter2));

        // Act
        await ws.WorkspaceManager.ScheduleReloadAsync(ct: TestContext.Current.CancellationToken);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Assert — multicast: both subscribers were invoked.
        counter1.ShouldBe(1);
        counter2.ShouldBe(1);
    }

    [Fact]
    public async Task RegisterBeforeReload_DisposeSubscription_StopsFiring()
    {
        // Arrange
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: TestContext.Current.CancellationToken);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        var counter = 0;
        IDisposable sub = ws.WorkspaceManager.RegisterBeforeReload(() => Interlocked.Increment(ref counter));
        sub.Dispose();

        // Act
        await ws.WorkspaceManager.ScheduleReloadAsync(ct: TestContext.Current.CancellationToken);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Assert — disposed subscription does not fire.
        counter.ShouldBe(0);
    }

    [Fact]
    public async Task DisposeAsync_StopsTimerDeterministically()
    {
        // Arrange / Act — load with the watcher enabled (autoRefreshDisabled: false) so the timer
        // is actually running, then dispose. Repeating exercises the timer-drain race window.
        // Timer.DisposeAsync semantics give us "no in-flight callback survives Dispose", so no
        // exception should escape any iteration.
        for (var i = 0; i < 10; i++)
        {
            TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(false, TestContext.Current.CancellationToken);
            await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
            await ws.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_WhilePendingUpdateScheduled_DoesNotThrow()
    {
        // Schedule a real file-change (TryApplyChanges under the gate) immediately before dispose so
        // teardown maximally overlaps NotifyFileChangedAsync's gate-held mutation window. Pre-fix this
        // half-applied the edit / raced the temp-dir delete; the fix drains before tearing down.
        for (var i = 0; i < 20; i++)
        {
            TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(false, TestContext.Current.CancellationToken);
            Solution solution = await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

            string target = solution.Projects
                .SelectMany(p => p.Documents)
                .First(d => d.FilePath is not null && !d.FilePath.Contains("obj")).FilePath!;

            ws.WorkspaceManager.ScheduleFileChanged(target, $"// touched {i}\n");
            await ws.WorkspaceManager.DisposeAsync(); // explicit dispose under test
            await ws.DisposeAsync(); // second dispose (idempotency) + temp-dir cleanup
        }
    }

    [Fact]
    public async Task DisposeAsync_WhileLoadPreReady_BlockedGetSolutionThrowsUserError()
    {
        // Park the load in its pre-ready window (after open/strip, before the snapshot-ready signal) via
        // the test hook so a GetSolutionAsync is genuinely blocked on solutionReadyTask. Disposing then
        // must complete that reader with UserErrorException (the post-dispose contract), not leave it
        // hung — the only direct guard for the must-fault-on-dispose-mid-load invariant the warmup
        // decoupling introduces.
        var reachedPreReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var wm = new WorkspaceManager(
            NullLogger<WorkspaceManager>.Instance,
            WorkspaceFixture.FixtureSolutionPath,
            true,
            async () =>
            {
                reachedPreReady.TrySetResult();
                await release.Task;
            });

        try
        {
            // Wait until the load is parked pre-ready (holding the gate, ready signal not yet fired).
            // 120s ceiling: under a fully-parallel suite run the fixture load can take well over 30s
            // just to reach pre-ready (observed 2026-07-08); a healthy run parks in seconds.
            await reachedPreReady.Task.WaitAsync(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);

            // This reader blocks on solutionReadyTask.
            Task<Solution> reader = wm.GetSolutionAsync(TestContext.Current.CancellationToken);

            // Begin disposing while the load is still pre-ready. DisposeAsync synchronously cancels
            // warmup, nulls the workspace, and completes the ready signal before its first await —
            // unblocking the reader — then parks on the gate (held by the still-paused load).
            ValueTask disposeTask = wm.DisposeAsync();
            try
            {
                // The blocked reader resumes, sees workspace == null, and throws UserErrorException.
                await Should.ThrowAsync<UserErrorException>(async () => await reader);
            }
            finally
            {
                release.TrySetResult(); // let the load unwind so it releases the gate and dispose finishes
                await disposeTask;
            }
        }
        finally
        {
            release.TrySetResult(); // never leave the load parked if an earlier await threw
            await wm.DisposeAsync(); // idempotent second dispose
        }
    }
}
