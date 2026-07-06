using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using Zphil.Roz.Presentation;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Fixtures;

/// <summary>
///     Thin test-only wrappers over <see cref="CodeEditTools.EditSymbol" /> that preserve the
///     pre-merge, pre-batch call shape (<c>ReplaceSymbol</c>, <c>RemoveSymbol</c>,
///     <c>InsertSymbol</c>). Each wrapper drives the batch service with a 1-element request
///     and re-throws per-op errors as exceptions so existing <c>Should.ThrowAsync</c> tests
///     continue to work unchanged.
/// </summary>
internal static class CodeEditToolsTestExtensions
{
    internal static Task<string> ReplaceSymbol(
        this CodeEditTools tools,
        string filePath,
        string symbolName,
        string newDeclaration,
        int? line = null,
        int? column = null,
        string? containingType = null,
        SymbolicKind? kind = null,
        CancellationToken ct = default) =>
        RunSingleAsync(tools, new EditSymbolRequest(
            EditSymbolAction.Replace,
            TestFileHelper.Loc(filePath, line, column),
            symbolName,
            newDeclaration,
            ContainingType: containingType,
            Kind: kind), ct);

    internal static Task<string> RemoveSymbol(
        this CodeEditTools tools,
        string filePath,
        string symbolName,
        int? line = null,
        int? column = null,
        string? containingType = null,
        SymbolicKind? kind = null,
        CancellationToken ct = default) =>
        RunSingleAsync(tools, new EditSymbolRequest(
            EditSymbolAction.Remove,
            TestFileHelper.Loc(filePath, line, column),
            symbolName,
            ContainingType: containingType,
            Kind: kind), ct);

    internal static Task<string> InsertSymbol(
        this CodeEditTools tools,
        string filePath,
        string symbolName,
        string content,
        InsertPosition position = InsertPosition.After,
        int? line = null,
        int? column = null,
        string? containingType = null,
        SymbolicKind? kind = null,
        CancellationToken ct = default) =>
        RunSingleAsync(tools, new EditSymbolRequest(
            EditSymbolAction.Insert,
            TestFileHelper.Loc(filePath, line, column),
            symbolName,
            Content: content,
            Position: position,
            ContainingType: containingType,
            Kind: kind), ct);

    private static async Task<string> RunSingleAsync(CodeEditTools tools, EditSymbolRequest request, CancellationToken ct)
    {
        EditSymbolBatchOutcome outcome = await tools.SymbolEditServiceForTests.EditSymbolBatchAsync([request], ct: ct);
        IReadOnlyList<EditSymbolOpResult> results = outcome.Ops;
        if (results[0] is EditSymbolErrorOp err)
        {
            throw new UserErrorException(err.Error);
        }

        return ResponseFormatter.Format(results);
    }
}
