using System.ComponentModel;
using ModelContextProtocol.Server;
using Zphil.Roz.Constants;
using Zphil.Roz.Enums;
using Zphil.Roz.Pipeline;
using Zphil.Roz.Presentation;
using Zphil.Roz.Services;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Tools;

/// <summary>
///     MCP tools for type hierarchy: inheritance chains and derived classes.
/// </summary>
[McpServerToolType]
internal sealed class TypeHierarchyTools(TypeHierarchyService typeHierarchyService)
{
    [McpServerTool(Name = "get_type_hierarchy", ReadOnly = true, OpenWorld = false, Title = "Get Type Hierarchy")]
    [Description("Show inheritance chain; pass locations[] or symbolNames[].")]
    [NarrowingHint("Batch fewer symbols per call, or set includeDocs=false.")]
    public async Task<string> GetTypeHierarchy(
        [Description(ToolDescriptions.BatchLocations)]
        string[]? locations = null,
        [Description(ToolDescriptions.BatchSymbolNames)]
        string[]? symbolNames = null,
        [Description(ToolDescriptions.ContainingType)]
        string? containingType = null,
        [Description(ToolDescriptions.Kind)] SymbolicKind? kind = null,
        [Description(ToolDescriptions.Project)]
        string? project = null,
        [Description(ToolDescriptions.IncludeDocs)]
        bool includeDocs = false,
        [Description(ToolDescriptions.IncludeTests)]
        bool includeTests = false,
        CancellationToken ct = default)
    {
        return await BatchOrSingle.RunAsync(symbolNames, locations,
            name =>
            {
                string? effectiveContaining = String.Equals(name, containingType, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : containingType;
                return typeHierarchyService.GetTypeHierarchyAsync(
                    null, null, null, name, effectiveContaining, kind, project, !includeTests, ct);
            },
            loc => typeHierarchyService.GetTypeHierarchyAsync(
                loc.FilePath, loc.Line, (loc as CursorLocation)?.Column, null, containingType, kind, project, !includeTests, ct),
            (rs, level) => ResponseFormatter.Format(rs, includeDocs, level),
            "get_type_hierarchy");
    }
}
