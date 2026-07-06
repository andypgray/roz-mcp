using Zphil.Roz.Infrastructure;
using Zphil.Roz.Services;

namespace Zphil.Roz.Tests.Fixtures;

/// <summary>
///     Common interface for test workspace providers, allowing helpers to work
///     with both per-test <see cref="TempWorkspace" /> and shared <see cref="EditWorkspaceFixture" />.
/// </summary>
internal interface ITestWorkspace
{
    public WorkspaceManager WorkspaceManager { get; }
    public DiagnosticBaselineManager BaselineManager { get; }
}
