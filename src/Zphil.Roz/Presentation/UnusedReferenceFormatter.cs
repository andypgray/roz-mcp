using System.Text;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Models;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Formats results for the <c>get_unused_references</c> tool.
/// </summary>
internal static class UnusedReferenceFormatter
{
    private const string WeakSignalNote =
        "Note: package detection is a weak signal. Packages may provide analyzers, source " +
        "generators, MSBuild targets, or runtime-only dependencies that don't appear in source. " +
        "Verify each before removal.";

    /// <summary>
    ///     Formats an <see cref="UnusedReferencesResult" />. Projects with no hits are not listed.
    /// </summary>
    public static string Format(UnusedReferencesResult result)
    {
        if (result.Projects.Count == 0)
        {
            string scope = result.ProjectFilter is not null
                ? $"in projects matching '{result.ProjectFilter}'"
                : "in solution";
            return $"No unused {DescribeKind(result.Kind)} references found {scope}.";
        }

        var sb = new StringBuilder();
        for (var i = 0; i < result.Projects.Count; i++)
        {
            if (i > 0)
            {
                sb.AppendLine();
            }

            AppendProject(sb, result.Projects[i]);
        }

        sb.AppendLine();
        AppendFooter(sb, result);

        if (HasPackageHits(result))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(WeakSignalNote);
        }

        return sb.ToString();
    }

    private static void AppendProject(StringBuilder sb, ProjectUnusedReferences project)
    {
        string name = ProjectExtensions.StripTfmSuffix(project.ProjectName);

        if (project.AnalysisError is not null)
        {
            sb.AppendLine($"=== Error in {name} ===");
            sb.AppendLine(project.AnalysisError);
            return;
        }

        sb.AppendLine($"Unused references in {name}:");
        sb.AppendLine();

        foreach (string projectRef in project.UnusedProjects)
        {
            sb.AppendLine($"  unused project: {projectRef}");
        }

        foreach (string packageRef in project.UnusedPackages)
        {
            sb.AppendLine($"  unused package: {packageRef} [weak — verify; may be analyzer/generator/runtime-only]");
        }
    }

    private static void AppendFooter(StringBuilder sb, UnusedReferencesResult result)
    {
        int projectHits = result.Projects.Sum(p => p.UnusedProjects.Count);
        int packageHits = result.Projects.Sum(p => p.UnusedPackages.Count);
        int projectsWithHits = result.Projects.Count;

        List<string> parts = [];
        if (result.Kind != UnusedReferencesKind.Packages)
        {
            parts.Add($"{projectHits} unused project {(projectHits == 1 ? "reference" : "references")}");
        }

        if (result.Kind != UnusedReferencesKind.Projects)
        {
            parts.Add($"{packageHits} unused package {(packageHits == 1 ? "reference" : "references")}");
        }

        var countsPhrase = String.Join(", ", parts);
        string projectsPhrase = projectsWithHits == 1 ? "1 project" : $"{projectsWithHits} projects";
        sb.Append($"{countsPhrase}, across {projectsPhrase}.");
    }

    private static bool HasPackageHits(UnusedReferencesResult result) =>
        result.Kind != UnusedReferencesKind.Projects && result.Projects.Any(p => p.UnusedPackages.Count > 0);

    private static string DescribeKind(UnusedReferencesKind kind) => kind switch
    {
        UnusedReferencesKind.Projects => "project",
        UnusedReferencesKind.Packages => "package",
        _ => "project or package"
    };
}
