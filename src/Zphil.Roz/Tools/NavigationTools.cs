using System.ComponentModel;
using ModelContextProtocol.Server;
using Zphil.Roz.Constants;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Models;
using Zphil.Roz.Pipeline;
using Zphil.Roz.Presentation;
using Zphil.Roz.Services;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Tools;

/// <summary>
///     MCP tools for navigating symbols: find by name, file overview, and go-to-definition.
/// </summary>
[McpServerToolType]
internal sealed class NavigationTools(NavigationService navigationService, MethodAnalysisService methodAnalysisService)
{
    [McpServerTool(Name = "find_symbol", ReadOnly = true, OpenWorld = false, Title = "Find Symbol")]
    [Description("Search symbols by name.")]
    [NarrowingHint("Use containingType, kind, matchMode, project, or excludePattern to narrow results, or reduce maxResults.")]
    public async Task<string> FindSymbol(
        [Description("Names or partial names; supports glob wildcards (* and ?).")]
        string[] symbolNames,
        [Description(ToolDescriptions.Kind)] SymbolicKind? kind = null,
        [Description("Depth: 0 = match only, 1 = direct members, 2 = nested.")]
        int depth = 0,
        [Description("Glob to exclude symbols by name (e.g. '*Tests*').")]
        string? excludePattern = null,
        [Description("Filter to symbols inside this type.")]
        string? containingType = null,
        [Description(ToolDescriptions.MatchMode)]
        SymbolMatchMode matchMode = SymbolMatchMode.Contains,
        [Description(ToolDescriptions.Project)]
        string? project = null,
        [Description(ToolDescriptions.FilePathsFilter)]
        string[]? filePaths = null,
        [Description(ToolDescriptions.IncludeTests)]
        bool includeTests = false,
        [Description(ToolDescriptions.MaxResults)]
        int? maxResults = 10,
        [Description(ToolDescriptions.MemberKinds)]
        SymbolicKind[]? memberKinds = null,
        [Description(ToolDescriptions.MaxMembers)]
        int? maxMembers = null,
        [Description(ToolDescriptions.IncludeGenerated)]
        bool includeGenerated = false,
        [Description(ToolDescriptions.IncludeBodyFindSymbol)]
        bool includeBody = false,
        [Description(ToolDescriptions.IncludeDocs)]
        bool includeDocs = false,
        [Description(ToolDescriptions.MaxBodyLines)]
        int? maxBodyLines = null,
        CancellationToken ct = default)
    {
        ListExtensions.ThrowIfMaxBodyLinesInvalid(maxBodyLines);
        BatchItem<FindSymbolResult>[] items = await BatchOrSingle.RunAllAsync(symbolNames,
            name => navigationService.FindSymbolAsync(
                name, kind, depth, excludePattern, containingType, project,
                matchMode, includeBody, !includeTests, maxResults, memberKinds, includeGenerated, maxMembers, filePaths, ct));
        return ProgressiveRenderer.Render(items,
            (r, level) => ResponseFormatter.Format(r, includeDocs, level, maxBodyLines, includeGenerated));
    }

    [McpServerTool(Name = "get_symbols_overview", ReadOnly = true, OpenWorld = false, Title = "Get Symbols Overview")]
    [Description("List symbols in files: pass filePaths=[...] (globs OK) or project=<name>.")]
    [NarrowingHint("Request fewer files at once, or use depth=0 to show only type names.")]
    public async Task<string> GetSymbolsOverview(
        [Description(ToolDescriptions.FilePathsFilter)]
        string[]? filePaths = null,
        [Description("Depth: 0 = type names only, 1 = direct members, 2 = nested.")]
        int depth = 1,
        [Description(ToolDescriptions.IncludeDocs)]
        bool includeDocs = false,
        [Description(ToolDescriptions.MemberKinds)]
        SymbolicKind[]? memberKinds = null,
        [Description(ToolDescriptions.MaxMembers)]
        int? maxMembers = null,
        [Description(ToolDescriptions.MaxTypes)]
        int maxTypes = 50,
        [Description("Max files a glob may expand to.")]
        int maxFiles = 20,
        [Description(ToolDescriptions.Project)]
        string? project = null,
        CancellationToken ct = default)
    {
        MultiFileOverviewResult overview = await navigationService.GetMultiFileOverviewAsync(
            filePaths, project, depth, memberKinds, maxMembers, maxTypes, maxFiles, ct);

        if (overview.Results.Length == 0 && project is not null)
        {
            return $"Project '{project}' found but contains no source documents.";
        }

        string rendered = ProgressiveRenderer.Render(overview.Results,
            (r, level) => ResponseFormatter.Format(r, includeDocs, level));

        if (overview.GlobalTotalTypes > maxTypes)
        {
            string scope = overview.FileCount == 1
                ? ""
                : $" across {overview.FileCount} files";
            rendered += $"\n\n({overview.GlobalTotalTypes} total types{scope} — increase maxTypes or narrow with filePaths/kind filter)";
        }

        return rendered;
    }

    [McpServerTool(Name = "find_overloads", ReadOnly = true, OpenWorld = false, Title = "Find Overloads")]
    [Description("Find method overloads; pass locations[] or symbolNames[].")]
    [NarrowingHint("Set includeBody=false, or use maxBodyLines to cap body size.")]
    public async Task<string> FindOverloads(
        [Description(ToolDescriptions.BatchLocations)]
        string[]? locations = null,
        [Description(ToolDescriptions.BatchSymbolNames)]
        string[]? symbolNames = null,
        [Description(ToolDescriptions.ContainingType)]
        string? containingType = null,
        [Description(ToolDescriptions.Kind)] SymbolicKind? kind = null,
        [Description(ToolDescriptions.Project)]
        string? project = null,
        [Description(ToolDescriptions.IncludeBody)]
        bool includeBody = false,
        [Description(ToolDescriptions.IncludeDocs)]
        bool includeDocs = false,
        [Description(ToolDescriptions.IncludeTests)]
        bool includeTests = false,
        [Description(ToolDescriptions.IncludeGenerated)]
        bool includeGenerated = false,
        [Description(ToolDescriptions.MaxBodyLines)]
        int? maxBodyLines = null,
        CancellationToken ct = default)
    {
        ListExtensions.ThrowIfMaxBodyLinesInvalid(maxBodyLines);
        return await BatchOrSingle.RunAsync(symbolNames, locations,
            name => navigationService.FindOverloadsAsync(
                null, null, null, name, containingType, kind, project, !includeTests, includeGenerated, ct),
            loc => navigationService.FindOverloadsAsync(
                loc.FilePath, loc.Line, (loc as CursorLocation)?.Column, null, containingType, kind, project, !includeTests, includeGenerated, ct),
            (rs, level) => ResponseFormatter.Format(rs, includeDocs, includeBody, level, maxBodyLines),
            "find_overloads");
    }

    [McpServerTool(Name = "analyze_method", ReadOnly = true, OpenWorld = false, Title = "Analyze Method")]
    [Description("Method signature + inbound callers + outbound calls; pass locations[] or symbolNames[].")]
    [NarrowingHint("Reduce maxResults or contextLines; set includeBody=false; drop includeOverloads.")]
    public async Task<string> AnalyzeMethod(
        [Description(ToolDescriptions.BatchLocations)]
        string[]? locations = null,
        [Description(ToolDescriptions.BatchSymbolNames)]
        string[]? symbolNames = null,
        [Description(ToolDescriptions.ContainingType)]
        string? containingType = null,
        [Description(ToolDescriptions.Kind)] SymbolicKind? kind = null,
        [Description(ToolDescriptions.Project)]
        string? project = null,
        [Description(ToolDescriptions.IncludeOverloads)]
        bool includeOverloads = false,
        [Description(ToolDescriptions.IncludeExternalCalls)]
        bool includeExternalCalls = false,
        [Description(ToolDescriptions.IncludeTests)]
        bool includeTests = false,
        [Description(ToolDescriptions.MaxResults)]
        int? maxResults = 100,
        [Description(ToolDescriptions.IncludeGenerated)]
        bool includeGenerated = false,
        [Description(ToolDescriptions.ContextLines)]
        int contextLines = 0,
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
            name => methodAnalysisService.AnalyzeMethodAsync(
                null, null, null, name, containingType, includeOverloads, includeExternalCalls,
                !includeTests, maxResults, contextLines, includeBody, includeGenerated, kind, project, ct),
            loc => methodAnalysisService.AnalyzeMethodAsync(
                loc.FilePath, loc.Line, (loc as CursorLocation)?.Column, null, containingType, includeOverloads,
                includeExternalCalls, !includeTests, maxResults, contextLines, includeBody, includeGenerated, kind, project, ct),
            (items, level) => ResponseFormatter.Format(items, includeDocs, level, maxBodyLines),
            "analyze_method");
    }

    [McpServerTool(Name = "go_to_definition", ReadOnly = true, OpenWorld = false, Title = "Go to Definition")]
    [Description("Go to definition of the symbol at a cursor position.")]
    [NarrowingHint("Set includeBody=false to see just the signature.")]
    public async Task<string> GoToDefinition(
        [Description("Cursor position: 'path:line:col' or 'path:line' (snaps to the member declared on that line). Avoid 'path:1:1' — line 1 is usually a 'using' keyword.")]
        string location,
        [Description(ToolDescriptions.IncludeBody)]
        bool includeBody = false,
        [Description(ToolDescriptions.IncludeDocs)]
        bool includeDocs = false,
        [Description(ToolDescriptions.MaxMembers)]
        int? maxMembers = 30,
        [Description(ToolDescriptions.MaxBodyLines)]
        int? maxBodyLines = null,
        CancellationToken ct = default)
    {
        ListExtensions.ThrowIfMaxBodyLinesInvalid(maxBodyLines);
        PositionLocation loc = LocationParser.ParsePosition(location, "go_to_definition");
        int? column = (loc as CursorLocation)?.Column;

        SymbolAtPositionResult result = await navigationService.GoToDefinitionAsync(loc.FilePath, loc.Line, column, includeBody, maxMembers, ct);
        return ProgressiveRenderer.Render(result,
            (r, level) => ResponseFormatter.Format(r, includeDocs, level, maxBodyLines));
    }
}
