using Microsoft.CodeAnalysis;
using Zphil.Roz.Enums;

namespace Zphil.Roz.Models;

/// <summary>
///     The compiler-error difference between two immutable <see cref="Solution" /> snapshots (an edit's
///     "before" and "after"), scoped to the changed projects plus everything that transitively depends
///     on them.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Introduced" /> carries real <see cref="Diagnostic" /> objects (precedent:
///         <see cref="DiagnosticsResult" />), deduped by <see cref="DiagnosticKey" /> — the same
///         line-free identity <c>get_diagnostics incremental</c> uses, so the delta survives the line
///         shifts an edit causes. <see cref="ResolvedCount" /> counts errors that were present before and
///         are gone after.
///     </para>
///     <para>
///         <see cref="ScopeProjects" /> lists the distinct project names in the recompiled cone
///         (multi-TFM entries collapsed). <see cref="UncoveredFiles" /> names changed files that are not
///         part of any loaded project — the compiler cannot see them, so they carry no delta coverage.
///     </para>
/// </remarks>
internal sealed record DiagnosticsDelta(
    IReadOnlyList<Diagnostic> Introduced,
    int ResolvedCount,
    IReadOnlyList<string> ScopeProjects,
    string SolutionDir,
    IReadOnlyList<string>? UncoveredFiles = null);

/// <summary>
///     The verification outcome attached to a mutating tool's result.
/// </summary>
/// <remarks>
///     <see cref="Delta" /> is null only when <see cref="SkippedReason" /> is set (a no-op batch changed
///     nothing to verify). <see cref="Committed" /> is true for <see cref="VerifyMode.Delta" /> (files
///     were written) and false for <see cref="VerifyMode.DryRun" /> (nothing written).
/// </remarks>
internal sealed record EditVerification(
    VerifyMode Mode,
    DiagnosticsDelta? Delta,
    bool Committed,
    string? SkippedReason = null);

/// <summary>
///     The full outcome of an <c>edit_symbol</c> batch: the per-op results plus the batch-level
///     verification (null when <c>verify=None</c>). Keeping both on one record lets the service stay
///     the orchestration point and the tool stay thin.
/// </summary>
internal sealed record EditSymbolBatchOutcome(
    IReadOnlyList<EditSymbolOpResult> Ops,
    EditVerification? Verification);

/// <summary>
///     The outcome of the fork-side persist+verify contract
///     (<see cref="Services.EditVerificationService.FinalizeForkAsync" />), shared by the two tools that
///     produce a complete <see cref="Solution" /> fork rather than an <c>EditSession</c> —
///     <c>rename_symbol</c> and <c>apply_code_fix</c>. <see cref="Verification" /> is null for
///     <see cref="VerifyMode.None" />.
/// </summary>
/// <remarks>
///     <see cref="ChangedDocs" /> is a mutable <see cref="List{T}" /> on purpose: <c>rename_symbol</c>'s
///     file-move step swaps the old declaring-file path for the new one after the commit, so the caller
///     mutates the list in place before building its result record.
/// </remarks>
internal sealed record ForkFinalizeOutcome(
    List<string> ChangedDocs,
    EditVerification? Verification);

/// <summary>
///     The full outcome of a <c>replace_content</c> batch: the per-op results plus the batch-level
///     verification (null when <c>verify=None</c>).
/// </summary>
internal sealed record ReplaceContentBatchOutcome(
    IReadOnlyList<ReplaceContentResult> Ops,
    EditVerification? Verification);
