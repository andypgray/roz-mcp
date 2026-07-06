using Zphil.Roz.Models;

namespace Zphil.Roz.Utility;

/// <summary>
///     Computes per-file and per-project distribution summaries from a sequence of items.
/// </summary>
internal static class DistributionComputer
{
    /// <summary>
    ///     Groups items by file path and project name, producing both file-level and project-level
    ///     distribution entries sorted by descending count.
    /// </summary>
    /// <param name="items">The items to distribute.</param>
    /// <param name="solutionDir">Solution root for computing relative paths.</param>
    /// <param name="selector">Maps each item to its file path (nullable) and project name.</param>
    internal static (List<FileDistributionEntry> FileDistribution, IReadOnlyList<ProjectDistributionEntry> ProjectDistribution)
        Compute<T>(IEnumerable<T> items, string solutionDir, Func<T, (string? Path, string Project)> selector)
    {
        List<FileDistributionEntry> fileDistribution = items
            .Select(selector)
            .Where(x => x.Path is not null)
            .GroupBy(x => (x.Path!, x.Project))
            .Select(g => new FileDistributionEntry(
                Path.GetRelativePath(solutionDir, g.Key.Item1),
                g.Key.Project,
                g.Count()))
            .OrderByDescending(e => e.ReferenceCount)
            .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        IReadOnlyList<ProjectDistributionEntry> projectDistribution = fileDistribution
            .GroupBy(f => f.ProjectName)
            .Select(g => new ProjectDistributionEntry(
                g.Key,
                g.Sum(f => f.ReferenceCount),
                g.Count()))
            .OrderByDescending(e => e.ReferenceCount)
            .ToList();

        return (fileDistribution, projectDistribution);
    }
}
