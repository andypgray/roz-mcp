using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Zphil.Roz.Enums;
using Zphil.Roz.Models;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Shared helper methods used across category formatters.
/// </summary>
internal static class FormattingHelpers
{
    internal static EffectiveOptions ComputeEffective(
        DetailLevel level, bool includeBody = false, bool includeDocs = false) =>
        new(
            level == DetailLevel.Full && includeBody,
            level <= DetailLevel.High && includeDocs);

    internal static string FormatHeaderCount(int shown, int total) =>
        total > shown ? $"showing {shown} of {total}" : $"{shown}";

    internal static string FormatTruncationHint(int total, string increaseHint, int includedTestCount = 0) =>
        includedTestCount > 0
            ? $"({total} total: {total - includedTestCount} source, {includedTestCount} tests — {increaseHint})"
            : $"({total} total — {increaseHint})";

    internal static string FormatExcludedTestResultsHint(int count) =>
        $"(+{count} in test projects)";

    internal static string FormatExcludedMetadataHint(int count) =>
        $"(+{count} in metadata/framework assemblies — set includeMetadata=true to include)";

    internal static string FormatExcludedTestProjectsHint(int count) =>
        $"(skipped {count} test project(s))";

    internal static string FormatProjectIgnoredHint() =>
        "(project filter ignored — it scopes name-based resolution only; a cursor/location already " +
        "targets one symbol, so references across all projects are reported)";

    internal static string FormatNoResultsWithDiFallback(
        string resultKind, string symbolName, int excludedTestCount,
        IReadOnlyList<DiRegistration>? diRegistrations, string solutionDir)
    {
        // Distinguish "DI scan ran but found nothing" (empty list) from "DI scan didn't run" (null)
        string noResults = diRegistrations is { Count: 0 }
            ? $"No direct {resultKind} or DI registrations found for '{symbolName}'."
            : $"No {resultKind} found for '{symbolName}'.";
        if (excludedTestCount > 0)
        {
            noResults += $"\n{FormatExcludedTestResultsHint(excludedTestCount)}";
        }

        if (diRegistrations is { Count: > 0 })
        {
            noResults += $"\n\n{ReferenceFormatter.FormatDiRegistrationsFallback(diRegistrations, solutionDir)}";
        }

        return noResults;
    }

    internal static string FormatBatch<T>(
        IReadOnlyList<T> results, Func<T, string> header, Func<T, string> format)
    {
        if (results.Count == 1)
        {
            return format(results[0]);
        }

        var sb = new StringBuilder();
        foreach (T result in results)
        {
            sb.AppendLine($"=== {header(result)} ===");
            sb.AppendLine();
            // Normalize the trailing newline here, at the single embedding boundary, so inner
            // formatters need not each TrimEnd — this guards the whole batch-gluing defect class.
            sb.AppendLine(format(result).TrimEnd());
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Formats a batch of successes and per-item errors as labeled sections. A single
    ///     success renders without a header (matching <see cref="FormatBatch{T}" />); a single
    ///     error renders as a one-line message. Multi-item batches mark error blocks with
    ///     <c>=== Error: {name} ===</c> so they are easy to scan.
    /// </summary>
    internal static string FormatBatchWithErrors<T>(
        IReadOnlyList<BatchItem<T>> items,
        Func<T, string> successHeader,
        Func<T, string> successFormatter)
    {
        if (items.Count == 1)
        {
            return items[0] switch
            {
                BatchItemSuccess<T> s => successFormatter(s.Value),
                BatchItemError<T> e => $"Error looking up '{e.Name}': {e.Error}",
                _ => throw new InvalidOperationException("Unknown BatchItem variant.")
            };
        }

        var sb = new StringBuilder();
        foreach (BatchItem<T> item in items)
        {
            switch (item)
            {
                case BatchItemSuccess<T> s:
                    sb.AppendLine($"=== {successHeader(s.Value)} ===");
                    sb.AppendLine();
                    // TrimEnd at the embedding boundary — see FormatBatch. One normalization
                    // point makes every inner formatter's trailing-newline policy irrelevant.
                    sb.AppendLine(successFormatter(s.Value).TrimEnd());
                    break;
                case BatchItemError<T> e:
                    sb.AppendLine($"=== Error: {e.Name} ===");
                    sb.AppendLine();
                    sb.AppendLine(e.Error.TrimEnd());
                    break;
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Builds a section-header selector that keeps the bare symbol name unless two
    ///     successful batch items resolve to different symbols sharing it. Colliding groups
    ///     escalate together to the lowest tier at which they are mutually distinct:
    ///     bare → <c>Type.Member</c> → <c>Namespace.Type.Member</c> → Full + an ordinal
    ///     suffix (guarantees no two headers are ever identical). Non-colliding items stay
    ///     bare — no over-qualification. Shared by every symbol-resolution batch formatter
    ///     (find_references, find_implementations, find_overloads, get_type_hierarchy).
    /// </summary>
    internal static Func<T, string> CollisionAwareHeader<T>(
        IReadOnlyList<BatchItem<T>> items,
        Func<T, string> name,
        Func<T, SymbolQualifiers> qualifiers) where T : class
    {
        // Keyed by REFERENCE identity, not record value-equality. Result types are records
        // (value semantics): two distinct lookups whose members happen to match — Tier-3's
        // degenerate case of two cursors on two overloads of the same method, sharing one
        // containing-type qualification and an empty result list — would collapse to a single
        // dictionary slot under structural equality, and the second write would overwrite the
        // first, so both items would resolve to the same header and defeat the ordinal-suffix
        // uniqueness guarantee. ReferenceComparer keeps every BatchItemSuccess.Value its own
        // slot; the same object looked up twice intentionally shares its one header.
        Dictionary<T, string> headerByValue = new(ReferenceComparer<T>.Instance);

        foreach (IGrouping<string, BatchItemSuccess<T>> group in
                 items.OfType<BatchItemSuccess<T>>().GroupBy(s => name(s.Value)))
        {
            List<BatchItemSuccess<T>> g = group.ToList();
            if (g.Count == 1)
            {
                headerByValue[g[0].Value] = name(g[0].Value);
                continue;
            }

            Func<BatchItemSuccess<T>, string>? tier =
                Distinct(g, s => qualifiers(s.Value).Short) ? s => qualifiers(s.Value).Short
                : Distinct(g, s => qualifiers(s.Value).Full) ? s => qualifiers(s.Value).Full
                : null;

            for (var i = 0; i < g.Count; i++)
            {
                headerByValue[g[i].Value] = tier is not null
                    ? tier(g[i])
                    : $"{qualifiers(g[i].Value).Full} #{i + 1}";
            }
        }

        return value => headerByValue.TryGetValue(value, out string? h) ? h : name(value);

        static bool Distinct(List<BatchItemSuccess<T>> g, Func<BatchItemSuccess<T>, string> key)
        {
            return g.Select(key).Distinct(StringComparer.Ordinal).Count() == g.Count;
        }
    }

    internal static string FormatAmbiguityWarning(string symbolName, int totalMatches, int? targetLine = null)
    {
        string lineHint = targetLine is not null ? $" Targeted first at line {targetLine}." : "";
        return $"Warning: Multiple symbols named '{symbolName}' found ({totalMatches} total).{lineHint} Use line/column to disambiguate.";
    }

    internal static void FormatSymbolNamesOnlyList(StringBuilder sb, IReadOnlyList<ISymbol> symbols, string solutionDir)
    {
        for (var i = 0; i < symbols.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {SymbolFormatter.FormatSymbolNameOnly(symbols[i], solutionDir)}");
        }
    }

    internal static void FormatSymbolNamesOnlyList(
        StringBuilder sb, List<(ISymbol Symbol, Location[]? LocationOverride)> merged, string solutionDir) =>
        FormatSymbolNamesOnlyList(sb, merged.Select(m => m.Symbol).ToList(), solutionDir);

    /// <summary>
    ///     Reference-identity comparer so record value-equality cannot merge distinct batch
    ///     results into one <see cref="CollisionAwareHeader{T}" /> slot.
    /// </summary>
    private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        internal static readonly ReferenceComparer<T> Instance = new();
        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }

    internal readonly record struct EffectiveOptions(bool Body, bool Docs);
}
