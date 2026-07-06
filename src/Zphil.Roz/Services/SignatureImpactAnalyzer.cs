using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Zphil.Roz.Enums;
using Zphil.Roz.Models;

namespace Zphil.Roz.Services;

/// <summary>
///     Classifies every reference site of a signature-change target against a proposed parameter list,
///     using real overload resolution in a forked <see cref="Solution" /> rather than heuristics. Each
///     site becomes a <see cref="SiteVerdict" /> (<see cref="ImpactSeverity.Compatible" /> /
///     <see cref="ImpactSeverity.RequiresUpdate" /> / <see cref="ImpactSeverity.Unsafe" />) per the
///     verdict matrix in the design plan.
/// </summary>
internal static class SignatureImpactAnalyzer
{
    /// <summary>
    ///     Classifies <paramref name="census" /> against <paramref name="newSig" /> using the
    ///     <paramref name="fork" /> (built by <see cref="SignatureForkBuilder.BuildForkAsync" /> from the
    ///     same <paramref name="baseSolution" /> and census).
    /// </summary>
    public static async Task<SignatureImpact> ClassifyAsync(
        IMethodSymbol target, ParsedSignature newSig, SignatureDelta delta, SignatureFamily family,
        IReadOnlyList<ReferenceLocation> census, Solution baseSolution, Solution fork, CancellationToken ct)
    {
        List<SiteVerdict> verdicts = [];

        IEnumerable<IGrouping<DocumentId, (ReferenceLocation Loc, int Index)>> byDocument = census
            .Select((loc, index) => (Loc: loc, Index: index))
            .GroupBy(x => x.Loc.Document.Id);

        foreach (IGrouping<DocumentId, (ReferenceLocation Loc, int Index)> group in byDocument)
        {
            Document? baseDoc = baseSolution.GetDocument(group.Key);
            Document? forkDoc = fork.GetDocument(group.Key);
            if (baseDoc is null || forkDoc is null)
            {
                continue;
            }

            SemanticModel? baseModel = await baseDoc.GetSemanticModelAsync(ct);
            SyntaxNode? baseRoot = await baseDoc.GetSyntaxRootAsync(ct);
            SemanticModel? forkModel = await forkDoc.GetSemanticModelAsync(ct);
            SyntaxNode? forkRoot = await forkDoc.GetSyntaxRootAsync(ct);
            if (baseModel is null || baseRoot is null || forkModel is null || forkRoot is null)
            {
                continue;
            }

            Dictionary<int, SyntaxNode> forkByIndex = forkRoot
                .GetAnnotatedNodes(SignatureForkBuilder.SiteMarkerKind)
                .Select(node => (Node: node,
                    node.GetAnnotations(SignatureForkBuilder.SiteMarkerKind).First().Data))
                .Where(x => x.Data is not null)
                .ToDictionary(x => Int32.Parse(x.Data!), x => x.Node);

            foreach ((ReferenceLocation loc, int index) in group)
            {
                SyntaxNode baseNode = baseRoot.FindNode(
                    loc.Location.SourceSpan, true, true);
                SyntaxNode forkNode = forkByIndex.TryGetValue(index, out SyntaxNode? annotated)
                    ? annotated
                    : forkRoot.FindNode(loc.Location.SourceSpan, true, true);

                SiteVerdict? verdict = ClassifySite(
                    baseNode, forkNode, baseModel, forkModel, target, newSig, delta, loc, ct);
                if (verdict is { } v)
                {
                    verdicts.Add(v);
                }
            }
        }

        return new SignatureImpact(verdicts, BuildNotes(target, family, delta));
    }

    private static SiteVerdict? ClassifySite(
        SyntaxNode baseNode, SyntaxNode forkNode, SemanticModel baseModel, SemanticModel forkModel,
        IMethodSymbol target, ParsedSignature newSig, SignatureDelta delta,
        ReferenceLocation loc, CancellationToken ct)
    {
        // Row 1: nameof operand — a compile-time string, unaffected.
        if (ReferenceKindClassifier.IsNameofOperand(baseNode))
        {
            return V(loc, ImpactSeverity.Compatible, "nameof reference — unaffected by a signature change");
        }

        // A documentation cref references the member by name, not by binding — a signature change leaves it.
        if (baseNode.FirstAncestorOrSelf<CrefSyntax>() is not null)
        {
            return V(loc, ImpactSeverity.Compatible, "documentation cref — name-based, unaffected");
        }

        // Row 2: method group / delegate conversion (any non-invocation reference to the method).
        if (ReferenceKindClassifier.Classify(baseNode) != ReferenceRole.Invocation)
        {
            return ClassifyMethodGroup(target, newSig, loc);
        }

        SyntaxNode? baseCall = SignatureCallSite.ResolveCallNode(baseNode);
        SyntaxNode? forkCall = SignatureCallSite.ResolveCallNode(forkNode);
        if (baseCall is null)
        {
            return V(loc, ImpactSeverity.RequiresUpdate, "call site must be updated for the new signature");
        }

        // Attribute sites carry an AttributeArgumentList (not a BaseArgumentList) — conservative row 11.
        if (baseCall is AttributeSyntax)
        {
            return V(loc, ImpactSeverity.RequiresUpdate,
                "attribute usage — verify its constructor arguments against the new signature");
        }

        BaseArgumentListSyntax? baseArgs = SignatureCallSite.ArgumentListOf(baseCall);
        if (baseArgs is null)
        {
            return V(loc, ImpactSeverity.RequiresUpdate, "call site must be updated for the new signature");
        }

        var baseBound = baseModel.GetSymbolInfo(baseCall, ct).Symbol as IMethodSymbol;
        IMethodSymbol? forkBound = forkCall is not null
            ? forkModel.GetSymbolInfo(forkCall, ct).Symbol as IMethodSymbol
            : null;

        if (forkBound is not null)
        {
            if (!IsFamily(forkBound, ct))
            {
                // Row 5: silently retargets to another overload.
                return V(loc, ImpactSeverity.Unsafe, $"silently retargets to {Display(forkBound)}");
            }

            // Rows 3 / 4: re-binds to a family member — same parameters, or a reorder trap.
            return ArgumentsBindSameParameters(baseArgs, baseBound, forkBound)
                ? V(loc, ImpactSeverity.Compatible, "re-binds unchanged under the new signature")
                : V(loc, ImpactSeverity.Unsafe, "compiles but arguments silently bind to different parameters");
        }

        return ClassifyFailedRebind(
            baseCall, forkCall, baseArgs, baseBound, baseModel, forkModel, delta, loc, ct);
    }

    // ── Row 2: method group ─────────────────────────────────────────────────

    private static SiteVerdict ClassifyMethodGroup(IMethodSymbol target, ParsedSignature newSig, ReferenceLocation loc)
    {
        List<(string Type, RefKind RefKind)> oldSeq = target.Parameters
            .Select(p => (p.Type.ToDisplayString(), p.RefKind)).ToList();
        List<(string Type, RefKind RefKind)> newSeq = newSig.Parameters
            .Select(p => (p.ResolvedType?.ToDisplayString() ?? p.TypeText, p.RefKind)).ToList();

        bool identical = oldSeq.Count == newSeq.Count
                         && oldSeq.Zip(newSeq).All(z => z.First.Type == z.Second.Type && z.First.RefKind == z.Second.RefKind);

        return identical
            ? V(loc, ImpactSeverity.Compatible, "method-group reference — parameter types unchanged, conversion holds")
            : V(loc, ImpactSeverity.Unsafe,
                "method-group / delegate conversion breaks — optional and params do not apply to method groups");
    }

    // ── Rows 3 / 4: successful re-bind ──────────────────────────────────────

    private static bool ArgumentsBindSameParameters(
        BaseArgumentListSyntax baseArgs, IMethodSymbol? baseBound, IMethodSymbol forkBound)
    {
        if (baseBound is null)
        {
            return true;
        }

        List<ArgumentSyntax> arguments = baseArgs.Arguments.ToList();
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].NameColon is not null)
            {
                continue; // named arguments bind the same parameter in both signatures
            }

            if (i >= baseBound.Parameters.Length || i >= forkBound.Parameters.Length)
            {
                continue; // params-expansion tail — TouchesParams already routes those elsewhere
            }

            if (baseBound.Parameters[i].Name != forkBound.Parameters[i].Name)
            {
                return false;
            }
        }

        return true;
    }

    // ── Rows 6-11: failed re-bind ───────────────────────────────────────────

    private static SiteVerdict ClassifyFailedRebind(
        SyntaxNode baseCall, SyntaxNode? forkCall, BaseArgumentListSyntax baseArgs, IMethodSymbol? baseBound,
        SemanticModel baseModel, SemanticModel forkModel, SignatureDelta delta,
        ReferenceLocation loc, CancellationToken ct)
    {
        if (baseBound is null)
        {
            return V(loc, ImpactSeverity.RequiresUpdate, "call site must be updated for the new signature");
        }

        bool reducedForm = baseBound.ReducedFrom is not null;
        IMethodSymbol boundUnreduced = baseBound.ReducedFrom ?? baseBound;

        // Rows 9 / 10: a retype whose matched argument no longer converts is the failure cause.
        if (delta.Retyped.Count > 0
            && ClassifyRetype(baseArgs, baseModel, delta, reducedForm, loc) is { } retypeVerdict)
        {
            return retypeVerdict;
        }

        RewritePlan plan = CallSiteRewritePlanner.Plan(baseArgs, boundUnreduced, delta, reducedForm);
        if (plan.Blocker is { } blocker)
        {
            return ClassifyBlocker(blocker, delta, loc);
        }

        if (plan.NewArgs is not null && forkCall is not null)
        {
            string rewritten = RewrittenCallText(baseCall, plan.NewArgs);
            IMethodSymbol? speculative = SpeculativeBind(forkModel, forkCall, plan.NewArgs);
            return speculative is not null && IsFamily(speculative, ct)
                ? V(loc, ImpactSeverity.RequiresUpdate, $"update call to: {rewritten}") // row 6
                : V(loc, ImpactSeverity.Unsafe, $"rewrite {rewritten} does not bind the intended method"); // row 7
        }

        return V(loc, ImpactSeverity.RequiresUpdate, "call site must be updated for the new signature");
    }

    private static SiteVerdict? ClassifyRetype(
        BaseArgumentListSyntax baseArgs, SemanticModel baseModel, SignatureDelta delta, bool reducedForm,
        ReferenceLocation loc)
    {
        int receiverOffset = reducedForm ? 1 : 0;
        var anyExplicit = false;
        foreach ((IParameterSymbol old, SignatureParameter @new) in delta.Retyped)
        {
            ArgumentSyntax? argument = FindArgumentForParameter(baseArgs, old, receiverOffset);
            if (argument is null || @new.ResolvedType is null)
            {
                continue; // optional omitted, or unbound — not a failure driver
            }

            Conversion conversion = baseModel.ClassifyConversion(argument.Expression, @new.ResolvedType);
            if (!conversion.Exists)
            {
                return V(loc, ImpactSeverity.Unsafe,
                    $"argument for '{old.Name}' does not convert to {@new.TypeText}"); // row 10
            }

            if (!conversion.IsImplicit)
            {
                anyExplicit = true;
            }
        }

        return anyExplicit
            ? V(loc, ImpactSeverity.RequiresUpdate, "argument needs an explicit cast for a retyped parameter") // row 9
            : null;
    }

    private static SiteVerdict ClassifyBlocker(RewriteBlocker blocker, SignatureDelta delta, ReferenceLocation loc) =>
        blocker switch
        {
            RewriteBlocker.NeedsCallerValue => V(loc, ImpactSeverity.RequiresUpdate,
                $"add an argument for required parameter '{FirstRequiredAdded(delta)}'"), // row 8
            RewriteBlocker.TouchesReceiverParam => V(loc, ImpactSeverity.RequiresUpdate,
                "reduced extension call touches the receiver parameter — rewrite by hand"), // row 11
            RewriteBlocker.TouchesParamsArray => V(loc, ImpactSeverity.RequiresUpdate,
                "a params parameter is affected — rewrite the call by hand"), // row 11
            RewriteBlocker.ExpandedParamsForm => V(loc, ImpactSeverity.RequiresUpdate,
                "call passes a params argument in expanded form — rewrite by hand"), // row 11
            _ => V(loc, ImpactSeverity.RequiresUpdate, "call site must be updated for the new signature")
        };

    private static string FirstRequiredAdded(SignatureDelta delta) =>
        delta.Added.FirstOrDefault(a => !a.HasExplicitDefault)?.Name ?? "new";

    // ── Speculative binding & call-node helpers ─────────────────────────────

    private static IMethodSymbol? SpeculativeBind(
        SemanticModel forkModel, SyntaxNode forkCall, ArgumentListSyntax newArgs)
    {
        SyntaxNode rewritten = ReplaceArguments(forkCall, newArgs);
        SymbolInfo info = rewritten switch
        {
            ExpressionSyntax expression => forkModel.GetSpeculativeSymbolInfo(
                forkCall.SpanStart, expression, SpeculativeBindingOption.BindAsExpression),
            ConstructorInitializerSyntax initializer => forkModel.GetSpeculativeSymbolInfo(
                forkCall.SpanStart, initializer),
            _ => default
        };

        return info.Symbol as IMethodSymbol;
    }

    private static SyntaxNode ReplaceArguments(SyntaxNode call, ArgumentListSyntax newArgs) => call switch
    {
        InvocationExpressionSyntax i => i.WithArgumentList(newArgs),
        ObjectCreationExpressionSyntax o => o.WithArgumentList(newArgs),
        ImplicitObjectCreationExpressionSyntax ic => ic.WithArgumentList(newArgs),
        ConstructorInitializerSyntax ci => ci.WithArgumentList(newArgs),
        _ => call
    };

    private static string RewrittenCallText(SyntaxNode call, ArgumentListSyntax newArgs) => call switch
    {
        InvocationExpressionSyntax i => i.Expression + newArgs.ToString(),
        ObjectCreationExpressionSyntax o => $"new {o.Type}{newArgs}",
        ImplicitObjectCreationExpressionSyntax => $"new{newArgs}",
        ConstructorInitializerSyntax ci => ci.ThisOrBaseKeyword + newArgs.ToString(),
        _ => newArgs.ToString()
    };

    private static ArgumentSyntax? FindArgumentForParameter(
        BaseArgumentListSyntax args, IParameterSymbol parameter, int receiverOffset)
    {
        ArgumentSyntax? named = args.Arguments
            .FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == parameter.Name);
        if (named is not null)
        {
            return named;
        }

        int argIndex = parameter.Ordinal - receiverOffset;
        return argIndex >= 0 && argIndex < args.Arguments.Count && args.Arguments[argIndex].NameColon is null
            ? args.Arguments[argIndex]
            : null;
    }

    // Family membership is read off the fork itself: SignatureForkBuilder stamps every rewritten family
    // declaration with FamilyMarkerKind, so a re-bind that lands on a family member has an annotated
    // declaration. This is stable under the xmldoc <param> line shift a (path, line) key could not survive.
    private static bool IsFamily(IMethodSymbol method, CancellationToken ct) =>
        (method.ReducedFrom ?? method).DeclaringSyntaxReferences
        .Any(reference => reference.GetSyntax(ct).HasAnnotations(SignatureForkBuilder.FamilyMarkerKind));

    // ── Notes ───────────────────────────────────────────────────────────────

    private static List<string> BuildNotes(IMethodSymbol target, SignatureFamily family, SignatureDelta delta)
    {
        List<string> notes = [];

        if (family.UpwardMembers.Count > 0)
        {
            var list = String.Join(", ", family.UpwardMembers.Select(Display).Distinct());
            notes.Add(
                $"'{target.Name}' overrides/implements {list} — the whole family changes in lockstep; " +
                "re-run analyze_change_impact against the base/interface member to see its own direct references.");
        }

        bool renameShape = delta.Removed
            .Any(r => delta.Added.Any(a => a.ResolvedType is not null && SameType(r.Type, a.ResolvedType)));
        if (renameShape)
        {
            notes.Add(
                "a parameter was removed and another of the same type added — if this is a rename, use " +
                "rename_symbol instead (it rewrites named arguments too).");
        }

        return notes;
    }

    private static bool SameType(ITypeSymbol a, ITypeSymbol b) =>
        SymbolEqualityComparer.Default.Equals(a, b) || a.ToDisplayString() == b.ToDisplayString();

    private static SiteVerdict V(ReferenceLocation loc, ImpactSeverity severity, string reason) =>
        new(loc, severity, reason);

    private static string Display(ISymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
}
