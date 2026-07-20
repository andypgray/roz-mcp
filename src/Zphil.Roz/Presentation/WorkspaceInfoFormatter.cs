using System.Text;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Models;
using ProjectInfo = Zphil.Roz.Models.ProjectInfo;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Formats workspace metadata (projects, TFMs, dependency graphs) at progressive detail levels.
/// </summary>
internal static class WorkspaceInfoFormatter
{
    /// <summary>
    ///     Formats workspace info at the specified detail level. Progressive detail reduction
    ///     allows large solutions to fit within response limits without hard truncation.
    /// </summary>
    public static string Format(WorkspaceInfoResult result, DetailLevel level = DetailLevel.Full)
    {
        List<GroupedProjectInfo> grouped = GroupMultiTfmProjects(result.Projects);
        return level switch
        {
            DetailLevel.Full => FormatFull(result, grouped),
            DetailLevel.High => FormatHigh(result, grouped),
            DetailLevel.Medium => FormatMedium(result, grouped),
            DetailLevel.Low => FormatLow(result, grouped),
            _ => FormatMinimal(result, grouped)
        };
    }

    /// <summary>
    ///     Groups multi-TFM projects by stripping the <c>(tfm)</c> suffix from Roslyn project names
    ///     and merging their target frameworks. Single-TFM projects pass through unchanged.
    /// </summary>
    internal static List<GroupedProjectInfo> GroupMultiTfmProjects(List<ProjectInfo> projects)
    {
        return projects
            .GroupBy(p => ProjectExtensions.StripTfmSuffix(p.Name))
            .Select(g =>
            {
                ProjectInfo first = g.First();
                List<string> tfms = g
                    .Select(p => p.Tfm)
                    .Where(t => t is not null)
                    .Select(t => t!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Sum doc counts across TFMs but use max to avoid double-counting
                // (multi-TFM projects share the same source files)
                int docCount = g.Max(p => p.DocCount);

                return new GroupedProjectInfo(
                    g.Key, first.ProjectType, first.Language, tfms,
                    first.LanguageVersion, first.NullableContext, docCount);
            })
            .ToList();
    }

    /// <summary>
    ///     Full: all metadata per project + complete dependency graph.
    /// </summary>
    private static string FormatFull(WorkspaceInfoResult result, List<GroupedProjectInfo> grouped)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, result);
        AppendProjectCount(sb, grouped.Count, result.Projects.Count);
        AppendGroupedProjects(sb, grouped);
        AppendGroupedDependencyGraph(sb, result.ProjectDependencies);
        AppendGroupedReverseDependencyGraph(sb, result.ProjectDependencies);
        AppendFooter(sb, grouped.Count, result.TotalDocs);
        return sb.ToString();
    }

    /// <summary>
    ///     High: all metadata per project, no dependency graph.
    /// </summary>
    private static string FormatHigh(WorkspaceInfoResult result, List<GroupedProjectInfo> grouped)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, result);
        AppendProjectCount(sb, grouped.Count, result.Projects.Count);
        AppendGroupedProjects(sb, grouped);
        AppendFooter(sb, grouped.Count, result.TotalDocs);
        sb.AppendLine();
        sb.Append("Dependencies omitted. Use project parameter to see dependencies for a specific project.");
        return sb.ToString();
    }

    /// <summary>
    ///     Medium: TFM and file count only, no language version or nullable context.
    /// </summary>
    private static string FormatMedium(WorkspaceInfoResult result, List<GroupedProjectInfo> grouped)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, result);
        AppendProjectCount(sb, grouped.Count, result.Projects.Count);
        AppendGroupedProjectsCompact(sb, grouped);
        AppendFooter(sb, grouped.Count, result.TotalDocs);
        sb.AppendLine();
        sb.Append("Dependencies omitted. Use project parameter to see dependencies for a specific project.");
        return sb.ToString();
    }

    /// <summary>
    ///     Low: project names grouped by type, no per-project metadata.
    /// </summary>
    private static string FormatLow(WorkspaceInfoResult result, List<GroupedProjectInfo> grouped)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Solution: {result.SolutionName} ({result.Projects.Count} projects, {result.TotalDocs:N0} documents)");
        sb.AppendLine($"Path: {result.SolutionPath}");

        IOrderedEnumerable<IGrouping<string, GroupedProjectInfo>> byType = grouped
            .GroupBy(p => p.ProjectType ?? "other")
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, GroupedProjectInfo> typeGroup in byType)
        {
            List<string> names = typeGroup.Select(p => p.BaseName).ToList();
            sb.AppendLine();
            sb.Append($"{typeGroup.Key} ({names.Count}): {String.Join(", ", names)}");
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Minimal: totals + type breakdown counts only.
    /// </summary>
    private static string FormatMinimal(WorkspaceInfoResult result, List<GroupedProjectInfo> grouped)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Solution: {result.SolutionName} ({result.Projects.Count} projects, {result.TotalDocs:N0} documents)");
        sb.AppendLine($"Path: {result.SolutionPath}");

        IEnumerable<string> typeCounts = grouped
            .GroupBy(p => p.ProjectType ?? "other")
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Count()} {g.Key}");

        sb.Append($"Types: {String.Join(", ", typeCounts)}");
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, WorkspaceInfoResult result)
    {
        sb.AppendLine($"Solution: {result.SolutionName}");
        sb.AppendLine($"Path: {result.SolutionPath}");
        AppendConfigFileLine(sb, result);
        sb.AppendLine();
    }

    /// <summary>
    ///     Provenance line for the <c>.roz.json</c> project config: which file was discovered and what
    ///     it contributed, via the canonical <see cref="Infrastructure.ProjectConfigSeedResult.Summary" />.
    ///     Omitted entirely when no file was found.
    /// </summary>
    private static void AppendConfigFileLine(StringBuilder sb, WorkspaceInfoResult result)
    {
        if (result.Config is not { ConfigFilePath: not null } config)
        {
            return;
        }

        sb.AppendLine($"Config file: {config.ConfigFilePath} ({config.Summary()})");
    }

    private static void AppendProjectCount(StringBuilder sb, int logicalCount, int totalCount)
    {
        string counts = logicalCount < totalCount
            ? $"{logicalCount} logical, {totalCount} total"
            : $"{logicalCount}";
        sb.AppendLine($"Projects ({counts}):");
    }

    private static void AppendGroupedProjects(StringBuilder sb, List<GroupedProjectInfo> grouped)
    {
        foreach (GroupedProjectInfo project in grouped)
        {
            string typeSuffix = project.ProjectType is not null ? $" [{project.ProjectType}]" : "";
            string langPrefix = project.LanguageVersion is not null ? $"C# {project.LanguageVersion}" : project.Language;
            string tfmSuffix = project.Tfms.Count > 0 ? $", {String.Join(" | ", project.Tfms)}" : "";
            string nullableSuffix = project.NullableContext is not null ? $", nullable={project.NullableContext}" : "";
            sb.AppendLine($"  {project.BaseName}{typeSuffix} ({langPrefix}{tfmSuffix}{nullableSuffix}, {project.DocCount} files)");
        }
    }

    private static void AppendGroupedProjectsCompact(StringBuilder sb, List<GroupedProjectInfo> grouped)
    {
        foreach (GroupedProjectInfo project in grouped)
        {
            string typeSuffix = project.ProjectType is not null ? $" [{project.ProjectType}]" : "";
            string tfmSuffix = project.Tfms.Count > 0 ? $", {String.Join(" | ", project.Tfms)}" : "";
            sb.AppendLine($"  {project.BaseName}{typeSuffix} ({project.DocCount} files{tfmSuffix})");
        }
    }

    private static void AppendGroupedDependencyGraph(StringBuilder sb, List<ProjectDependencyInfo> projectDependencies) =>
        AppendGroupedRelationships(sb, projectDependencies, p => p.Dependencies, "Project Dependencies:", "\u2192");

    private static void AppendGroupedReverseDependencyGraph(StringBuilder sb, List<ProjectDependencyInfo> projectDependencies) =>
        AppendGroupedRelationships(sb, projectDependencies, p => p.Dependents, "Depended on by:", "\u2190");

    private static void AppendGroupedRelationships(
        StringBuilder sb,
        List<ProjectDependencyInfo> projectDependencies,
        Func<ProjectDependencyInfo, IReadOnlyList<string>> selector,
        string header,
        string arrow)
    {
        List<(string Name, List<string> Related)> grouped = projectDependencies
            .Where(p => selector(p).Count > 0)
            .GroupBy(p => ProjectExtensions.StripTfmSuffix(p.Name))
            .Select(g => (
                Name: g.Key,
                Related: g
                    .SelectMany(p => selector(p))
                    .Select(ProjectExtensions.StripTfmSuffix)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .ToList();

        if (grouped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(header);
            foreach ((string name, List<string> related) in grouped)
            {
                sb.AppendLine($"  {name} {arrow} {String.Join(", ", related)}");
            }
        }
    }

    private static void AppendFooter(StringBuilder sb, int projectCount, int totalDocs)
    {
        sb.AppendLine();
        sb.Append($"Total: {projectCount} projects, {totalDocs} documents");
    }
}
