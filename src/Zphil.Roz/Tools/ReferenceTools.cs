using System.ComponentModel;
using ModelContextProtocol.Server;
using Zphil.Roz.Constants;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Pipeline;
using Zphil.Roz.Presentation;
using Zphil.Roz.Services;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Tools;

/// <summary>
///     MCP tools for finding references and implementations.
/// </summary>
[McpServerToolType]
internal sealed class ReferenceTools(ReferenceService referenceService, ImpactAnalysisService impactService)
{
    [McpServerTool(Name = "find_references", ReadOnly = true, OpenWorld = false, Title = "Find References")]
    [Description("Find references; pass locations[] or symbolNames[].")]
    [NarrowingHint("Use referenceKinds=invocations|reads|writes or project to filter, reduce maxResults or contextLines.")]
    public async Task<string> FindReferences(
        [Description(ToolDescriptions.BatchLocations)]
        string[]? locations = null,
        [Description(ToolDescriptions.BatchSymbolNames)]
        string[]? symbolNames = null,
        [Description(ToolDescriptions.ContainingType)]
        string? containingType = null,
        [Description(ToolDescriptions.Kind)] SymbolicKind? kind = null,
        [Description(ToolDescriptions.Project)]
        string? project = null,
        [Description(ToolDescriptions.IncludeTests)]
        bool includeTests = false,
        [Description(ToolDescriptions.MaxResults)]
        int? maxResults = 20,
        [Description(ToolDescriptions.IncludeGenerated)]
        bool includeGenerated = false,
        [Description(ToolDescriptions.ContextLines)]
        int contextLines = 0,
        [Description(ToolDescriptions.ReferenceKinds)]
        ReferenceKind referenceKinds = ReferenceKind.All,
        [Description(ToolDescriptions.IncludeOverloads)]
        bool includeOverloads = false,
        [Description(ToolDescriptions.ExcludeBaseCalls)]
        bool excludeBaseCalls = false,
        CancellationToken ct = default)
    {
        if (includeOverloads || excludeBaseCalls)
        {
            // includeOverloads/excludeBaseCalls are invocation-only refinements. An explicit
            // referenceKinds=reads|writes contradicts them, so surface a correctable error rather than
            // silently overriding the caller's filter. referenceKinds=all (the default) still promotes to
            // Invocations — an explicit referenceKinds=all is indistinguishable from omission.
            if (referenceKinds is ReferenceKind.Reads or ReferenceKind.Writes)
            {
                throw new UserErrorException(
                    "includeOverloads and excludeBaseCalls apply to invocations only; they cannot be combined with "
                    + $"referenceKinds={referenceKinds.ToString().ToLowerInvariant()}. Drop the flag, or use referenceKinds=invocations.");
            }

            referenceKinds = ReferenceKind.Invocations;
        }

        return await BatchOrSingle.RunAsync(symbolNames, locations,
            name => referenceService.FindReferencesAsync(
                null, null, null, name, containingType, referenceKinds, includeOverloads, excludeBaseCalls,
                !includeTests, maxResults, contextLines, includeGenerated, kind, project, ct),
            loc => referenceService.FindReferencesAsync(
                loc.FilePath, loc.Line, (loc as CursorLocation)?.Column, null, containingType, referenceKinds, includeOverloads, excludeBaseCalls,
                !includeTests, maxResults, contextLines, includeGenerated, kind, project, ct),
            (rs, level) => ResponseFormatter.Format(rs, level),
            "find_references");
    }

    [McpServerTool(Name = "analyze_change_impact", ReadOnly = true, OpenWorld = false, Title = "Analyze Change Impact")]
    [Description("What breaks if you remove a symbol, change its type, narrow its access, or change its signature — tags every reference Compatible/RequiresUpdate/Unsafe (the impact, not just the sites); pass locations[] or symbolNames[].")]
    [NarrowingHint("Use project to filter or reduce maxResults.")]
    public async Task<string> AnalyzeChangeImpact(
        [Description(ToolDescriptions.BatchLocations)]
        string[]? locations = null,
        [Description(ToolDescriptions.BatchSymbolNames)]
        string[]? symbolNames = null,
        [Description(ToolDescriptions.ContainingType)]
        string? containingType = null,
        [Description(ToolDescriptions.Kind)] SymbolicKind? kind = null,
        [Description(ToolDescriptions.ChangeKind)]
        ChangeKind changeKind = ChangeKind.SignatureChange,
        [Description(ToolDescriptions.NewType)]
        string? newType = null,
        [Description(ToolDescriptions.NewAccessibility)]
        AccessibilityLevel? newAccessibility = null,
        [Description(ToolDescriptions.NewSignature)]
        string? newSignature = null,
        [Description(ToolDescriptions.Project)]
        string? project = null,
        [Description(ToolDescriptions.IncludeTests)]
        bool includeTests = false,
        [Description(ToolDescriptions.MaxResults)]
        int? maxResults = 20,
        [Description(ToolDescriptions.ContextLines)]
        int contextLines = 0,
        [Description(ToolDescriptions.IncludeGenerated)]
        bool includeGenerated = false,
        CancellationToken ct = default)
    {
        // newSignature describes ONE method, so it forbids batching — enforced here, the only layer that
        // sees the arrays (BatchOrSingle fans out per item).
        if (!String.IsNullOrWhiteSpace(newSignature)
            && (symbolNames is { Length: > 1 } || locations is { Length: > 1 }))
        {
            throw new UserErrorException(
                "newSignature analyzes one method — pass a single symbolNames/locations entry, not a batch.");
        }

        return await BatchOrSingle.RunAsync(symbolNames, locations,
            name => impactService.AnalyzeAsync(
                null, null, null, name, containingType, changeKind, newType, newAccessibility, newSignature,
                !includeTests, maxResults, contextLines, includeGenerated, kind, project, ct),
            loc => impactService.AnalyzeAsync(
                loc.FilePath, loc.Line, (loc as CursorLocation)?.Column, null, containingType, changeKind, newType,
                newAccessibility, newSignature, !includeTests, maxResults, contextLines, includeGenerated, kind, project, ct),
            (rs, level) => ResponseFormatter.Format(rs, level),
            "analyze_change_impact");
    }

    [McpServerTool(Name = "find_implementations", ReadOnly = true, OpenWorld = false, Title = "Find Implementations")]
    [Description("Find impls / overrides (members) or derived types; pass locations[] or symbolNames[].")]
    [NarrowingHint("Reduce maxResults.")]
    public async Task<string> FindImplementations(
        [Description(ToolDescriptions.BatchLocations)]
        string[]? locations = null,
        [Description(ToolDescriptions.BatchSymbolNames)]
        string[]? symbolNames = null,
        [Description(ToolDescriptions.ContainingType)]
        string? containingType = null,
        [Description(ToolDescriptions.Kind)] SymbolicKind? kind = null,
        [Description(ToolDescriptions.Project)]
        string? project = null,
        [Description(ToolDescriptions.IncludeTests)]
        bool includeTests = false,
        [Description(ToolDescriptions.MaxResults)]
        int? maxResults = 10,
        [Description(ToolDescriptions.IncludeGenerated)]
        bool includeGenerated = false,
        [Description(ToolDescriptions.IncludeMetadata)]
        bool includeMetadata = false,
        [Description(ToolDescriptions.IncludeBody)]
        bool includeBody = false,
        [Description(ToolDescriptions.IncludeDocs)]
        bool includeDocs = false,
        [Description(ToolDescriptions.MaxBodyLines)]
        int? maxBodyLines = null,
        CancellationToken ct = default)
    {
        ListExtensions.ThrowIfMaxBodyLinesInvalid(maxBodyLines);
        return await BatchOrSingle.RunAsync(symbolNames, locations,
            name => referenceService.FindImplementationsAsync(
                null, null, null, name, containingType, !includeTests, maxResults, includeGenerated, kind,
                !includeMetadata, project, ct),
            loc => referenceService.FindImplementationsAsync(
                loc.FilePath, loc.Line, (loc as CursorLocation)?.Column, null, containingType, !includeTests, maxResults, includeGenerated, kind,
                !includeMetadata, project, ct),
            (rs, level) => ResponseFormatter.Format(rs, includeDocs, includeBody, level, maxBodyLines, includeGenerated),
            "find_implementations");
    }
}
