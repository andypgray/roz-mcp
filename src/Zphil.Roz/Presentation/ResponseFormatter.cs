using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using Zphil.Roz.Pipeline;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Thin facade that delegates to category-specific formatters.
///     Preserves the single entry point used by all Tool classes.
/// </summary>
internal static class ResponseFormatter
{
    // Navigation

    public static string Format(
        IReadOnlyList<BatchItem<FindSymbolResult>> items, bool includeDocs = false,
        DetailLevel level = DetailLevel.Full, int? maxBodyLines = null, bool includeGenerated = false) =>
        NavigationResultFormatter.Format(items, includeDocs, level, maxBodyLines, includeGenerated);

    public static string Format(
        IReadOnlyList<SymbolsOverviewResult> results, bool includeDocs = false,
        DetailLevel level = DetailLevel.Full) =>
        NavigationResultFormatter.Format(results, includeDocs, level);

    public static string Format(SymbolAtPositionResult result, bool includeDocs = false, DetailLevel level = DetailLevel.Full, int? maxBodyLines = null) =>
        NavigationResultFormatter.Format(result, includeDocs, level, maxBodyLines);

    public static string Format(
        IReadOnlyList<BatchItem<FindOverloadsResult>> items, bool includeDocs = false,
        bool includeBody = false, DetailLevel level = DetailLevel.Full, int? maxBodyLines = null) =>
        NavigationResultFormatter.Format(items, includeDocs, includeBody, level, maxBodyLines);

    public static string Format(
        IReadOnlyList<BatchItem<AnalyzeMethodResult>> items, bool includeDocs = false,
        DetailLevel level = DetailLevel.Full, int? maxBodyLines = null) =>
        MethodAnalysisFormatter.Format(items, includeDocs, level, maxBodyLines);

    // References

    public static string Format(
        IReadOnlyList<BatchItem<ReferenceSearchResult>> items, DetailLevel level = DetailLevel.Full) =>
        ReferenceResultFormatter.Format(items, level);

    public static string Format(
        IReadOnlyList<BatchItem<ImpactAnalysisResult>> items, DetailLevel level = DetailLevel.Full) =>
        ImpactAnalysisFormatter.Format(items, level);

    public static string Format(
        IReadOnlyList<BatchItem<FindImplementationsResult>> items, bool includeDocs = false,
        bool includeBody = false, DetailLevel level = DetailLevel.Full,
        int? maxBodyLines = null, bool includeGenerated = false) =>
        ReferenceResultFormatter.Format(items, includeDocs, includeBody, level, maxBodyLines, includeGenerated);

    // Editing

    public static string Format(RenameSymbolResult result) => EditResultFormatter.Format(result);

    public static string Format(IReadOnlyList<ReplaceContentResult> results) =>
        EditResultFormatter.Format(results);

    public static string Format(IReadOnlyList<EditSymbolOpResult> results) =>
        EditResultFormatter.Format(results);

    public static string Format(ApplyCodeFixResult result) => EditResultFormatter.Format(result);

    public static string Format(ChangeSignatureResult result) => EditResultFormatter.Format(result);

    /// <summary>
    ///     Renders a verified batch's op output with its <see cref="EditVerification" /> block prepended.
    ///     The op body is rendered with the truncation budget reduced by the block's length — rendering
    ///     to the full budget and prepending afterwards could push the total past
    ///     <see cref="Pipeline.ResponseTruncator.MaxChars" />, and the pipeline truncator would then cut
    ///     the tail op results. A null verification (verify=None) renders with the full budget, so that
    ///     path is byte-identical.
    /// </summary>
    public static string RenderWithVerification<T>(
        EditVerification? verification, T ops, Func<T, DetailLevel, string> format)
    {
        if (verification is null)
        {
            return ProgressiveRenderer.Render(ops, format);
        }

        string block = VerificationFormatter.Format(verification);
        int bodyBudget = Math.Max(0, ResponseTruncator.MaxChars - block.Length - 2);
        string body = ProgressiveRenderer.Render(ops, format, bodyBudget);
        return $"{block}\n\n{body}";
    }

    /// <summary>
    ///     Fork-tool variant of <see cref="RenderWithVerification{T}" />: prepends the
    ///     <see cref="EditVerification" /> block to an already-rendered <paramref name="body" /> (for
    ///     <c>rename_symbol</c> / <c>apply_code_fix</c>). Unlike the batch overload, no truncation-budget
    ///     reduction is needed — these bodies are bounded file lists, and
    ///     <see cref="Pipeline.ResponseTruncator" /> cuts from the end, so the prepended block always
    ///     survives. A null verification (verify=None) returns the body unchanged, keeping that path
    ///     byte-identical.
    /// </summary>
    public static string RenderWithVerification(EditVerification? verification, string body) =>
        VerificationFormatter.Prepend(verification, body);

    // Type hierarchy

    public static string Format(
        IReadOnlyList<BatchItem<TypeHierarchyResult>> items, bool includeDocs = false,
        DetailLevel level = DetailLevel.Full) =>
        TypeHierarchyResultFormatter.Format(items, includeDocs, level);

    // Usings

    public static string Format(AddUsingsResult result) => UsingResultFormatter.Format(result);

    public static string Format(RemoveUnusedUsingsResult result) => UsingResultFormatter.Format(result);

    // Diagnostics

    public static string Format(DiagnosticsResult result, DetailLevel level = DetailLevel.Full) =>
        DiagnosticResultFormatter.Format(result, level);

    public static string Format(IncrementalDiagnosticsResult result) =>
        DiagnosticResultFormatter.Format(result);

    // Workspace

    public static string Format(WorkspaceInfoResult result, DetailLevel level = DetailLevel.Full) =>
        WorkspaceInfoFormatter.Format(result, level);

    public static string Format(ReloadResult result) =>
        $"Workspace reloaded successfully.\n{result.ProjectCount} projects, {result.TotalDocs} documents.";

    public static string Format(UnusedReferencesResult result) =>
        UnusedReferenceFormatter.Format(result);

    public static string Format(ResetBaselineResult result) => DiagnosticResultFormatter.Format(result);
}
