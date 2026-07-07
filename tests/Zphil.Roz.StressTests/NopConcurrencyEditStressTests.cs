using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Enums;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;
using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

/// <summary>
///     Concurrency tests involving mutations on nopCommerce's large codebase.
///     The 35-project, 2500+ file solution amplifies race conditions in
///     ScheduleFileChanged, workspace semaphore, and SaveSolutionChangesAsync.
/// </summary>
[Trait("Category", "Stress")]
public class NopConcurrencyEditStressTests(NopTempWorkspaceFixture fixture, ITestOutputHelper output) : IClassFixture<NopTempWorkspaceFixture>
{
    /// <summary>
    ///     Rename Customer → CustomerEntity while 10 parallel FindSymbol queries run.
    ///     Cross-project rename touches ~20+ files across Nop.Core, Nop.Services, Nop.Data.
    ///     Concurrent ScheduleFileChanged calls + reads could produce inconsistent snapshots.
    /// </summary>
    [Fact]
    public async Task ConcurrentReads_DuringCrossProjectRename_AllComplete()
    {
        // Arrange
        await using TempWorkspace temp = await TempWorkspaceFactory.CreateAsync(TestSolutionConfig.NopCommerce, TestContext.Current.CancellationToken);
        CodeEditTools codeEdit = CreateEditTools(temp);
        NavigationTools nav = CreateNavigationTools(temp);
        string customerFile = CustomerFile(temp.WorkspaceManager);

        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(customerFile, "Customer");

        // Act — fire rename and reads concurrently
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));

        Task renameTask = codeEdit.RenameSymbol(Loc(customerFile, line, column), "Customer", "CustomerEntity", ct: TestContext.Current.CancellationToken);

        string[] searchTerms =
        [
            "Product", "Order", "BaseEntity", "ProductService",
            "OrderService", "ShippingService", "TaxService",
            "Category", "Manufacturer", "Discount"
        ];
        Task<string>[] readTasks = searchTerms
            .Select(term => nav.FindSymbol([term], ct: cts.Token))
            .ToArray();

        await Task.WhenAll(readTasks.Append(renameTask));

        // Assert — every concurrent read resolved the symbol it searched for, not merely
        // returned something non-empty while the rename mutated the workspace.
        for (var i = 0; i < readTasks.Length; i++)
        {
            string result = await readTasks[i];
            result.ShouldContain(searchTerms[i]);
        }

        // Rename should have taken effect
        string findResult = await nav.FindSymbol(["CustomerEntity"], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);
        findResult.ShouldContain("CustomerEntity");
    }

    /// <summary>
    ///     Reloading 35 projects is very heavy.
    ///     The workspace semaphore and lazy loading are stressed harder.
    ///     A reload during a query could deadlock if the semaphore isn't properly released.
    /// </summary>
    [Fact]
    public async Task ReloadDuringActiveOperations_LargeSolution_DoesNotCorrupt()
    {
        // Arrange
        NavigationTools nav = CreateNavigationTools(fixture);
        DiagnosticTools diag = CreateDiagnosticTools(fixture);

        // Act — interleave reloads with queries 5 times
        for (var i = 0; i < 5; i++)
        {
            Task reloadTask = fixture.WorkspaceManager.ReloadAsync(ct: TestContext.Current.CancellationToken);
            Task<string> findTask = nav.FindSymbol(["Product"], ct: TestContext.Current.CancellationToken);
            Task<string> diagTask = diag.GetDiagnostics(severity: DiagnosticSeverity.Error, ct: TestContext.Current.CancellationToken);

            await Task.WhenAll(reloadTask, findTask, diagTask);
        }

        // Assert — workspace is still valid
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        solution.Projects.Count().ShouldBeGreaterThanOrEqualTo(25,
            "Solution should still have 25+ projects after repeated reloads");
    }

    /// <summary>
    ///     Three simultaneous edits across different nop projects exercise the
    ///     workspace mutation semaphore. Each edit triggers more Roslyn work
    ///     on the larger 35-project solution.
    /// </summary>
    [Fact]
    public async Task ParallelEdits_ThreeNopProjects_AllSucceed()
    {
        // Arrange
        CodeEditTools codeEdit = CreateEditTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);
        string productFile = ProductFile(fixture.WorkspaceManager);
        string orderFile = OrderFile(fixture.WorkspaceManager);

        // Act — three simultaneous edits across Nop.Services, Nop.Core.Domain.Catalog, Nop.Core.Domain.Orders
        Task serviceEdit = codeEdit.InsertSymbol(productServiceFile, "GetProductByIdAsync", "public virtual Task<int> ParallelEditService() => Task.FromResult(1);", ct: TestContext.Current.CancellationToken);
        Task productEdit = codeEdit.ReplaceContent([new ReplaceContentRequest(productFile, "public partial class Product", "public partial class Product /* parallel-edit */")], ct: TestContext.Current.CancellationToken);
        Task orderEdit = codeEdit.ReplaceContent([new ReplaceContentRequest(orderFile, "public partial class Order", "public partial class Order /* parallel-edit */")], ct: TestContext.Current.CancellationToken);

        await Task.WhenAll(serviceEdit, productEdit, orderEdit);

        // Assert — all three edits applied
        string serviceContent = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        serviceContent.ShouldContain("ParallelEditService");

        string productContent = await File.ReadAllTextAsync(productFile, TestContext.Current.CancellationToken);
        productContent.ShouldContain("/* parallel-edit */");

        string orderContent = await File.ReadAllTextAsync(orderFile, TestContext.Current.CancellationToken);
        orderContent.ShouldContain("/* parallel-edit */");
    }

    /// <summary>
    ///     Rapidly fire concurrent ScheduleFileChanged calls on ProductService.cs (2500 lines)
    ///     with alternating content mutations. The larger file means each call triggers more
    ///     Roslyn parsing work, widening the race window.
    /// </summary>
    [Fact]
    public async Task ScheduleFileChanged_RapidConcurrent_GodClass_NoExceptions()
    {
        // Arrange
        WorkspaceManager ws = fixture.WorkspaceManager;
        string productServiceFile = ProductServiceFile(ws);
        string originalContent = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);

        // Act — 30 iterations of concurrent ScheduleFileChanged with alternating content
        for (var i = 0; i < 30; i++)
        {
            string contentA = originalContent.Replace("ProductService", $"ProductServiceA{i}");
            string contentB = originalContent.Replace("ProductService", $"ProductServiceB{i}");

            var raceA = Task.Run(() => ws.ScheduleFileChanged(productServiceFile, contentA), TestContext.Current.CancellationToken);
            var raceB = Task.Run(() => ws.ScheduleFileChanged(productServiceFile, contentB), TestContext.Current.CancellationToken);
            await Task.WhenAll(raceA, raceB);

            Solution solution = await ws.GetSolutionAsync(TestContext.Current.CancellationToken);
            solution.Projects.Count().ShouldBeGreaterThan(0);

            // Reset for next iteration
            ws.ScheduleFileChanged(productServiceFile, originalContent);
            await ws.GetSolutionAsync(TestContext.Current.CancellationToken);
        }

        // Assert — workspace is still functional
        Solution finalSolution = await ws.GetSolutionAsync(TestContext.Current.CancellationToken);
        finalSolution.Projects.Count().ShouldBeGreaterThanOrEqualTo(25);
    }

    /// <summary>
    ///     The nuclear option: rename IRepository → IDataRepository.
    ///     IRepository is used across nearly every nopCommerce project.
    ///     SaveSolutionChangesAsync fires dozens of ScheduleFileChanged calls
    ///     via Task.WhenAll. Any lost update = disk/Roslyn divergence.
    /// </summary>
    [Fact]
    public async Task RenameSymbol_IRepository_MultipleDocs_NoLostUpdates()
    {
        // Arrange
        await using TempWorkspace temp = await TempWorkspaceFactory.CreateAsync(TestSolutionConfig.NopCommerce, TestContext.Current.CancellationToken);
        CodeEditTools codeEdit = CreateEditTools(temp);
        WorkspaceManager ws = temp.WorkspaceManager;
        string iRepoFile = IRepositoryFile(ws);

        // Act — rename IRepository to IDataRepository (touches files across nearly every project)
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(iRepoFile, "IRepository");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        await TimingHelper.TimeAsync("Nop_RenameSymbol_IRepository_Nuclear",
            () => codeEdit.RenameSymbol(Loc(iRepoFile, line, column), "IRepository", "IDataRepository"), output);

        // Drain all pending file-change notifications
        Solution solution = await ws.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Assert — verify that the Roslyn solution and disk are consistent for every document
        List<string> mismatches = new();
        var checkedCount = 0;

        foreach (Project project in solution.Projects)
        {
            foreach (Document doc in project.Documents)
            {
                if (doc.FilePath is null || !File.Exists(doc.FilePath))
                {
                    continue;
                }

                string diskContent = await File.ReadAllTextAsync(doc.FilePath, TestContext.Current.CancellationToken);
                SourceText solutionText = await doc.GetTextAsync(TestContext.Current.CancellationToken);
                var roslynContent = solutionText.ToString();

                string normalizedDisk = diskContent.Replace("\r\n", "\n");
                string normalizedRoslyn = roslynContent.Replace("\r\n", "\n");

                if (normalizedRoslyn != normalizedDisk)
                {
                    mismatches.Add(doc.FilePath);
                }

                checkedCount++;
            }
        }

        output.WriteLine($"Checked {checkedCount} documents for disk/Roslyn consistency");

        if (mismatches.Count > 0)
        {
            output.WriteLine($"MISMATCHES in {mismatches.Count} files:");
            foreach (string mismatch in mismatches.Take(20))
            {
                output.WriteLine($"  {mismatch}");
            }
        }

        mismatches.Count.ShouldBe(0,
            $"{mismatches.Count}/{checkedCount} files have disk/Roslyn divergence after IRepository rename");

        // The rename should have taken effect
        string iRepoContent = await File.ReadAllTextAsync(iRepoFile, TestContext.Current.CancellationToken);
        iRepoContent.ShouldContain("IDataRepository");
        iRepoContent.ShouldNotContain("IRepository<");
    }
}
