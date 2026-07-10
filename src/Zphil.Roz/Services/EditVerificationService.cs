using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using ModelContextProtocol;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;

namespace Zphil.Roz.Services;

/// <summary>
///     Computes the compiler-error delta an edit introduces, comparing two immutable
///     <see cref="Solution" /> snapshots (before and after). Backs the <c>verify=Delta</c> /
///     <c>verify=DryRun</c> modes on the mutating tools.
/// </summary>
/// <remarks>
///     <para>
///         The scope is the changed projects plus everything that transitively depends on them
///         (<see cref="ProjectDependencyGraph.GetProjectsThatTransitivelyDependOnThisProject" />) —
///         cross-project breakage is the whole point. Per project, both snapshots are compiled and their
///         <em>compiler</em> errors (no analyzers) are diffed by <see cref="DiagnosticKey" />, restricted
///         to in-source, non-generated locations. Before and after sides run concurrently; both are
///         immutable snapshots and Roslyn memoizes compilation per snapshot, so the before side is nearly
///         free post-warmup.
///     </para>
///     <para>
///         Two public entry points own the commit→verify composition so no consumer hand-rolls it:
///         <see cref="FinalizeAsync" /> for the session tools (<c>edit_symbol</c> / <c>replace_content</c>)
///         and <see cref="FinalizeForkAsync" /> for the tools that produce a complete <see cref="Solution" />
///         fork (<c>rename_symbol</c> / <c>apply_code_fix</c>). Both share the <see cref="SkippedNoContent" />
///         contract for an empty change set.
///     </para>
///     <para>
///         This state is ephemeral and entirely separate from <see cref="DiagnosticBaselineManager" />,
///         which owns the persistent baseline behind <c>get_diagnostics incremental</c>.
///     </para>
/// </remarks>
internal sealed class EditVerificationService(WorkspaceManager workspaceManager)
{
    /// <summary>
    ///     Finalizes a session-based edit batch (<c>edit_symbol</c> / <c>replace_content</c>). Returns
    ///     null for <see cref="VerifyMode.None" /> (no verification requested) and a skipped result when
    ///     the batch changed nothing. For <see cref="VerifyMode.Delta" /> the batch is committed to disk
    ///     <em>before</em> the delta is computed, so a faulting verification can never hold the edit
    ///     hostage.
    /// </summary>
    public async Task<EditVerification?> FinalizeAsync(
        EditSession? session, VerifyMode mode,
        IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        if (mode == VerifyMode.None || session is null)
        {
            return null;
        }

        if (!session.HasEffectiveChanges)
        {
            return SkippedNoContent(mode);
        }

        if (mode == VerifyMode.Delta)
        {
            await session.CommitAsync(ct);
            DiagnosticsDelta committedDelta = await ComputeCommittedDeltaAsync(
                session.BaseSolution, session.Fork, session.ChangedPaths.Count, session.UncoveredPaths, progress, ct);
            return new EditVerification(mode, committedDelta, true);
        }

        DiagnosticsDelta delta = await ComputeDeltaAsync(
            session.BaseSolution, session.Fork, session.UncoveredPaths, progress, ct);
        return new EditVerification(mode, delta, false);
    }

    /// <summary>
    ///     Finalizes an edit expressed as a complete <see cref="Solution" /> fork — the fork-side sibling of
    ///     <see cref="FinalizeAsync" />, owning persist + verify for the two tools that already hold a
    ///     post-edit <see cref="Solution" /> (<c>rename_symbol</c>, <c>apply_code_fix</c>). Persists via
    ///     <see cref="SolutionChangeWriter" /> and returns the changed docs plus the verification:
    ///     <see cref="VerifyMode.None" /> commits with a null verification; <see cref="VerifyMode.DryRun" />
    ///     collects the changes without writing and previews the delta; <see cref="VerifyMode.Delta" />
    ///     commits <em>first</em> (report, don't police) then reports the committed delta.
    /// </summary>
    /// <remarks>
    ///     An empty change set under <see cref="VerifyMode.Delta" /> / <see cref="VerifyMode.DryRun" />
    ///     short-circuits to <see cref="SkippedNoContent" /> — the same contract <see cref="FinalizeAsync" />
    ///     honors — so <see cref="ComputeCommittedDeltaAsync" /> is never called with zero committed files.
    ///     <c>uncoveredPaths</c> is always null here: fork changes come from <see cref="Solution.GetChanges" />,
    ///     so every changed document belongs to a loaded project by construction (unlike a
    ///     <c>replace_content</c> target that can sit outside the workspace).
    /// </remarks>
    public async Task<ForkFinalizeOutcome> FinalizeForkAsync(
        Solution baseSolution, Solution fork, VerifyMode mode,
        IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        if (mode == VerifyMode.DryRun)
        {
            List<(string FilePath, string Content, Encoding Encoding, string? ExpectedOriginal)> changes =
                await SolutionChangeWriter.CollectFileChangesAsync(baseSolution, fork, ct);
            List<string> docs = changes.Select(c => workspaceManager.GetRelativePath(c.FilePath)).ToList();
            if (changes.Count == 0)
            {
                return new ForkFinalizeOutcome(docs, SkippedNoContent(mode));
            }

            DiagnosticsDelta delta = await ComputeDeltaAsync(baseSolution, fork, null, progress, ct);
            return new ForkFinalizeOutcome(docs, new EditVerification(mode, delta, false));
        }

        // None and Delta both commit first — report, don't police.
        List<string> changedDocs = await SolutionChangeWriter.SaveAsync(workspaceManager, baseSolution, fork, ct);

        if (mode == VerifyMode.None)
        {
            return new ForkFinalizeOutcome(changedDocs, null);
        }

        if (changedDocs.Count == 0)
        {
            return new ForkFinalizeOutcome(changedDocs, SkippedNoContent(mode));
        }

        DiagnosticsDelta committedDelta = await ComputeCommittedDeltaAsync(
            baseSolution, fork, changedDocs.Count, null, progress, ct);
        return new ForkFinalizeOutcome(changedDocs, new EditVerification(mode, committedDelta, true));
    }

    /// <summary>
    ///     The shared "nothing changed" verification both finalize paths return when a batch under
    ///     <see cref="VerifyMode.Delta" /> / <see cref="VerifyMode.DryRun" /> produced no content change —
    ///     a skipped result carrying no delta, rather than a pointless zero-project one.
    /// </summary>
    private static EditVerification SkippedNoContent(VerifyMode mode) =>
        new(mode, null, false, "no content changed");

    /// <summary>
    ///     Computes the delta for an edit that is <em>already committed</em> to disk. A fault here must
    ///     not read as a failed edit — the agent would re-apply it — so any non-cancellation exception is
    ///     wrapped in a message stating the commit landed and naming the recovery step. Nothing is
    ///     swallowed: the wrapper rethrows with the original as <see cref="Exception.InnerException" />,
    ///     so the crash signal survives <c>GlobalCallToolFilter</c>'s logging. Cancellation is not
    ///     wrapped — a cancelled request delivers no response.
    /// </summary>
    private async Task<DiagnosticsDelta> ComputeCommittedDeltaAsync(
        Solution baseSolution, Solution fork, int committedFileCount, IReadOnlyList<string>? uncoveredPaths,
        IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        try
        {
            return await ComputeDeltaAsync(baseSolution, fork, uncoveredPaths, progress, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"The edit was committed ({committedFileCount} file(s) written) but the post-commit verification failed: {ex.Message} " +
                "Do not re-apply the edit; run get_diagnostics incremental=true to check for new errors.",
                ex);
        }
    }

    /// <summary>
    ///     Computes the compiler-error delta between <paramref name="baseSolution" /> and
    ///     <paramref name="fork" /> across the changed projects' dependent cone.
    /// </summary>
    private async Task<DiagnosticsDelta> ComputeDeltaAsync(
        Solution baseSolution, Solution fork, IReadOnlyList<string>? uncoveredPaths,
        IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        // The solution directory is stable once loaded; read it from the workspace field rather than
        // GetSolutionAsync so a rename's background reload can't make the delta block behind a reload.
        string solutionDir = workspaceManager.SolutionDirectory
                             ?? await workspaceManager.GetRequiredSolutionDirectoryAsync(ct);

        List<ProjectId> scope = ComputeScope(baseSolution, fork);

        // Both sides are immutable snapshots — run them concurrently. Only the after side reports
        // progress; the before side is nearly free (memoized) and would just double the tick noise.
        Task<List<Diagnostic>> beforeTask = CollectScopeErrorsAsync(baseSolution, scope, null, ct);
        Task<List<Diagnostic>> afterTask = CollectScopeErrorsAsync(fork, scope, progress, ct);
        await Task.WhenAll(beforeTask, afterTask);

        HashSet<DiagnosticKey> beforeKeys = ToKeySet(beforeTask.Result, solutionDir);

        HashSet<DiagnosticKey> afterKeys = new();
        HashSet<DiagnosticKey> introducedSeen = new();
        List<Diagnostic> introduced = new();
        foreach (Diagnostic diag in afterTask.Result)
        {
            var key = DiagnosticKey.From(diag, solutionDir);
            afterKeys.Add(key);
            if (!beforeKeys.Contains(key) && introducedSeen.Add(key))
            {
                introduced.Add(diag);
            }
        }

        int resolvedCount = beforeKeys.Count(k => !afterKeys.Contains(k));

        IReadOnlyList<string> scopeProjects = scope
            .Select(pid => fork.GetProject(pid)?.Name)
            .Where(name => name is not null)
            .Select(name => ProjectExtensions.StripTfmSuffix(name!))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        IReadOnlyList<string>? uncoveredRel = uncoveredPaths is { Count: > 0 }
            ? uncoveredPaths.Select(p => Path.GetRelativePath(solutionDir, p)).ToList()
            : null;

        return new DiagnosticsDelta(introduced, resolvedCount, scopeProjects, solutionDir, uncoveredRel);
    }

    /// <summary>
    ///     Seeds the scope from the projects whose documents differ between the two snapshots, then
    ///     expands each seed with every project that transitively depends on it. A multi-TFM change
    ///     seeds every TFM's project (each owns its own <see cref="DocumentId" />).
    /// </summary>
    private static List<ProjectId> ComputeScope(Solution baseSolution, Solution fork)
    {
        ProjectDependencyGraph graph = fork.GetProjectDependencyGraph();
        HashSet<ProjectId> scope = new();

        foreach (ProjectChanges projectChanges in fork.GetChanges(baseSolution).GetProjectChanges())
        {
            ProjectId seed = projectChanges.ProjectId;
            scope.Add(seed);
            foreach (ProjectId dependent in graph.GetProjectsThatTransitivelyDependOnThisProject(seed))
            {
                scope.Add(dependent);
            }
        }

        return scope.ToList();
    }

    /// <summary>
    ///     Compiles every project in <paramref name="scope" /> and returns its in-source, non-generated
    ///     compiler <em>errors</em>. Analyzers are deliberately not run — the delta is about whether an
    ///     edit breaks the build, and analyzer passes would multiply the recompile cost.
    /// </summary>
    private static async Task<List<Diagnostic>> CollectScopeErrorsAsync(
        Solution solution, IReadOnlyList<ProjectId> scope,
        IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        var completed = 0;
        ImmutableArray<Diagnostic>[] perProject = await Task.WhenAll(scope.Select(async pid =>
        {
            Project? project = solution.GetProject(pid);
            Compilation? compilation = project is null ? null : await project.GetCompilationAsync(ct);

            // Null-compilation defensive skip (non-C# project, or one with no compilation) — the only
            // swallow allowed here, mirroring DiagnosticService. Any other failure faults the delta.
            ImmutableArray<Diagnostic> diagnostics =
                compilation is null ? ImmutableArray<Diagnostic>.Empty : compilation.GetDiagnostics(ct);

            int done = Interlocked.Increment(ref completed);
            progress?.Report(new ProgressNotificationValue
            {
                Progress = done,
                Total = scope.Count,
                Message = $"Verified {done}/{scope.Count}: {project?.Name ?? "(unknown)"}"
            });
            return diagnostics;
        }));

        return perProject
            .SelectMany(d => d)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Where(d => d.Location.IsInSource)
            .Where(d => !d.Location.IsInGeneratedFile())
            .ToList();
    }

    private static HashSet<DiagnosticKey> ToKeySet(IEnumerable<Diagnostic> diagnostics, string solutionDir)
    {
        HashSet<DiagnosticKey> keys = new();
        foreach (Diagnostic diag in diagnostics)
        {
            keys.Add(DiagnosticKey.From(diag, solutionDir));
        }

        return keys;
    }
}
