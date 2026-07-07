using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.StressTests.Helpers;

namespace Zphil.Roz.StressTests;

/// <summary>
///     Cold-start timing for the WorkspaceManager warmup-decoupling change. Deliberately standalone
///     (no shared <c>NopWorkspaceFixture</c>): once reads are decoupled from warmup, a fixture's
///     warmup keeps running in the background and would contend for CPU with these loads, polluting
///     the very timings under test. Each test constructs its own fresh, cold WorkspaceManager off the
///     nopCommerce solution and disposes it promptly.
/// </summary>
[Trait("Category", "Stress")]
public class NopColdStartStressTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ColdStart_FirstGetSolution_RecordsTimeToFirstRead()
    {
        // A *cold* start: a fresh WorkspaceManager, timing construction -> first GetSolutionAsync.
        // Before the warmup-decoupling change this latency is essentially total warmup; after it, it
        // drops to the open+strip cost. The number is persisted to the rolling baseline cache so the
        // before/after runs are directly comparable. ColdStart_FirstReadReturnsBeforeWarmupCompletes
        // proves the structural gap.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));

        WorkspaceManager? wm = null;
        try
        {
            // Construct inside the timed region so the background load's head start is counted too.
            wm = await TimingHelper.TimeAsync("Nop_ColdStart_TimeToFirstRead", async () =>
            {
                var manager = new WorkspaceManager(
                    NullLogger<WorkspaceManager>.Instance, NopPaths.SolutionPath, true);
                await manager.GetSolutionAsync(cts.Token);
                return manager;
            }, output);

            // Sanity: the first read really did load the full solution, not an empty/aborted one.
            Solution solution = await wm.GetSolutionAsync(cts.Token);
            int projectCount = solution.Projects.Count();
            output.WriteLine($"Cold-start loaded {projectCount} projects");
            projectCount.ShouldBeGreaterThanOrEqualTo(25, "cold start should load the full nopCommerce solution");
        }
        finally
        {
            if (wm is not null)
            {
                await wm.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ColdStart_FirstReadReturnsBeforeWarmupCompletes()
    {
        // Proves the decoupling: time-to-first-GetSolutionAsync (open + strip) returns while compilation
        // warmup is still running, and is below total warmup (open + strip + compile every project).
        // Before the change these were equal (~the Phase 1 baseline). LoadReadyTask is the full-load
        // task — it completes only after warmup — so it is the warmup-done observation point.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));

        var wm = new WorkspaceManager(
            NullLogger<WorkspaceManager>.Instance, NopPaths.SolutionPath, true);
        try
        {
            Task fullLoad = wm.LoadReadyTask!; // completes only after full compilation warmup

            var sw = Stopwatch.StartNew();
            await wm.GetSolutionAsync(cts.Token);
            long firstReadMs = sw.ElapsedMilliseconds;

            // The structural claim: the snapshot was served before warmup compiled every project.
            bool warmupStillRunning = !fullLoad.IsCompleted;

            await fullLoad.WaitAsync(cts.Token);
            long fullWarmupMs = sw.ElapsedMilliseconds;
            sw.Stop();

            // Record for trend + rolling-baseline regression flagging (warmup must not regress over time).
            TimingComparison? firstReadCmp = await TimingResultsCache.RecordAsync("Nop_ColdStart_FirstReadMs", firstReadMs);
            TimingComparison? warmupCmp = await TimingResultsCache.RecordAsync("Nop_ColdStart_FullWarmupMs", fullWarmupMs);
            long warmupTailMs = fullWarmupMs - firstReadMs;
            output.WriteLine($"[COLD START] first read: {firstReadMs:N0} ms | full warmup: {fullWarmupMs:N0} ms | " +
                             $"warmup tail after first read: {warmupTailMs:N0} ms | still warming at first read: {warmupStillRunning}");
            output.WriteLine($"  first-read trend: {firstReadCmp?.FormatReport() ?? "first run"}");
            output.WriteLine($"  full-warmup trend: {warmupCmp?.FormatReport() ?? "first run"}");

            warmupStillRunning.ShouldBeTrue(
                "the first GetSolutionAsync should return before warmup compiles every project");
            firstReadMs.ShouldBeLessThan(fullWarmupMs,
                "time-to-first-read must be below total warmup once reads are decoupled from warmup");
            // Material, not marginal: compiling 34 projects leaves a multi-second tail after the snapshot.
            warmupTailMs.ShouldBeGreaterThan(1000,
                "a meaningful slice of warmup should still run after the first read is served");
        }
        finally
        {
            await wm.DisposeAsync();
        }
    }
}
