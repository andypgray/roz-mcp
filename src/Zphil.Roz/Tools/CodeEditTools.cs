using System.ComponentModel;
using Microsoft.CodeAnalysis.Rename;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Zphil.Roz.Constants;
using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using Zphil.Roz.Presentation;
using Zphil.Roz.Services;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Tools;

/// <summary>
///     MCP tools for editing code: replace symbol bodies, insert code, rename, and text replacement.
///     All edit tools write to disk and update the workspace incrementally.
/// </summary>
[McpServerToolType]
internal sealed class CodeEditTools(
    SymbolEditService symbolEditService,
    RenameService renameService,
    TextReplacementService textReplacementService,
    CodeFixService codeFixService,
    ChangeSignatureService changeSignatureService)
{
    /// <summary>
    ///     Test-only accessor: lets <c>CodeEditToolsTestExtensions</c> wrappers run 1-element
    ///     batches through the batch service and observe per-op <see cref="EditSymbolErrorOp" /> results,
    ///     re-throwing to preserve the pre-batch throw semantics existing tests depend on.
    /// </summary>
    internal SymbolEditService SymbolEditServiceForTests => symbolEditService;

    [McpServerTool(Name = "edit_symbol", Destructive = true, Idempotent = false, OpenWorld = false, Title = "Edit Symbol")]
    [Description("Replace/remove/insert at named symbols. Each `edits[]` op applies independently. action=replace preserves doc comments; omit them from newDeclaration.")]
    public async Task<string> EditSymbol(
        [Description("Edit operations.")] EditSymbolRequest[] edits,
        [Description(ToolDescriptions.Verify)] VerifyMode verify = VerifyMode.None,
        IProgress<ProgressNotificationValue> progress = null!,
        CancellationToken ct = default)
    {
        BatchGuards.RejectEmptyBatch(edits);
        EditSymbolBatchOutcome outcome = await symbolEditService.EditSymbolBatchAsync(edits, verify, progress, ct);
        return ResponseFormatter.RenderWithVerification(outcome.Verification, outcome.Ops, (r, _) => ResponseFormatter.Format(r));
    }

    [McpServerTool(Name = "rename_symbol", Destructive = true, Idempotent = false, OpenWorld = false, Title = "Rename Symbol")]
    [Description("Rename symbol solution-wide (no namespaces/ctors/operators/indexers).")]
    public async Task<string> RenameSymbol(
        [Description("File path with optional cursor: 'path' (uses symbolName + containingType to disambiguate) or 'path:line:col' (positional disambiguator).")]
        string location,
        [Description("Name of the symbol to rename.")]
        string symbolName,
        string newName,
        [Description(ToolDescriptions.ContainingType)]
        string? containingType = null,
        [Description(ToolDescriptions.Kind)] SymbolicKind? kind = null,
        [Description("Also rename the containing file.")]
        bool renameFile = false,
        [Description("Also rename overloads.")]
        bool renameOverloads = false,
        [Description("Rename in string literals.")]
        bool renameInStrings = false,
        [Description("Rename in comments.")] bool renameInComments = false,
        [Description(ToolDescriptions.Verify)] VerifyMode verify = VerifyMode.None,
        IProgress<ProgressNotificationValue> progress = null!,
        CancellationToken ct = default)
    {
        LocationArg loc = LocationParser.ParseFileOrCursor(location, "rename_symbol", false);
        var options = new SymbolRenameOptions(
            renameOverloads,
            renameInStrings,
            renameInComments,
            renameFile);
        int? line = (loc as CursorLocation)?.Line;
        int? column = (loc as CursorLocation)?.Column;
        RenameSymbolResult result = await renameService.RenameSymbolAsync(loc.FilePath, symbolName, line, column, newName, options, verify, progress, ct, containingType, kind);
        return ResponseFormatter.RenderWithVerification(result.Verification, ResponseFormatter.Format(result));
    }

    [McpServerTool(Name = "replace_content", Destructive = true, Idempotent = false, OpenWorld = false, Title = "Replace Content")]
    [Description("Find/replace in .cs files (workspace-aware; use Edit for non-C#). Each `edits[]` op applies independently. Literal (default) supports multi-line search with CRLF/LF normalization.")]
    public async Task<string> ReplaceContent(
        [Description("Replacement operations.")]
        ReplaceContentRequest[] edits,
        [Description(ToolDescriptions.Verify)] VerifyMode verify = VerifyMode.None,
        IProgress<ProgressNotificationValue> progress = null!,
        CancellationToken ct = default)
    {
        BatchGuards.RejectEmptyBatch(edits);
        ReplaceContentBatchOutcome outcome = await textReplacementService.ReplaceContentBatchAsync(edits, verify, progress, ct);
        return ResponseFormatter.RenderWithVerification(outcome.Verification, outcome.Ops, (r, _) => ResponseFormatter.Format(r));
    }

    [McpServerTool(Name = "apply_code_fix", Destructive = true, Idempotent = false, OpenWorld = false, Title = "Apply Code Fix")]
    [Description("Apply a registered Roslyn code fix (FixAll) for one diagnostic ID across a scope.")]
    public async Task<string> ApplyCodeFix(
        [Description(ToolDescriptions.ApplyCodeFixDiagnosticId)]
        string diagnosticId,
        [Description(ToolDescriptions.ApplyCodeFixEquivalenceKey)]
        string? equivalenceKey = null,
        [Description(ToolDescriptions.Project)]
        string? project = null,
        [Description(ToolDescriptions.FilePathsFilter)]
        string[]? filePaths = null,
        [Description(ToolDescriptions.IncludeTests)]
        bool includeTests = false,
        [Description(ToolDescriptions.Verify)] VerifyMode verify = VerifyMode.None,
        IProgress<ProgressNotificationValue> progress = null!,
        CancellationToken ct = default)
    {
        ApplyCodeFixResult result = await codeFixService.ApplyCodeFixAsync(
            diagnosticId, equivalenceKey, project, filePaths, includeTests, verify, progress, ct);
        return ResponseFormatter.RenderWithVerification(result.Verification, ResponseFormatter.Format(result));
    }

    [McpServerTool(Name = "change_signature", Destructive = true, Idempotent = false, OpenWorld = false, Title = "Change Signature")]
    [Description("Add-optional / remove-unused / reorder a method's parameters across its declaration family and all call sites; analysis-gated (refuses on any unsafe or un-rewritable site).")]
    public async Task<string> ChangeSignature(
        [Description("File path with optional cursor: 'path' or 'path:line:col'.")]
        string location,
        [Description("Method name to change.")]
        string symbolName,
        [Description(ToolDescriptions.NewSignature)]
        string newSignature,
        [Description(ToolDescriptions.ContainingType)]
        string? containingType = null,
        [Description(ToolDescriptions.Kind)] SymbolicKind? kind = null,
        [Description(ToolDescriptions.Verify)] VerifyMode verify = VerifyMode.None,
        IProgress<ProgressNotificationValue> progress = null!,
        CancellationToken ct = default)
    {
        ChangeSignatureResult result = await changeSignatureService.ChangeSignatureAsync(
            location, symbolName, newSignature, containingType, kind, verify, progress, ct);
        return ResponseFormatter.RenderWithVerification(result.Verification, ResponseFormatter.Format(result));
    }
}
