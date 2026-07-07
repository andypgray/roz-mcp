using System.Text;
using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using static Zphil.Roz.Presentation.FormattingHelpers;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Formats results for diagnostic tools: get_diagnostics.
/// </summary>
internal static class DiagnosticResultFormatter
{
    /// <summary>
    ///     Formats compiler diagnostics, or a "no diagnostics" message when clean.
    /// </summary>
    /// <example>
    ///     <code>
    ///     TestFixture/Services/ShapeService.cs:7: warning CS0219: The variable 'unusedLocal' is assigned but its value is never used
    /// 
    ///     Summary: 0 error(s), 1 warning(s), 0 info
    ///     </code>
    ///     When clean: <c>No diagnostics at severity 'Warning' or above in solution.</c>
    /// </example>
    public static string Format(DiagnosticsResult result, DetailLevel level = DetailLevel.Full)
    {
        string? errorPrefix = null;
        if (result.Errors is { Count: > 0 })
        {
            errorPrefix = String.Join("\n", result.Errors.Select(e => $"Error: {e}"));
        }

        string? prefix = JoinPrefixes(result.WorkspaceHint, errorPrefix);

        if (result.Diagnostics.Count == 0)
        {
            if (prefix is not null)
            {
                return prefix;
            }

            string scope = result.FilePaths is { Length: > 0 }
                ? $"in [{String.Join(", ", result.FilePaths)}]"
                : result.Project is not null
                    ? $"in project '{result.Project}'"
                    : "in solution";
            return $"No diagnostics{FormatIdFilter(result.DiagnosticIds)} at severity '{result.Severity}' or above {scope}.";
        }

        string diagnostics = DiagnosticOutputFormatter.FormatDiagnostics(
            result.Diagnostics, result.SolutionDir, DiagnosticMaxCount(level), result.FixerSummary);
        string formatted = prefix is not null ? $"{prefix}\n\n{diagnostics}" : diagnostics;
        return result.ExcludedTestProjectCount > 0
            ? $"{formatted}\n{FormatExcludedTestProjectsHint(result.ExcludedTestProjectCount)}"
            : formatted;
    }

    /// <summary>
    ///     Formats incremental diagnostics showing new issues since baseline, with a summary of
    ///     new/resolved/unchanged counts.
    /// </summary>
    /// <example>
    ///     <code>
    ///     Incremental diagnostics (baseline from 14:30:00 UTC):
    ///
    ///     NEW (3):
    ///       error CS1501 (2): No overload for method 'DoDragDrop' takes 3 arguments
    ///         SharpTreeNode.cs:644
    ///         AssemblyTreeNode.cs:373
    ///       error CS0535 (1): Does not implement interface member
    ///         WpfWindowsDragDropManager.cs:7
    ///
    ///     Summary: 3 new, 0 resolved, 12 unchanged
    ///     </code>
    /// </example>
    public static string Format(IncrementalDiagnosticsResult result)
    {
        var sb = new StringBuilder();

        if (result.WorkspaceHint is not null)
        {
            sb.AppendLine(result.WorkspaceHint);
            sb.AppendLine();
        }

        if (result.Errors is { Count: > 0 })
        {
            foreach (string error in result.Errors)
            {
                sb.AppendLine($"Error: {error}");
            }

            if (result.NewDiagnostics.Count == 0 && result.ResolvedCount == 0 && result.UnchangedCount == 0)
            {
                return sb.ToString().TrimEnd();
            }

            sb.AppendLine();
        }

        sb.AppendLine($"Incremental diagnostics (baseline from {result.BaselineCapturedAtUtc:HH:mm:ss} UTC):");
        sb.AppendLine();

        if (result.NewDiagnostics.Count == 0)
        {
            sb.AppendLine($"No new diagnostics{FormatIdFilter(result.DiagnosticIds)} introduced since baseline.");
        }
        else
        {
            sb.AppendLine($"NEW ({result.NewDiagnostics.Count}):");
            sb.AppendLine(DiagnosticOutputFormatter.FormatIncrementalDiagnostics(result.NewDiagnostics, result.SolutionDir));
        }

        sb.AppendLine();
        sb.Append($"Summary: {result.NewDiagnostics.Count} new, {result.ResolvedCount} resolved, {result.UnchangedCount} unchanged");

        DiagnosticOutputFormatter.AppendFixerSummary(sb, result.FixerSummary);

        if (result.ExcludedTestProjectCount > 0)
        {
            sb.AppendLine();
            sb.Append(FormatExcludedTestProjectsHint(result.ExcludedTestProjectCount));
        }

        return sb.ToString();
    }

    public static string Format(ResetBaselineResult result)
    {
        if (result.TotalCount == 0)
        {
            return "Diagnostic baseline reset. No diagnostics captured (clean baseline).";
        }

        var breakdown = String.Join("\n", result.Breakdown.Select(e => $"  {e.DiagnosticId}: {e.Count}"));
        return $"Diagnostic baseline reset. {result.TotalCount} diagnostic(s) captured as new baseline:\n{breakdown}";
    }

    private static int? DiagnosticMaxCount(DetailLevel level) => level switch
    {
        DetailLevel.Low => 50,
        DetailLevel.Minimal => 25,
        _ => null
    };

    private static string FormatIdFilter(string[]? diagnosticIds) =>
        diagnosticIds is { Length: > 0 } ? $" matching [{String.Join(", ", diagnosticIds)}]" : "";

    private static string? JoinPrefixes(string? workspaceHint, string? errorPrefix) =>
        (workspaceHint, errorPrefix) switch
        {
            (null, null) => null,
            (not null, null) => workspaceHint,
            (null, not null) => errorPrefix,
            _ => $"{workspaceHint}\n\n{errorPrefix}"
        };
}
