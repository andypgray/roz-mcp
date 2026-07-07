using System.Text;
using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using static Zphil.Roz.Presentation.FormattingHelpers;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Formats <see cref="ImpactAnalysisResult" /> blast-radius reports into plain text, mirroring
///     <see cref="ReferenceResultFormatter" />: a header with the per-severity summary and project
///     distribution, then file-grouped source snippets with a <c>[Severity: reason]</c> tag per site.
/// </summary>
internal static class ImpactAnalysisFormatter
{
    /// <summary>
    ///     Formats a batch of impact reports as labeled sections, qualifying headers only when two
    ///     items resolve to different symbols sharing a simple name (same policy as the reference
    ///     formatters).
    /// </summary>
    public static string Format(
        IReadOnlyList<BatchItem<ImpactAnalysisResult>> items, DetailLevel level = DetailLevel.Full) =>
        FormatBatchWithErrors(items,
            CollisionAwareHeader(items, r => r.SymbolName, r => r.Qualifiers),
            r => Format(r, level));

    /// <summary>
    ///     Formats a single impact report.
    /// </summary>
    /// <example>
    ///     <code>
    ///     Impact of TypeChange on 'GetCount' → long:
    ///       3 site(s) — 1 compatible, 1 requires-update, 1 unsafe — across 2 file(s), 1 project(s)
    ///
    ///     Services/ImpactConsumer.cs [TestFixture]:
    ///         12 | public int NarrowConsumer() => surface.NarrowlyConsumed();  [RequiresUpdate: needs cast: (int) — long converts only explicitly to int]
    ///     </code>
    /// </example>
    public static string Format(ImpactAnalysisResult result, DetailLevel level = DetailLevel.Full)
    {
        if (result.TotalCount == 0)
        {
            return FormatNoImpact(result);
        }

        bool includeSourceContext = level < DetailLevel.Low;
        var sb = new StringBuilder();

        sb.AppendLine($"Impact of {result.ChangeKind} on '{result.SymbolName}'{TargetSuffix(result.Target)}:");
        sb.AppendLine(FormatSummaryLine(result));

        // Per-project distribution is only worth the lines once results are truncated.
        if (result.Sites.Count < result.TotalCount && result.Distribution is { Count: > 0 })
        {
            sb.AppendLine(ReferenceFormatter.FormatDistributionSummary(result.Distribution));
        }

        sb.AppendLine();
        sb.AppendLine(FormatImpactSites(result.Sites, result.SolutionDir, includeSourceContext));

        if (result.TotalCount > result.Sites.Count)
        {
            sb.AppendLine(FormatTruncationHint(result.TotalCount, "increase maxResults", result.IncludedTestCount));
        }

        if (result.ExcludedTestCount > 0)
        {
            sb.AppendLine(FormatExcludedTestResultsHint(result.ExcludedTestCount));
        }

        AppendNotes(sb, result.Notes);

        return sb.ToString().TrimEnd();
    }

    private static string FormatSummaryLine(ImpactAnalysisResult result)
    {
        int fileCount = result.FileDistribution?.Count ?? 0;
        int projectCount = result.Distribution?.Count ?? 0;
        return $"  {result.TotalCount} site(s) — {result.CompatibleCount} compatible, " +
               $"{result.RequiresUpdateCount} requires-update, {result.UnsafeCount} unsafe — " +
               $"across {fileCount} file(s), {projectCount} project(s)";
    }

    private static string FormatNoImpact(ImpactAnalysisResult result)
    {
        var sb = new StringBuilder();
        sb.Append($"No impacted sites found for {result.ChangeKind} on '{result.SymbolName}'{TargetSuffix(result.Target)}.");

        if (result.ExcludedTestCount > 0)
        {
            sb.AppendLine();
            sb.Append(FormatExcludedTestResultsHint(result.ExcludedTestCount));
        }

        AppendNotes(sb, result.Notes);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Renders impact sites grouped by file, appending a <c>[Severity: reason]</c> tag to each
    ///     matched line. Reuses <see cref="ReferenceFormatter.AppendSourceSnippet" /> so the snippet
    ///     gutter is identical to <c>find_references</c>.
    /// </summary>
    private static string FormatImpactSites(
        IReadOnlyList<ImpactSite> sites, string solutionDir, bool includeSourceContext)
    {
        IOrderedEnumerable<IGrouping<string, ImpactSite>> grouped = sites
            .Where(s => s.Loc.Location.IsInSource)
            .GroupBy(s => s.Loc.Location.GetLineSpan().Path)
            .OrderBy(g => g.Key);

        var sb = new StringBuilder();
        foreach (IGrouping<string, ImpactSite> group in grouped)
        {
            string relPath = Path.GetRelativePath(solutionDir, group.Key);
            string? projectName = group.First().ProjectName;
            string projectTag = projectName is not null ? $" [{projectName}]" : "";
            sb.AppendLine($"{relPath}{projectTag}:");

            foreach (ImpactSite site in group.OrderBy(s => s.Loc.Location.GetLineSpan().StartLinePosition.Line))
            {
                int line = site.Loc.Location.GetLineSpan().StartLinePosition.Line + 1;
                var tag = $"  [{site.Severity}: {site.Reason}]";

                if (!includeSourceContext || site.Lines.Length == 0)
                {
                    sb.AppendLine($"  Line {line}{tag}");
                    continue;
                }

                ReferenceFormatter.AppendSourceSnippet(sb, site.Lines, site.StartLineNumber, line, "  ", tag);
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendNotes(StringBuilder sb, IReadOnlyList<string>? notes)
    {
        if (notes is not { Count: > 0 })
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine();
        foreach (string note in notes)
        {
            sb.AppendLine($"Note: {note}");
        }
    }

    private static string TargetSuffix(string? target) => target is not null ? $" → {target}" : "";
}
