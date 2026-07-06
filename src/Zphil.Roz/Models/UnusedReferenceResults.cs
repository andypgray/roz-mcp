using Zphil.Roz.Enums;

namespace Zphil.Roz.Models;

/// <summary>
///     Top-level result returned by <see cref="Services.UnusedReferenceService" />.
///     <see cref="Projects" /> only contains entries for projects with at least one hit.
/// </summary>
internal sealed record UnusedReferencesResult(
    UnusedReferencesKind Kind,
    string? ProjectFilter,
    IReadOnlyList<ProjectUnusedReferences> Projects);

/// <summary>
///     Per-project unused-reference findings. <see cref="UnusedProjects" /> is the list of project
///     references not used by source; <see cref="UnusedPackages" /> is the list of package IDs not
///     used by source (deduplicated across multiple DLLs from the same package).
///     <see cref="AnalysisError" /> is set when
///     <see cref="Microsoft.CodeAnalysis.Compilation.GetUsedAssemblyReferences" />
///     could not run for the project (e.g. the compilation has errors that prevent reasoning about
///     used references); when set, <see cref="UnusedProjects" /> and <see cref="UnusedPackages" />
///     are empty and the formatter renders an error block instead of hits.
/// </summary>
internal sealed record ProjectUnusedReferences(
    string ProjectName,
    IReadOnlyList<string> UnusedProjects,
    IReadOnlyList<string> UnusedPackages,
    string? AnalysisError = null);
