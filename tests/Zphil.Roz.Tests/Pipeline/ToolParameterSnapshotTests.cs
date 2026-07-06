using System.Reflection;
using ModelContextProtocol.Server;
using Zphil.Roz.Pipeline;

namespace Zphil.Roz.Tests.Pipeline;

/// <summary>
///     Snapshot pin for every <c>[McpServerTool]</c>'s JSON-bound parameter set.
///     Any addition, removal, or rename of a tool parameter trips a single
///     <see cref="InlineDataAttribute" /> row with a diff that names the tool and the
///     deviating names, so updates are mechanical.
/// </summary>
/// <remarks>
///     This complements <see cref="UnknownParameterGuardTests" />: that file pins the
///     <em>why</em> of specific name choices (the `symbolNames`/`filePath` convention pins);
///     this file pins the <em>what</em> — the full set per tool — so renames cannot drift
///     past review unnoticed. See the "Tool parameter naming" section in CLAUDE.md for
///     the convention every shared slot follows.
/// </remarks>
public class ToolParameterSnapshotTests
{
    [Theory]
    [InlineData("find_symbol", "symbolNames,kind,depth,excludePattern,containingType,matchMode,project,filePaths,includeTests,maxResults,memberKinds,maxMembers,includeGenerated,includeBody,includeDocs,maxBodyLines")]
    [InlineData("get_symbols_overview", "filePaths,depth,includeDocs,memberKinds,maxMembers,maxTypes,maxFiles,project")]
    [InlineData("find_overloads", "locations,symbolNames,containingType,kind,project,includeBody,includeDocs,includeTests,includeGenerated,maxBodyLines")]
    [InlineData("analyze_method", "locations,symbolNames,containingType,kind,project,includeOverloads,includeExternalCalls,includeTests,maxResults,includeGenerated,contextLines,includeBody,includeDocs,maxBodyLines")]
    [InlineData("go_to_definition", "location,includeBody,includeDocs,maxMembers,maxBodyLines")]
    [InlineData("find_references", "locations,symbolNames,containingType,kind,project,includeTests,maxResults,includeGenerated,contextLines,referenceKinds,includeOverloads,excludeBaseCalls")]
    [InlineData("analyze_change_impact", "changeKind,containingType,contextLines,includeGenerated,includeTests,kind,locations,maxResults,newAccessibility,newSignature,newType,project,symbolNames")]
    [InlineData("find_implementations", "locations,symbolNames,containingType,kind,project,includeTests,maxResults,includeGenerated,includeMetadata,includeBody,includeDocs,maxBodyLines")]
    [InlineData("get_type_hierarchy", "locations,symbolNames,containingType,kind,project,includeDocs,includeTests")]
    [InlineData("edit_symbol", "edits,verify")]
    [InlineData("rename_symbol", "location,symbolName,newName,containingType,kind,renameFile,renameOverloads,renameInStrings,renameInComments,verify")]
    [InlineData("replace_content", "edits,verify")]
    [InlineData("apply_code_fix", "diagnosticId,equivalenceKey,filePaths,includeTests,project,verify")]
    [InlineData("change_signature", "location,symbolName,newSignature,containingType,kind,verify")]
    [InlineData("get_workspace_info", "project,reload")]
    [InlineData("get_unused_references", "dependencyKind,project")]
    [InlineData("get_diagnostics", "filePaths,severity,includeTests,diagnosticIds,project,incremental,resetBaseline")]
    [InlineData("add_usings", "filePath,usings,sortUsings")]
    [InlineData("remove_unused_usings", "filePaths,sortUsings")]
    public void Tool_ParameterSet_MatchesSnapshot(string toolName, string expectedCsv)
    {
        // Arrange
        MethodInfo method = ToolAttributeDiscovery.GetToolMethods()
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);

        string[] actual = method.GetParameters()
            .Where(IsJsonBound)
            .Select(p => p.Name!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        string[] expected = expectedCsv.Split(',')
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Assert — diff names both directions so the failure tells you exactly what to update.
        actual.ShouldBe(expected, $"Parameter snapshot drift on '{toolName}'. Update the InlineData row to match the tool's signature, or revert the rename.");
    }

    // Independent oracle for "is this a JSON-bound parameter" — deliberately duplicated
    // from UnknownParameterGuardTests.IsJsonBound so a divergence between the two
    // predicates (or the production guard) shows up as a snapshot mismatch.
    private static bool IsJsonBound(ParameterInfo p)
    {
        if (p.Name is null || p.ParameterType == typeof(CancellationToken))
        {
            return false;
        }

        return !(p.ParameterType.IsGenericType
                 && p.ParameterType.GetGenericTypeDefinition() == typeof(IProgress<>));
    }
}
