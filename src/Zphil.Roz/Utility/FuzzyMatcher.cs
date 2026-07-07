namespace Zphil.Roz.Utility;

/// <summary>
///     Provides fuzzy string matching for "did you mean..." suggestions.
/// </summary>
internal static class FuzzyMatcher
{
    /// <summary>
    ///     Computes the Levenshtein edit distance between two strings (case-insensitive).
    /// </summary>
    /// <remarks>
    ///     https://en.wikipedia.org/wiki/Levenshtein_distance
    /// </remarks>
    public static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        // Single-row optimization: O(min(n,m)) space
        if (a.Length > b.Length)
        {
            (a, b) = (b, a);
        }

        var row = new int[a.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            row[i] = i;
        }

        for (var j = 1; j <= b.Length; j++)
        {
            int prev = row[0];
            row[0] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                int cost = Char.ToLowerInvariant(a[i - 1]) == Char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                int temp = row[i];
                row[i] = Math.Min(
                    Math.Min(row[i] + 1, row[i - 1] + 1),
                    prev + cost);
                prev = temp;
            }
        }

        return row[a.Length];
    }

    /// <summary>
    ///     Returns the top candidate names closest to <paramref name="searchTerm" />, sorted by similarity.
    /// </summary>
    /// <param name="searchTerm">The search term that produced no results.</param>
    /// <param name="candidates">Available symbol names to compare against.</param>
    /// <param name="maxSuggestions">Maximum number of suggestions to return.</param>
    /// <param name="maxNormalizedDistance">
    ///     Threshold for inclusion: edit distance / max(len(a), len(b)). Default 0.3.
    /// </param>
    public static List<string> GetSuggestions(
        string searchTerm, IEnumerable<string> candidates,
        int maxSuggestions = 5, double maxNormalizedDistance = 0.3)
    {
        if (searchTerm.Length < 3)
        {
            return [];
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(c => !String.Equals(c, searchTerm, StringComparison.OrdinalIgnoreCase))
            .Select(c => (Name: c, Distance: LevenshteinDistance(searchTerm, c)))
            .Select(x => (x.Name, x.Distance, Normalized: (double)x.Distance / Math.Max(searchTerm.Length, x.Name.Length)))
            .Where(x => x.Normalized <= maxNormalizedDistance)
            .OrderBy(x => x.Normalized)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Name)
            .Take(maxSuggestions)
            .ToList();
    }
}
