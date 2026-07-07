namespace Zphil.Roz.Presentation;

/// <summary>
///     Guards for tools that accept either a batch of symbol names or a batch of cursor locations.
/// </summary>
internal static class BatchGuards
{
    /// <summary>
    ///     Enforces that a tool call provides exactly one of <paramref name="symbolNames" />
    ///     (name batch) or <paramref name="locations" /> (cursor batch) — never both, never neither,
    ///     never empty.
    /// </summary>
    public static void EnforceBatchOrPositions(string[]? symbolNames, string[]? locations)
    {
        if (symbolNames is { Length: 0 })
        {
            throw new UserErrorException("symbolNames must not be empty.");
        }

        if (locations is { Length: 0 })
        {
            throw new UserErrorException("locations must not be empty.");
        }

        if (symbolNames is not null && locations is not null)
        {
            throw new UserErrorException(
                "Pass either locations (cursors) or symbolNames (names), not both — they are alternative ways to identify symbols. Scope a name search with project=<name>.");
        }

        if (symbolNames is null && locations is null)
        {
            throw new UserErrorException(
                "Provide one of: locations=['path:line:col',...] (cursors) or symbolNames=['A','B',...] (names). Both batch — pass many items per call.");
        }
    }

    /// <summary>
    ///     Enforces that a batch tool call has at least one item.
    /// </summary>
    public static void RejectEmptyBatch<T>(IReadOnlyList<T>? items, string paramName = "edits")
    {
        if (items is null || items.Count == 0)
        {
            throw new UserErrorException($"{paramName} must not be empty.");
        }
    }
}
