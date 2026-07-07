using Microsoft.CodeAnalysis;
using Zphil.Roz.Extensions;

namespace Zphil.Roz.Symbols;

/// <summary>
///     Merges namespace symbols from multiple compilations into a single representative
///     and collects/deduplicates source locations.
/// </summary>
internal static class SymbolMerger
{
    /// <summary>
    ///     Merges namespace symbols that appear once per compilation into a single representative
    ///     with all source locations combined.
    /// </summary>
    /// <remarks>
    ///     Non-namespace symbols pass through unchanged with a <c>null</c> location override.
    ///     Preserves original ordering — each namespace group appears at the position of its first occurrence.
    /// </remarks>
    internal static List<(ISymbol Symbol, Location[]? LocationOverride)> MergeNamespaceSymbols(
        IEnumerable<ISymbol> symbols, bool includeGenerated = false)
    {
        // Fast path: most find_symbol calls return types/methods, not namespaces.
        // Materialize once, scan for any namespace, and skip merge machinery if none found.
        List<ISymbol> materialized = symbols as List<ISymbol> ?? symbols.ToList();
        if (!materialized.Exists(s => s is INamespaceSymbol))
        {
            return materialized.Select(s => (s, (Location[]?)null)).ToList();
        }

        List<(ISymbol, Location[]?)> result = new();
        Dictionary<string, (int InsertionIndex, List<INamespaceSymbol> Symbols)> namespaceGroups = new();

        foreach (ISymbol symbol in materialized)
        {
            if (symbol is INamespaceSymbol ns)
            {
                string fqn = ns.ToDisplayString();
                if (!namespaceGroups.TryGetValue(fqn, out (int InsertionIndex, List<INamespaceSymbol> Symbols) group))
                {
                    // Reserve a slot at first occurrence; filled in the second pass below
                    group = (result.Count, []);
                    namespaceGroups[fqn] = group;
                    result.Add(default);
                }

                group.Symbols.Add(ns);
            }
            else
            {
                result.Add((symbol, null));
            }
        }

        foreach ((string _, (int insertionIndex, List<INamespaceSymbol> group)) in namespaceGroups)
        {
            Location[] mergedLocations = CollectSourceLocations(
                group.SelectMany(ns => ns.Locations), includeGenerated);
            result[insertionIndex] = (group[0], mergedLocations);
        }

        return result;
    }

    /// <summary>
    ///     Filters, deduplicates, and sorts source locations for display.
    /// </summary>
    internal static Location[] CollectSourceLocations(IEnumerable<Location> locations, bool includeGenerated) =>
        locations
            .Where(l => l.IsInSource)
            .Where(l => includeGenerated || !l.IsInGeneratedFile())
            .DistinctBy(l => l.SourceTree?.FilePath)
            .OrderBy(l => l.SourceTree?.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
