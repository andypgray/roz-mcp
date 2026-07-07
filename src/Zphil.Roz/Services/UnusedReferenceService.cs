using Microsoft.CodeAnalysis;
using ModelContextProtocol;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;

namespace Zphil.Roz.Services;

/// <summary>
///     Detects <c>&lt;ProjectReference&gt;</c> and <c>&lt;PackageReference&gt;</c> entries that are
///     declared in csproj but unused by source. Uses
///     <see cref="Compilation.GetUsedAssemblyReferences(CancellationToken)" /> as the ground truth.
/// </summary>
/// <remarks>
///     <para>
///         Project-reference detection is confident: a project reference contributes one
///         <see cref="CompilationReference" /> to the dependent project's compilation, and absence
///         from the used-set means no source in the dependent project references a type from it.
///     </para>
///     <para>
///         Package-reference detection is a weak signal: packages may ship analyzers, source
///         generators, MSBuild targets, or runtime-only dependencies whose absence from
///         <see cref="Compilation.References" /> doesn't mean they're unused. Analyzer-only packages
///         don't appear in <c>MetadataReferences</c> at all (they live on
///         <see cref="Project.AnalyzerReferences" />), so they correctly never get flagged.
///     </para>
/// </remarks>
internal sealed class UnusedReferenceService(WorkspaceManager workspaceManager)
{
    public async Task<UnusedReferencesResult> GetUnusedReferencesAsync(
        UnusedReferencesKind kind = UnusedReferencesKind.Projects,
        string? project = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default)
    {
        Solution solution = await workspaceManager.GetSolutionAsync(ct);
        IReadOnlyList<Project> projectList = solution.FilterByProjectName(project);

        ProjectUnusedReferences[] perProject =
            await AnalyzeProjectsAsync(projectList, kind, progress, ct);

        List<ProjectUnusedReferences> withHits = perProject
            .Where(p => p.UnusedProjects.Count > 0
                        || p.UnusedPackages.Count > 0
                        || p.AnalysisError is not null)
            .OrderBy(p => p.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new UnusedReferencesResult(kind, project, withHits);
    }

    private static async Task<ProjectUnusedReferences[]> AnalyzeProjectsAsync(
        IReadOnlyList<Project> projects, UnusedReferencesKind kind,
        IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        var completed = 0;
        ProjectUnusedReferences[] results = new ProjectUnusedReferences[projects.Count];

        await Task.WhenAll(projects.Select(async (project, index) =>
        {
            results[index] = await AnalyzeProjectAsync(project, kind, ct);
            int done = Interlocked.Increment(ref completed);
            progress?.Report(new ProgressNotificationValue
            {
                Progress = done,
                Total = projects.Count,
                Message = $"Analyzed {done}/{projects.Count}: {project.Name}"
            });
        }));

        return results;
    }

    private static async Task<ProjectUnusedReferences> AnalyzeProjectAsync(
        Project project, UnusedReferencesKind kind, CancellationToken ct)
    {
        Compilation? compilation = await project.GetCompilationAsync(ct);
        if (compilation is null)
        {
            return new ProjectUnusedReferences(project.Name, [], []);
        }

        // Compare references directly — sidesteps cross-compilation symbol-equality pitfalls
        // (a project's IAssemblySymbol viewed via a CompilationReference is a different instance
        // than the same project's IAssemblySymbol from its own compilation).
        // Roslyn throws InvalidOperationException when the compilation has errors that prevent
        // reasoning about used references — capture per-project so a single bad project doesn't
        // fault the whole call. OperationCanceledException is not InvalidOperationException, so
        // it still propagates.
        HashSet<MetadataReference> usedReferences;
        try
        {
            usedReferences = [..compilation.GetUsedAssemblyReferences(ct)];
        }
        catch (InvalidOperationException ex)
        {
            return new ProjectUnusedReferences(project.Name, [], [], ex.Message);
        }

        IReadOnlyList<string> unusedProjects = kind == UnusedReferencesKind.Packages
            ? []
            : FindUnusedProjectReferences(project, compilation, usedReferences);

        IReadOnlyList<string> unusedPackages = kind == UnusedReferencesKind.Projects
            ? []
            : FindUnusedPackageReferences(compilation, usedReferences);

        return new ProjectUnusedReferences(project.Name, unusedProjects, unusedPackages);
    }

    private static IReadOnlyList<string> FindUnusedProjectReferences(
        Project project, Compilation compilation, HashSet<MetadataReference> usedReferences)
    {
        // Each ProjectReference appears in compilation.References as a CompilationReference
        // whose .Compilation.Assembly identity matches the referenced project's assembly.
        Dictionary<string, CompilationReference> referenceByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (CompilationReference compRef in compilation.References.OfType<CompilationReference>())
        {
            string name = compRef.Compilation.Assembly.Identity.Name;
            referenceByName[name] = compRef;
        }

        List<string> unused = [];
        foreach (ProjectReference reference in project.ProjectReferences)
        {
            Project? referenced = project.Solution.GetProject(reference.ProjectId);
            if (referenced is null)
            {
                continue;
            }

            string assemblyName = referenced.AssemblyName;
            if (!referenceByName.TryGetValue(assemblyName, out CompilationReference? compRef))
            {
                continue;
            }

            if (!usedReferences.Contains(compRef))
            {
                unused.Add(referenced.Name);
            }
        }

        return unused
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> FindUnusedPackageReferences(
        Compilation compilation, HashSet<MetadataReference> usedReferences)
    {
        Dictionary<string, bool> packageUsage = new(StringComparer.OrdinalIgnoreCase);

        foreach (MetadataReference reference in compilation.References)
        {
            if (reference is not PortableExecutableReference per || per.FilePath is null)
            {
                continue;
            }

            if (ProjectExtensions.IsFrameworkReference(per.FilePath))
            {
                continue;
            }

            string? packageId = NuGetPathExtractor.TryGetPackageId(per.FilePath);
            if (packageId is null)
            {
                continue;
            }

            bool isUsed = usedReferences.Contains(per);

            // OR-fold across multiple DLLs from the same package — a package counts as used
            // when any of its assemblies is used.
            packageUsage[packageId] = packageUsage.TryGetValue(packageId, out bool prior)
                ? prior || isUsed
                : isUsed;
        }

        return packageUsage
            .Where(kvp => !kvp.Value)
            .Select(kvp => kvp.Key)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
