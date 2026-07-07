using Microsoft.Extensions.Logging.Abstractions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Services;

namespace Zphil.Roz.StressTests.Fixtures;

[CollectionDefinition("NopReadOnlyWorkspace")]
public class NopReadOnlyWorkspaceCollection : ICollectionFixture<NopWorkspaceFixture>;

public sealed class NopWorkspaceFixture : ITestWorkspace, IAsyncLifetime
{
    internal static readonly string FixtureSolutionPath = NopPaths.SolutionPath;

    private WorkspaceManager? workspaceService;

    internal WorkspaceManager WorkspaceManager => workspaceService
                                                  ?? throw new InvalidOperationException("Fixture not initialized.");

    internal DiagnosticBaselineManager BaselineManager { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        workspaceService = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance, FixtureSolutionPath);
        await workspaceService.GetSolutionAsync();

        // GetSolutionAsync now returns at the usable-snapshot point, before compilation warmup finishes
        // (the cold-start decoupling). Await the full load so warmup doesn't run in the background during
        // the timing tests and inflate their durations — keeping the TimingHelper baselines accurate.
        if (workspaceService.LoadReadyTask is { } fullLoad)
        {
            await fullLoad;
        }

        BaselineManager = new DiagnosticBaselineManager(workspaceService, NullLogger<DiagnosticBaselineManager>.Instance);
    }

    public ValueTask DisposeAsync() => workspaceService?.DisposeAsync() ?? ValueTask.CompletedTask;

    WorkspaceManager ITestWorkspace.WorkspaceManager => WorkspaceManager;
    DiagnosticBaselineManager ITestWorkspace.BaselineManager => BaselineManager;
}

/// <summary>
///     Class fixture that creates an isolated temp copy of the nopCommerce solution
///     and loads the workspace once. Each test class gets its own instance via
///     <c>IClassFixture&lt;NopTempWorkspaceFixture&gt;</c>, providing isolation between classes.
/// </summary>
public sealed class NopTempWorkspaceFixture : IAsyncLifetime, ITestWorkspace
{
    private TempWorkspace? workspace;

    internal WorkspaceManager WorkspaceManager => workspace?.WorkspaceManager
                                                  ?? throw new InvalidOperationException("Fixture not initialized.");

    internal DiagnosticBaselineManager BaselineManager => workspace?.BaselineManager
                                                          ?? throw new InvalidOperationException("Fixture not initialized.");

    public async ValueTask InitializeAsync() => workspace = await TempWorkspaceFactory.CreateAsync(TestSolutionConfig.NopCommerce);

    public async ValueTask DisposeAsync()
    {
        if (workspace is not null)
        {
            await workspace.DisposeAsync();
        }
    }

    WorkspaceManager ITestWorkspace.WorkspaceManager => WorkspaceManager;
    DiagnosticBaselineManager ITestWorkspace.BaselineManager => BaselineManager;
}
