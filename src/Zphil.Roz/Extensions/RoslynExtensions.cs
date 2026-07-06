using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Constants;
using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Extensions;

/// <summary>
///     Position-based symbol resolution and syntax utilities for Roslyn types.
/// </summary>
internal static class RoslynExtensions
{
    /// <summary>
    ///     Converts a 1-based line and column to a 0-based text position in a document.
    /// </summary>
    public static int GetPosition(this SourceText text, int line, int column, bool clampColumn = false)
    {
        if (line < 1)
        {
            throw new UserErrorException(
                $"Line must be >= 1 (1-based). Got: {line}.");
        }

        if (column < 1)
        {
            throw new UserErrorException(
                $"Column must be >= 1 (1-based). Got: {column}.");
        }

        int zeroLine = line - 1;
        int zeroCol = column - 1;

        if (zeroLine >= text.Lines.Count)
        {
            throw new UserErrorException(
                $"Line {line} is out of range. File has {text.Lines.Count} lines.");
        }

        TextLine textLine = text.Lines[zeroLine];

        if (zeroCol > textLine.Span.Length)
        {
            if (clampColumn)
            {
                zeroCol = textLine.Span.Length;
            }
            else
            {
                throw new UserErrorException(
                    $"Column {column} is past end of line {line} (line has {textLine.Span.Length} characters).");
            }
        }

        return textLine.Start + zeroCol;
    }

    /// <summary>
    ///     Returns a relative path from the solution directory, using backslashes (Windows convention).
    /// </summary>
    public static string GetRelativePath(this Location location, string solutionDir)
    {
        string filePath = location.GetLineSpan().Path;
        if (String.IsNullOrEmpty(filePath))
        {
            return "(no file)";
        }

        return Path.GetRelativePath(solutionDir, filePath);
    }

    /// <summary>
    ///     Returns "File.cs:42" format for a location.
    /// </summary>
    public static string ToFileLineString(this Location location, string solutionDir)
    {
        FileLinePositionSpan span = location.GetLineSpan();
        string relPath = location.GetRelativePath(solutionDir);
        int line = span.StartLinePosition.Line + 1; // Convert to 1-based
        return LocationFormat.Format(relPath, line);
    }

    /// <summary>
    ///     Gets the document for a given file path within a solution.
    /// </summary>
    public static Document? GetDocumentByPath(this Solution solution, string filePath)
    {
        string fullPath = Path.GetFullPath(filePath);

        DocumentId? docId = solution
            .GetDocumentIdsWithFilePath(fullPath)
            .FirstOrDefault();

        return docId is not null ? solution.GetDocument(docId) : null;
    }

    /// <summary>
    ///     Enumerates the base type chain, excluding <see cref="SpecialType.System_Object" />.
    /// </summary>
    public static IEnumerable<INamedTypeSymbol> BaseTypes(this INamedTypeSymbol type)
    {
        INamedTypeSymbol? current = type.BaseType;
        while (current is not null && current.SpecialType != SpecialType.System_Object &&
               current.SpecialType != SpecialType.System_Enum &&
               current.SpecialType != SpecialType.System_MulticastDelegate &&
               current.SpecialType != SpecialType.System_Delegate)
        {
            yield return current;
            current = current.BaseType;
        }
    }

    /// <summary>
    ///     Finds the document for a pre-resolved absolute path and returns the symbol at the given 1-based position.
    ///     Common pipeline used by reference, type, and navigation tools.
    ///     Read-only tools use <paramref name="strict" /> = false (default) for forgiving snap-to-nearest.
    ///     Edit tools use <paramref name="strict" /> = true to require exact identifier positioning.
    /// </summary>
    /// <remarks>
    ///     Callers must pre-resolve <paramref name="resolvedPath" /> via
    ///     <see cref="FilePathResolver.ResolveAgainstSolutionAsync" /> — this helper does not combine
    ///     against the solution directory or fall back to suffix matching.
    /// </remarks>
    public static async Task<SymbolResolution> ResolveSymbolAtPositionAsync(
        this Solution solution, string resolvedPath, int line, int column,
        bool strict = false, CancellationToken ct = default)
    {
        Document? document = solution.GetDocumentByPath(resolvedPath);
        if (document is null)
        {
            throw new UserErrorException(ErrorMessages.FileNotInSolution(resolvedPath));
        }

        SourceText text = await document.GetTextAsync(ct);
        if (text.Length == 0)
        {
            return new SymbolResolution(null, "file is empty");
        }

        int position = text.GetPosition(line, column, !strict);

        // Try exact position first (cursor on identifier token)
        ISymbol? symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, ct);
        if (symbol is not null)
        {
            // When cursor lands on the return/element type of a member declaration,
            // non-strict mode snaps to the enclosing member. Users on a declaration line
            // almost always want the declared member, not its return type.
            if (!strict && symbol is ITypeSymbol)
            {
                SyntaxTree? snapTree = await document.GetSyntaxTreeAsync(ct);
                if (snapTree is not null)
                {
                    SyntaxToken tok = (await snapTree.GetRootAsync(ct)).FindToken(position);
                    SyntaxNode? memberNode = tok.Parent?.FirstAncestorOrSelf<MemberDeclarationSyntax>();
                    memberNode ??= tok.Parent?.FirstAncestorOrSelf<LocalFunctionStatementSyntax>();

                    // Snap when cursor is on the return/element type of a declaration.
                    SyntaxNode? returnType = GetReturnTypeSyntax(memberNode);
                    if (returnType is not null && returnType.Span.Contains(tok.Span))
                    {
                        SemanticModel? sem = await document.GetSemanticModelAsync(ct);

                        // Fields require GetDeclaredSymbol on the VariableDeclaratorSyntax child.
                        // For multi-variable fields (int x, y;) this resolves the first declarator,
                        // since the type token is shared and there's no way to disambiguate. A
                        // fieldless declaration (int ;) has no declarator — fall through rather than
                        // crash on First() (mirrors SymbolResolver.FindDeclaredSymbolOnLine).
                        ISymbol? declared;
                        if (memberNode is BaseFieldDeclarationSyntax fieldDecl)
                        {
                            VariableDeclaratorSyntax? declarator = fieldDecl.Declaration.Variables.FirstOrDefault();
                            declared = declarator is null ? null : sem?.GetDeclaredSymbol(declarator, ct);
                        }
                        else
                        {
                            declared = sem?.GetDeclaredSymbol(memberNode!, ct);
                        }

                        if (declared is not null)
                        {
                            return new SymbolResolution(declared);
                        }
                    }

                    // Indexers have no clickable identifier ("this" is a keyword), so also
                    // snap when cursor is anywhere in the indexer (e.g. on a parameter type).
                    if (memberNode is IndexerDeclarationSyntax)
                    {
                        SemanticModel? sem = await document.GetSemanticModelAsync(ct);
                        ISymbol? indexer = sem?.GetDeclaredSymbol(memberNode, ct);
                        if (indexer is not null)
                        {
                            return new SymbolResolution(indexer);
                        }
                    }
                }
            }

            return new SymbolResolution(symbol);
        }

        SyntaxTree? tree = await document.GetSyntaxTreeAsync(ct);

        // Strict mode: no fallback. Edit tools require cursor on the identifier token.
        if (strict)
        {
            PositionClassification classification = tree is not null
                ? ClassifyPosition(tree, position)
                : new PositionClassification(PositionKind.Unknown, "unknown");
            return new SymbolResolution(null, classification.Description);
        }

        SemanticModel? model = await document.GetSemanticModelAsync(ct);
        if (tree is null || model is null)
        {
            return new SymbolResolution(null, "could not get syntax tree");
        }

        // Comments and preprocessor directives (#region, #pragma, etc.) are not "near" a symbol.
        PositionClassification posKind = ClassifyPosition(tree, position);
        if (posKind.IsNonSymbolTrivia)
        {
            return new SymbolResolution(null, posKind.Description);
        }

        // Try resolving type references that FindSymbolAtPosition missed
        // (e.g., type arguments in generic base types: class Foo : IEndpoint<IResult>)
        SyntaxToken token = (await tree.GetRootAsync(ct)).FindToken(position);
        SyntaxNode? candidate = token.Parent;
        while (candidate is not null and not MemberDeclarationSyntax)
        {
            if (candidate is TypeSyntax)
            {
                SymbolInfo symbolInfo = model.GetSymbolInfo(candidate, ct);
                if (symbolInfo.Symbol is not null)
                {
                    return new SymbolResolution(symbolInfo.Symbol);
                }
            }

            candidate = candidate.Parent;
        }

        // Fallback: walk up from the position to find the enclosing declaration.
        // This handles cases where the cursor lands on a keyword (public, abstract, class, etc.)
        // rather than the identifier token. Only used by read-only tools.
        SyntaxNode? node = token.Parent;
        while (node is not null)
        {
            if (node is MemberDeclarationSyntax and not BaseNamespaceDeclarationSyntax
                or VariableDeclaratorSyntax or ParameterSyntax)
            {
                ISymbol? declared = model.GetDeclaredSymbol(node, ct);
                if (declared is not null)
                {
                    return new SymbolResolution(declared);
                }
            }

            node = node.Parent;
        }

        PositionClassification posDescription = ClassifyPosition(tree, position);
        return new SymbolResolution(null, posDescription.Description);
    }

    /// <summary>
    ///     Extracts the identifier text from a member declaration syntax node without resolving semantics.
    ///     Returns null for nodes without a simple identifier (fields, operators, indexers, conversion operators).
    /// </summary>
    public static string? GetIdentifierText(this SyntaxNode node)
    {
        return node switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            EventDeclarationSyntax e => e.Identifier.Text,
            BaseTypeDeclarationSyntax t => t.Identifier.Text,
            DelegateDeclarationSyntax d => d.Identifier.Text,
            EnumMemberDeclarationSyntax em => em.Identifier.Text,
            // Constructors (.ctor/.cctor) and destructors (Finalize) have symbol names that differ
            // from their syntax identifier (which is the class name), so return null to skip pre-filter.
            ConstructorDeclarationSyntax => null,
            DestructorDeclarationSyntax => null,
            LocalFunctionStatementSyntax lf => lf.Identifier.Text,
            _ => null
        };
    }

    /// <summary>
    ///     Classifies what is at a given position in a syntax tree (e.g. comment, keyword, punctuation).
    /// </summary>
    internal static PositionClassification ClassifyPosition(SyntaxTree tree, int position)
    {
        SyntaxNode root = tree.GetRoot();

        // Check trivia first (comments, whitespace, disabled code)
        SyntaxTrivia trivia = root.FindTrivia(position);
        if (trivia.Span.Contains(position))
        {
            return trivia.Kind() switch
            {
                SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia
                    => new PositionClassification(PositionKind.Comment, "in a comment"),
                SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia
                    => new PositionClassification(PositionKind.DocComment, "in a doc comment"),
                SyntaxKind.WhitespaceTrivia or SyntaxKind.EndOfLineTrivia
                    => new PositionClassification(PositionKind.Whitespace, "on whitespace"),
                SyntaxKind.DisabledTextTrivia
                    => new PositionClassification(PositionKind.DisabledCode, "in disabled code (#if false)"),
                _ => new PositionClassification(PositionKind.Trivia, $"in trivia ({trivia.Kind()})")
            };
        }

        // Check the token at the position
        SyntaxToken token = root.FindToken(position);
        if (token.Span.Contains(position) || token.Span.End == position)
        {
            if (IsStringLiteralKind(token.Kind()))
            {
                return new PositionClassification(PositionKind.StringLiteral, "in a string literal");
            }

            if (token.IsKind(SyntaxKind.NumericLiteralToken))
            {
                return new PositionClassification(PositionKind.NumericLiteral, "on a numeric literal");
            }

            if (SyntaxFacts.IsKeywordKind(token.Kind()))
            {
                return new PositionClassification(PositionKind.Keyword, $"on keyword '{token.Text}'");
            }

            if (SyntaxFacts.IsPunctuation(token.Kind()))
            {
                return new PositionClassification(PositionKind.Punctuation, $"on punctuation '{token.Text}'");
            }
        }

        return new PositionClassification(PositionKind.Whitespace, "on whitespace");
    }

    /// <summary>
    ///     True when <paramref name="kind" /> is a string- or character-literal content token. Lines
    ///     that resolve into such a token carry value-bearing whitespace — the interior of a verbatim
    ///     <c>@"..."</c>, the dedent-defining lines of a raw <c>"""..."""</c>, interpolated-string text —
    ///     so leading-whitespace rewrites (e.g. indent normalization) must skip them.
    /// </summary>
    internal static bool IsStringLiteralKind(SyntaxKind kind) =>
        kind is SyntaxKind.StringLiteralToken
            or SyntaxKind.Utf8StringLiteralToken
            or SyntaxKind.InterpolatedStringTextToken
            or SyntaxKind.SingleLineRawStringLiteralToken
            or SyntaxKind.MultiLineRawStringLiteralToken
            or SyntaxKind.CharacterLiteralToken;

    /// <summary>
    ///     Returns the return/element type syntax for a member declaration, or null for nodes
    ///     that don't have one (e.g. constructors, type declarations).
    /// </summary>
    private static SyntaxNode? GetReturnTypeSyntax(SyntaxNode? node) => node switch
    {
        MethodDeclarationSyntax m => m.ReturnType,
        PropertyDeclarationSyntax p => p.Type,
        IndexerDeclarationSyntax i => i.Type,
        OperatorDeclarationSyntax o => o.ReturnType,
        ConversionOperatorDeclarationSyntax c => c.Type,
        DelegateDeclarationSyntax d => d.ReturnType,
        EventDeclarationSyntax e => e.Type,
        BaseFieldDeclarationSyntax f => f.Declaration.Type,
        LocalFunctionStatementSyntax lf => lf.ReturnType,
        _ => null
    };
}

/// <summary>
///     Result of symbol resolution at a position: the symbol (if found) and a description of what's at the position
///     when no symbol was found.
/// </summary>
internal record SymbolResolution(ISymbol? Symbol, string? PositionDescription = null)
{
    /// <summary>
    ///     Formats a "no symbol found" error message including the position description.
    /// </summary>
    internal string FormatNoSymbolError(string filePath, int line, int column) =>
        $"No symbol found at {LocationFormat.Format(filePath, line, column)} — {PositionDescription ?? "unknown"}. Move the cursor to an identifier, or use find_symbol to search by name.";
}

/// <summary>
///     Checks whether a name matches a namespace (not a type) in the solution.
///     Used to produce better error messages when users pass a namespace as containingType.
/// </summary>
internal static class NamespaceDetection
{
    internal static async Task<bool> IsNamespaceInSolutionAsync(Solution solution, string name, CancellationToken ct)
    {
        foreach (Project project in solution.Projects)
        {
            Compilation? compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
            {
                continue;
            }

            INamespaceSymbol? ns = FindNamespace(compilation.GlobalNamespace, name);
            if (ns is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Throws <see cref="UserErrorException" /> if <paramref name="containingType" /> is a namespace.
    /// </summary>
    internal static async Task ThrowIfNamespaceAsync(Solution solution, string? containingType, CancellationToken ct)
    {
        if (containingType is not null && await IsNamespaceInSolutionAsync(solution, containingType, ct))
        {
            throw new UserErrorException(FormatNamespaceError(containingType));
        }
    }

    internal static string FormatNamespaceError(string containingType) =>
        $"'{containingType}' is a namespace, not a type. " +
        "containingType filters by the enclosing type (e.g., a nested class or the class containing a member). " +
        "For top-level types, omit containingType and use symbolName alone.";

    private static INamespaceSymbol? FindNamespace(INamespaceSymbol root, string fullName)
    {
        string[] parts = fullName.Split('.');
        INamespaceSymbol current = root;

        foreach (string part in parts)
        {
            INamespaceSymbol? child = current.GetNamespaceMembers()
                .FirstOrDefault(ns => String.Equals(ns.Name, part, StringComparison.OrdinalIgnoreCase));

            if (child is null)
            {
                return null;
            }

            current = child;
        }

        // Only match if we consumed all parts and it's not the global namespace
        return current.IsGlobalNamespace ? null : current;
    }
}
