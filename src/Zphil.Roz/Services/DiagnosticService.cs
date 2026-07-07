using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using ModelContextProtocol;
using Zphil.Roz.Constants;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;

namespace Zphil.Roz.Services;

/// <summary>
///     Service encapsulating compiler diagnostic retrieval.
/// </summary>
internal sealed class DiagnosticService(
    WorkspaceManager workspaceManager,
    DiagnosticBaselineManager baselineManager,
    FixerCatalog fixerCatalog)
{
    /// <summary>
    ///     Assembly-resolution error codes: unknown type, unknown namespace, unreferenced assembly.
    ///     CS0103 (name does not exist) is deliberately excluded — it fires on plain typos too.
    /// </summary>
    private static readonly HashSet<string> AssemblyResolutionCodes =
        new(StringComparer.OrdinalIgnoreCase) { "CS0012", "CS0234", "CS0246" };

    /// <summary>
    ///     Kill-switch for analyzer execution. When set, both the file-scoped and solution-wide
    ///     paths skip <see cref="CompilationWithAnalyzers" /> and return compiler-only diagnostics —
    ///     a fallback for users hit by a misbehaving analyzer pack.
    /// </summary>
    private static readonly bool AnalyzersDisabled = RozEnvVars.DisableAnalyzers.Read();

    /// <summary>
    ///     Gets compiler diagnostics, optionally scoped to specific files and filtered by severity.
    /// </summary>
    public async Task<DiagnosticsResult> GetDiagnosticsAsync(
        string[]? filePaths = null, DiagnosticSeverity severity = DiagnosticSeverity.Warning,
        bool excludeTests = false, string[]? diagnosticIds = null, string? project = null,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
    {
        string solutionDir = await workspaceManager.GetRequiredSolutionDirectoryAsync(ct);
        Solution solution = await workspaceManager.GetSolutionAsync(ct);

        // Expand globs then resolve per-file — bogus paths surface as inline errors instead of failing the batch.
        // FilePathResolver handles redundant leading directories (e.g. "src/Foo.cs" when SolutionDirectory is already <repo>/src).
        string[]? expandedPaths = filePaths is { Length: > 0 }
            ? await workspaceManager.ExpandGlobPatternsAsync(filePaths, 50,
                "Or use project=<name> for whole-project checks.", ct)
            : null;

        string[]? resolvedPaths = null;
        List<string>? resolveErrors = null;
        if (expandedPaths is not null)
        {
            List<string> resolvedList = new(expandedPaths.Length);
            foreach (string p in expandedPaths)
            {
                try
                {
                    resolvedList.Add(await FilePathResolver.ResolveAgainstSolutionAsync(p, workspaceManager, ct));
                }
                catch (UserErrorException ex)
                {
                    (resolveErrors ??= []).Add(ex.Message);
                }
            }

            resolvedPaths = resolvedList.ToArray();
        }

        if (resolvedPaths is { Length: > 0 })
        {
            await workspaceManager.EnsureFilesFreshAsync(resolvedPaths, ct);
            solution = await workspaceManager.GetSolutionAsync(ct);
        }

        DiagnosticSeverity minSeverity = severity;

        List<Diagnostic> allDiagnostics;
        List<string>? errors = null;
        var excludedTestProjectCount = 0;
        if (expandedPaths is not null)
        {
            Task<(List<Diagnostic>? Diagnostics, string? Error)>[] fileTasks = (resolvedPaths ?? [])
                .Select(async resolvedPath =>
                {
                    Document? document = solution.GetDocumentByPath(resolvedPath);
                    if (document is null)
                    {
                        return (null, (string?)ErrorMessages.FileNotInSolution(resolvedPath));
                    }

                    SemanticModel? model = await document.GetSemanticModelAsync(ct);
                    if (model is null)
                    {
                        return (null, (string?)ErrorMessages.CouldNotAnalyze(resolvedPath));
                    }

                    return ((List<Diagnostic>?)await GetFileDiagnosticsAsync(document, model, ct), (string?)null);
                })
                .ToArray();

            (List<Diagnostic>? Diagnostics, string? Error)[] fileResults = await Task.WhenAll(fileTasks);
            allDiagnostics = fileResults.Where(r => r.Diagnostics is not null).SelectMany(r => r.Diagnostics!).ToList();

            List<string> combinedErrors = fileResults.Where(r => r.Error is not null).Select(r => r.Error!).ToList();
            if (resolveErrors is not null)
            {
                combinedErrors.AddRange(resolveErrors);
            }

            errors = combinedErrors.Count > 0 ? combinedErrors : null;
        }
        else
        {
            IReadOnlyList<Project> unfilteredList = solution.FilterByProjectName(project);
            IReadOnlyList<Project> projectList = excludeTests
                ? unfilteredList.Where(p => !p.IsTestProject()).ToList()
                : unfilteredList;
            // Count distinct stripped names so multi-TFM test projects don't inflate the hint.
            excludedTestProjectCount = excludeTests ? ProjectExtensions.CountDistinctTestProjects(unfilteredList) : 0;

            allDiagnostics = await GetSolutionDiagnosticsAsync(projectList, progress, ct);
        }

        // Project GetLineSpan() once — it's non-trivial and needed for both dedup and sorting.
        allDiagnostics = allDiagnostics
            .Where(d => d.Severity >= minSeverity)
            .Where(d => !d.Location.IsInGeneratedFile())
            .Where(d => diagnosticIds is null || diagnosticIds.Contains(d.Id, StringComparer.OrdinalIgnoreCase))
            .Select(d => (Diag: d, Span: d.Location.GetLineSpan()))
            .DistinctBy(x => (x.Span.Path, x.Span.StartLinePosition.Line, x.Diag.Id, x.Diag.GetMessage()))
            .OrderByDescending(x => x.Diag.Severity)
            .ThenBy(x => x.Span.Path)
            .Select(x => x.Diag)
            .ToList();

        string? workspaceHint = BuildWorkspaceHint(allDiagnostics);
        IReadOnlyList<FixerSummaryEntry>? fixerSummary = await BuildFixerSummaryAsync(allDiagnostics, ct);

        return new DiagnosticsResult(
            allDiagnostics, solutionDir, filePaths, severity, diagnosticIds, project, errors,
            excludedTestProjectCount, workspaceHint, fixerSummary);
    }

    /// <summary>
    ///     Intersects the diagnostics with the fixer catalog and returns ordered (id, count)
    ///     entries for IDs that have an available <see cref="CodeFixProvider" />. Returns null
    ///     when no fixable IDs are present.
    /// </summary>
    private async Task<IReadOnlyList<FixerSummaryEntry>?> BuildFixerSummaryAsync(
        IReadOnlyList<Diagnostic> diagnostics, CancellationToken ct)
    {
        if (diagnostics.Count == 0)
        {
            return null;
        }

        IReadOnlyDictionary<string, FixerInfo> fixerMap = await fixerCatalog.GetAsync(ct);
        if (fixerMap.Count == 0)
        {
            return null;
        }

        List<FixerSummaryEntry> summary = diagnostics
            .GroupBy(d => d.Id)
            .Where(g => fixerMap.ContainsKey(g.Key))
            .Select(g => new FixerSummaryEntry(g.Key, g.Count()))
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.DiagnosticId, StringComparer.Ordinal)
            .ToList();

        return summary.Count > 0 ? summary : null;
    }

    /// <summary>
    ///     Detects the "NuGet isn't restored" signature: a flood of assembly-resolution errors
    ///     (CS0012/CS0234/CS0246) that dwarfs real compile errors. Returns a one-line banner
    ///     telling the caller to run <c>dotnet build</c> for ground truth before trusting the
    ///     diagnostic list; returns <c>null</c> when the signature isn't present.
    /// </summary>
    /// <remarks>
    ///     Thresholds: ≥20 errors total AND ≥80% of them in the assembly-resolution set.
    ///     80% (not 50%) so mixed real-bug + missing-ref clusters don't false-trigger.
    /// </remarks>
    internal static string? BuildWorkspaceHint(IReadOnlyList<Diagnostic> diagnostics)
    {
        const int CountFloor = 20;
        const double RatioFloor = 0.8;

        var errorCount = 0;
        var assemblyResolutionCount = 0;
        foreach (Diagnostic d in diagnostics)
        {
            if (d.Severity != DiagnosticSeverity.Error)
            {
                continue;
            }

            errorCount++;
            if (AssemblyResolutionCodes.Contains(d.Id))
            {
                assemblyResolutionCount++;
            }
        }

        if (errorCount < CountFloor || assemblyResolutionCount < errorCount * RatioFloor)
        {
            return null;
        }

        var codes = String.Join("/", AssemblyResolutionCodes.OrderBy(c => c));
        return $"Note: {assemblyResolutionCount}/{errorCount} errors are assembly-resolution codes " +
               $"({codes}), which usually indicates NuGet isn't restored in this workspace. " +
               "Run 'dotnet build' for ground truth before trusting these diagnostics.";
    }

    /// <summary>
    ///     Gets only diagnostics that are new since the baseline was captured (before the first edit).
    /// </summary>
    public async Task<IncrementalDiagnosticsResult> GetIncrementalDiagnosticsAsync(
        string[]? filePaths = null, DiagnosticSeverity severity = DiagnosticSeverity.Warning,
        bool excludeTests = false, string[]? diagnosticIds = null, string? project = null,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
    {
        DiagnosticBaseline baseline = await baselineManager.GetOrCaptureBaselineAsync(ct);

        DiagnosticsResult current = await GetDiagnosticsAsync(filePaths, severity, excludeTests, diagnosticIds, project, progress, ct);
        string solutionDir = await workspaceManager.GetRequiredSolutionDirectoryAsync(ct);

        List<Diagnostic> newDiagnostics = new();
        HashSet<DiagnosticKey> currentKeys = new();

        foreach (Diagnostic d in current.Diagnostics)
        {
            var key = DiagnosticKey.From(d, solutionDir);
            currentKeys.Add(key);

            if (!baseline.Contains(key))
            {
                newDiagnostics.Add(d);
            }
        }

        // Scope the baseline the same way the live query is scoped (severity floor, plus files or
        // project/test). Otherwise baseline keys the live query never emits appear "resolved."
        IReadOnlyCollection<DiagnosticKey> baselineScope = await ScopeBaselineAsync(
            baseline, filePaths, severity, excludeTests, project, solutionDir, ct);

        int resolvedCount = baselineScope.Count(k => !currentKeys.Contains(k));
        int unchangedCount = baselineScope.Count - resolvedCount;

        IReadOnlyList<FixerSummaryEntry>? fixerSummary = await BuildFixerSummaryAsync(newDiagnostics, ct);

        return new IncrementalDiagnosticsResult(
            newDiagnostics, resolvedCount, unchangedCount,
            baseline.CapturedAtUtc, solutionDir, diagnosticIds, current.Errors,
            current.ExcludedTestProjectCount, current.WorkspaceHint, fixerSummary);
    }

    /// <summary>
    ///     Scopes baseline keys to match the filters applied to the current query, so the
    ///     resolved/unchanged math compares like with like.
    /// </summary>
    /// <remarks>
    ///     Severity always applies; the path scope mirrors the live query — file-scoped when
    ///     <paramref name="filePaths" /> is given, otherwise project/test-scoped. With no path
    ///     filter active, only severity narrows.
    /// </remarks>
    private async Task<IReadOnlyCollection<DiagnosticKey>> ScopeBaselineAsync(
        DiagnosticBaseline baseline, string[]? filePaths, DiagnosticSeverity minSeverity,
        bool excludeTests, string? project, string solutionDir, CancellationToken ct)
    {
        IEnumerable<DiagnosticKey> scoped = baseline.KeysAtOrAboveSeverity(minSeverity);

        HashSet<string>? scopePaths = null;
        if (filePaths is { Length: > 0 })
        {
            scopePaths = await ResolveFilePathScopeAsync(filePaths, solutionDir, ct);
        }
        else if (project is not null || excludeTests)
        {
            scopePaths = await ResolveProjectScopeAsync(project, excludeTests, solutionDir, ct);
        }

        if (scopePaths is not null)
        {
            scoped = scoped.Where(k => scopePaths.Contains(k.RelPath));
        }

        return scoped.ToList();
    }

    /// <summary>
    ///     Resolves the file filter to the set of relative paths the main query sees.
    /// </summary>
    /// <remarks>
    ///     Uses <see cref="FilePathResolver" /> so that redundant-prefix / short-tail inputs resolve
    ///     to the same absolute path — otherwise a typed <c>src/src/Foo.cs</c> would scope against a
    ///     bogus path and every baseline key would look "resolved."
    /// </remarks>
    private async Task<HashSet<string>> ResolveFilePathScopeAsync(
        string[] filePaths, string solutionDir, CancellationToken ct)
    {
        string[] expanded = await workspaceManager.ExpandGlobPatternsAsync(filePaths, 50, ct: ct);
        HashSet<string> scopePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string fp in expanded)
        {
            try
            {
                string resolved = await FilePathResolver.ResolveAgainstSolutionAsync(fp, workspaceManager, ct);
                scopePaths.Add(Path.GetRelativePath(solutionDir, resolved));
            }
            catch (UserErrorException)
            {
                // Unresolvable paths produce inline errors in the main query; drop them here
                // so they don't widen or corrupt the baseline scope.
            }
        }

        return scopePaths;
    }

    /// <summary>
    ///     Resolves the project/test filter to the relative paths of the in-scope documents,
    ///     mirroring the live query's solution-wide path (<see cref="ProjectExtensions.FilterByProjectName" />
    ///     + <see cref="ProjectExtensions.IsTestProject" />).
    /// </summary>
    private async Task<HashSet<string>> ResolveProjectScopeAsync(
        string? project, bool excludeTests, string solutionDir, CancellationToken ct)
    {
        Solution solution = await workspaceManager.GetSolutionAsync(ct);
        IReadOnlyList<Project> projects = solution.FilterByProjectName(project);
        if (excludeTests)
        {
            projects = projects.Where(p => !p.IsTestProject()).ToList();
        }

        HashSet<string> scopePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (Project p in projects)
        {
            foreach (Document doc in p.Documents)
            {
                if (doc.FilePath is not null)
                {
                    scopePaths.Add(Path.GetRelativePath(solutionDir, doc.FilePath));
                }
            }
        }

        return scopePaths;
    }

    private static ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(Project project) =>
        project.AnalyzerReferences
            .SelectMany(r => r.GetAnalyzers(project.Language))
            .ToImmutableArray();

    private static async Task<List<Diagnostic>> GetFileDiagnosticsAsync(
        Document document, SemanticModel model, CancellationToken ct)
    {
        ImmutableArray<DiagnosticAnalyzer> analyzers =
            AnalyzersDisabled ? [] : GetAnalyzers(document.Project);

        if (analyzers.IsEmpty)
        {
            return [..model.GetDiagnostics(cancellationToken: ct)];
        }

        Compilation compilation = (await document.Project.GetCompilationAsync(ct))!;
        CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers(analyzers, document.Project.AnalyzerOptions);

        ImmutableArray<Diagnostic> compilerDiags = model.GetDiagnostics(cancellationToken: ct);
        ImmutableArray<Diagnostic> analyzerSyntaxDiags =
            await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(model.SyntaxTree, ct);
        ImmutableArray<Diagnostic> analyzerSemanticDiags =
            await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, null, ct);

        return [..compilerDiags, ..analyzerSyntaxDiags, ..analyzerSemanticDiags];
    }

    internal static async Task<List<Diagnostic>> GetSolutionDiagnosticsAsync(
        IEnumerable<Project> projects, IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
    {
        List<Project> projectList = projects.ToList();
        var completed = 0;

        ImmutableArray<Diagnostic>[] perProject = await Task.WhenAll(projectList.Select(async project =>
        {
            Compilation? compilation = await project.GetCompilationAsync(ct);

            ImmutableArray<Diagnostic> diagnostics;
            if (compilation is null)
            {
                diagnostics = ImmutableArray<Diagnostic>.Empty;
            }
            else
            {
                ImmutableArray<DiagnosticAnalyzer> analyzers =
                    AnalyzersDisabled ? [] : GetAnalyzers(project);

                diagnostics = analyzers.IsEmpty
                    ? compilation.GetDiagnostics(ct)
                    : await compilation
                        .WithAnalyzers(analyzers, project.AnalyzerOptions)
                        .GetAllDiagnosticsAsync(ct);
            }

            // Progress always ticks per project so callers see N reports for N projects,
            // even when a project (e.g. an unsupported language) has no Compilation.
            int done = Interlocked.Increment(ref completed);
            progress?.Report(new ProgressNotificationValue
            {
                Progress = done,
                Total = projectList.Count,
                Message = $"Analyzed {done}/{projectList.Count}: {project.Name}"
            });
            return diagnostics;
        }));

        return perProject.SelectMany(d => d).ToList();
    }
}
