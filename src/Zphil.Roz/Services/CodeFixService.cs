using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using ModelContextProtocol;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;

namespace Zphil.Roz.Services;

/// <summary>
///     Applies a registered Roslyn <see cref="CodeFixProvider" /> (via FixAll) for a single diagnostic ID
///     across a scope, producing a changed <see cref="Solution" />, persisting it, and optionally reporting
///     the compiler-error delta. Backs the <c>apply_code_fix</c> tool.
/// </summary>
/// <remarks>
///     The fixers come from the <em>target</em> solution's analyzer packages
///     (<see cref="Project.AnalyzerReferences" /> — xUnit.analyzers, StyleCop, NetAnalyzers, Roslynator,
///     …), discovered by <see cref="FixerCatalog" />. Persistence and the verify delta reuse the same
///     machinery as <c>rename_symbol</c> (<see cref="SolutionChangeWriter" /> +
///     <see cref="EditVerificationService" />). Conservative-writes design: a fixer that offers several
///     flavors, does not support FixAll, performs non-text changes, or throws is surfaced as a
///     <see cref="UserErrorException" /> rather than silently guessing or crashing.
/// </remarks>
internal sealed class CodeFixService(
    WorkspaceManager workspaceManager,
    DiagnosticBaselineManager baselineManager,
    FixerCatalog fixerCatalog,
    EditVerificationService verificationService)
{
    /// <summary>
    ///     Applies the registered fixer for <paramref name="diagnosticId" /> across the scope described by
    ///     <paramref name="project" />/<paramref name="filePaths" />/<paramref name="includeTests" />.
    /// </summary>
    public async Task<ApplyCodeFixResult> ApplyCodeFixAsync(
        string diagnosticId, string? equivalenceKey, string? project, string[]? filePaths,
        bool includeTests, VerifyMode verify, IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticId);

        // Keep get_diagnostics incremental independent — capture in every mode, like the other mutators.
        baselineManager.ScheduleBaselineCaptureIfNeeded();

        // This snapshot is both the fork base and the source of the diagnostics to fix, so their spans
        // line up — the fork is derived from the exact solution the diagnostics were collected from.
        Solution solution = await workspaceManager.GetSolutionAsync(ct);

        CodeFixProvider provider = await fixerCatalog.GetProviderAsync(diagnosticId, ct)
                                   ?? throw new UserErrorException(
                                       $"No registered code fix for '{diagnosticId}'. Fixers come from the solution's analyzer packages — "
                                       + "run get_diagnostics to see which IDs are fixable.");

        FixAllProvider fixAll = provider.GetFixAllProvider()
                                ?? throw new UserErrorException(
                                    $"The fixer for '{diagnosticId}' ({provider.GetType().FullName}) does not support fix-all — "
                                    + "apply_code_fix has no per-site fallback in v1.");

        IReadOnlyList<Project> projects = ResolveScopeProjects(solution, project, includeTests);
        HashSet<string>? fileFilter = await NormalizeFileFilterAsync(filePaths, ct);

        List<Diagnostic> all = await DiagnosticService.GetSolutionDiagnosticsAsync(projects, progress, ct);
        List<Diagnostic> toFix = all
            .Where(d => String.Equals(d.Id, diagnosticId, StringComparison.Ordinal))
            .Where(d => d.Location.IsInSource && !d.Location.IsInGeneratedFile())
            .Where(d => fileFilter is null
                        || (d.Location.SourceTree?.FilePath is { } p && fileFilter.Contains(Path.GetFullPath(p))))
            .ToList();

        if (toFix.Count == 0)
        {
            return Skipped(diagnosticId, $"no '{diagnosticId}' diagnostics in scope");
        }

        ImmutableDictionary<Document, ImmutableArray<Diagnostic>> byDocument = toFix
            .GroupBy(d => solution.GetDocument(d.Location.SourceTree))
            .Where(g => g.Key is not null)
            .ToImmutableDictionary(g => g.Key!, g => g.ToImmutableArray());

        if (byDocument.IsEmpty)
        {
            return Skipped(diagnosticId, $"no in-source '{diagnosticId}' diagnostics in scope");
        }

        (Document repDoc, Diagnostic repDiag) = SelectRepresentative(byDocument);

        Solution newSolution;
        string? fixTitle;
        try
        {
            // Steps 6-9 touch untrusted third-party fixer code, which may assume IDE-host MEF services
            // and throw. A narrow guard turns that into a friendly UserErrorException — this is a
            // deliberate exception to the no-catch-all rule, scoped to the fixer interaction only.
            string? key = await ResolveEquivalenceKeyAsync(provider, repDoc, repDiag, diagnosticId, equivalenceKey, ct);

            var diagnosticProvider = new PrecollectedDiagnosticProvider(byDocument);
            var fixAllContext = new FixAllContext(
                repDoc, provider, FixAllScope.Solution, key, [diagnosticId], diagnosticProvider, ct);

            CodeAction? action = await fixAll.GetFixAsync(fixAllContext);
            if (action is null)
            {
                return Skipped(diagnosticId, "fixer produced no changes", toFix.Count);
            }

            ImmutableArray<CodeActionOperation> ops = await action.GetOperationsAsync(ct);
            newSolution = ExtractApplyChangesOrThrow(ops, diagnosticId, provider).ChangedSolution;
            fixTitle = action.Title;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UserErrorException)
        {
            throw new UserErrorException(
                $"The fixer for '{diagnosticId}' ({provider.GetType().FullName}) failed: {ex.Message}");
        }

        return await PersistAsync(diagnosticId, fixTitle, toFix.Count, solution, newSolution, verify, progress, ct);
    }

    /// <summary>
    ///     Narrows the solution to the projects in scope: the <paramref name="project" /> substring filter
    ///     (throws when it matches none), minus test projects unless <paramref name="includeTests" />.
    /// </summary>
    private static IReadOnlyList<Project> ResolveScopeProjects(Solution solution, string? project, bool includeTests)
    {
        IReadOnlyList<Project> scoped = solution.FilterByProjectName(project);
        return includeTests ? scoped : scoped.Where(p => !p.IsTestProject()).ToList();
    }

    /// <summary>
    ///     Resolves <paramref name="filePaths" /> (globs allowed) to a set of absolute paths used to
    ///     narrow which diagnostics get fixed, or null when no file filter was supplied. An unresolvable
    ///     path is a <see cref="UserErrorException" /> — a typo must not silently fix nothing.
    /// </summary>
    private async Task<HashSet<string>?> NormalizeFileFilterAsync(string[]? filePaths, CancellationToken ct)
    {
        if (filePaths is not { Length: > 0 })
        {
            return null;
        }

        string[] expanded = await workspaceManager.ExpandGlobPatternsAsync(
            filePaths, 200, "Or scope with project=<name>.", ct);

        HashSet<string> resolved = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in expanded)
        {
            string absolute = await FilePathResolver.ResolveAgainstSolutionAsync(path, workspaceManager, ct);
            resolved.Add(Path.GetFullPath(absolute));
        }

        return resolved;
    }

    /// <summary>
    ///     Picks a deterministic representative (document, diagnostic) pair for equivalence-key discovery:
    ///     first document by path, first diagnostic by span start within it.
    /// </summary>
    private static (Document Document, Diagnostic Diagnostic) SelectRepresentative(
        ImmutableDictionary<Document, ImmutableArray<Diagnostic>> byDocument)
    {
        KeyValuePair<Document, ImmutableArray<Diagnostic>> first = byDocument
            .OrderBy(kvp => kvp.Key.FilePath, StringComparer.OrdinalIgnoreCase)
            .First();
        Diagnostic diag = first.Value.OrderBy(d => d.Location.SourceSpan.Start).First();
        return (first.Key, diag);
    }

    /// <summary>
    ///     The conservative-writes gate. Runs the fixer's registration on a representative occurrence to
    ///     enumerate the fix flavors it offers, then delegates the pure choice to
    ///     <see cref="ResolveEquivalenceKey" />.
    /// </summary>
    private static async Task<string?> ResolveEquivalenceKeyAsync(
        CodeFixProvider provider, Document doc, Diagnostic diag, string id, string? requested, CancellationToken ct)
    {
        List<(string Title, string? Key)> actions = [];
        var context = new CodeFixContext(doc, diag, (a, _) => actions.Add((a.Title, a.EquivalenceKey)), ct);
        await provider.RegisterCodeFixesAsync(context);
        return ResolveEquivalenceKey(actions, id, requested);
    }

    /// <summary>
    ///     Pure equivalence-key decision (no Roslyn interaction), extracted so the conservative-writes
    ///     gate is unit-testable: apply the requested key (validated against what the fixer offers), the
    ///     single available key, or throw listing every flavor when the choice is ambiguous — never a
    ///     silent pick between flavors.
    /// </summary>
    internal static string? ResolveEquivalenceKey(
        IReadOnlyList<(string Title, string? Key)> actions, string id, string? requested)
    {
        if (actions.Count == 0)
        {
            throw new UserErrorException($"The fixer for '{id}' registered no fixes for this occurrence.");
        }

        List<string?> keys = actions.Select(a => a.Key).Distinct().ToList();

        if (requested is not null)
        {
            return keys.Contains(requested)
                ? requested
                : throw new UserErrorException(
                    $"equivalenceKey '{requested}' is not offered for '{id}'. Available: {DescribeFlavors(actions)}");
        }

        if (keys.Count == 1)
        {
            // A lone null key from several registered actions cannot be disambiguated by FixAll
            // (BatchFixer matches actions by equivalence key), so refuse rather than fix an unintended one.
            if (keys[0] is null && actions.Count > 1)
            {
                throw new UserErrorException(
                    $"The fixer for '{id}' offers multiple fixes with no equivalence key — cannot disambiguate: {DescribeFlavors(actions)}");
            }

            return keys[0];
        }

        throw new UserErrorException(
            $"The fixer for '{id}' offers multiple fixes — pass equivalenceKey to choose: {DescribeFlavors(actions)}");
    }

    private static string DescribeFlavors(IEnumerable<(string Title, string? Key)> actions) =>
        String.Join("; ", actions.Select(a => $"\"{a.Title}\" (key={a.Key ?? "<null>"})"));

    /// <summary>
    ///     Requires the fixer's operations to be exactly one <see cref="ApplyChangesOperation" /> (a pure
    ///     text change). A second apply, or any other operation (add/rename file, open document), is
    ///     outside v1's contract and becomes a <see cref="UserErrorException" /> naming the fixer.
    /// </summary>
    private static ApplyChangesOperation ExtractApplyChangesOrThrow(
        ImmutableArray<CodeActionOperation> ops, string id, CodeFixProvider provider)
    {
        ApplyChangesOperation? applyOp = null;
        foreach (CodeActionOperation op in ops)
        {
            if (op is ApplyChangesOperation changes && applyOp is null)
            {
                applyOp = changes;
                continue;
            }

            throw new UserErrorException(
                $"The fixer for '{id}' ({provider.GetType().FullName}) performs a non-text or multi-step change "
                + $"('{op.GetType().Name}'), which apply_code_fix does not support in v1.");
        }

        return applyOp
               ?? throw new UserErrorException(
                   $"The fixer for '{id}' ({provider.GetType().FullName}) produced no applicable text change.");
    }

    /// <summary>
    ///     Persists the fix and computes the optional verify delta by delegating to the shared fork
    ///     contract (<see cref="EditVerificationService.FinalizeForkAsync" />) — the same persist+verify
    ///     path <c>rename_symbol</c> uses.
    /// </summary>
    private async Task<ApplyCodeFixResult> PersistAsync(
        string diagnosticId, string? fixTitle, int fixedCount, Solution solution, Solution newSolution,
        VerifyMode verify, IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        ForkFinalizeOutcome outcome = await verificationService.FinalizeForkAsync(solution, newSolution, verify, progress, ct);
        return new ApplyCodeFixResult(diagnosticId, fixTitle, fixedCount, outcome.ChangedDocs, outcome.Verification);
    }

    private static ApplyCodeFixResult Skipped(string diagnosticId, string reason, int fixedCount = 0) =>
        new(diagnosticId, null, fixedCount, [], null, reason);

    /// <summary>
    ///     Serves the precollected, in-scope diagnostics to the FixAll engine. For <c>Solution</c> and
    ///     <c>Project</c> scope the engine calls <see cref="GetAllDiagnosticsAsync" /> per project and
    ///     re-groups to documents itself, so a project outside the collected set simply yields nothing —
    ///     which is how the scope filter is enforced.
    /// </summary>
    private sealed class PrecollectedDiagnosticProvider(
        ImmutableDictionary<Document, ImmutableArray<Diagnostic>> byDocument)
        : FixAllContext.DiagnosticProvider
    {
        public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<Diagnostic>>(
                byDocument.TryGetValue(document, out ImmutableArray<Diagnostic> diagnostics)
                    ? diagnostics
                    : ImmutableArray<Diagnostic>.Empty);

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            // We filtered to in-source diagnostics, so there are no project-level (no-location) ones to serve.
            Task.FromResult<IEnumerable<Diagnostic>>(ImmutableArray<Diagnostic>.Empty);

        public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<Diagnostic>>(
                byDocument
                    .Where(kvp => kvp.Key.Project.Id == project.Id)
                    .SelectMany(kvp => kvp.Value)
                    .ToImmutableArray());
    }
}
