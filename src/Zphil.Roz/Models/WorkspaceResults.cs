namespace Zphil.Roz.Models;

internal sealed record WorkspaceInfoResult(
    string SolutionName,
    string SolutionPath,
    List<ProjectInfo> Projects,
    int TotalDocs,
    List<ProjectDependencyInfo> ProjectDependencies);

internal sealed record ProjectInfo(
    string Name,
    string? ProjectType,
    string Language,
    string? Tfm,
    string? LanguageVersion,
    string? NullableContext,
    int DocCount);

internal sealed record ProjectDependencyInfo(string Name, IReadOnlyList<string> Dependencies, IReadOnlyList<string> Dependents);

internal sealed record GroupedProjectInfo(
    string BaseName,
    string? ProjectType,
    string Language,
    List<string> Tfms,
    string? LanguageVersion,
    string? NullableContext,
    int DocCount);

internal sealed record ReloadResult(
    int ProjectCount,
    int TotalDocs);
