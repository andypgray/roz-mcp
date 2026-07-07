using Microsoft.Extensions.Logging.Abstractions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Services;

namespace Zphil.Roz.Tests.Fixtures;

/// <summary>
///     Shared xUnit fixture that loads the checked-in TestFixture solution once
///     for all read-only tests in the "ReadOnlyWorkspace" collection.
/// </summary>
public sealed class WorkspaceFixture : ITestWorkspace, IAsyncLifetime
{
    internal static readonly string FixtureSolutionPath = Path.Combine(
        Path.GetDirectoryName(typeof(WorkspaceFixture).Assembly.Location)!,
        "Fixtures", "TestSolutions", "TestFixture.sln");

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

    /// <summary>Returns the absolute path to a file in the TestFixture/Shapes directory.</summary>
    internal string ShapesFile(string name) => Path.Combine(WorkspaceManager.SolutionDirectory!, "TestFixture", "Shapes", name);

    /// <summary>Returns the absolute path to a file in the TestFixture/Services directory.</summary>
    internal string ServicesFile(string name) => Path.Combine(WorkspaceManager.SolutionDirectory!, "TestFixture", "Services", name);

    /// <summary>Returns the absolute path to a file in the TestFixture/MultiType directory.</summary>
    internal string MultiTypeFile(string name) => Path.Combine(WorkspaceManager.SolutionDirectory!, "TestFixture", "MultiType", name);

    /// <summary>Returns the absolute path to a file in the TestFixture.TopLevel directory.</summary>
    internal string TopLevelFile(string name) => Path.Combine(WorkspaceManager.SolutionDirectory!, "TestFixture.TopLevel", name);
}
