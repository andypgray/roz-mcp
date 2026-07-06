using System.Text;
using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using static Zphil.Roz.Presentation.FormattingHelpers;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Formats <c>analyze_method</c> results. The signature, inbound and overload sections are pure
///     delegation to the navigation/reference formatters; the only net-new rendering is the outbound
///     section and its external-call summary line.
/// </summary>
internal static class MethodAnalysisFormatter
{
    /// <summary>
    ///     Formats a batch of analyses as labeled sections, qualifying colliding headers like every
    ///     other symbol-resolution batch formatter.
    /// </summary>
    public static string Format(
        IReadOnlyList<BatchItem<AnalyzeMethodResult>> items, bool includeDocs = false,
        DetailLevel level = DetailLevel.Full, int? maxBodyLines = null) =>
        FormatBatchWithErrors(items,
            CollisionAwareHeader(items, r => r.Target.Name, r => r.Qualifiers),
            r => FormatSingle(r, includeDocs, level, maxBodyLines));

    private static string FormatSingle(
        AnalyzeMethodResult result, bool includeDocs, DetailLevel level, int? maxBodyLines)
    {
        EffectiveOptions eff = ComputeEffective(level, result.IncludeBody, includeDocs);
        bool includeSourceContext = level < DetailLevel.Low;

        var sb = new StringBuilder();

        // Signature
        sb.AppendLine(SymbolFormatter.FormatSymbol(
            result.Target, result.SolutionDir, includeBody: eff.Body, includeDocs: eff.Docs,
            maxBodyLines: maxBodyLines));
        sb.AppendLine();

        // Inbound — reused find_references referenceKinds=invocations formatter (callers, DI fallback, interface tip).
        sb.AppendLine(ReferenceResultFormatter.Format(result.Inbound, level));
        sb.AppendLine();

        // Outbound — the only net-new rendering.
        AppendOutbound(sb, result, includeSourceContext);

        // Overloads
        if (result.Overloads is not null)
        {
            sb.AppendLine();
            sb.AppendLine(NavigationResultFormatter.Format(result.Overloads, false, false, level));
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendOutbound(StringBuilder sb, AnalyzeMethodResult result, bool includeSourceContext)
    {
        int inSolutionShown = result.Outbound.Count(g => g.IsInSolution);
        int externalShown = result.Outbound.Count(g => !g.IsInSolution);

        // externalShown (includeExternalCalls=true) and ExternalCallCount (suppressed) are mutually
        // exclusive — one is always zero — so summing them gives the external total either way.
        int externalTotal = externalShown + result.ExternalCallCount;
        string externalClause = externalTotal > 0 ? $", {externalTotal} external" : "";
        sb.AppendLine($"Outbound calls ({inSolutionShown} in-solution{externalClause}):");

        if (result.Outbound.Count > 0)
        {
            IEnumerable<CallerWithLineText> wrapped = result.Outbound
                .Select(g => new CallerWithLineText(g.Target, g.Sites));
            sb.AppendLine(ReferenceFormatter.FormatCallers(wrapped, result.SolutionDir, includeSourceContext));
        }

        if (result.ExternalCallCount > 0)
        {
            var types = String.Join(", ", result.ExternalCallTypeNames.Take(8));
            string more = result.ExternalCallTypeNames.Count > 8 ? ", ..." : "";
            sb.AppendLine($"  (+{result.ExternalCallCount} external: {types}{more})");
        }
    }
}
