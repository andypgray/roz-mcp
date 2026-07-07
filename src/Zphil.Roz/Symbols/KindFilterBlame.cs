using Microsoft.CodeAnalysis;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;

namespace Zphil.Roz.Symbols;

/// <summary>
///     Detects when a <see cref="SymbolicKind" /> filter is the sole cause of an empty
///     name-search result and produces a user-facing hint listing the kinds that were
///     dropped, so callers can surface a targeted "exists as ..." message instead of a
///     generic "not found".
/// </summary>
internal static class KindFilterBlame
{
    /// <summary>
    ///     Returns the distinct <see cref="SymbolicKind" />s of <paramref name="nameMatches" />
    ///     when the kind filter is the sole reason the result set is empty.
    /// </summary>
    /// <remarks>
    ///     Returns null when the filter wasn't applied, when the name itself didn't match
    ///     anything, or when some matches survived the filter.
    /// </remarks>
    public static SymbolicKind[]? GetDroppedKinds<TSymbol>(
        IReadOnlyCollection<TSymbol> nameMatches, int kindMatchCount, SymbolicKind? kind)
        where TSymbol : class, ISymbol
    {
        if (!kind.HasValue || nameMatches.Count == 0 || kindMatchCount > 0)
        {
            return null;
        }

        return nameMatches
            .Select(s => s.ToSymbolicKind())
            .Where(k => k.HasValue)
            .Select(k => k!.Value)
            .Distinct()
            .OrderBy(k => k)
            .ToArray();
    }

    /// <summary>
    ///     Renders the canonical hint phrase for an empty result set caused by a kind filter,
    ///     suitable for appending (after a newline) to a "no symbols found" error message.
    /// </summary>
    public static string FormatHint(string symbolName, IReadOnlyList<SymbolicKind> droppedKinds) =>
        $"\"{symbolName}\" exists as {String.Join(", ", droppedKinds)} — drop the kind filter or use a different kind.";
}
