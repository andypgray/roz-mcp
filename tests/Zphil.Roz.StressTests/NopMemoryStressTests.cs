using Microsoft.Extensions.Logging.Abstractions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Services;
using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

/// <summary>
///     Memory-growth profiling for the find_references hot path. Runs a long loop and samples
///     managed heap, working set, and GC counters to detect leaks. Excluded from default runs
///     via the MemoryProfiling category — opt in with
///     <c>--filter "Category=MemoryProfiling"</c>.
/// </summary>
/// <remarks>
///     <para>
///         Background: a long-lived production session saw repeated <c>Connection closed</c>
///         errors after ~205 find_references calls, and OS-level crash telemetry pointed at
///         process memory growth. Static review of <see cref="WorkspaceManager" /> didn't
///         surface a definitive leak, so these tests exercise two scenarios to discriminate
///         between hypotheses:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <see cref="FindReferences_500x_NoWatcher_MeasureMemoryGrowth" />: watcher and
///                 entry-time sweep disabled. If memory grows here, the leak is in the
///                 find_references / SymbolFinder path itself.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="FindReferences_500x_WithWatcherAndTouchLoop_MeasureMemoryGrowth" />:
///                 watcher enabled and a background task touches an unrelated file every 3 s,
///                 simulating ReSharper's reported touch-loop. If only this scenario grows, the
///                 leak is in the WithDocumentText / TryApplyChanges cycle.
///             </description>
///         </item>
///     </list>
///     <para>
///         Each test is self-contained — it constructs its own <see cref="WorkspaceManager" />
///         rather than sharing a fixture, so the two scenarios don't pollute each other's state.
///     </para>
/// </remarks>
[Trait("Category", "MemoryProfiling")]
public sealed class NopMemoryStressTests(ITestOutputHelper output)
{
    private const int IterationCount = 500;
    private const int SampleEveryN = 50;
    private const int TouchIntervalMs = 3000;

    [Fact]
    public async Task FindReferences_500x_NoWatcher_MeasureMemoryGrowth()
    {
        // Arrange — construct an isolated workspace with the watcher and entry-time sweep both off.
        await using WorkspaceManager workspace = await CreateWorkspaceAsync(true);
        ReferenceTools refs = CreateRefs(workspace);
        (string filePath, int line, int column) = await ResolveIRepositoryPositionAsync(workspace);

        output.WriteLine("=== Scenario A: watcher disabled (baseline) ===");

        // Act — run the loop, sampling memory.
        List<MemorySnapshot> samples = await RunFindReferencesLoopAsync(refs, filePath, line, column, null);

        // Assert — print the table; no hard assertion (this is a profiling harness).
        ReportSamples(samples);
    }

    [Fact]
    public async Task FindReferences_500x_WithWatcherAndTouchLoop_MeasureMemoryGrowth()
    {
        // Arrange — watcher enabled (default) and a background task touches Order.cs every 3 s,
        // simulating a ResHarper touch-loop. The touched file is intentionally not the one we're
        // querying so the scenario isolates the watcher-induced WithDocumentText cycle.
        await using WorkspaceManager workspace = await CreateWorkspaceAsync(false);
        ReferenceTools refs = CreateRefs(workspace);
        (string queryFile, int line, int column) = await ResolveIRepositoryPositionAsync(workspace);

        string touchTarget = OrderFile(workspace);

        using var touchCts = new CancellationTokenSource();
        CancellationToken touchToken = touchCts.Token;
        var touchTask = Task.Run(() => RunTouchLoopAsync(touchTarget, touchToken), TestContext.Current.CancellationToken);

        output.WriteLine("=== Scenario B: watcher + 3 s touch loop on Order.cs ===");

        try
        {
            // Act
            List<MemorySnapshot> samples = await RunFindReferencesLoopAsync(refs, queryFile, line, column, touchTask);

            // Assert
            ReportSamples(samples);
        }
        finally
        {
            touchCts.Cancel();
            try
            {
                await touchTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when the touch loop is cancelled.
            }
        }
    }

    private async Task<List<MemorySnapshot>> RunFindReferencesLoopAsync(
        ReferenceTools refs, string filePath, int line, int column, Task? touchTask)
    {
        List<MemorySnapshot> samples = [MemorySnapshot.Capture(0)];
        output.WriteLine(MemorySnapshot.FormatHeader());
        output.WriteLine(samples[0].FormatRow(samples[0]));

        // Per-call timeout — generous enough for cold IRepository scans on a 35-project solution.
        for (var i = 1; i <= IterationCount; i++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await refs.FindReferences([Loc(filePath, line, column)], maxResults: 50, ct: cts.Token);

            if (i % SampleEveryN == 0 || i == IterationCount)
            {
                var snap = MemorySnapshot.Capture(i);
                samples.Add(snap);
                output.WriteLine(snap.FormatRow(samples[0]));
            }

            // Surface touch-loop crashes early rather than letting them dangle.
            if (touchTask is { IsFaulted: true })
            {
                throw new InvalidOperationException("Touch loop faulted", touchTask.Exception);
            }
        }

        return samples;
    }

    private void ReportSamples(List<MemorySnapshot> samples)
    {
        MemorySnapshot baseline = samples[0];
        MemorySnapshot final = samples[^1];

        output.WriteLine("");
        output.WriteLine($"Growth from baseline (after iteration 0): {final.FormatGrowthSummary(baseline)}");

        // Soft signal: monotonic-growth detection across the sample windows.
        long minHeapMb = samples.Min(s => s.ManagedHeapBytes) / 1024 / 1024;
        long maxHeapMb = samples.Max(s => s.ManagedHeapBytes) / 1024 / 1024;
        output.WriteLine($"Heap range across all samples: {minHeapMb:N0} MB .. {maxHeapMb:N0} MB " +
                         $"(span {maxHeapMb - minHeapMb:N0} MB)");
    }

    private static async Task<WorkspaceManager> CreateWorkspaceAsync(bool autoRefreshDisabled)
    {
        var workspace = new WorkspaceManager(
            NullLogger<WorkspaceManager>.Instance,
            NopWorkspaceFixture.FixtureSolutionPath,
            autoRefreshDisabled);
        await workspace.GetSolutionAsync();
        return workspace;
    }

    private static ReferenceTools CreateRefs(WorkspaceManager workspace)
    {
        var fixture = new ProfilingWorkspace(workspace);
        return CreateReferenceTools(fixture);
    }

    private static async Task<(string filePath, int line, int column)> ResolveIRepositoryPositionAsync(
        WorkspaceManager workspace)
    {
        string iRepoFile = IRepositoryFile(workspace);
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(iRepoFile, "IRepository");
        return (iRepoFile, line, column);
    }

    private static async Task RunTouchLoopAsync(string filePath, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow);
                await Task.Delay(TouchIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    ///     Minimal <see cref="ITestWorkspace" /> adapter that lets the profiling tests reuse
    ///     <see cref="NopTestFileHelper.CreateReferenceTools" /> without going through a class
    ///     fixture (we want fresh workspaces with different watcher settings per test).
    /// </summary>
    private sealed class ProfilingWorkspace(WorkspaceManager workspace) : ITestWorkspace
    {
        public WorkspaceManager WorkspaceManager => workspace;

        public DiagnosticBaselineManager BaselineManager { get; } =
            new(workspace, NullLogger<DiagnosticBaselineManager>.Instance);
    }
}
