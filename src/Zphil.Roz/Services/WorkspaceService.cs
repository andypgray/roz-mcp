using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;
using ProjectInfo = Zphil.Roz.Models.ProjectInfo;

namespace Zphil.Roz.Services;

/// <summary>
///     Service encapsulating workspace information gathering: project details, NuGet packages, TFMs.
/// </summary>
internal sealed class WorkspaceService(WorkspaceManager workspaceManager)
{
    // The minor part is optional: SDK .NET Core/5+ defines NET8_0/NETCOREAPP3_1/NETSTANDARD2_0,
    // but .NET Framework defines NET48/NET472/NET20 with no underscore. Anchored $ still excludes
    // NET48_OR_GREATER, NETFRAMEWORK, and bare NET/NETCOREAPP.
    private static readonly Regex TfmRegex = new(@"^NET(STANDARD|COREAPP)?\d+(_\d+)?$", RegexOptions.Compiled);

    /// <summary>
    ///     Returns structured information about the loaded solution, optionally filtered to matching projects.
    /// </summary>
    public async Task<WorkspaceInfoResult> GetInfoAsync(string? project = null, CancellationToken ct = default)
    {
        Solution solution = await workspaceManager.GetSolutionAsync(ct);

        string solutionPath = workspaceManager.SolutionPath ?? "(unknown)";
        string solutionName = Path.GetFileNameWithoutExtension(solutionPath);
        List<Project> projects = solution.Projects.OrderBy(p => p.Name).ToList();

        List<ProjectInfo> projectInfos = new();
        var totalDocs = 0;
        foreach (Project proj in projects)
        {
            int docCount = proj.Documents
                .Count(d => !ProjectExtensions.IsGeneratedFile(d.FilePath));
            totalDocs += docCount;
            string? tfm = GetTargetFramework(proj);
            string? langVersion = GetLanguageVersion(proj);
            string? nullable = GetNullableContext(proj);
            string? projectType = proj.DetectProjectType();
            projectInfos.Add(new ProjectInfo(proj.Name, projectType, proj.Language, tfm, langVersion, nullable, docCount));
        }

        List<ProjectDependencyInfo> projectDependencies = BuildProjectDependencies(solution, projects);

        if (project is not null)
        {
            // Info-style tool: deliberately silent-empty when the filter matches no projects,
            // unlike action tools which throw via Solution.FilterByProjectName.
            List<ProjectInfo> filtered = projectInfos
                .Where(p => p.Name.Contains(project, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (filtered.Count > 0)
            {
                HashSet<string> filteredNames = filtered.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                projectDependencies = projectDependencies
                    .Where(d => filteredNames.Contains(d.Name))
                    .ToList();
                totalDocs = filtered.Sum(p => p.DocCount);
                projectInfos = filtered;
            }
            else
            {
                projectInfos = [];
            }
        }

        return new WorkspaceInfoResult(solutionName, solutionPath, projectInfos, totalDocs, projectDependencies);
    }

    /// <summary>
    ///     Reloads the workspace and returns a summary.
    /// </summary>
    public async Task<ReloadResult> ReloadAndSummarizeAsync(
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
    {
        // The diagnostic baseline is cleared automatically: DiagnosticBaselineManager registers
        // ClearBaseline via WorkspaceManager.RegisterBeforeReload, which ReloadAsync fires.
        await workspaceManager.ReloadAsync(progress, ct);
        Solution solution = await workspaceManager.GetSolutionAsync(ct);
        List<Project> projects = solution.Projects.OrderBy(p => p.Name).ToList();
        int totalDocs = CountDocuments(projects);
        return new ReloadResult(projects.Count, totalDocs);
    }

    internal static int CountDocuments(IEnumerable<Project> projects) => projects.Sum(p => p.Documents.Count(d => !ProjectExtensions.IsGeneratedFile(d.FilePath)));

    internal static string? GetTargetFramework(Project project)
    {
        if (project.ParseOptions is not CSharpParseOptions csOptions)
        {
            return null;
        }

        string? tfm = TfmFromPreprocessorSymbols(csOptions.PreprocessorSymbolNames);
        if (tfm is not null)
        {
            return tfm;
        }

        // Non-SDK legacy projects don't auto-define NETxx preprocessor symbols, so the symbol
        // scan returns null. Fall back to the real TFM (<TargetFrameworkVersion>v4.8</...>)
        // from the csproj. Forgiving: a missing/malformed csproj yields null, not a crash.
        XDocument? doc = ProjectExtensions.TryLoadCsproj(project);
        return doc is null ? null : ProjectExtensions.GetTargetFrameworkFromCsproj(doc);
    }

    /// <summary>
    ///     Maps the first .NET TFM preprocessor symbol to its lowercase moniker:
    ///     <c>NET8_0</c>→<c>net8.0</c>, <c>NET48</c>→<c>net48</c>, <c>NETSTANDARD2_0</c>→<c>netstandard2.0</c>.
    /// </summary>
    /// <remarks>
    ///     Returns null when no symbol matches <see cref="TfmRegex" />.
    /// </remarks>
    internal static string? TfmFromPreprocessorSymbols(IEnumerable<string> symbols)
    {
        string? tfmSymbol = symbols.FirstOrDefault(s => TfmRegex.IsMatch(s));
        return tfmSymbol?.ToLowerInvariant().Replace("_", ".");
    }

    internal static string? GetLanguageVersion(Project project)
    {
        if (project.ParseOptions is not CSharpParseOptions csOptions)
        {
            return null;
        }

        return csOptions.LanguageVersion.ToDisplayString();
    }

    internal static string? GetNullableContext(Project project)
    {
        if (project.CompilationOptions is not CSharpCompilationOptions csOptions)
        {
            return null;
        }

        return csOptions.NullableContextOptions switch
        {
            NullableContextOptions.Disable => "disable",
            NullableContextOptions.Enable => "enable",
            NullableContextOptions.Warnings => "warnings",
            NullableContextOptions.Annotations => "annotations",
            _ => null
        };
    }

    private static List<ProjectDependencyInfo> BuildProjectDependencies(
        Solution solution, List<Project> projects)
    {
        // Build forward dependencies
        Dictionary<string, List<string>> forwardDeps = new(StringComparer.Ordinal);
        foreach (Project project in projects)
        {
            List<string> refs = project.ProjectReferences
                .Select(r => solution.GetProject(r.ProjectId)?.Name)
                .Where(n => n is not null)
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            forwardDeps[project.Name] = refs;
        }

        // Build reverse dependencies by inverting the forward graph
        Dictionary<string, List<string>> reverseDeps = new(StringComparer.Ordinal);
        foreach (Project project in projects)
        {
            reverseDeps[project.Name] = [];
        }

        foreach ((string projectName, List<string> deps) in forwardDeps)
        {
            foreach (string dep in deps)
            {
                if (reverseDeps.TryGetValue(dep, out List<string>? dependents))
                {
                    dependents.Add(projectName);
                }
            }
        }

        return projects
            .Select(p => new ProjectDependencyInfo(
                p.Name,
                forwardDeps[p.Name],
                reverseDeps[p.Name].OrderBy(n => n, StringComparer.Ordinal).ToList()))
            .ToList();
    }
}
