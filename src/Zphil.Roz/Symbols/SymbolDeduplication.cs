using Microsoft.CodeAnalysis;
using Zphil.Roz.Extensions;

namespace Zphil.Roz.Symbols;

/// <summary>
///     Cross-compilation symbol deduplication. <see cref="SymbolEqualityComparer" /> doesn't
///     treat the same source symbol from different project compilations (e.g. multi-TFM) as equal,
///     so we deduplicate by file path + line position instead.
/// </summary>
internal static class SymbolDeduplication
{
    /// <summary>
    ///     Deduplicates symbols that share the same source location (file path + line).
    /// </summary>
    /// <remarks>
    ///     Metadata-only symbols (no source location) are deduplicated by display string,
    ///     since the same metadata symbol from different compilations is functionally identical.
    /// </remarks>
    internal static List<T> DeduplicateByLocation<T>(IEnumerable<T> symbols) where T : ISymbol
    {
        HashSet<(string Path, int Line)> seenLocations = [];
        HashSet<string> seenMetadata = [];
        List<T> result = [];

        foreach (T symbol in symbols)
        {
            Location? loc = symbol.PreferNonGeneratedSourceLocation();
            if (loc is null)
            {
                string displayKey = symbol.ToDisplayString();
                if (seenMetadata.Add(displayKey))
                {
                    result.Add(symbol);
                }

                continue;
            }

            (string, int) key = (loc.SourceTree!.FilePath, loc.GetLineSpan().StartLinePosition.Line);
            if (seenLocations.Add(key))
            {
                result.Add(symbol);
            }
        }

        return result;
    }

    /// <summary>
    ///     Returns true if two symbols share the same primary source location,
    ///     indicating they are the same symbol from different compilations (e.g. multi-TFM).
    /// </summary>
    internal static bool AreSameSourceLocation(ISymbol a, ISymbol b)
    {
        (string, int)? keyA = GetLocationKey(a);
        (string, int)? keyB = GetLocationKey(b);
        return keyA is not null && keyA == keyB;
    }

    /// <summary>
    ///     Returns a source-location key for grouping symbols that may come from different compilations.
    /// </summary>
    internal static (string Path, int Line)? GetLocationKey(ISymbol symbol)
    {
        Location? loc = symbol.PreferNonGeneratedSourceLocation();
        if (loc is null)
        {
            return null;
        }

        return (loc.SourceTree!.FilePath, loc.GetLineSpan().StartLinePosition.Line);
    }
}
