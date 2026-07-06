using Microsoft.CodeAnalysis;

namespace Zphil.Roz.Models;

internal sealed record DiagnosticsResult(
    List<Diagnostic> Diagnostics,
    string SolutionDir,
    string[]? FilePaths = null,
    DiagnosticSeverity Severity = DiagnosticSeverity.Warning,
    string[]? DiagnosticIds = null,
    string? Project = null,
    List<string>? Errors = null,
    int ExcludedTestProjectCount = 0,
    string? WorkspaceHint = null,
    IReadOnlyList<FixerSummaryEntry>? FixerSummary = null);

internal sealed record ResetBaselineResult(int TotalCount, List<DiagnosticBreakdownEntry> Breakdown);

internal sealed record DiagnosticBreakdownEntry(string DiagnosticId, int Count);

/// <summary>
///     A bulk-fixable diagnostic ID present in a result, with how many occurrences hit it.
///     Surfaced under "Available analyzer fixes" so the model can reach for
///     <c>dotnet format &lt;solution&gt; analyzers --severity info --diagnostics &lt;id&gt;</c> instead of editing each
///     site.
/// </summary>
internal sealed record FixerSummaryEntry(string DiagnosticId, int Count);

internal sealed record IncrementalDiagnosticsResult(
    List<Diagnostic> NewDiagnostics,
    int ResolvedCount,
    int UnchangedCount,
    DateTime BaselineCapturedAtUtc,
    string SolutionDir,
    string[]? DiagnosticIds = null,
    List<string>? Errors = null,
    int ExcludedTestProjectCount = 0,
    string? WorkspaceHint = null,
    IReadOnlyList<FixerSummaryEntry>? FixerSummary = null);
