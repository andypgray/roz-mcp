using Zphil.Roz.Enums;
using Zphil.Roz.Pipeline;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Tries progressively lower detail levels until the formatted output fits within
///     the character limit, avoiding hard mid-response truncation. If even the lowest
///     detail level exceeds the limit, returns it and lets <see cref="ResponseTruncator" />
///     hard-truncate as a failsafe.
/// </summary>
internal static class ProgressiveRenderer
{
    private static readonly DetailLevel[] ReductionOrder =
    [
        DetailLevel.Full,
        DetailLevel.High,
        DetailLevel.Medium,
        DetailLevel.Low,
        DetailLevel.Minimal
    ];

    /// <summary>
    ///     Renders the result at progressively lower detail levels until the output fits
    ///     within <paramref name="maxChars" />.
    /// </summary>
    /// <typeparam name="T">The service result type.</typeparam>
    /// <param name="result">The service result to format.</param>
    /// <param name="format">A callback that renders the result at a given detail level.</param>
    /// <param name="maxChars">The maximum allowed response length in characters.</param>
    /// <returns>The formatted output, with a reduction note appended if detail was reduced.</returns>
    public static string Render<T>(T result, Func<T, DetailLevel, string> format, int maxChars)
    {
        string previousOutput = null!;
        DetailLevel lastTriedLevel = DetailLevel.Full;

        foreach (DetailLevel level in ReductionOrder)
        {
            string output = format(result, level);
            lastTriedLevel = level;

            // If this level produced the same output as the previous, skip it — no point
            // reporting a reduction that changed nothing. Compare content, not length:
            // two distinct levels can share a length, and a length-collision skip would
            // leave previousOutput stale while lastTriedLevel advanced, so the failsafe
            // would return an earlier level's content under a later level's label.
            if (output == previousOutput)
            {
                continue;
            }

            if (output.Length <= maxChars)
            {
                return level == DetailLevel.Full
                    ? output
                    : AppendReductionNote(output, level, maxChars);
            }

            previousOutput = output;
        }

        // Nothing fit — return the smallest rendering for the hard truncation failsafe.
        return AppendReductionNote(previousOutput, lastTriedLevel, maxChars);
    }

    /// <summary>
    ///     Convenience overload that reads the limit from <see cref="ResponseTruncator.MaxChars" />.
    /// </summary>
    public static string Render<T>(T result, Func<T, DetailLevel, string> format) =>
        Render(result, format, ResponseTruncator.MaxChars);

    private static string AppendReductionNote(string output, DetailLevel level, int maxChars)
    {
        string levelDescription = level switch
        {
            DetailLevel.High =>
                "Source bodies removed. Use includeBody on specific symbols to see source.",
            DetailLevel.Medium =>
                "Documentation removed. Use includeDocs on specific symbols.",
            DetailLevel.Low =>
                "Only signatures and locations shown. Narrow your search for details.",
            DetailLevel.Minimal =>
                "Only names and locations shown. Narrow your search for details.",
            _ => ""
        };

        return $"{output}\n\n--- DETAIL REDUCED ---\nOutput exceeded the {maxChars:N0} character limit. Reduced to {level}: {levelDescription}";
    }
}
