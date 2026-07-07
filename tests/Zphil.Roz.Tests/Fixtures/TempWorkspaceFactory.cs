using Microsoft.Extensions.Logging.Abstractions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Services;

namespace Zphil.Roz.Tests.Fixtures;

/// <summary>
///     A temporary workspace copied from the TestFixture solution, used by edit tests
///     so each test gets an isolated copy without polluting the checked-in fixture.
/// </summary>
internal sealed class TempWorkspace(WorkspaceManager workspaceManager, DiagnosticBaselineManager baselineManager, string tempDirectory) : ITestWorkspace, IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        // Dispose BaselineManager first to drain any in-flight capture and release its BeforeReload
        // subscription against the still-live workspace — mirrors the production DI container's
        // reverse-order disposal (singletons added later are disposed first).
        await BaselineManager.DisposeAsync();
        await WorkspaceManager.DisposeAsync();
        Directory.Delete(tempDirectory, true);
    }

    public WorkspaceManager WorkspaceManager { get; } = workspaceManager;
    public DiagnosticBaselineManager BaselineManager { get; } = baselineManager;
}

/// <summary>
///     Creates a fresh, isolated copy of the TestFixture solution in a temp directory.
/// </summary>
internal static class TempWorkspaceFactory
{
    private static readonly string FixturesDirectory =
        Path.GetDirectoryName(WorkspaceFixture.FixtureSolutionPath)!;

    /// <param name="autoRefreshDisabled">
    ///     Defaults to <c>true</c> to keep the background <see cref="FileSystemWatcher" /> off so it
    ///     doesn't race with fixture file mutations. Tests that exercise reconcile/watcher behavior
    ///     pass <c>false</c> to enable the production code path.
    /// </param>
    /// <param name="ct">Cancellation token for the load.</param>
    internal static async Task<TempWorkspace> CreateAsync(bool autoRefreshDisabled = true, CancellationToken ct = default)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "roslyn-tests", Guid.NewGuid().ToString("N"));
        TestFileHelper.CopyDirectory(FixturesDirectory, tempDir);

        string solutionPath = Path.Combine(tempDir, "TestFixture.sln");
        var service = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance, solutionPath, autoRefreshDisabled);
        await service.GetSolutionAsync(ct);

        // GetSolutionAsync now returns at the usable-snapshot point, before compilation warmup and the
        // external-edit watcher start (the cold-start decoupling). Edit/watcher tests want the fully
        // loaded, watcher-armed workspace the pre-decoupling barrier gave them — otherwise a file op
        // made right after construction races ahead of the watcher. Wait out the full load here; this
        // costs no more than the pre-decoupling GetSolutionAsync barrier did.
        if (service.LoadReadyTask is { } fullLoad)
        {
            await fullLoad;
        }

        var baselineManager = new DiagnosticBaselineManager(service, NullLogger<DiagnosticBaselineManager>.Instance);

        return new TempWorkspace(service, baselineManager, tempDir);
    }
}
