using Microsoft.CodeAnalysis.FindSymbols;
using Zphil.Roz.Enums;

namespace Zphil.Roz.Models;

/// <summary>
///     A single reference site classified against a proposed change: where it is, its source
///     snippet, and the <see cref="ImpactSeverity" /> verdict with a human-readable reason.
/// </summary>
/// <remarks>
///     Mirrors <see cref="ReferenceLocationWithContext" /> (location + snippet + project) and adds
///     the per-site verdict, so the impact formatter can reuse the reference snippet renderer.
/// </remarks>
internal sealed record ImpactSite(
    ReferenceLocation Loc,
    string[] Lines,
    int StartLineNumber,
    string? ProjectName,
    ImpactSeverity Severity,
    string Reason);

/// <summary>
///     Blast-radius report for a proposed change to a single symbol: the classified sites plus
///     per-project/per-file distribution and per-severity tallies computed over the full result
///     set (before truncation), so the summary line is accurate even when <see cref="Sites" /> is
///     truncated for display.
/// </summary>
/// <remarks>
///     <see cref="Target" /> is the rendered change target (the new type for <c>TypeChange</c>, the
///     new accessibility keyword for <c>AccessibilityNarrow</c>, the normalized parameter list for a
///     precise <c>SignatureChange</c>), or <c>null</c> for changes with no target. <see cref="Notes" />
///     carries cross-cutting caveats (orphaned overrides for <c>RemoveSymbol</c>, the override/interface
///     lockstep note and rename suggestion for a precise <c>SignatureChange</c>, the coarse disclaimer
///     when <c>newSignature</c> is omitted) not tied to a single site.
/// </remarks>
internal sealed record ImpactAnalysisResult(
    string SymbolName,
    SymbolQualifiers Qualifiers,
    ChangeKind ChangeKind,
    string? Target,
    List<ImpactSite> Sites,
    string SolutionDir,
    int TotalCount,
    IReadOnlyList<ProjectDistributionEntry>? Distribution,
    IReadOnlyList<FileDistributionEntry>? FileDistribution,
    int CompatibleCount,
    int RequiresUpdateCount,
    int UnsafeCount,
    int ExcludedTestCount,
    int IncludedTestCount,
    IReadOnlyList<string>? Notes);
