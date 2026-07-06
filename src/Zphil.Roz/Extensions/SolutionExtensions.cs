using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Extensions;

/// <summary>
///     Extension methods for <see cref="Solution" />.
/// </summary>
internal static class SolutionExtensions
{
    /// <summary>
    ///     Removes <see cref="UnresolvedAnalyzerReference" /> and <see cref="UnresolvedMetadataReference" />
    ///     instances from all projects, returning counts of each type removed.
    /// </summary>
    /// <remarks>
    ///     Called once at solution load to normalize the workspace. Unresolved analyzer references
    ///     cause Roslyn cross-project traversal APIs (SymbolFinder, Renamer) to crash with a switch
    ///     expression failure. Unresolved metadata references are stripped defensively for the same reason.
    /// </remarks>
    public static (Solution Solution, int AnalyzerCount, int MetadataCount, HashSet<ProjectId> AffectedProjects) StripUnresolvedReferences(this Solution solution)
    {
        var analyzerCount = 0;
        var metadataCount = 0;
        HashSet<ProjectId> affected = [];

        foreach (Project project in solution.Projects.ToList())
        {
            foreach (AnalyzerReference analyzerRef in project.AnalyzerReferences)
            {
                if (analyzerRef is UnresolvedAnalyzerReference)
                {
                    solution = solution.RemoveAnalyzerReference(project.Id, analyzerRef);
                    analyzerCount++;
                    affected.Add(project.Id);
                }
            }

            foreach (MetadataReference metadataRef in project.MetadataReferences)
            {
                if (metadataRef is UnresolvedMetadataReference)
                {
                    solution = solution.RemoveMetadataReference(project.Id, metadataRef);
                    metadataCount++;
                    affected.Add(project.Id);
                }
            }
        }

        return (solution, analyzerCount, metadataCount, affected);
    }

    /// <summary>
    ///     Enumerates each distinct non-null source document across <paramref name="projects" />,
    ///     paired as (absolute, normalized-relative-to-<paramref name="solutionDir" />), with build
    ///     output (<c>obj/</c>, <c>bin/</c>) excluded. Relative paths use forward slashes.
    /// </summary>
    /// <remarks>
    ///     Distinct by <see cref="Document.FilePath" />: a file linked into multiple projects (or
    ///     present under several TFMs) yields one entry.
    /// </remarks>
    public static IEnumerable<DocumentPathEntry> EnumerateSourceDocumentPaths(
        this IEnumerable<Project> projects, string solutionDir) =>
        projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath is not null)
            .DistinctBy(d => d.FilePath)
            .Select(d => new DocumentPathEntry(
                d.FilePath!,
                Path.GetRelativePath(solutionDir, d.FilePath!).Replace('\\', '/')))
            .Where(e => !WorkspaceManager.IsBuildOutputPath(e.NormalizedRelativePath));

    /// <summary>
    ///     Whole-solution convenience overload of
    ///     <see cref="EnumerateSourceDocumentPaths(IEnumerable{Project}, string)" />.
    /// </summary>
    public static IEnumerable<DocumentPathEntry> EnumerateSourceDocumentPaths(
        this Solution solution, string solutionDir) =>
        solution.Projects.EnumerateSourceDocumentPaths(solutionDir);
}
