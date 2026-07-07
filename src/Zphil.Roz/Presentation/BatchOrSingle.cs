using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Dispatches tool calls that accept either a batch of symbol names or a batch of
///     cursor locations. Enforces the symbolNames-xor-locations invariant via
///     <see cref="BatchGuards" />, fans out in parallel, then renders through
///     <see cref="ProgressiveRenderer" />. Both paths flow through the same
///     <see cref="BatchItem{T}" /> pipeline so per-item errors render as inline blocks.
/// </summary>
internal static class BatchOrSingle
{
    /// <summary>
    ///     Dispatches a batch tool call to either the per-name or per-location lambda based on
    ///     which input array was supplied, fans out per-item, and renders the combined result.
    ///     <paramref name="toolName" /> is interpolated into per-item parse-error messages.
    /// </summary>
    public static async Task<string> RunAsync<TResult>(
        string[]? symbolNames, string[]? locations,
        Func<string, Task<TResult>> perName,
        Func<PositionLocation, Task<TResult>> perLocation,
        Func<IReadOnlyList<BatchItem<TResult>>, DetailLevel, string> renderBatch,
        string toolName)
    {
        BatchGuards.EnforceBatchOrPositions(symbolNames, locations);
        BatchItem<TResult>[] items = symbolNames is not null
            ? await RunAllAsync(symbolNames, perName)
            : await RunAllAsync(locations!, perLocation, toolName);
        return ProgressiveRenderer.Render(items, renderBatch);
    }

    /// <summary>
    ///     Fans out per-name calls in parallel, capturing each expected
    ///     <see cref="UserErrorException" /> as a <see cref="BatchItemError{T}" /> so a single
    ///     bad name doesn't fault the batch.
    /// </summary>
    /// <remarks>
    ///     Only <see cref="UserErrorException" /> is captured — unexpected exceptions propagate
    ///     up to <see cref="Pipeline.GlobalCallToolFilter" /> where they are logged as warnings,
    ///     preserving the "expected vs crash" signal. <see cref="OperationCanceledException" />
    ///     also propagates so cooperative cancellation isn't swallowed.
    /// </remarks>
    public static async Task<BatchItem<TResult>[]> RunAllAsync<TResult>(
        IEnumerable<string> names, Func<string, Task<TResult>> perName)
    {
        Task<BatchItem<TResult>>[] tasks = names.Select(async name =>
        {
            try
            {
                TResult value = await perName(name);
                return (BatchItem<TResult>)new BatchItemSuccess<TResult>(name, value);
            }
            catch (UserErrorException ex)
            {
                return new BatchItemError<TResult>(name, ex.Message);
            }
        }).ToArray();
        return await Task.WhenAll(tasks);
    }

    /// <summary>
    ///     Fans out per-location calls in parallel. Each raw location string is parsed via
    ///     <see cref="LocationParser.ParsePosition" /> per-item; parse failures become
    ///     <see cref="BatchItemError{T}" /> blocks keyed on the original raw string, so a
    ///     single malformed location doesn't fault the whole batch (mirrors the by-name path).
    /// </summary>
    public static Task<BatchItem<TResult>[]> RunAllAsync<TResult>(
        IEnumerable<string> rawLocations, Func<PositionLocation, Task<TResult>> perLocation, string toolName)
        => RunAllAsync(rawLocations, async raw =>
        {
            PositionLocation loc = LocationParser.ParsePosition(raw, toolName);
            return await perLocation(loc);
        });
}
