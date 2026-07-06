using System.Text;
using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using static Zphil.Roz.Presentation.FormattingHelpers;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Formats results for edit tools: edit_symbol (replace/remove/insert actions),
///     rename_symbol, replace_content.
/// </summary>
internal static class EditResultFormatter
{
    /// <summary>
    ///     Formats an edit_symbol (action=replace) confirmation with old/new line counts and file location.
    /// </summary>
    /// <example>
    ///     <code>
    ///     Replaced 'Describe' (3 -> 5 lines)
    ///     File: TestFixture/Shapes/Triangle.cs
    ///     Starting at line 16.
    ///     </code>
    /// </example>
    private static string Format(ReplaceSymbolResult result)
    {
        if (result.IsNoOp)
        {
            return $"No changes made — new declaration for '{result.SymbolName}' is identical to existing code in {result.RelPath}.";
        }

        string text = $"Replaced '{result.SymbolName}' ({result.OldLineCount} -> {result.NewLineCount} lines)\n" +
                      $"File: {result.RelPath}\n" +
                      $"Starting at line {result.StartLine}.";
        return result.TotalMatches > 1
            ? $"{text}\n{FormatAmbiguityWarning(result.SymbolName, result.TotalMatches, result.StartLine)}"
            : text;
    }

    /// <summary>
    ///     Formats an edit_symbol (action=remove) confirmation with the removed line count and file location.
    /// </summary>
    /// <example>
    ///     <code>
    ///     Removed 'Describe' (5 lines) from TestFixture/Shapes/Triangle.cs
    ///     Was at line 16.
    ///     </code>
    /// </example>
    private static string Format(RemoveSymbolResult result)
    {
        string text = $"Removed '{result.SymbolName}' ({result.RemovedLineCount} lines) from {result.RelPath}\n" +
                      $"Was at line {result.StartLine}.";
        return result.TotalMatches > 1
            ? $"{text}\n{FormatAmbiguityWarning(result.SymbolName, result.TotalMatches, result.StartLine)}"
            : text;
    }

    /// <summary>
    ///     Formats an edit_symbol (action=insert) confirmation.
    /// </summary>
    /// <example>
    ///     <code>
    ///     Inserted 5 line(s) after 'Describe' in TestFixture/Shapes/Circle.cs.
    ///     </code>
    /// </example>
    private static string Format(InsertResult result)
    {
        string direction = result.After ? "after" : "before";
        var text = $"Inserted {result.LineCount} line(s) {direction} '{result.SymbolName}' in {result.RelPath}.";
        return result.TotalMatches > 1
            ? $"{text}\n{FormatAmbiguityWarning(result.SymbolName, result.TotalMatches)}"
            : text;
    }

    /// <summary>
    ///     Formats a rename_symbol confirmation with the list of changed files.
    /// </summary>
    /// <example>
    ///     <code>
    ///     Renamed 'ProcessShape' to 'HandleShape'.
    ///     Changed 2 file(s):
    ///       TestFixture/Services/ShapeService.cs
    ///       TestFixture/Services/ShapeHelper.cs
    ///     </code>
    /// </example>
    public static string Format(RenameSymbolResult result)
    {
        if (result.ChangedDocs.Count == 0)
        {
            return $"Symbol is already named '{result.NewName}'. No changes made.";
        }

        // A DryRun stages nothing; word the body as a preview so it doesn't read as an applied edit.
        bool written = result.Verification is null || result.Verification.Committed;
        string verb = written ? "Renamed" : "Would rename";
        string changed = written ? "Changed" : "Would change";

        var sb = new StringBuilder();
        sb.AppendLine($"{verb} '{result.OldName}' to '{result.NewName}'.");
        sb.AppendLine($"{changed} {result.ChangedDocs.Count} file(s):");
        foreach (string doc in result.ChangedDocs.OrderBy(d => d))
        {
            sb.AppendLine($"  {doc}");
        }

        if (result.DisabledBranchFixups > 0)
        {
            sb.AppendLine();
            string fixupVerb = written ? "were updated" : "would be updated";
            sb.Append($"Note: {result.DisabledBranchFixups} occurrence(s) in inactive preprocessor branches (#if/#else) {fixupVerb} via text replacement.");
        }

        if (result.DeferredFileRenameNote is not null)
        {
            sb.AppendLine();
            sb.Append($"Note: {result.DeferredFileRenameNote}");
        }

        if (result.StrayReferences is { Count: > 0 } strays)
        {
            sb.AppendLine();
            AppendStrayReferenceWarning(sb, result.OldName, strays);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Formats an apply_code_fix result: a one-line skip when nothing matched or changed, otherwise the
    ///     fix title, the occurrence and file counts, and the changed-file list. The tool layer prepends any
    ///     verification (DryRun/Delta) block.
    /// </summary>
    /// <example>
    ///     <code>
    ///     Fixed 3 occurrence(s) of 'xUnit2004' in 2 file(s) — Use Assert.True.
    ///       TestFixture.Tests/AssertBoolTests.cs
    ///       TestFixture.Tests/MoreAssertTests.cs
    ///     </code>
    /// </example>
    public static string Format(ApplyCodeFixResult result)
    {
        if (result.SkippedReason is not null)
        {
            return $"apply_code_fix '{result.DiagnosticId}': {result.SkippedReason} — nothing changed.";
        }

        // A DryRun stages nothing; word the body as a preview so it doesn't read as an applied edit.
        bool written = result.Verification is null || result.Verification.Committed;
        string verb = written ? "Fixed" : "Would fix";

        var sb = new StringBuilder();
        sb.AppendLine(
            $"{verb} {result.FixedDiagnosticCount} occurrence(s) of '{result.DiagnosticId}' in {result.ChangedDocs.Count} file(s) — {result.FixTitle}.");
        foreach (string doc in result.ChangedDocs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"  {doc}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Formats a change_signature result: a one-line skip when nothing matched, otherwise the old→new
    ///     parameter lists, the declaration and call-site rewrite counts, the changed-file list, and any
    ///     advisory notes. The tool layer prepends any verification (DryRun/Delta) block.
    /// </summary>
    /// <example>
    ///     <code>
    ///     Changed signature of 'Log' — (string msg, int level) → (string msg).
    ///     Rewrote 1 declaration(s) and 1 call site(s) across 1 file(s):
    ///       TestFixture/Services/SignatureChangeSurface.cs
    ///     </code>
    /// </example>
    public static string Format(ChangeSignatureResult result)
    {
        if (result.SkippedReason is not null)
        {
            return $"change_signature '{result.MethodName}': {result.SkippedReason} — nothing changed.";
        }

        // A DryRun stages nothing; word the body as a preview so it doesn't read as an applied edit.
        bool written = result.Verification is null || result.Verification.Committed;
        string changeVerb = written ? "Changed" : "Would change";
        string rewriteVerb = written ? "Rewrote" : "Would rewrite";

        var sb = new StringBuilder();
        sb.AppendLine($"{changeVerb} signature of '{result.MethodName}' — {result.OldSignature} → {result.NewSignature}.");
        sb.AppendLine(
            $"{rewriteVerb} {result.DeclarationsRewritten} declaration(s) and {result.CallSitesRewritten} call site(s) across {result.ChangedDocs.Count} file(s):");
        foreach (string doc in result.ChangedDocs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"  {doc}");
        }

        if (result.Notes is { Count: > 0 } notes)
        {
            sb.AppendLine();
            foreach (string note in notes)
            {
                sb.AppendLine($"Note: {note}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendStrayReferenceWarning(StringBuilder sb, string oldName, IReadOnlyList<StrayReference> strays)
    {
        const int MaxListed = 10;
        List<StrayReference> sorted = strays.OrderBy(s => s.RelPath, StringComparer.OrdinalIgnoreCase).ToList();
        sb.AppendLine($"WARNING: {sorted.Count} file(s) outside the loaded solution still reference '{oldName}':");

        foreach (StrayReference stray in sorted.Take(MaxListed))
        {
            string occurrences = stray.Count == 1 ? "1 occurrence" : $"{stray.Count} occurrences";
            sb.AppendLine($"  {stray.RelPath} ({occurrences}, first at line {stray.FirstLine})");
        }

        int remaining = sorted.Count - MaxListed;
        if (remaining > 0)
        {
            sb.AppendLine($"  … and {remaining} more file(s).");
        }

        sb.AppendLine("These files are not in the loaded solution; references were NOT updated.");
        sb.Append("Either add the projects to the .sln and re-run rename_symbol, or fix manually with replace_content.");
    }

    /// <summary>
    ///     Formats a replace_content confirmation with the number of replacements made.
    /// </summary>
    /// <example>
    ///     <code>Replaced 3 occurrence(s) in TestFixture/Services/ShapeService.cs.</code>
    /// </example>
    private static string Format(ReplaceContentResult result)
    {
        if (result.Error is not null)
        {
            return $"Error in {result.RelPath}: {result.Error}";
        }

        if (result.IsNoOp)
        {
            return $"No changes made — old and new content are identical in {result.RelPath}.";
        }

        var text = $"Replaced {result.MatchCount} occurrence(s) in {result.RelPath}.";
        if (result.MatchLineNumbers is { Count: > 0 and <= 20 })
        {
            text += $"\nAt line(s): {String.Join(", ", result.MatchLineNumbers)}";
        }

        return text;
    }

    /// <summary>
    ///     Formats a batch of replace_content results — one section per op.
    /// </summary>
    public static string Format(IReadOnlyList<ReplaceContentResult> results) =>
        FormatBatch(results, BuildBatchHeader, Format);

    private static string BuildBatchHeader(ReplaceContentResult r)
    {
        string search = r.Search ?? "";
        string oneLine = search.Replace("\r", "").Replace("\n", "\\n");
        string preview = oneLine.Length > 40 ? oneLine[..40] + "…" : oneLine;
        return $"{r.RelPath}: \"{preview}\"";
    }

    /// <summary>
    ///     Formats one edit_symbol batch entry — either an error line or the matching
    ///     single-action formatter output from the contained sub-result.
    /// </summary>
    private static string Format(EditSymbolOpResult result) => result switch
    {
        EditSymbolReplaceOp r => Format(r.Result),
        EditSymbolRemoveOp r => Format(r.Result),
        EditSymbolInsertOp r => Format(r.Result),
        EditSymbolErrorOp e => $"Error on {ActionWord(e.Action)} '{e.SymbolName}' in {e.FilePath}: {e.Error}",
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Unknown EditSymbolOpResult subtype.")
    };

    /// <summary>
    ///     Formats a batch of edit_symbol results — one section per op. Reuses per-action
    ///     formatters for successful ops and renders errors inline on failed ops.
    /// </summary>
    public static string Format(IReadOnlyList<EditSymbolOpResult> results) =>
        FormatBatch(results, BuildEditSymbolBatchHeader, Format);

    private static string BuildEditSymbolBatchHeader(EditSymbolOpResult r) => r switch
    {
        EditSymbolReplaceOp op => $"replace '{op.SymbolName}' in {op.Result.RelPath}",
        EditSymbolRemoveOp op => $"remove '{op.SymbolName}' in {op.Result.RelPath}",
        EditSymbolInsertOp op => $"insert '{op.SymbolName}' in {op.Result.RelPath}",
        EditSymbolErrorOp op => $"{ActionWord(op.Action)} '{op.SymbolName}' in {op.FilePath}",
        _ => throw new ArgumentOutOfRangeException(nameof(r), r, "Unknown EditSymbolOpResult subtype.")
    };

    private static string ActionWord(EditSymbolAction a) => a switch
    {
        EditSymbolAction.Replace => "replace",
        EditSymbolAction.Remove => "remove",
        EditSymbolAction.Insert => "insert",
        _ => a.ToString().ToLowerInvariant()
    };
}
