using Microsoft.CodeAnalysis;
using Zphil.Roz.Models;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Services;

/// <summary>
///     Binds each proposed parameter's type at the target's declaration scope, then computes the
///     name-matched <see cref="SignatureDelta" /> between the target's current parameters and the
///     parsed <c>newSignature</c>.
/// </summary>
/// <remarks>
///     Old→new parameters are matched by name (case-sensitive) — parameter rename is out of scope
///     (<c>rename_symbol</c> already renames parameters and named arguments). A removed+added pair with
///     identical types therefore surfaces as one removal and one addition, and the analyzer emits a
///     rename_symbol suggestion for that shape.
/// </remarks>
internal static class SignatureDeltaComputer
{
    /// <summary>
    ///     Resolves every new parameter's <see cref="SignatureParameter.ResolvedType" /> and computes the
    ///     delta. Throws <see cref="UserErrorException" /> when a parameter type cannot be bound.
    /// </summary>
    public static async Task<(ParsedSignature Bound, SignatureDelta Delta)> ComputeAsync(
        IMethodSymbol target, ParsedSignature parsed, Solution solution, CancellationToken ct)
    {
        List<SignatureParameter> bound = new(parsed.Parameters.Count);
        foreach (SignatureParameter parameter in parsed.Parameters)
        {
            ITypeSymbol? type = await TypeNameResolver.BindTypeSyntaxAsync(target, parameter.TypeSyntax, solution, ct);
            if (type is null)
            {
                throw new UserErrorException(
                    $"Could not resolve type '{parameter.TypeText}' for parameter '{parameter.Name}' in newSignature. " +
                    "Use a keyword, a simple or fully-qualified type name, or generic syntax (e.g. IReadOnlyList<Order>).");
            }

            bound.Add(parameter with { ResolvedType = type });
        }

        ParsedSignature boundSignature = parsed with { Parameters = bound };
        SignatureDelta delta = Compute(target.Parameters, bound);
        return (boundSignature, delta);
    }

    /// <summary>
    ///     Pure name-matched delta (types must already be bound). Extracted for unit testing.
    /// </summary>
    internal static SignatureDelta Compute(
        IReadOnlyList<IParameterSymbol> oldParameters, IReadOnlyList<SignatureParameter> newParameters)
    {
        Dictionary<string, SignatureParameter> newByName =
            newParameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
        Dictionary<string, IParameterSymbol> oldByName =
            oldParameters.ToDictionary(p => p.Name, StringComparer.Ordinal);

        List<IParameterSymbol> removed = oldParameters.Where(o => !newByName.ContainsKey(o.Name)).ToList();
        List<SignatureParameter> added = newParameters.Where(n => !oldByName.ContainsKey(n.Name)).ToList();

        List<(IParameterSymbol Old, SignatureParameter New)> retyped = [];
        List<(IParameterSymbol Old, SignatureParameter New)> kept = [];
        foreach (IParameterSymbol old in oldParameters)
        {
            if (!newByName.TryGetValue(old.Name, out SignatureParameter? match))
            {
                continue;
            }

            if (DiffersInShape(old, match))
            {
                retyped.Add((old, match));
            }
            else
            {
                kept.Add((old, match));
            }
        }

        // Reorder is measured over ALL name-matched parameters (kept ∪ retyped): a matched parameter
        // that moves relative to another is a reorder even if it was also retyped.
        List<(int OldOrdinal, int NewOrdinal)> matched = kept.Concat(retyped)
            .Select(m => (m.Old.Ordinal, m.New.Ordinal))
            .OrderBy(m => m.Item1)
            .ToList();

        var reordered = false;
        for (var i = 1; i < matched.Count; i++)
        {
            if (matched[i].NewOrdinal < matched[i - 1].NewOrdinal)
            {
                reordered = true;
                break;
            }
        }

        List<int> newOrderOfKept = kept.OrderBy(k => k.Old.Ordinal).Select(k => k.New.Ordinal).ToList();

        bool anyParams = oldParameters.Any(p => p.IsParams) || newParameters.Any(p => p.IsParams);
        bool nonEmptyDelta = removed.Count > 0 || added.Count > 0 || retyped.Count > 0 || reordered;
        bool touchesParams = anyParams && nonEmptyDelta;

        return new SignatureDelta(removed, added, retyped, kept, newOrderOfKept, reordered, touchesParams);
    }

    /// <summary>
    ///     True when a name-matched parameter changed in a non-mechanical way: a different type,
    ///     <see cref="RefKind" />, or <c>params</c>-ness. Such a parameter lands in
    ///     <see cref="SignatureDelta.Retyped" />, which alone makes the delta non-deterministic.
    /// </summary>
    private static bool DiffersInShape(IParameterSymbol old, SignatureParameter @new)
    {
        if (old.RefKind != @new.RefKind || old.IsParams != @new.IsParams)
        {
            return true;
        }

        return @new.ResolvedType is null || !SameType(old.Type, @new.ResolvedType);
    }

    // Same-type bridge across metadata/source and distinct compilations (mirrors ImpactAnalysisService).
    private static bool SameType(ITypeSymbol a, ITypeSymbol b) =>
        SymbolEqualityComparer.Default.Equals(a, b)
        || a.ToDisplayString() == b.ToDisplayString();
}
