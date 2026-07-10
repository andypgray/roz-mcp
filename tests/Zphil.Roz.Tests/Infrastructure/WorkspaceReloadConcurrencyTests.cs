using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Infrastructure;

/// <summary>
///     Cross-component reload/dispose concurrency invariants on <see cref="WorkspaceManager" />: the
///     diagnostic-baseline generation leak across a reload (B1), the delete-sweep reload coalescing (N3),
///     and the post-dispose auto-reload guard (A1).
/// </summary>
public sealed class WorkspaceReloadConcurrencyTests
{
    [Fact]
    public async Task ScheduleReload_BaselineCaptureRacesReloadWindow_LeavesNoStaleBaseline()
    {
        // B1: a baseline capture scheduled in the window between the reload's before-reload callbacks
        // (which clear the baseline) and its solution swap must not read the still-current pre-swap
        // solution and persist a baseline that outlives the reload. We park the reload at the
        // before-reload callbacks via a second callback — registered after DiagnosticBaselineManager's
        // ClearBaseline, so it runs after it — attempt a capture inside that window, release, and assert
        // no baseline survived. Pre-fix (callbacks fire before the swap) the capture reads the old
        // solution and the stale baseline sticks; post-fix (callbacks fire after the swap) the solution
        // reads as not-loaded and the capture no-ops.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: ct);

        var reachedWindow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable park = ws.WorkspaceManager.RegisterBeforeReload(() =>
        {
            reachedWindow.TrySetResult();
            release.Task.Wait(TimeSpan.FromSeconds(30));
        });

        // Start the reload on a background thread — the before-reload callbacks can run synchronously on
        // the caller (pre-fix they fire before the first await), so parking them must not block the test
        // thread that drives the capture/release below.
        var reload = Task.Run(() => ws.WorkspaceManager.ScheduleReloadAsync(ct: ct), ct);
        await reachedWindow.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        // Inside the window: attempt a baseline capture.
        await ws.BaselineManager.CaptureBaselineIfNeededAsync(ct);

        release.TrySetResult();
        await reload;
        await ws.WorkspaceManager.GetSolutionAsync(ct);

        // No stale baseline survived the reload.
        ws.BaselineManager.GetBaseline().ShouldBeNull();
    }

    [Fact]
    public async Task ScheduleReload_BeforeReloadHandlerThrows_ReloadCompletesAndSiblingsFire()
    {
        // B1 relocated the before-reload callbacks to AFTER the swap, so the reload is already committed
        // when they run: a throwing handler must not abort the reload, must not leak the old workspace
        // (disposed before the callbacks), and must not skip a sibling handler. Register a throwing
        // handler before a counter and assert the reload completes and the counter still fires.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: ct);
        await ws.WorkspaceManager.GetSolutionAsync(ct);

        var siblingFired = 0;
        using IDisposable throwing = ws.WorkspaceManager.RegisterBeforeReload(() => throw new InvalidOperationException("before-reload handler boom"));
        using IDisposable sibling = ws.WorkspaceManager.RegisterBeforeReload(() => Interlocked.Increment(ref siblingFired));

        // The reload must not surface the handler's throw...
        await Should.NotThrowAsync(() => ws.WorkspaceManager.ScheduleReloadAsync(ct: ct));

        // ...must complete (a usable new solution), proving the swap + old-workspace dispose ran...
        Solution reloaded = await ws.WorkspaceManager.GetSolutionAsync(ct);
        reloaded.ShouldNotBeNull();

        // ...and the sibling handler still fired despite the earlier throw.
        Volatile.Read(ref siblingFired).ShouldBe(1);
    }

    [Fact]
    public async Task TriggerAutoReload_AfterDispose_IsNoOp()
    {
        // A1: after DisposeAsync nulls loadReadyTask, TriggerAutoReloadAsync must not resurrect a reload
        // on the disposed workspace. It must complete without throwing and without firing before-reload
        // callbacks (pre-fix it fell through the `is { IsCompleted: false }` check that let null pass and
        // called ScheduleReloadAsync, firing them before hitting the disposed gate).
        CancellationToken ct = TestContext.Current.CancellationToken;
        TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: ct);

        var reloadFired = 0;
        using IDisposable sub = ws.WorkspaceManager.RegisterBeforeReload(() => Interlocked.Increment(ref reloadFired));

        await ws.WorkspaceManager.DisposeAsync();

        await Should.NotThrowAsync(() => ws.WorkspaceManager.TriggerAutoReloadAsync());

        Volatile.Read(ref reloadFired).ShouldBe(0);

        // TempWorkspace teardown (idempotent WM dispose + baseline drain + temp-dir delete).
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task TriggerAutoReload_ReloadAlreadyInFlight_CoalescesWithoutSecondReload()
    {
        // N3/A1: the watcher's add/delete detection fires TriggerAutoReloadAsync (via FlushExternalEdits /
        // OnExternalWatcherError). Its coalescing must collapse a second trigger while a reload is already
        // in flight into a no-op — a running reload re-reads disk and picks up the change — instead of
        // stacking a second full reload; and after A1 the guard only proceeds for a *completed* load (null
        // or in-flight → return). Park a reload pre-ready, fire a second trigger, and assert the
        // before-reload callback fired only once. Uses a directly-constructed manager with the pre-ready
        // hook (the shared fixture solution is only re-opened, never mutated). (The entry-time delete
        // sweep deliberately does NOT route through this coalescing — see ReconcileAllExternalEditsAsync.)
        CancellationToken ct = TestContext.Current.CancellationToken;

        var loadCount = 0;
        var reloadParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wm = new WorkspaceManager(
            NullLogger<WorkspaceManager>.Instance,
            WorkspaceFixture.FixtureSolutionPath,
            true,
            async () =>
            {
                // Park only the reload (second load), not the initial load.
                if (Interlocked.Increment(ref loadCount) >= 2)
                {
                    reloadParked.TrySetResult();
                    await release.Task;
                }
            });

        try
        {
            await wm.GetSolutionAsync(ct);
            await wm.LoadReadyTask!; // initial load fully complete → loadReadyTask completed

            var reloadFired = 0;
            using IDisposable sub = wm.RegisterBeforeReload(() => Interlocked.Increment(ref reloadFired));

            // Reload #1: launched via the same trigger the delete sweep now uses.
            Task reload1 = wm.TriggerAutoReloadAsync();
            await reload1; // ScheduleReloadAsync returns after the swap + gate release
            await reloadParked.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); // new load parks pre-ready

            // loadReadyTask is now the parked (incomplete) reload. A second trigger must coalesce.
            await wm.TriggerAutoReloadAsync();

            // Only reload #1 fired the before-reload callback; the second trigger coalesced.
            Volatile.Read(ref reloadFired).ShouldBe(1);

            release.TrySetResult();
        }
        finally
        {
            release.TrySetResult();
            await wm.DisposeAsync();
        }
    }
}
