using System.ComponentModel;
using Zphil.Roz.Constants;
using Zphil.Roz.Enums;

namespace Zphil.Roz.Models;

internal sealed record ReplaceSymbolResult(
    string SymbolName,
    int OldLineCount,
    int NewLineCount,
    int StartLine,
    string RelPath,
    int TotalMatches = 1,
    bool IsNoOp = false);

internal sealed record RemoveSymbolResult(
    string SymbolName,
    int RemovedLineCount,
    int StartLine,
    string RelPath,
    int TotalMatches = 1);

internal sealed record InsertResult(
    string SymbolName,
    int LineCount,
    bool After,
    string RelPath,
    int TotalMatches = 1);

internal sealed record StrayReference(string RelPath, int Count, int FirstLine);

internal sealed record RenameSymbolResult(
    string OldName,
    string NewName,
    List<string> ChangedDocs,
    int DisabledBranchFixups = 0,
    IReadOnlyList<StrayReference>? StrayReferences = null,
    EditVerification? Verification = null,
    string? DeferredFileRenameNote = null);

/// <summary>
///     The outcome of an <c>apply_code_fix</c> call: which diagnostic was fixed, the fixer's title,
///     how many occurrences it covered, the changed files, the optional verification delta, and — when
///     nothing was applied — an informative (non-error) skip reason.
/// </summary>
/// <remarks>
///     <see cref="FixTitle" /> is null and <see cref="ChangedDocs" /> empty exactly when
///     <see cref="SkippedReason" /> is set (no matching diagnostics, or the fixer produced no changes).
/// </remarks>
internal sealed record ApplyCodeFixResult(
    string DiagnosticId,
    string? FixTitle,
    int FixedDiagnosticCount,
    List<string> ChangedDocs,
    EditVerification? Verification = null,
    string? SkippedReason = null);

/// <summary>
///     The outcome of a <c>change_signature</c> call: the method changed, its old and new parameter
///     lists, how many declarations and call sites were rewritten, the changed files, the optional
///     verification delta, any advisory notes (lockstep family, rename suggestion), and — when the
///     gate refused or nothing matched — a skip reason.
/// </summary>
/// <remarks>
///     <see cref="ChangedDocs" /> empty and <see cref="SkippedReason" /> set exactly when the tool
///     wrote nothing because there was nothing to change. A refusal (an unsafe or un-rewritable site)
///     is a <see cref="UserErrorException" />, not a skip.
/// </remarks>
internal sealed record ChangeSignatureResult(
    string MethodName,
    string OldSignature,
    string NewSignature,
    int DeclarationsRewritten,
    int CallSitesRewritten,
    List<string> ChangedDocs,
    EditVerification? Verification = null,
    IReadOnlyList<string>? Notes = null,
    string? SkippedReason = null);

internal sealed record ReplaceContentResult(
    int MatchCount,
    string RelPath,
    List<int>? MatchLineNumbers = null,
    bool IsNoOp = false,
    string? Error = null,
    string? Search = null);

internal sealed record ReplaceContentRequest(
    [property: Description("File path.")] string FilePath,
    [property: Description("Text to find (multi-line OK), or .NET regex when isRegex=true. Use \\n for newlines in multi-line patterns.")]
    string Search,
    [property: Description("Replacement text. In regex mode: $1/$2 backrefs, \\n = newline, \\t = tab, \\\\ = backslash.")]
    string Replace,
    [property: Description("Treat search as a .NET regex pattern.")]
    bool IsRegex = false,
    [property: Description("Let . match newlines for cross-line patterns. Requires isRegex=true.")]
    bool Singleline = false);

internal sealed record EditSymbolRequest(
    [property: Description("Edit action: Replace|Remove|Insert.")]
    EditSymbolAction Action,
    [property: Description("File path, optionally 'path:line:col'. symbolName (+ containingType/kind) is authoritative; a :line:col cursor only disambiguates same-name overloads and may be omitted or stale.")]
    string Location,
    [property: Description("Name of the symbol to edit.")]
    string SymbolName,
    [property: Description("Replacement declaration. Required when action=replace.")]
    string? NewDeclaration = null,
    [property: Description("Content to insert. Required when action=insert.")]
    string? Content = null,
    [property: Description("Insert position (After|Before) when action=insert.")]
    InsertPosition Position = InsertPosition.After,
    [property: Description(ToolDescriptions.ContainingType)]
    string? ContainingType = null,
    [property: Description(ToolDescriptions.Kind)]
    SymbolicKind? Kind = null);

internal abstract record EditSymbolOpResult(string FilePath, string SymbolName);

internal sealed record EditSymbolReplaceOp(string FilePath, string SymbolName, ReplaceSymbolResult Result)
    : EditSymbolOpResult(FilePath, SymbolName);

internal sealed record EditSymbolRemoveOp(string FilePath, string SymbolName, RemoveSymbolResult Result)
    : EditSymbolOpResult(FilePath, SymbolName);

internal sealed record EditSymbolInsertOp(string FilePath, string SymbolName, InsertResult Result)
    : EditSymbolOpResult(FilePath, SymbolName);

internal sealed record EditSymbolErrorOp(string FilePath, string SymbolName, EditSymbolAction Action, string Error)
    : EditSymbolOpResult(FilePath, SymbolName);
