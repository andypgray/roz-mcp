namespace Zphil.Roz.Extensions;

/// <summary>
///     Extension methods for list truncation with total-count tracking.
/// </summary>
internal static class ListExtensions
{
    /// <summary>
    ///     Validates that <paramref name="maxResults" /> is at least 1 when provided.
    /// </summary>
    internal static void ThrowIfMaxResultsInvalid(int? maxResults)
    {
        if (maxResults is < 1)
        {
            throw new UserErrorException(
                $"maxResults must be >= 1. Omit the parameter to use the default. Got: {maxResults.Value}.");
        }
    }

    /// <summary>
    ///     Validates that <paramref name="maxBodyLines" /> is at least 0 when provided. A negative
    ///     value would reach <c>SymbolFormatter</c>'s <c>Take</c>/slice and throw an unfriendly
    ///     <see cref="ArgumentOutOfRangeException" />; this surfaces a correctable user error instead.
    /// </summary>
    internal static void ThrowIfMaxBodyLinesInvalid(int? maxBodyLines)
    {
        if (maxBodyLines is < 0)
        {
            throw new UserErrorException(
                $"maxBodyLines must be >= 0. Omit the parameter for the full body. Got: {maxBodyLines.Value}.");
        }
    }

    /// <summary>
    ///     Records the total count and truncates the list if it exceeds <paramref name="maxResults" />.
    /// </summary>
    internal static (List<T> Items, int TotalCount) Truncate<T>(this List<T> items, int? maxResults)
    {
        ThrowIfMaxResultsInvalid(maxResults);

        int totalCount = items.Count;
        if (maxResults.HasValue && items.Count > maxResults.Value)
        {
            items = items.Take(maxResults.Value).ToList();
        }

        return (items, totalCount);
    }
}
