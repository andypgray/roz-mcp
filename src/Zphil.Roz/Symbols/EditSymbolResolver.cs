using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Constants;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Symbols;

/// <summary>
///     Resolves symbols from file path + name/position into a syntax context
///     suitable for editing operations.
/// </summary>
internal sealed class EditSymbolResolver(WorkspaceManager workspaceManager)
{
    /// <summary>
    ///     Resolves a symbol by name or position within a file, returning the document,
    ///     syntax root, target node, and resolved file path.
    /// </summary>
    public async Task<ResolvedSymbolContext> ResolveAsync(
        string filePath, string symbolName, int? line, int? column, CancellationToken ct,
        string? containingType = null, SymbolicKind? kind = null, bool preferName = false,
        Solution? solutionOverride = null)
    {
        (Solution solution, Document document, string resolvedPath) = await ResolveDocumentAsync(filePath, ct, solutionOverride);
        EnsureCSharpDocument(document, filePath);

        // Edit path (preferName): the symbol name — scoped to this file plus containingType/kind —
        // is authoritative. A :line:col cursor only tie-breaks same-name overloads and may be
        // stale, because an earlier op in the same batch can shift every later op's pre-batch
        // coordinates. Resolve by name first; fall through to the position/name logic below only
        // when the name alone cannot decide (0 matches, or >1 with no cursor).
        if (preferName && !String.IsNullOrEmpty(symbolName))
        {
            ResolvedSymbolContext? byName = await TryResolvePreferNameAsync(
                document, filePath, resolvedPath, symbolName, line, column, containingType, kind, ct);
            if (byName is not null)
            {
                return byName;
            }
        }

        if (line.HasValue)
        {
            int resolvedColumn = column ?? 1;
            return await ResolveByPositionAsync(
                solution, document, filePath, resolvedPath, line.Value, resolvedColumn, symbolName, ct);
        }

        return await ResolveByNameAsync(document, filePath, resolvedPath, symbolName, containingType, kind, ct);
    }

    /// <summary>
    ///     Resolves a document from its file path within the loaded solution.
    /// </summary>
    /// <remarks>
    ///     When <paramref name="solutionOverride" /> is set (a mid-batch verified edit resolving against
    ///     the fork that carries the prior op's change), path resolution still runs against the live
    ///     workspace — file paths don't move within a batch — but the freshness sync and
    ///     <see cref="WorkspaceManager.GetSolutionAsync" /> are skipped: syncing would reload the file
    ///     from disk (which lacks the un-committed prior edit) and hand back a snapshot diverging from
    ///     the fork. The document is taken from the override instead.
    /// </remarks>
    private async Task<(Solution Solution, Document Document, string ResolvedPath)> ResolveDocumentAsync(
        string filePath, CancellationToken ct, Solution? solutionOverride)
    {
        string resolvedPath = await FilePathResolver.ResolveAgainstSolutionAsync(filePath, workspaceManager, ct);

        Solution solution;
        if (solutionOverride is not null)
        {
            solution = solutionOverride;
        }
        else
        {
            await workspaceManager.EnsureFilesFreshAsync([resolvedPath], ct);
            solution = await workspaceManager.GetSolutionAsync(ct);
        }

        Document? document = solution.GetDocumentByPath(resolvedPath);
        if (document is null)
        {
            throw new UserErrorException(ErrorMessages.FileNotInSolution(filePath));
        }

        return (solution, document, resolvedPath);
    }

    /// <summary>
    ///     Throws if the document's project language is not C#.
    ///     Edit tools only support C# syntax parsing and manipulation.
    /// </summary>
    internal static void EnsureCSharpDocument(Document document, string filePath)
    {
        if (document.Project.Language != LanguageNames.CSharp)
        {
            throw new UserErrorException(
                $"Edit tools only support C# files. '{filePath}' is in a {document.Project.Language} project.");
        }
    }

    private static async Task<ResolvedSymbolContext> ResolveByNameAsync(
        Document document, string filePath, string resolvedPath, string symbolName, string? containingType,
        SymbolicKind? kind, CancellationToken ct)
    {
        SemanticModel? model = await document.GetSemanticModelAsync(ct);
        SyntaxTree? tree = await document.GetSyntaxTreeAsync(ct);
        if (model is null || tree is null)
        {
            throw new UserErrorException(ErrorMessages.CouldNotAnalyze(filePath));
        }

        SyntaxNode root = await tree.GetRootAsync(ct);
        (SyntaxNode? targetNode, int totalMatches, SymbolicKind[]? droppedKinds) =
            FindDeclarationByName(root, model, symbolName, containingType, kind, ct);
        if (targetNode is null)
        {
            List<string> allNames = CollectDeclarationNames(root);
            List<string> suggestions = FuzzyMatcher.GetSuggestions(symbolName, allNames);
            string didYouMean = FuzzySuggestionHelper.FormatSuggestionSuffix(suggestions);
            string containingTypeHint = containingType is not null ? $" in type '{containingType}'" : "";
            string kindHint = kind.HasValue ? $" with kind '{kind}'" : "";
            string kindBlame = droppedKinds is { Length: > 0 }
                ? "\n" + KindFilterBlame.FormatHint(symbolName, droppedKinds)
                : "";
            throw new UserErrorException(
                $"Symbol '{symbolName}'{containingTypeHint}{kindHint} not found in {filePath}.{didYouMean} " +
                $"Check spelling and casing (names are case-sensitive). Use get_symbols_overview to list symbols in this file.{kindBlame}");
        }

        return new ResolvedSymbolContext(document, root, targetNode, resolvedPath, totalMatches);
    }

    /// <summary>
    ///     Edit-path resolution where the symbol name is authoritative. Returns the resolved
    ///     context when the name (scoped to this file plus <paramref name="containingType" />/
    ///     <paramref name="kind" />) decides the target, or <c>null</c> to let
    ///     <see cref="ResolveAsync" /> fall through to its existing position/name logic.
    /// </summary>
    /// <remarks>
    ///     <list type="bullet">
    ///         <item>
    ///             0 matches → <c>null</c> (fall through preserves the position cross-check and
    ///             the friendly "not found" suggestions for a genuine name typo).
    ///         </item>
    ///         <item>
    ///             Exactly 1 match → that node; any (possibly stale) cursor is ignored. This is
    ///             the dominant batch-delete case: one uniquely-named member in a mutating god-file.
    ///         </item>
    ///         <item>
    ///             &gt;1 match, no cursor → <c>null</c> (fall through to the documented path-only
    ///             "first node + TotalMatches=count" contract, unchanged).
    ///         </item>
    ///         <item>
    ///             &gt;1 match, cursor present → the overload whose <em>identifier-token</em>
    ///             span contains the cursor (resolved exactly, never clamped). A stale or
    ///             out-of-range cursor lands on no name token, so an actionable ambiguity error
    ///             is thrown rather than the wrong overload being edited.
    ///         </item>
    ///     </list>
    /// </remarks>
    private static async Task<ResolvedSymbolContext?> TryResolvePreferNameAsync(
        Document document, string filePath, string resolvedPath, string symbolName,
        int? line, int? column, string? containingType, SymbolicKind? kind, CancellationToken ct)
    {
        SemanticModel? model = await document.GetSemanticModelAsync(ct);
        SyntaxTree? tree = await document.GetSyntaxTreeAsync(ct);
        if (model is null || tree is null)
        {
            throw new UserErrorException(ErrorMessages.CouldNotAnalyze(filePath));
        }

        SyntaxNode root = await tree.GetRootAsync(ct);
        (IReadOnlyList<(SyntaxNode Node, ISymbol Symbol)> matches, _) =
            FindAllDeclarationsByName(root, model, symbolName, containingType, kind, ct);

        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Count == 1)
        {
            return new ResolvedSymbolContext(document, root, matches[0].Node, resolvedPath);
        }

        if (!line.HasValue)
        {
            return null;
        }

        // >1 match with a cursor: match it against each candidate's identifier-token span
        // (Symbol.Locations), NOT the declaration span — a stale cursor cannot land on another
        // overload's tiny name token, so it yields no hit and we throw an actionable ambiguity
        // error instead of silently editing the wrong overload. The cursor is resolved without
        // clamping: an out-of-range line OR column (the file shrank under an earlier batch op)
        // becomes "no position", routing to the ambiguity error — clamping to end-of-line could
        // land the offset inside a neighbouring overload's name token (a false hit).
        SourceText text = await document.GetTextAsync(ct);
        int? position = TryGetExactPosition(text, line.Value, column ?? 1);

        List<(SyntaxNode Node, ISymbol Symbol)> hits = position is { } pos
            ? matches.Where(m => m.Symbol.Locations.Any(l => l.IsInSource && l.SourceSpan.Contains(pos))).ToList()
            : [];

        if (hits.Count == 1)
        {
            return new ResolvedSymbolContext(document, root, hits[0].Node, resolvedPath);
        }

        string containingTypeHint = containingType is not null ? $" in type '{containingType}'" : "";
        throw new UserErrorException(
            $"'{symbolName}'{containingTypeHint} is ambiguous in {filePath}: {matches.Count} declarations share that name " +
            "and the cursor did not land on any of their name tokens (it may be stale after an earlier edit in this batch). " +
            "Put a precise 'path:line:col' cursor on the intended declaration's name, or narrow with containingType/kind.");
    }

    /// <summary>
    ///     The 0-based offset of a 1-based <paramref name="line" />/<paramref name="column" />, or
    ///     <c>null</c> when the cursor is out of range (line past EOF, or column past end-of-line).
    ///     Unlike <see cref="RoslynExtensions.GetPosition" /> this neither clamps nor throws:
    ///     for stale-cursor overload disambiguation a clamped end-of-line offset could fall inside
    ///     a *different* overload's name token (a false hit), so an out-of-range cursor must yield
    ///     "no match" and route to the actionable ambiguity error instead.
    /// </summary>
    private static int? TryGetExactPosition(SourceText text, int line, int column)
    {
        if (line < 1 || line > text.Lines.Count)
        {
            return null;
        }

        TextLine textLine = text.Lines[line - 1];
        int zeroColumn = column - 1;
        return zeroColumn >= 0 && zeroColumn <= textLine.Span.Length
            ? textLine.Start + zeroColumn
            : null;
    }

    private static async Task<ResolvedSymbolContext> ResolveByPositionAsync(
        Solution solution, Document document, string filePath, string resolvedPath, int line, int column, string? symbolName, CancellationToken ct)
    {
        SymbolResolution resolution = await solution.ResolveSymbolAtPositionAsync(
            resolvedPath, line, column, true, ct);
        if (resolution.Symbol is null)
        {
            throw NoSymbolAtPositionException(filePath, line, column, resolution.PositionDescription);
        }

        ISymbol symbol = resolution.Symbol;

        if (!String.IsNullOrEmpty(symbolName) && !SymbolResolver.NameMatchesSymbol(symbol, symbolName))
        {
            string resolvedKind = symbol.GetKindString();
            throw new UserErrorException(
                $"Position {LocationFormat.Format(filePath, line, column)} resolved to '{symbol.Name}' ({resolvedKind}) but symbolName is '{symbolName}'. " +
                "Verify the line/column points to the intended symbol.");
        }

        // Find the declaring syntax node in this document
        string fullResolvedPath = Path.GetFullPath(resolvedPath);
        SyntaxNode? targetNode = null;
        foreach (SyntaxReference syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            string syntaxRefFullPath = Path.GetFullPath(syntaxRef.SyntaxTree.FilePath);
            if (String.Equals(syntaxRefFullPath, fullResolvedPath, StringComparison.OrdinalIgnoreCase))
            {
                targetNode = await syntaxRef.GetSyntaxAsync(ct);
                break;
            }
        }

        if (targetNode is null)
        {
            throw new UserErrorException(
                $"Symbol '{symbol.Name}' at {LocationFormat.Format(filePath, line, column)} has no declaration in this file — it may be defined in metadata or another file. Use go_to_definition to find its source.");
        }

        SyntaxNode root = (await document.GetSyntaxRootAsync(ct))!;
        return new ResolvedSymbolContext(document, root, targetNode, resolvedPath);
    }

    private static (SyntaxNode? Node, int TotalMatches, SymbolicKind[]? DroppedKinds) FindDeclarationByName(
        SyntaxNode root, SemanticModel model, string symbolName, string? containingType, SymbolicKind? kind, CancellationToken ct)
    {
        (IReadOnlyList<(SyntaxNode Node, ISymbol Symbol)> matches, SymbolicKind[]? droppedKinds) =
            FindAllDeclarationsByName(root, model, symbolName, containingType, kind, ct);
        SyntaxNode? firstMatch = matches.Count > 0 ? matches[0].Node : null;
        return (firstMatch, matches.Count, droppedKinds);
    }

    /// <summary>
    ///     Collects every declaration in <paramref name="root" /> whose name (and optional
    ///     <paramref name="containingType" />/<paramref name="kind" />) matches, each paired with
    ///     its symbol. The kind filter is applied after name+type so <see cref="KindFilterBlame" />
    ///     can attribute an otherwise-empty result to the kind filter alone.
    /// </summary>
    private static (IReadOnlyList<(SyntaxNode Node, ISymbol Symbol)> Matches, SymbolicKind[]? DroppedKinds) FindAllDeclarationsByName(
        SyntaxNode root, SemanticModel model, string symbolName, string? containingType, SymbolicKind? kind, CancellationToken ct)
    {
        // Collect (node, symbol) pairs that match name + containingType without applying the
        // kind filter, so KindFilterBlame can detect when the kind filter alone is responsible
        // for an empty result.
        List<(SyntaxNode Node, ISymbol Symbol)> nameMatches = [];

        foreach (SyntaxNode node in root.DescendantNodes())
        {
            if (node is not (MemberDeclarationSyntax or LocalFunctionStatementSyntax))
            {
                continue;
            }

            // Field/event-field declarations: the symbol lives on VariableDeclaratorSyntax, not the
            // parent FieldDeclarationSyntax, so GetDeclaredSymbol on the parent returns null.
            // Check each declarator and return the parent field declaration as the target node.
            if (node is BaseFieldDeclarationSyntax fieldDecl)
            {
                foreach (VariableDeclaratorSyntax declarator in fieldDecl.Declaration.Variables)
                {
                    if (!String.Equals(declarator.Identifier.ValueText, symbolName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    ISymbol? fieldSymbol = model.GetDeclaredSymbol(declarator, ct);
                    if (fieldSymbol is not null && MatchesContainingType(fieldSymbol, containingType))
                    {
                        nameMatches.Add((fieldDecl, fieldSymbol));
                    }
                }

                continue;
            }

            // Cheap identifier check avoids expensive GetDeclaredSymbol calls on non-matching nodes.
            // Returns null for constructors (.ctor), destructors, operators — those fall through
            // to the semantic check below.
            string? identifier = node.GetIdentifierText();
            if (identifier is not null && !String.Equals(identifier, symbolName, StringComparison.Ordinal))
            {
                continue;
            }

            ISymbol? declared = model.GetDeclaredSymbol(node, ct);
            if (declared is not null && String.Equals(declared.Name, symbolName, StringComparison.Ordinal)
                                     && MatchesContainingType(declared, containingType))
            {
                nameMatches.Add((node, declared));
            }
        }

        List<(SyntaxNode Node, ISymbol Symbol)> kindMatches = kind.HasValue
            ? nameMatches.Where(p => p.Symbol.MatchesKindFilter(kind)).ToList()
            : nameMatches;

        SymbolicKind[]? droppedKinds = KindFilterBlame.GetDroppedKinds(
            nameMatches.Select(p => p.Symbol).ToList(), kindMatches.Count, kind);

        return (kindMatches, droppedKinds);
    }

    private static bool MatchesContainingType(ISymbol symbol, string? containingType) =>
        containingType is null ||
        String.Equals(symbol.ContainingType?.Name, containingType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Collects all declaration names from the syntax tree using cheap identifier extraction.
    ///     Only called on the error path for fuzzy suggestion matching.
    /// </summary>
    private static List<string> CollectDeclarationNames(SyntaxNode root)
    {
        List<string> names = [];

        foreach (SyntaxNode node in root.DescendantNodes())
        {
            if (node is not (MemberDeclarationSyntax or LocalFunctionStatementSyntax))
            {
                continue;
            }

            if (node is BaseFieldDeclarationSyntax fieldDecl)
            {
                foreach (VariableDeclaratorSyntax declarator in fieldDecl.Declaration.Variables)
                {
                    names.Add(declarator.Identifier.ValueText);
                }

                continue;
            }

            string? identifier = node.GetIdentifierText();
            if (identifier is not null)
            {
                names.Add(identifier);
            }
        }

        return names;
    }

    internal static UserErrorException NoSymbolAtPositionException(
        string filePath, int line, int column, string? positionDescription) =>
        new($"No symbol found at exact position {LocationFormat.Format(filePath, line, column)} — " +
            $"{positionDescription ?? "unknown"}. " +
            "Cursor must be on the symbol's identifier token.");
}

/// <summary>
///     Encapsulates a resolved symbol's document context for editing operations.
/// </summary>
internal record ResolvedSymbolContext(
    Document Document,
    SyntaxNode Root,
    SyntaxNode TargetNode,
    string ResolvedPath,
    int TotalMatches = 1);
