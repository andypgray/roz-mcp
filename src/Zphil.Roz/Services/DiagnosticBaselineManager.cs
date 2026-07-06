using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;

namespace Zphil.Roz.Services;

/// <summary>
///     Manages the diagnostic baseline for incremental comparisons.
///     The baseline is auto-captured before the first edit and can be manually reset.
/// </summary>
internal sealed class DiagnosticBaselineManager : IAsyncDisposable
{
    /// <summary>
    ///     Upper bound on how long <see cref="DisposeAsync" /> waits for a cancelled in-flight capture
    ///     to unwind before tearing down anyway — mirrors <see cref="WorkspaceManager" />'s drain bound
    ///     so a stuck compilation can't block process exit.
    /// </summary>
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly IDisposable beforeReloadSubscription;
    private readonly Lock fieldLock = new();
    private readonly ILogger<DiagnosticBaselineManager> logger;

    /// <summary>
    ///     Test-only async hook awaited at the start of <see cref="CaptureBaselineAsync" />, before any
    ///     cancellation-aware work, so a test can park a capture in-flight and assert that
    ///     <see cref="DisposeAsync" /> drains (awaits) it rather than returning. Null (a no-op) in
    ///     production.
    /// </summary>
    private readonly Func<Task>? onBeforeCaptureForTest;

    private readonly WorkspaceManager workspaceManager;
    private CancellationTokenSource? baselineCaptureCts;
    private DiagnosticBaseline? diagnosticBaseline;
    private Task? pendingBaselineCapture;

    public DiagnosticBaselineManager(WorkspaceManager workspaceManager, ILogger<DiagnosticBaselineManager> logger)
        : this(workspaceManager, logger, null) { }

    /// <summary>
    ///     Test seam: the public constructor delegates here passing <c>null</c>.
    ///     <paramref name="onBeforeCaptureForTest" /> is awaited at the start of each background capture
    ///     (see <see cref="onBeforeCaptureForTest" />), letting a test park a capture in-flight to
    ///     exercise the <see cref="DisposeAsync" /> drain.
    /// </summary>
    internal DiagnosticBaselineManager(
        WorkspaceManager workspaceManager, ILogger<DiagnosticBaselineManager> logger, Func<Task>? onBeforeCaptureForTest)
    {
        this.workspaceManager = workspaceManager;
        this.logger = logger;
        this.onBeforeCaptureForTest = onBeforeCaptureForTest;
        beforeReloadSubscription = workspaceManager.RegisterBeforeReload(ClearBaseline);
    }

    /// <summary>
    ///     Tears down the manager, draining any in-flight background capture. Unlike
    ///     <see cref="ClearBaseline" /> — which cancels and returns immediately — teardown awaits the
    ///     cancelled capture (bounded by <see cref="DisposeDrainTimeout" />) so it can't outlive the
    ///     manager and overlap a disposing workspace. Idempotent: a second call finds the fields
    ///     already nulled and returns.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        beforeReloadSubscription.Dispose();

        (CancellationTokenSource? oldCts, Task? oldCapture) = DetachAndCancelCapture();
        if (oldCts is null)
        {
            return;
        }

        if (oldCapture is null)
        {
            oldCts.Dispose();
            return;
        }

        // Drain the cancelled capture. Task.WhenAny never surfaces the capture's own outcome — the
        // capture swallows its exceptions internally and completes — so no catch is needed here.
        Task finished = await Task.WhenAny(oldCapture, Task.Delay(DisposeDrainTimeout));
        if (ReferenceEquals(finished, oldCapture))
        {
            oldCts.Dispose();
        }
        else
        {
            // Drain timed out — don't dispose the CTS out from under the still-running capture (it
            // would observe ObjectDisposedException). Defer disposal until the capture completes.
            _ = oldCapture.ContinueWith(_ => oldCts.Dispose(), TaskScheduler.Default);
        }
    }

    /// <summary>
    ///     Schedules a background baseline capture using the current solution snapshot.
    /// </summary>
    /// <remarks>
    ///     The capture runs asynchronously — edit tools call this and proceed immediately.
    ///     No-op if a baseline already exists or a capture is already in flight.
    /// </remarks>
    public void ScheduleBaselineCaptureIfNeeded()
    {
        lock (fieldLock)
        {
            if (diagnosticBaseline is not null || pendingBaselineCapture is not null)
            {
                return;
            }

            Solution? solution = workspaceManager.GetSolutionIfLoaded();
            if (solution is null)
            {
                return;
            }

            CancellationTokenSource cts = new();
            baselineCaptureCts = cts;
            CancellationToken token = cts.Token;
            // CaptureBaselineAsync is async but involves heavy synchronous Roslyn compilation work
            // that would block the caller's thread — offload to the thread pool.
            pendingBaselineCapture = Task.Run(() => CaptureBaselineAsync(solution, cts, token), token);
        }
    }

    /// <summary>
    ///     Captures the current diagnostics as a baseline for incremental comparison.
    /// </summary>
    public async Task CaptureBaselineIfNeededAsync(CancellationToken ct = default)
    {
        ScheduleBaselineCaptureIfNeeded();

        Task? capture;
        lock (fieldLock)
        {
            capture = pendingBaselineCapture;
        }

        if (capture is not null)
        {
            await capture.WaitAsync(ct);
        }
    }

    public DiagnosticBaseline? GetBaseline()
    {
        lock (fieldLock)
        {
            return diagnosticBaseline;
        }
    }

    /// <summary>
    ///     Awaits any in-flight background baseline capture, then returns the baseline.
    /// </summary>
    public async Task<DiagnosticBaseline?> GetBaselineAsync()
    {
        Task? capture;
        lock (fieldLock)
        {
            capture = pendingBaselineCapture;
        }

        if (capture is not null)
        {
            await capture;
        }

        lock (fieldLock)
        {
            return diagnosticBaseline;
        }
    }

    /// <summary>
    ///     Returns the existing baseline, capturing one from current state if none exists.
    /// </summary>
    public async Task<DiagnosticBaseline> GetOrCaptureBaselineAsync(CancellationToken ct = default)
    {
        await CaptureBaselineIfNeededAsync(ct);
        return await GetBaselineAsync()
               ?? throw new InvalidOperationException("Baseline capture produced no baseline");
    }

    /// <summary>
    ///     Atomically clears the baseline, recaptures from current solution state, and returns a summary.
    /// </summary>
    public async Task<ResetBaselineResult> ResetBaselineAsync(CancellationToken ct = default)
    {
        ClearBaseline();
        await CaptureBaselineIfNeededAsync(ct);

        DiagnosticBaseline? baseline = await GetBaselineAsync();
        if (baseline is null || baseline.Count == 0)
        {
            return new ResetBaselineResult(0, []);
        }

        List<DiagnosticBreakdownEntry> breakdown = baseline.Keys
            .GroupBy(k => k.Id)
            .OrderByDescending(g => g.Count())
            .Select(g => new DiagnosticBreakdownEntry(g.Key, g.Count()))
            .ToList();

        return new ResetBaselineResult(baseline.Count, breakdown);
    }

    /// <summary>
    ///     Clears the current diagnostic baseline. The next edit will capture a fresh one.
    /// </summary>
    public void ClearBaseline()
    {
        (CancellationTokenSource? oldCts, Task? oldCapture) = DetachAndCancelCapture();

        if (oldCts is not null)
        {
            // Dispose the source only AFTER the capture finishes. Disposing while the task is still
            // registering on the token would surface as an ObjectDisposedException the capture logs at
            // Error. If no capture is in flight the token is unobserved and the source disposes now.
            if (oldCapture is null)
            {
                oldCts.Dispose();
            }
            else
            {
                _ = oldCapture.ContinueWith(_ => oldCts.Dispose(), TaskScheduler.Default);
            }
        }

        logger.LogInformation("Diagnostic baseline cleared");
    }

    /// <summary>
    ///     Atomically detaches the in-flight capture: nulls the baseline, pending-capture, and CTS
    ///     fields under <see cref="fieldLock" />, then signals cancellation. Returns the detached CTS
    ///     and capture task (either may be null) so the caller disposes the CTS on the right schedule —
    ///     <see cref="ClearBaseline" /> fire-and-forgets it, <see cref="DisposeAsync" /> drains first.
    /// </summary>
    /// <remarks>
    ///     <c>Cancel()</c> runs outside the lock deliberately: correctness rests on
    ///     <see cref="baselineCaptureCts" /> being assigned and compared only under the lock (so a
    ///     racing capture observes the swap and bails via its <c>ReferenceEquals</c> guard), while
    ///     keeping cancellation callbacks from running while <see cref="fieldLock" /> is held.
    /// </remarks>
    private (CancellationTokenSource? cts, Task? capture) DetachAndCancelCapture()
    {
        CancellationTokenSource? oldCts;
        Task? oldCapture;
        lock (fieldLock)
        {
            oldCts = baselineCaptureCts;
            oldCapture = pendingBaselineCapture;
            baselineCaptureCts = null;
            pendingBaselineCapture = null;
            diagnosticBaseline = null;
        }

        oldCts?.Cancel();
        return (oldCts, oldCapture);
    }

    private async Task CaptureBaselineAsync(Solution solution, CancellationTokenSource ownCts, CancellationToken ct)
    {
        try
        {
            if (onBeforeCaptureForTest is not null)
            {
                await onBeforeCaptureForTest();
            }

            List<Diagnostic> diagnostics = await DiagnosticService.GetSolutionDiagnosticsAsync(
                solution.Projects, ct: ct);
            string solutionDir = await workspaceManager.GetRequiredSolutionDirectoryAsync(ct);

            lock (fieldLock)
            {
                // A concurrent ClearBaseline()/reschedule swaps baselineCaptureCts under this same
                // lock. If we are no longer the current capture, write nothing (the baseline would
                // be stale) and leave pendingBaselineCapture alone — that handle belongs to the
                // newer capture. This is authoritative because baselineCaptureCts is only ever
                // mutated under fieldLock, whereas ClearBaseline cancels its CTS outside the lock.
                if (!ReferenceEquals(baselineCaptureCts, ownCts))
                {
                    return;
                }

                if (diagnosticBaseline is null)
                {
                    diagnosticBaseline = DiagnosticBaseline.CaptureFrom(diagnostics, solutionDir);
                    logger.LogInformation("Diagnostic baseline captured: {Count} diagnostics", diagnosticBaseline.Count);
                }

                pendingBaselineCapture = null;
            }
        }
        catch (OperationCanceledException)
        {
            // Baseline capture was cancelled by ClearBaseline or reload — expected
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background baseline capture failed");
            lock (fieldLock)
            {
                if (ReferenceEquals(baselineCaptureCts, ownCts))
                {
                    pendingBaselineCapture = null;
                }
            }
        }
    }
}
