using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Zphil.Roz.Constants;
using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using Zphil.Roz.Pipeline;
using Zphil.Roz.Presentation;
using Zphil.Roz.Services;

namespace Zphil.Roz.Tools;

/// <summary>
///     MCP tools for workspace management: info, reload, and reference hygiene.
/// </summary>
[McpServerToolType]
internal sealed class WorkspaceTools(WorkspaceService service, UnusedReferenceService unusedReferenceService)
{
    [McpServerTool(Name = "get_workspace_info", ReadOnly = true, OpenWorld = false, Title = "Get Workspace Info")]
    [Description("Solution info. reload=true refreshes from disk.")]
    [NarrowingHint("Use project parameter to filter to specific projects.")]
    public async Task<string> GetWorkspaceInfo(
        [Description(ToolDescriptions.Project)]
        string? project = null,
        [Description("Reload the workspace from disk before returning info. Cannot combine with project filter.")]
        bool reload = false,
        IProgress<ProgressNotificationValue> progress = null!,
        CancellationToken ct = default)
    {
        if (reload)
        {
            if (project is not null)
            {
                throw new UserErrorException(
                    "Cannot combine reload=true with a project filter. Reload affects the entire workspace.");
            }

            ReloadResult reloadResult = await service.ReloadAndSummarizeAsync(progress, ct);
            return ResponseFormatter.Format(reloadResult);
        }

        WorkspaceInfoResult result = await service.GetInfoAsync(project, ct);

        if (project is not null && result.Projects.Count == 0)
        {
            return $"No projects match '{project}'. Use get_workspace_info without a filter to see all projects.";
        }

        return ProgressiveRenderer.Render(result, ResponseFormatter.Format);
    }

    [McpServerTool(Name = "get_unused_references", ReadOnly = true, OpenWorld = false, Title = "Get Unused References")]
    [Description("Find <ProjectReference>/<PackageReference> entries unused by source. dependencyKind=Packages is a weak signal.")]
    [NarrowingHint("Scope with project=<name>.")]
    public async Task<string> GetUnusedReferences(
        [Description("Projects|Packages|All.")]
        UnusedReferencesKind dependencyKind = UnusedReferencesKind.Projects,
        [Description(ToolDescriptions.Project)]
        string? project = null,
        IProgress<ProgressNotificationValue> progress = null!,
        CancellationToken ct = default)
    {
        UnusedReferencesResult result =
            await unusedReferenceService.GetUnusedReferencesAsync(dependencyKind, project, progress, ct);
        return ResponseFormatter.Format(result);
    }
}
