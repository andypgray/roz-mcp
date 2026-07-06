using Microsoft.CodeAnalysis;

namespace Zphil.Roz.Models;

/// <summary>
///     One outbound call target invoked from the analyzed body, with every call site grouped under it.
/// </summary>
/// <remarks>
///     <see cref="Sites" /> reuses <see cref="LocationWithContext" /> so the group can be wrapped as a
///     <see cref="CallerWithLineText" /> and rendered through the shared caller formatter.
///     <see cref="IsInSolution" /> is <c>true</c> when the target is declared in solution source and
///     <c>false</c> for external (BCL/NuGet/LINQ) targets — the latter are only materialised when
///     <c>includeExternalCalls</c> is set.
/// </remarks>
internal sealed record OutboundCallGroup(
    ISymbol Target,
    List<LocationWithContext> Sites,
    bool IsInSolution);

/// <summary>
///     Compound per-method analysis: signature + inbound callers (reused <see cref="FindCallersResult" />,
///     including the DI-registration fallback) + outbound in-solution callees + an external-call summary
///     + an optional overload aggregate.
/// </summary>
/// <remarks>
///     <see cref="ExternalCallCount" /> and <see cref="ExternalCallTypeNames" /> describe external callees
///     that were <em>suppressed</em> (collapsed to a summary) when <c>includeExternalCalls</c> is false;
///     when it is true those callees appear in <see cref="Outbound" /> with <see cref="OutboundCallGroup.IsInSolution" />
///     <c>= false</c> instead. <see cref="Outbound" /> is complete (body-bounded): <c>maxResults</c> caps
///     only the inbound caller list, never outbound.
/// </remarks>
internal sealed record AnalyzeMethodResult(
    ISymbol Target,
    SymbolQualifiers Qualifiers,
    string SolutionDir,
    FindCallersResult Inbound,
    IReadOnlyList<OutboundCallGroup> Outbound,
    int ExternalCallCount,
    IReadOnlyList<string> ExternalCallTypeNames,
    bool IncludeBody,
    FindOverloadsResult? Overloads = null);
