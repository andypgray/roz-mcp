using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Zphil.Roz.Enums;

namespace Zphil.Roz.Models;

/// <summary>
///     A parsed <c>newSignature</c> descriptor: the Roslyn <see cref="ParameterListSyntax" /> plus a
///     per-parameter projection (<see cref="SignatureParameter" />) that carries the fields the delta
///     and rewrite planner need without re-walking syntax.
/// </summary>
internal sealed record ParsedSignature(ParameterListSyntax List, IReadOnlyList<SignatureParameter> Parameters);

/// <summary>
///     One parameter of a proposed <c>newSignature</c>. <see cref="ResolvedType" /> is bound lazily
///     against the target's declaration scope (via the <see cref="Symbols.TypeNameResolver" /> pattern)
///     so keywords, aliases, and generics resolve exactly as they would if written at the declaration.
/// </summary>
internal sealed record SignatureParameter(
    string Name,
    TypeSyntax TypeSyntax,
    string TypeText,
    RefKind RefKind,
    bool IsParams,
    bool HasExplicitDefault,
    string? DefaultText,
    int Ordinal)
{
    /// <summary>The bound type symbol, or null before <c>SignatureDeltaComputer</c> resolves it.</summary>
    public ITypeSymbol? ResolvedType { get; init; }
}

/// <summary>
///     The difference between a method's current parameters and a proposed <c>newSignature</c>,
///     matched by name (case-sensitive). Drives both the impact classifier and the apply-gate.
/// </summary>
/// <remarks>
///     <see cref="Retyped" /> holds kept-name parameters whose type OR <see cref="RefKind" /> differs.
///     <see cref="NewOrdinalByOldOrdinal" /> maps every name-matched parameter (kept and retyped) from
///     its old ordinal to its new one — keyed by ordinal, not parameter symbol, because a call site may
///     bind any member of the slot family (interface member, base virtual) whose parameter symbols
///     differ from the anchor's while sharing arity and order. <see cref="IsDeterministicSubset" /> is
///     the gate the apply tool enforces: add-with-default, remove, and reorder are mechanical; retype
///     and params-shape changes are analysis-only in v1.
/// </remarks>
internal sealed record SignatureDelta(
    IReadOnlyList<IParameterSymbol> Removed,
    IReadOnlyList<SignatureParameter> Added,
    IReadOnlyList<(IParameterSymbol Old, SignatureParameter New)> Retyped,
    IReadOnlyDictionary<int, int> NewOrdinalByOldOrdinal,
    bool Reordered,
    bool TouchesParams)
{
    /// <summary>
    ///     True when the change is limited to the mechanically-appliable subset: no retype/RefKind
    ///     change, no <c>params</c>-shape change, and every added parameter has an explicit default.
    /// </summary>
    public bool IsDeterministicSubset =>
        Retyped.Count == 0 && !TouchesParams && Added.All(a => a.HasExplicitDefault);
}

/// <summary>
///     The full override/interface slot family whose declarations must change in lockstep with the
///     target: source members (the target plus its overrides/implementations and partial siblings)
///     and the upward chain (overridden methods / interface members). <see cref="ExtendsIntoMetadata" />
///     is true when the slot reaches a base/interface declared in metadata — the guard case.
/// </summary>
internal sealed record SignatureFamily(
    IReadOnlyList<IMethodSymbol> SourceMembers,
    IReadOnlyList<IMethodSymbol> UpwardMembers,
    bool ExtendsIntoMetadata);

/// <summary>
///     The precise-mode classification of every reference site against a proposed signature change,
///     plus any cross-cutting notes (upward lockstep, rename suggestion, v1-scope caveats).
/// </summary>
internal sealed record SignatureImpact(IReadOnlyList<SiteVerdict> Sites, IReadOnlyList<string> Notes);

/// <summary>
///     A single reference site's verdict from <see cref="Services.SignatureImpactAnalyzer" />, before the
///     service maps it into its own <c>ClassifiedSite</c>. A null verdict (nameof) is filtered upstream.
/// </summary>
internal readonly record struct SiteVerdict(ReferenceLocation Loc, ImpactSeverity Severity, string Reason);

/// <summary>
///     The plan for rewriting one call site's argument list under a deterministic signature change:
///     the new argument list, or a <see cref="RewriteBlocker" /> naming why it cannot be mechanically
///     rewritten. <see cref="DroppedArgs" /> lists the expressions dropped for removed parameters (the
///     apply-gate checks them against a side-effect-free whitelist).
/// </summary>
internal sealed record RewritePlan(
    ArgumentListSyntax? NewArgs,
    RewriteBlocker? Blocker,
    IReadOnlyList<ArgumentSyntax> DroppedArgs);

/// <summary>
///     Why a call site cannot be mechanically rewritten under a deterministic signature change.
/// </summary>
internal enum RewriteBlocker
{
    /// <summary>An added parameter has no default, so the caller must supply a value.</summary>
    NeedsCallerValue,

    /// <summary>The change touches a <c>params</c> parameter — out of the deterministic subset.</summary>
    TouchesParamsArray,

    /// <summary>A reduced extension-method call whose change touches the receiver (parameter 0).</summary>
    TouchesReceiverParam,

    /// <summary>The site passes a <c>params</c> parameter in expanded (comma-separated) form.</summary>
    ExpandedParamsForm
}
