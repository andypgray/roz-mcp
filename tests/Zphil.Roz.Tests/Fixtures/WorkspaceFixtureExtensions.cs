using Microsoft.CodeAnalysis;
using Zphil.Roz.Extensions;
using Zphil.Roz.Models;
using Zphil.Roz.Presentation;
using ProjectInfo = Zphil.Roz.Models.ProjectInfo;

namespace Zphil.Roz.Tests.Fixtures;

/// <summary>
///     Test helpers that compute expected document and project counts from the loaded
///     fixture solution at test time, so assertions don't break when fixture files are
///     added or removed. The counting logic here must stay in sync with
///     <c>WorkspaceService.GetInfoAsync</c> — in particular the raw sum across projects
///     (including both TFM variants of multi-TFM projects) and the same project-name
///     filter predicate.
/// </summary>
internal static class WorkspaceFixtureExtensions
{
    /// <summary>
    ///     Returns the expected document count for the formatter footer, optionally filtered
    ///     to projects matching <paramref name="projectFilter" /> (case-insensitive substring).
    ///     Excludes generated files using <see cref="ProjectExtensions.IsGeneratedFile" />.
    ///     Sums across all projects — matches the raw sum in <c>WorkspaceService.GetInfoAsync</c>,
    ///     including both TFM variants of multi-TFM projects.
    /// </summary>
    internal static async Task<int> GetExpectedDocCountAsync(
        this WorkspaceFixture fixture, string? projectFilter = null, CancellationToken ct = default)
    {
        IEnumerable<Project> projects = await GetFilteredProjectsAsync(fixture, projectFilter, ct);
        return projects.Sum(p => p.Documents.Count(d => !ProjectExtensions.IsGeneratedFile(d.FilePath)));
    }

    /// <summary>
    ///     Returns the count of generated documents (those filtered out by
    ///     <see cref="ProjectExtensions.IsGeneratedFile" />) across all projects. Used as a
    ///     fixture invariant guard for exclusion tests.
    /// </summary>
    internal static async Task<int> GetGeneratedDocCountAsync(
        this WorkspaceFixture fixture, CancellationToken ct = default)
    {
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(ct);
        return solution.Projects.Sum(p => p.Documents.Count(d => ProjectExtensions.IsGeneratedFile(d.FilePath)));
    }

    /// <summary>
    ///     Returns the expected project counts: <c>Total</c> is the raw project count,
    ///     <c>Logical</c> is the count after multi-TFM projects are merged via
    ///     <see cref="WorkspaceInfoFormatter.GroupMultiTfmProjects" />.
    /// </summary>
    internal static async Task<(int Logical, int Total)> GetExpectedProjectCountsAsync(
        this WorkspaceFixture fixture, CancellationToken ct = default)
    {
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(ct);
        List<ProjectInfo> projectInfos = solution.Projects
            .Select(p => new ProjectInfo(p.Name, null, p.Language, null, null, null, 0))
            .ToList();
        List<GroupedProjectInfo> grouped = WorkspaceInfoFormatter.GroupMultiTfmProjects(projectInfos);
        return (grouped.Count, projectInfos.Count);
    }

    private static async Task<IEnumerable<Project>> GetFilteredProjectsAsync(
        WorkspaceFixture fixture, string? projectFilter, CancellationToken ct)
    {
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(ct);
        if (projectFilter is null)
        {
            return solution.Projects;
        }

        return solution.Projects
            .Where(p => p.Name.Contains(projectFilter, StringComparison.OrdinalIgnoreCase));
    }
}
