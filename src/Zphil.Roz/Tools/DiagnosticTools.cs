using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Zphil.Roz.Constants;
using Zphil.Roz.Models;
using Zphil.Roz.Pipeline;
using Zphil.Roz.Presentation;
using Zphil.Roz.Services;

namespace Zphil.Roz.Tools;

/// <summary>
///     MCP tools for compiler diagnostics.
/// </summary>
[McpServerToolType]
internal sealed class DiagnosticTools(DiagnosticService diagnosticService, DiagnosticBaselineManager baselineManager)
{
    [McpServerTool(Name = "get_diagnostics", ReadOnly = true, OpenWorld = false, Title = "Get Diagnostics")]
    [Description("Compiler and analyzer diagnostics.")]
    [NarrowingHint("Scope to specific files with filePaths, filter by diagnosticIds, raise severity filter, or use incremental=true.")]
    public async Task<string> GetDiagnostics(
        [Description(ToolDescriptions.FilePathsFilter)]
        string[]? filePaths = null,
        [Description(ToolDescriptions.DiagnosticSeverity)]
        DiagnosticSeverity severity = DiagnosticSeverity.Warning,
        [Description(ToolDescriptions.IncludeTests)]
        bool includeTests = false,
        [Description("Filter to specific diagnostic codes (e.g. CS0246, CS0103).")]
        string[]? diagnosticIds = null,
        [Description(ToolDescriptions.Project)]
        string? project = null,
        [Description("Show only new diagnostics since baseline.")]
        bool incremental = false,
        [Description("Clear and recapture the diagnostic baseline from current state. Cannot combine with incremental=true.")]
        bool resetBaseline = false,
        IProgress<ProgressNotificationValue> progress = null!,
        CancellationToken ct = default)
    {
        if (resetBaseline)
        {
            if (incremental)
            {
                throw new UserErrorException(
                    "Cannot combine resetBaseline=true with incremental=true. Reset wipes the baseline, leaving no prior state to diff against.");
            }

            ResetBaselineResult resetResult = await baselineManager.ResetBaselineAsync(ct);
            return ResponseFormatter.Format(resetResult);
        }

        if (incremental)
        {
            IncrementalDiagnosticsResult incrementalResult =
                await diagnosticService.GetIncrementalDiagnosticsAsync(filePaths, severity, !includeTests, diagnosticIds, project, progress, ct);
            return ResponseFormatter.Format(incrementalResult);
        }

        DiagnosticsResult result = await diagnosticService.GetDiagnosticsAsync(filePaths, severity, !includeTests, diagnosticIds, project, progress, ct);
        return ProgressiveRenderer.Render(result, ResponseFormatter.Format);
    }
}
