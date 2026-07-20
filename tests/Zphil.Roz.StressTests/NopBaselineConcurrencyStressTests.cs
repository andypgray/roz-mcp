using Microsoft.CodeAnalysis;
using Zphil.Roz.Models;
using Zphil.Roz.Services;
using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;

namespace Zphil.Roz.StressTests;

/// <summary>
///     Diagnostic baseline race condition tests on nopCommerce.
///     nopCommerce has many diagnostics (legacy code, nullable warnings),
///     so baseline capture takes longer — widening the race windows between
///     ClearBaseline, ScheduleBaselineCaptureIfNeeded, and background captures.
/// </summary>
[Trait("Category", "Stress")]
public class NopBaselineConcurrencyStressTests(NopTempWorkspaceFixture fixture, ITestOutputHelper output) : IClassFixture<NopTempWorkspaceFixture>
{
    // Iteration counts deliberately kept modest. Each nopCommerce baseline capture is a full
    // ~34-project diagnostic pass and each ReloadAsync re-opens all 34 projects (~54s apiece),
    // so the original 30–50 counts pushed a single test to ~27 min of CPU thrash. These still
    // exercise each race several times; reducing the per-reload/-capture cost is the real lever.
    private const int RaceIterations = 8; // was 50
    private const int ReloadIterations = 5; // was 30
    private const int EditIterations = 8; // was 20

    /// <summary>
    ///     Repeatedly clears then schedules captures from 10 threads, widening the window for two
    ///     threads to both read pendingBaselineCapture as null and race to create a CTS.
    ///     nopCommerce's larger diagnostic set makes each capture take longer, widening the race.
    /// </summary>
    [Fact]
    public async Task ScheduleBaselineCaptureIfNeeded_RapidClearAndReschedule_DoesNotThrow()
    {
        // Arrange
        DiagnosticBaselineManager bm = fixture.BaselineManager;

        // Act — repeatedly clear and re-schedule from multiple threads
        for (var i = 0; i < RaceIterations; i++)
        {
            bm.ClearBaseline();

            Task[] scheduleTasks = Enumerable.Range(0, 10)
                .Select(_ => Task.Run(() => bm.ScheduleBaselineCaptureIfNeeded()))
                .ToArray();

            await Task.WhenAll(scheduleTasks);
            await bm.GetBaselineAsync();
        }

        // Assert — workspace is still functional
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        solution.Projects.Count().ShouldBeGreaterThanOrEqualTo(25);
    }

    /// <summary>
    ///     Interleaved edits to ProductService (~2500 lines) with incremental diagnostic queries.
    ///     Each edit re-parses 2500 lines. The background baseline capture vs. edit-triggered
    ///     re-schedule race is amplified by the larger file and solution.
    /// </summary>
    [Fact]
    public async Task IncrementalDiagnostics_DuringRapidEdits_GodClass_DoesNotThrow()
    {
        // Arrange
        CodeEditTools codeEdit = NopTestFileHelper.CreateEditTools(fixture);
        DiagnosticTools diag = NopTestFileHelper.CreateDiagnosticTools(fixture);
        string productServiceFile = NopTestFileHelper.ProductServiceFile(fixture.WorkspaceManager);

        // Establish baseline with an initial edit
        await codeEdit.InsertSymbol(productServiceFile, "GetProductByIdAsync", "public virtual string NopBaselineSetup() => \"setup\";", ct: TestContext.Current.CancellationToken);
        await fixture.BaselineManager.CaptureBaselineIfNeededAsync(TestContext.Current.CancellationToken);
        fixture.BaselineManager.GetBaseline().ShouldNotBeNull();

        // Act — interleave edits with incremental diagnostic queries
        await TimingHelper.TimeAsync("Nop_IncrementalDiagnostics_20EditsWithDiagQueries", async () =>
        {
            var lastSymbol = "NopBaselineSetup";
            for (var i = 0; i < EditIterations; i++)
            {
                var methodName = $"NopDiagRace{i}";

                Task editTask = codeEdit.InsertSymbol(productServiceFile, lastSymbol,
                    $"public virtual string {methodName}() => \"{i}\";");
                Task<string> diagTask = diag.GetDiagnostics(incremental: true);

                await Task.WhenAll(editTask, diagTask);
                lastSymbol = methodName;
            }
        }, output);

        // Assert — all inserted methods are present
        string content = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        for (var i = 0; i < EditIterations; i++)
        {
            content.ShouldContain($"NopDiagRace{i}");
        }
    }

    /// <summary>
    ///     Repeated rounds of 5 concurrent resetBaseline calls on the nop workspace.
    ///     nopCommerce's larger diagnostic set means each baseline capture takes longer,
    ///     increasing the odds of concurrent reset calls canceling each other's in-flight
    ///     captures and hitting the Dispose race on the CTS.
    /// </summary>
    [Fact]
    public async Task ResetBaseline_ConcurrentCalls_NopWorkspace_DoesNotCorrupt()
    {
        // Arrange
        DiagnosticTools diag = NopTestFileHelper.CreateDiagnosticTools(fixture);

        // Act — fire concurrent resets
        for (var i = 0; i < RaceIterations; i++)
        {
            Task<string>[] resetTasks = Enumerable.Range(0, 5)
                .Select(_ => diag.GetDiagnostics(resetBaseline: true))
                .ToArray();

            try
            {
                await Task.WhenAll(resetTasks);
            }
            catch (TaskCanceledException)
            {
                // Expected: concurrent resets cancel each other's in-flight captures
            }
        }

        // Assert — a final serial reset should succeed cleanly
        string finalResult = await diag.GetDiagnostics(resetBaseline: true, ct: TestContext.Current.CancellationToken);
        finalResult.ShouldContain("baseline");
        fixture.BaselineManager.GetBaseline().ShouldNotBeNull();
    }

    /// <summary>
    ///     Race: CancelBaselineCapture() calls baselineCaptureCts?.Dispose() while
    ///     CaptureBaselineInBackgroundAsync is still running. nopCommerce's larger
    ///     diagnostic set widens this window.
    /// </summary>
    [Fact]
    public async Task ClearBaseline_WhileCaptureInFlight_DoesNotThrow()
    {
        // Arrange
        await using TempWorkspace temp = await TempWorkspaceFactory.CreateAsync(TestContext.Current.CancellationToken);
        DiagnosticBaselineManager bm = temp.BaselineManager;

        // Act — start a capture then immediately try to clear it from another thread
        for (var i = 0; i < RaceIterations; i++)
        {
            bm.ClearBaseline();
            bm.ScheduleBaselineCaptureIfNeeded();

            // Race: clear from one thread while draining from another.
            // TaskCanceledException is expected when clear cancels an in-flight capture.
            var clearTask = Task.Run(() => bm.ClearBaseline(), TestContext.Current.CancellationToken);
            Task drainTask = bm.GetBaselineAsync();

            try
            {
                await Task.WhenAll(clearTask, drainTask);
            }
            catch (TaskCanceledException)
            {
                // Expected: clear cancelled the in-flight capture
            }
        }

        // Assert — no ObjectDisposedException, workspace still functional
        Solution solution = await temp.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        solution.Projects.Count().ShouldBeGreaterThanOrEqualTo(25);
    }

    /// <summary>
    ///     Race: reload clears the baseline and cancels capture,
    ///     but CaptureBaselineInBackgroundAsync runs outside the gate.
    /// </summary>
    [Fact]
    public async Task ReloadAsync_CancelsBaselineCapture_DoesNotLeaveStaleBaseline()
    {
        // Arrange
        await using TempWorkspace temp = await TempWorkspaceFactory.CreateAsync(TestContext.Current.CancellationToken);
        DiagnosticBaselineManager bm = temp.BaselineManager;

        // Act
        for (var i = 0; i < ReloadIterations; i++)
        {
            bm.ClearBaseline();
            bm.ScheduleBaselineCaptureIfNeeded();

            await temp.WorkspaceManager.ReloadAsync(ct: TestContext.Current.CancellationToken);

            DiagnosticBaseline? baseline = bm.GetBaseline();
            baseline.ShouldBeNull($"Iteration {i}: baseline should be null after reload");
        }

        // Assert — workspace is still functional
        Solution solution = await temp.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        solution.Projects.Count().ShouldBeGreaterThanOrEqualTo(25);
    }

    /// <summary>
    ///     Race: GetBaselineAsync reads pendingBaselineCapture then diagnosticBaseline
    ///     without synchronization.
    /// </summary>
    [Fact]
    public async Task GetBaselineAsync_DuringConcurrentCapture_ReturnsConsistently()
    {
        // Arrange
        await using TempWorkspace temp = await TempWorkspaceFactory.CreateAsync(TestContext.Current.CancellationToken);
        DiagnosticBaselineManager bm = temp.BaselineManager;

        // Act
        for (var i = 0; i < RaceIterations; i++)
        {
            bm.ClearBaseline();

            Task[] scheduleTasks = Enumerable.Range(0, 5)
                .Select(_ => Task.Run(() => bm.ScheduleBaselineCaptureIfNeeded()))
                .ToArray();

            Task<DiagnosticBaseline?>[] drainTasks = Enumerable.Range(0, 5)
                .Select(_ => bm.GetBaselineAsync())
                .ToArray();

            await Task.WhenAll(scheduleTasks.Concat(drainTasks));

            foreach (Task<DiagnosticBaseline?> drainTask in drainTasks)
            {
                DiagnosticBaseline? result = await drainTask;
                if (result is not null)
                {
                    result.Count.ShouldBeGreaterThanOrEqualTo(0);
                }
            }
        }
    }

    /// <summary>
    ///     Race: every edit tool calls ScheduleBaselineCaptureIfNeeded at entry.
    ///     Three concurrent edits all race through the guard check simultaneously.
    ///     nopCommerce's larger file set amplifies the window.
    /// </summary>
    [Fact]
    public async Task ConcurrentEdits_AllTriggerBaselineCapture_DoesNotCorrupt()
    {
        // Arrange
        await using TempWorkspace temp = await TempWorkspaceFactory.CreateAsync(TestContext.Current.CancellationToken);
        CodeEditTools codeEdit = NopTestFileHelper.CreateEditTools(temp);
        string productServiceFile = NopTestFileHelper.ProductServiceFile(temp.WorkspaceManager);
        string productFile = NopTestFileHelper.ProductFile(temp.WorkspaceManager);
        string orderFile = NopTestFileHelper.OrderFile(temp.WorkspaceManager);

        // Act — three parallel edits, each triggering ScheduleBaselineCaptureIfNeeded
        Task serviceEdit = codeEdit.InsertSymbol(productServiceFile, "GetProductByIdAsync", "public virtual string BaselineRaceService() => \"s\";", ct: TestContext.Current.CancellationToken);
        Task productEdit = codeEdit.ReplaceContent([
            new ReplaceContentRequest(productFile, "public partial class Product",
                "public partial class Product /* baseline-race */")
        ], ct: TestContext.Current.CancellationToken);
        Task orderEdit = codeEdit.ReplaceContent([
            new ReplaceContentRequest(orderFile, "public partial class Order",
                "public partial class Order /* baseline-race */")
        ], ct: TestContext.Current.CancellationToken);

        await Task.WhenAll(serviceEdit, productEdit, orderEdit);

        // Drain any pending baseline capture
        DiagnosticBaseline? baseline = await temp.BaselineManager.GetBaselineAsync();

        // Assert — all edits applied, no exceptions
        string serviceContent = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        serviceContent.ShouldContain("BaselineRaceService");

        string productContent = await File.ReadAllTextAsync(productFile, TestContext.Current.CancellationToken);
        productContent.ShouldContain("/* baseline-race */");

        string orderContent = await File.ReadAllTextAsync(orderFile, TestContext.Current.CancellationToken);
        orderContent.ShouldContain("/* baseline-race */");

        if (baseline is not null)
        {
            baseline.Count.ShouldBeGreaterThanOrEqualTo(0);
        }
    }
}
