using Microsoft.Extensions.Logging.Abstractions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Services;

namespace Zphil.Roz.Tests.Fixtures;

/// <summary>
///     Shared xUnit fixture that loads the DiagnosticFixture solution once
///     for all tests in the "DiagnosticWorkspace" collection.
///     Unlike WorkspaceFixture, this solution intentionally contains compiler diagnostics.
/// </summary>
public sealed class DiagnosticWorkspaceFixture : ITestWorkspace, IAsyncLifetime
{
    internal static readonly string FixtureSolutionPath = Path.Combine(
        Path.GetDirectoryName(typeof(DiagnosticWorkspaceFixture).Assembly.Location)!,
        "Fixtures", "TestSolutions", "DiagnosticFixture", "DiagnosticFixture.sln");

    private DiagnosticBaselineManager? baselineManager;

    private WorkspaceManager? workspaceService;

    internal WorkspaceManager WorkspaceManager => workspaceService
                                                  ?? throw new InvalidOperationException("Fixture not initialized.");

    internal DiagnosticBaselineManager BaselineManager => baselineManager
                                                          ?? throw new InvalidOperationException("Fixture not initialized.");

    public async ValueTask InitializeAsync()
    {
        // autoRefreshDisabled: true — keep the FileSystemWatcher off in tests so background reloads
        // don't race with fixture file operations.
        workspaceService = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance, FixtureSolutionPath, true);
        baselineManager = new DiagnosticBaselineManager(workspaceService, NullLogger<DiagnosticBaselineManager>.Instance);
        await workspaceService.GetSolutionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // Dispose BaselineManager first to drain any in-flight capture and release its BeforeReload
        // subscription against the still-live workspace — mirrors EditWorkspaceFixture's documented
        // reverse-order disposal.
        if (baselineManager is not null)
        {
            await baselineManager.DisposeAsync();
        }

        if (workspaceService is not null)
        {
            await workspaceService.DisposeAsync();
        }
    }

    WorkspaceManager ITestWorkspace.WorkspaceManager => WorkspaceManager;
    DiagnosticBaselineManager ITestWorkspace.BaselineManager => BaselineManager;

    /// <summary>Returns the absolute path to a file in the DiagnosticFixture project directory.</summary>
    internal string ProjectFile(string name) => Path.Combine(WorkspaceManager.SolutionDirectory!, "DiagnosticFixture", name);
}
