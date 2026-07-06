using System.Text;
using Microsoft.CodeAnalysis;
using Zphil.Roz.Extensions;
using Zphil.Roz.Models;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Formats compiler diagnostics into human-readable plain text optimized for LLM consumption.
/// </summary>
/// <example>
///     <code>
///     DiagnosticFixture/ErrorExamples.cs:7: error CS0246: The type or namespace name 'NonExistentType' could not be found
///     DiagnosticFixture/WarningExamples.cs:7: warning CS0219: The variable 'unusedLocal' is assigned but its value is never used
///     DiagnosticFixture/WarningExamples.cs:12: warning CS0618: 'ObsoleteApi.OldMethod()' is obsolete
///
///     Summary: 1 error(s), 2 warning(s), 0 info
///     </code>
/// </example>
internal static class DiagnosticOutputFormatter
{
    private const int MaxSourceDiagnostics = 100;

    private const int MaxMetadataSamplesPerGroup = 3;

    private const int MaxGroupedErrorCodes = 20;

    private const int MaxGroupedFiles = 10;

    public static string FormatDiagnostics(
        IEnumerable<Diagnostic> diagnostics, string solutionDir, int? maxCount = null,
        IReadOnlyList<FixerSummaryEntry>? fixerSummary = null)
    {
        List<Diagnostic> allDiagnostics = diagnostics.ToList();

        int errorCount = allDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warningCount = allDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        int infoCount = allDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Info);

        List<Diagnostic> sourceDiags = allDiagnostics
            .Where(d => d.Location.IsInSource)
            .OrderBy(d => d.Severity)
            .ThenBy(d => d.Location.GetLineSpan().Path)
            .ToList();

        List<Diagnostic> metadataDiags = allDiagnostics
            .Where(d => !d.Location.IsInSource)
            .ToList();

        int effectiveCap = maxCount ?? MaxSourceDiagnostics;

        var sb = new StringBuilder();

        // Source diagnostics — grouped summary when over threshold, otherwise individual listing
        if (sourceDiags.Count > 0)
        {
            if (sourceDiags.Count > effectiveCap)
            {
                FormatSourceDiagnosticsGrouped(sb, sourceDiags, solutionDir, effectiveCap);
            }
            else
            {
                foreach (Diagnostic diag in sourceDiags)
                {
                    string severity = FormatSeverity(diag.Severity);
                    string location = diag.Location.ToFileLineString(solutionDir);
                    sb.AppendLine($"{location}: {severity} {diag.Id}: {diag.GetMessage()}");
                }
            }
        }

        // Metadata diagnostics — group by Id, show counts + samples
        if (metadataDiags.Count > 0)
        {
            if (sourceDiags.Count > 0)
            {
                sb.AppendLine();
            }

            IOrderedEnumerable<IGrouping<(string Id, DiagnosticSeverity Severity), Diagnostic>> groups = metadataDiags
                .GroupBy(d => (d.Id, d.Severity))
                .OrderBy(g => g.Key.Severity)
                .ThenBy(g => g.Key.Id);

            sb.AppendLine("Metadata diagnostics (assembly/reference level):");
            foreach (IGrouping<(string Id, DiagnosticSeverity Severity), Diagnostic> group in groups)
            {
                string severity = FormatSeverity(group.Key.Severity);
                int count = group.Count();

                List<string> distinctMessages = group
                    .Select(d => d.GetMessage())
                    .Distinct()
                    .ToList();

                List<string> sampleMessages = distinctMessages.Take(MaxMetadataSamplesPerGroup).ToList();
                int distinctTotal = distinctMessages.Count;

                sb.AppendLine($"  {severity} {group.Key.Id}: {count} occurrence(s)");
                foreach (string msg in sampleMessages)
                {
                    sb.AppendLine($"    - {msg}");
                }

                if (distinctTotal > MaxMetadataSamplesPerGroup)
                {
                    sb.AppendLine($"    ... and {distinctTotal - MaxMetadataSamplesPerGroup} more distinct message(s)");
                }
            }
        }

        sb.AppendLine();
        sb.Append($"Summary: {errorCount} error(s), {warningCount} warning(s), {infoCount} info");

        AppendFixerSummary(sb, fixerSummary);

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Appends the "Available analyzer fixes" block listing fixable diagnostic IDs and
    ///     occurrence counts. No-op when <paramref name="fixerSummary" /> is null or empty.
    /// </summary>
    /// <remarks>
    ///     Bottom-only (not per-line). Per-line annotations would add ~10 chars × every diagnostic
    ///     for no signal beyond what the bottom roll-up already conveys.
    /// </remarks>
    public static void AppendFixerSummary(StringBuilder sb, IReadOnlyList<FixerSummaryEntry>? fixerSummary)
    {
        if (fixerSummary is not { Count: > 0 } summary)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Available analyzer fixes (apply via `dotnet format <solution> analyzers --severity info --diagnostics <id>`):");
        foreach (FixerSummaryEntry entry in summary)
        {
            sb.AppendLine($"  {entry.DiagnosticId}: {entry.Count}");
        }
    }

    /// <summary>
    ///     Formats incremental (new) diagnostics grouped by error code for root-cause clustering.
    /// </summary>
    /// <example>
    ///     <code>
    ///       error CS1501 (2): No overload for method 'DoDragDrop' takes 3 arguments
    ///         SharpTreeNode.cs:644
    ///         AssemblyTreeNode.cs:373
    ///       error CS0535 (1): Does not implement interface member
    ///         WpfWindowsDragDropManager.cs:7
    ///     </code>
    /// </example>
    public static string FormatIncrementalDiagnostics(IReadOnlyList<Diagnostic> diagnostics, string solutionDir)
    {
        var sb = new StringBuilder();

        List<(string Id, DiagnosticSeverity Severity, string Message, List<Diagnostic> Items)> groups = diagnostics
            .GroupBy(d => d.Id)
            .Select(g => (g.Key, g.First().Severity, g.First().GetMessage(), g.ToList()))
            .OrderBy(g => g.Severity)
            .ThenBy(g => g.Key)
            .ToList();

        foreach ((string id, DiagnosticSeverity severity, string message, List<Diagnostic> items) in groups)
        {
            sb.AppendLine($"  {FormatSeverity(severity)} {id} ({items.Count}): {message}");
            foreach (Diagnostic diag in items.OrderBy(d => d.Location.GetLineSpan().Path))
            {
                string location = diag.Location.ToFileLineString(solutionDir);
                sb.AppendLine($"    {location}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatSeverity(DiagnosticSeverity severity) =>
        severity switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            DiagnosticSeverity.Info => "info",
            _ => "hidden"
        };

    private static void FormatSourceDiagnosticsGrouped(
        StringBuilder sb, List<Diagnostic> sourceDiags, string solutionDir, int effectiveCap)
    {
        int maxGroups = effectiveCap switch { <= 25 => 5, <= 50 => 10, _ => MaxGroupedErrorCodes };
        int maxFiles = effectiveCap switch { <= 25 => 0, <= 50 => 5, _ => MaxGroupedFiles };

        // Cache file paths upfront — GetLineSpan() is non-trivial and we need paths multiple times
        string[] filePaths = sourceDiags.Select(d => d.Location.GetLineSpan().Path).ToArray();
        int fileCount = filePaths.Distinct().Count();

        sb.AppendLine($"Source diagnostics ({sourceDiags.Count:N0} total across {fileCount:N0} files):");
        sb.AppendLine();

        // Group by error code + severity, ordered by count descending
        var groups = sourceDiags
            .Select((d, i) => (Diag: d, Path: filePaths[i]))
            .GroupBy(x => (x.Diag.Id, x.Diag.Severity))
            .Select(g => new
            {
                g.Key.Id,
                g.Key.Severity,
                Count = g.Count(),
                FileCount = g.Select(x => x.Path).Distinct().Count(),
                Title = g.First().Diag.Descriptor.Title.ToString()
            })
            .OrderByDescending(g => g.Count)
            .ToList();

        sb.AppendLine($"By error code (top {Math.Min(groups.Count, maxGroups)}):");
        foreach (var group in groups.Take(maxGroups))
        {
            string severity = FormatSeverity(group.Severity);
            string titleSuffix = group.Title.Length > 0 ? $" \u2014 {group.Title}" : "";
            sb.AppendLine($"  {severity} {group.Id}: {group.Count:N0} occurrences across {group.FileCount:N0} files{titleSuffix}");
        }

        if (groups.Count > maxGroups)
        {
            sb.AppendLine($"  ... and {groups.Count - maxGroups} more error codes");
        }

        // Top affected files
        if (maxFiles > 0)
        {
            var fileGroups = sourceDiags
                .Select((d, i) => (Diag: d, Path: filePaths[i]))
                .GroupBy(x => x.Path)
                .Select(g =>
                {
                    string relPath = Path.GetRelativePath(solutionDir, g.Key);
                    int errors = 0, warnings = 0;
                    foreach ((Diagnostic Diag, string Path) x in g)
                    {
                        if (x.Diag.Severity == DiagnosticSeverity.Error)
                        {
                            errors++;
                        }
                        else if (x.Diag.Severity == DiagnosticSeverity.Warning)
                        {
                            warnings++;
                        }
                    }

                    return new { RelPath = relPath, Errors = errors, Warnings = warnings, Total = g.Count() };
                })
                .OrderByDescending(f => f.Total)
                .ToList();

            sb.AppendLine();
            sb.AppendLine($"Top affected files ({Math.Min(fileGroups.Count, maxFiles)}):");
            foreach (var file in fileGroups.Take(maxFiles))
            {
                List<string> parts = new();
                if (file.Errors > 0)
                {
                    parts.Add($"{file.Errors} {(file.Errors == 1 ? "error" : "errors")}");
                }

                if (file.Warnings > 0)
                {
                    parts.Add($"{file.Warnings} {(file.Warnings == 1 ? "warning" : "warnings")}");
                }

                int otherCount = file.Total - file.Errors - file.Warnings;
                if (otherCount > 0)
                {
                    parts.Add($"{otherCount} info");
                }

                sb.AppendLine($"  {file.RelPath}: {String.Join(", ", parts)}");
            }

            if (fileGroups.Count > maxFiles)
            {
                sb.AppendLine($"  ... and {fileGroups.Count - maxFiles:N0} more files");
            }
        }
    }
}
