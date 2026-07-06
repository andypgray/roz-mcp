using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Models;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Services;

/// <summary>
///     Resolves the full override/interface slot family of a signature-change target and builds a
///     forked <see cref="Solution" /> in which every family declaration carries the proposed parameter
///     list. Shared by precise analysis (classify against the fork) and apply (persist the fork).
/// </summary>
internal static class SignatureForkBuilder
{
    /// <summary>
    ///     Annotation kind stamped on each census call site that shares a document with a rewritten
    ///     declaration (whose spans shift). The annotation's <see cref="SyntaxAnnotation.Data" /> holds the
    ///     census index, so <see cref="SignatureImpactAnalyzer" /> can map a fork node back to its site.
    /// </summary>
    public const string SiteMarkerKind = "Zphil.Roz.SignatureCensusSite";

    /// <summary>
    ///     Annotation kind stamped on every rewritten family declaration, so
    ///     <see cref="SignatureImpactAnalyzer" /> can recognize a fork re-bind as landing on a family member
    ///     by the declaration's annotation rather than a (path, line) key — the latter is not stable when
    ///     <see cref="XmlDocParamSync" /> adds or removes a <c>&lt;param&gt;</c> line above the method.
    /// </summary>
    public const string FamilyMarkerKind = "Zphil.Roz.SignatureFamilyDecl";

    /// <summary>
    ///     Resolves the full slot family: the target plus its override/implementation closure (up via
    ///     <see cref="IMethodSymbol.OverriddenMethod" /> and implemented interface members, down via
    ///     <see cref="SymbolFinder.FindOverridesAsync" /> / <see cref="SymbolFinder.FindImplementationsAsync" />)
    ///     and partial siblings. Traversal stops at any metadata member (setting
    ///     <see cref="SignatureFamily.ExtendsIntoMetadata" />) so an <c>object.ToString</c>-class slot never
    ///     fans out into an unbounded census.
    /// </summary>
    public static async Task<SignatureFamily> ResolveFamilyAsync(
        IMethodSymbol target, Solution solution, CancellationToken ct)
    {
        Dictionary<string, IMethodSymbol> closure = new(StringComparer.Ordinal);
        List<IMethodSymbol> upward = [];
        var extendsIntoMetadata = false;

        Queue<(IMethodSymbol Method, bool IsUpward)> worklist = new();
        worklist.Enqueue((target.OriginalDefinition, false));

        while (worklist.Count > 0)
        {
            (IMethodSymbol method, bool isUpward) = worklist.Dequeue();
            string key = method.OriginalDefinition.ToDisplayString();
            if (!closure.TryAdd(key, method))
            {
                continue;
            }

            bool inSource = method.DeclaringSyntaxReferences.Length > 0 && method.Locations.Any(l => l.IsInSource);
            if (isUpward)
            {
                upward.Add(method);
            }

            if (!inSource)
            {
                // The slot's contract is anchored in metadata — do NOT traverse further (an unbounded
                // FindOverrides on e.g. object.ToString) and flag the guard case.
                extendsIntoMetadata = true;
                continue;
            }

            // Up: overridden base method and implemented interface members.
            if (method.OverriddenMethod is { } overridden)
            {
                worklist.Enqueue((overridden.OriginalDefinition, true));
            }

            foreach (ISymbol ifaceMember in InterfaceImplementationLookup.FindInterfaceMembers(method))
            {
                if (ifaceMember is IMethodSymbol ifaceMethod)
                {
                    worklist.Enqueue((ifaceMethod.OriginalDefinition, true));
                }
            }

            // Down: overrides, plus implementations when this is an interface member.
            foreach (ISymbol o in await SymbolFinder.FindOverridesAsync(method, solution, cancellationToken: ct))
            {
                if (o is IMethodSymbol overrideMethod)
                {
                    worklist.Enqueue((overrideMethod.OriginalDefinition, false));
                }
            }

            if (method.ContainingType?.TypeKind == TypeKind.Interface)
            {
                foreach (ISymbol impl in await SymbolFinder.FindImplementationsAsync(method, solution, cancellationToken: ct))
                {
                    if (impl is IMethodSymbol implMethod)
                    {
                        worklist.Enqueue((implMethod.OriginalDefinition, false));
                    }
                }
            }

            // Partial definition/implementation siblings.
            if (method.PartialDefinitionPart is { } definitionPart)
            {
                worklist.Enqueue((definitionPart.OriginalDefinition, isUpward));
            }

            if (method.PartialImplementationPart is { } implementationPart)
            {
                worklist.Enqueue((implementationPart.OriginalDefinition, isUpward));
            }
        }

        List<IMethodSymbol> sourceMembers = closure.Values
            .Where(m => m.DeclaringSyntaxReferences.Length > 0 && m.Locations.Any(l => l.IsInSource))
            .ToList();

        return new SignatureFamily(sourceMembers, upward, extendsIntoMetadata);
    }

    /// <summary>
    ///     Builds a forked solution replacing the parameter list on every source family declaration
    ///     across every <see cref="DocumentId" /> at each declaration file path (multi-TFM siblings
    ///     included). Census call sites in a rewritten declaration document are pre-annotated (their spans
    ///     shift afterward) with <see cref="SiteMarkerKind" /> carrying the census index, so the classifier
    ///     can map a fork node back to its site.
    /// </summary>
    public static async Task<Solution> BuildForkAsync(
        SignatureFamily family, ParameterListSyntax newList,
        IReadOnlyList<ReferenceLocation> census,
        bool syncXmlDoc, Solution solution, CancellationToken ct)
    {
        Dictionary<string, List<(TextSpan Span, string Name)>> declsByPath =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (IMethodSymbol member in family.SourceMembers)
        {
            string name = member.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
                ? member.ContainingType?.Name ?? member.Name
                : member.Name;
            foreach (SyntaxReference reference in member.DeclaringSyntaxReferences)
            {
                string? path = reference.SyntaxTree.FilePath;
                if (String.IsNullOrEmpty(path))
                {
                    continue;
                }

                if (!declsByPath.TryGetValue(path, out List<(TextSpan, string)>? list))
                {
                    list = [];
                    declsByPath[path] = list;
                }

                list.Add((reference.Span, name));
            }
        }

        IReadOnlyList<string> newParamNames = newList.Parameters.Select(p => p.Identifier.ValueText).ToList();

        Solution fork = solution;
        foreach ((string path, List<(TextSpan Span, string Name)> decls) in declsByPath)
        {
            foreach (DocumentId docId in solution.GetDocumentIdsWithFilePath(path))
            {
                Document? document = fork.GetDocument(docId);
                if (document is null)
                {
                    continue;
                }

                SyntaxNode? root = await document.GetSyntaxRootAsync(ct);
                if (root is null)
                {
                    continue;
                }

                root = AnnotateCensusSites(root, census, docId);
                root = RewriteDeclarations(root, decls, newList, syncXmlDoc, newParamNames);
                fork = fork.WithDocumentSyntaxRoot(docId, root);
            }
        }

        return fork;
    }

    /// <summary>
    ///     Stamps each census node in <paramref name="docId" /> with a <see cref="SiteMarkerKind" />
    ///     annotation carrying its census index. Annotation preserves text and spans, so the subsequent
    ///     declaration rewrite's span lookups still resolve, and the marker rides the rewrite so the
    ///     classifier can retrieve the (shifted) node and recover which census site it is.
    /// </summary>
    private static SyntaxNode AnnotateCensusSites(
        SyntaxNode root, IReadOnlyList<ReferenceLocation> census, DocumentId docId)
    {
        Dictionary<SyntaxNode, int> indexByNode = new();
        for (var i = 0; i < census.Count; i++)
        {
            if (census[i].Document.Id != docId)
            {
                continue;
            }

            SyntaxNode node = root.FindNode(census[i].Location.SourceSpan, true, true);

            // A doc-comment cref reference resolves to a node inside structured trivia. It never needs
            // fork-node retrieval (the classifier reads it as a name-based, Compatible reference off the
            // base node) and — crucially — annotating it would replace an ancestor declaration node,
            // clobbering the nested call-site annotations. Skip it.
            if (node.IsPartOfStructuredTrivia())
            {
                continue;
            }

            indexByNode.TryAdd(node, i);
        }

        return indexByNode.Count == 0
            ? root
            : root.ReplaceNodes(indexByNode.Keys,
                (original, _) => original.WithAdditionalAnnotations(
                    new SyntaxAnnotation(SiteMarkerKind, indexByNode[original].ToString())));
    }

    private static SyntaxNode RewriteDeclarations(
        SyntaxNode root, List<(TextSpan Span, string Name)> decls, ParameterListSyntax newList,
        bool syncXmlDoc, IReadOnlyList<string> newParamNames)
    {
        List<BaseMethodDeclarationSyntax> declNodes = [];
        foreach ((TextSpan span, string name) in decls.DistinctBy(d => d.Span))
        {
            if (span.End > root.FullSpan.End)
            {
                continue;
            }

            BaseMethodDeclarationSyntax? decl = root.FindNode(span).FirstAncestorOrSelf<BaseMethodDeclarationSyntax>();
            // Guard multi-TFM span aliasing: a preprocessor-divergent sibling tree can map the span to a
            // different declaration; the name check skips it rather than rewriting the wrong member.
            if (decl is not null && DeclarationMatchesName(decl, name))
            {
                declNodes.Add(decl);
            }
        }

        return declNodes.Count == 0
            ? root
            : root.ReplaceNodes(declNodes, (_, rewritten) => RewriteDeclaration(rewritten, newList, syncXmlDoc, newParamNames));
    }

    private static BaseMethodDeclarationSyntax RewriteDeclaration(
        BaseMethodDeclarationSyntax decl, ParameterListSyntax newList, bool syncXmlDoc,
        IReadOnlyList<string> newParamNames)
    {
        ParameterListSyntax adapted = PreserveThisModifier(decl.ParameterList, newList).WithTriviaFrom(decl.ParameterList);
        BaseMethodDeclarationSyntax updated = decl.WithParameterList(adapted);
        BaseMethodDeclarationSyntax synced = syncXmlDoc ? XmlDocParamSync.Sync(updated, newParamNames) : updated;

        // Mark the declaration so a fork re-bind can be recognized as a family member regardless of any
        // line shift the xmldoc sync introduces.
        return synced.WithAdditionalAnnotations(new SyntaxAnnotation(FamilyMarkerKind));
    }

    /// <summary>
    ///     Re-attaches a lost <c>this</c> receiver modifier: a descriptor is naturally written without it
    ///     (<c>(ISigSurface s, string t)</c>), and a fork whose extension method silently stopped being an
    ///     extension makes every reduced call site (<c>s.Tag(…)</c>) fail its re-bind — false Unsafe
    ///     verdicts. Transplanted only when the first parameter is the same kept-in-place receiver; a
    ///     moved or removed receiver legitimately un-extensions the fork, routing reduced sites to the
    ///     receiver-touch blocker.
    /// </summary>
    private static ParameterListSyntax PreserveThisModifier(ParameterListSyntax original, ParameterListSyntax newList)
    {
        if (original.Parameters.Count == 0 || newList.Parameters.Count == 0)
        {
            return newList;
        }

        ParameterSyntax originalFirst = original.Parameters[0];
        ParameterSyntax newFirst = newList.Parameters[0];
        bool receiverKeptInPlace = originalFirst.Modifiers.Any(SyntaxKind.ThisKeyword)
                                   && !newFirst.Modifiers.Any(SyntaxKind.ThisKeyword)
                                   && originalFirst.Identifier.ValueText == newFirst.Identifier.ValueText;
        if (!receiverKeptInPlace)
        {
            return newList;
        }

        ParameterSyntax withThis = newFirst.WithModifiers(newFirst.Modifiers.Insert(
            0, SyntaxFactory.Token(SyntaxKind.ThisKeyword).WithTrailingTrivia(SyntaxFactory.Space)));
        return newList.ReplaceNode(newFirst, withThis);
    }

    private static bool DeclarationMatchesName(BaseMethodDeclarationSyntax decl, string name) => decl switch
    {
        MethodDeclarationSyntax m => m.Identifier.ValueText == name,
        ConstructorDeclarationSyntax c => c.Identifier.ValueText == name,
        _ => true
    };
}
