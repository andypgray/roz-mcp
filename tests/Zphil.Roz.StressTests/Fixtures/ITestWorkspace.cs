using Zphil.Roz.Infrastructure;
using Zphil.Roz.Services;

namespace Zphil.Roz.StressTests.Fixtures;

internal interface ITestWorkspace
{
    public WorkspaceManager WorkspaceManager { get; }
    public DiagnosticBaselineManager BaselineManager { get; }
}
