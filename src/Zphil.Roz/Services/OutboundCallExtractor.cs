using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Constants;
using Zphil.Roz.Models;
using Zphil.Roz.Symbols;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Services;

/// <summary>
///     Extracts the outbound calls of a single method body — every method/constructor invocation,
///     property/event access grouped by the resolved target symbol. The semantic-model walk mirrors
///     <see cref="DiRegistrationScanner" />'s symbol resolution and
///     <see cref="ReferenceKindClassifier" />'s node classification (so the two tools agree on what
///     counts as a call) but answers the inverse question: not "who calls X" but "what does X call".
/// </summary>
/// <remarks>
///     Calls inside lambdas and local functions declared in the body are counted as the method's
///     outbound calls (<c>DescendantNodes</c> descends into them) — intended, since those bodies
///     execute as part of the method. Overload resolution is best-effort: a failed binding still
///     yields a candidate via <see cref="SymbolInfo.CandidateSymbols" />, which may pick the wrong
///     overload; fully-unresolved calls are skipped.
/// </remarks>
internal static class OutboundCallExtractor
{
    /// <summary>
    ///     Extracts outbound calls from <paramref name="method" />'s body.
    /// </summary>
    /// <returns>
    ///     <c>Groups</c>: in-solution callees always, plus external callees when
    ///     <paramref name="includeExternalCalls" /> is set, each with its call sites.
    ///     <c>ExternalCount</c>/<c>ExternalTypeNames</c>: the distinct external targets that were
    ///     suppressed (collapsed to a summary) — both empty when <paramref name="includeExternalCalls" /> is set.
    /// </returns>
    public static async Task<(List<OutboundCallGroup> Groups, int ExternalCount, List<string> ExternalTypeNames)>
        ExtractAsync(IMethodSymbol method, Solution solution, int contextLines, bool includeExternalCalls, CancellationToken ct)
    {
        SyntaxNode? body = SelectBodyBearingNode(method, ct);
        if (body is null)
        {
            return ([], 0, []);
        }

        Document? document = solution.GetDocument(body.SyntaxTree);
        if (document is null)
        {
            return ([], 0, []);
        }

        SemanticModel? model = await document.GetSemanticModelAsync(ct);
        if (model is null)
        {
            return ([], 0, []);
        }

        SourceText text = await document.GetTextAsync(ct);
        int clampedContext = Math.Clamp(contextLines, 0, ToolDescriptions.MaxContextLines);

        // Collect (target, call-site node) for every call/creation/access, grouped by target identity.
        Dictionary<string, CallGroup> byTarget = new();
        foreach (SyntaxNode node in body.DescendantNodes())
        {
            ct.ThrowIfCancellationRequested();

            (ISymbol? target, SyntaxNode site) = Resolve(node, model, ct);
            if (target is null)
            {
                continue;
            }

            string key = GroupKey(target);
            if (!byTarget.TryGetValue(key, out CallGroup? group))
            {
                group = new CallGroup(target);
                byTarget[key] = group;
            }

            group.SiteNodes.Add(site);
        }

        List<OutboundCallGroup> rendered = [];
        Dictionary<string, string> suppressed = new(); // target key -> containing-type short name

        foreach (CallGroup group in byTarget.Values)
        {
            bool inSolution = IsInSolution(group.Target, solution);
            if (!inSolution && !includeExternalCalls)
            {
                suppressed[GroupKey(group.Target)] = group.Target.ContainingType?.Name ?? group.Target.Name;
                continue;
            }

            List<LocationWithContext> sites = group.SiteNodes
                .Select(n => n.GetLocation())
                .Where(l => l.IsInSource)
                .DistinctBy(l => (l.SourceTree?.FilePath, l.GetLineSpan().StartLinePosition.Line))
                .OrderBy(l => l.GetLineSpan().StartLinePosition.Line)
                .Select(l =>
                {
                    int lineIndex = l.GetLineSpan().StartLinePosition.Line;
                    string[] lines = SourceTextUtility.GetSurroundingLines(text, lineIndex, clampedContext);
                    return new LocationWithContext(l, lines, SourceTextUtility.GetDisplayStartLine(lineIndex, clampedContext));
                })
                .ToList();

            if (sites.Count == 0)
            {
                continue;
            }

            rendered.Add(new OutboundCallGroup(group.Target, sites, inSolution));
        }

        List<OutboundCallGroup> ordered = rendered
            .OrderBy(g => g.Sites[0].Loc.GetLineSpan().StartLinePosition.Line)
            .ToList();

        return (ordered, suppressed.Count, suppressed.Values.Distinct().ToList());
    }

    /// <summary>
    ///     Stable grouping key for a target symbol: source location when available, else the
    ///     original-definition display string (so metadata symbols from different compilations merge).
    ///     Shared with <see cref="MethodAnalysisService" /> so per-overload groups merge identically.
    /// </summary>
    internal static string GroupKey(ISymbol symbol) =>
        SymbolDeduplication.GetLocationKey(symbol) is { } key
            ? $"{key.Path}:{key.Line}"
            : symbol.OriginalDefinition.ToDisplayString();

    /// <summary>
    ///     Classifies a syntax node as a call/creation/access and resolves its target, or returns
    ///     <c>(null, node)</c> when the node is not an outbound call or cannot be resolved.
    /// </summary>
    private static (ISymbol? Target, SyntaxNode Site) Resolve(SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        switch (node)
        {
            case InvocationExpressionSyntax inv when !ReferenceKindClassifier.IsNameofInvocation(inv):
                return (ResolveCallable(model, inv, ct), inv);

            case ObjectCreationExpressionSyntax oc:
                return (ResolveCallable(model, oc, ct), oc);

            case ImplicitObjectCreationExpressionSyntax ioc:
                return (ResolveCallable(model, ioc, ct), ioc);

            case MemberAccessExpressionSyntax ma:
                // Skip the receiver of an invocation (obj.Method() — the call is counted via the
                // invocation) and compile-time nameof(...) operands.
                if (ma.Parent is InvocationExpressionSyntax parent && parent.Expression == ma)
                {
                    return (null, node);
                }

                if (ReferenceKindClassifier.IsNameofOperand(ma))
                {
                    return (null, node);
                }

                ISymbol? accessed = model.GetSymbolInfo(ma, ct).Symbol;
                return accessed is IPropertySymbol or IEventSymbol ? (accessed, ma) : (null, node);

            case MemberBindingExpressionSyntax mb: // obj?.Prop — the conditional-access analog of obj.Prop
                // obj?.Method() is counted via its invocation; the compile-time nameof(...) operand isn't a call.
                if (mb.Parent is InvocationExpressionSyntax bindCall && bindCall.Expression == mb)
                {
                    return (null, node);
                }

                if (ReferenceKindClassifier.IsNameofOperand(mb))
                {
                    return (null, node);
                }

                ISymbol? bound = model.GetSymbolInfo(mb, ct).Symbol;
                return bound is IPropertySymbol or IEventSymbol ? (bound, mb) : (null, node);

            case IdentifierNameSyntax id when IsImplicitThisMemberCandidate(id): // bare Count -> this.Count
                if (ReferenceKindClassifier.IsNameofOperand(id))
                {
                    return (null, node);
                }

                ISymbol? bare = model.GetSymbolInfo(id, ct).Symbol;
                return bare is IPropertySymbol or IEventSymbol ? (bare, id) : (null, node);

            default:
                return (null, node);
        }
    }

    /// <summary>
    ///     A bare identifier is an implicit-this property/event access only when it is NOT the member
    ///     name of an <c>obj.Member</c> / <c>obj?.Member</c> access (handled by those cases) and NOT
    ///     the thing being invoked. The inverse of <see cref="ReferenceKindClassifier" />'s unwrap.
    /// </summary>
    private static bool IsImplicitThisMemberCandidate(IdentifierNameSyntax id) => id.Parent switch
    {
        MemberAccessExpressionSyntax ma when ma.Name == id => false,
        MemberBindingExpressionSyntax mb when mb.Name == id => false,
        InvocationExpressionSyntax inv when inv.Expression == id => false,
        _ => true
    };

    private static ISymbol? ResolveCallable(SemanticModel model, SyntaxNode node, CancellationToken ct)
    {
        SymbolInfo info = model.GetSymbolInfo(node, ct);
        return info.Symbol ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
    }

    private static bool IsInSolution(ISymbol target, Solution solution) =>
        target.Locations.Any(l => l.IsInSource && l.SourceTree is { } tree && solution.GetDocument(tree) is not null);

    /// <summary>
    ///     Picks the body-bearing declaration for <paramref name="method" />, preferring a part with a
    ///     block or arrow body over a bodyless one (handles partial methods: defining vs implementing
    ///     part). Returns <c>null</c> for abstract/extern/partial-definition-only methods.
    /// </summary>
    private static SyntaxNode? SelectBodyBearingNode(IMethodSymbol method, CancellationToken ct)
    {
        foreach (IMethodSymbol part in Parts(method))
        {
            foreach (SyntaxReference reference in part.DeclaringSyntaxReferences)
            {
                SyntaxNode node = reference.GetSyntax(ct);
                if (HasBody(node))
                {
                    return node;
                }
            }
        }

        return null;
    }

    private static IEnumerable<IMethodSymbol> Parts(IMethodSymbol method)
    {
        yield return method;
        if (method.PartialImplementationPart is { } impl)
        {
            yield return impl;
        }

        if (method.PartialDefinitionPart is { } def)
        {
            yield return def;
        }
    }

    private static bool HasBody(SyntaxNode node) => node switch
    {
        BaseMethodDeclarationSyntax m => m.Body is not null || m.ExpressionBody is not null,
        LocalFunctionStatementSyntax lf => lf.Body is not null || lf.ExpressionBody is not null,
        AccessorDeclarationSyntax a => a.Body is not null || a.ExpressionBody is not null,
        _ => false
    };

    private sealed class CallGroup(ISymbol target)
    {
        public ISymbol Target { get; } = target;
        public List<SyntaxNode> SiteNodes { get; } = [];
    }
}
