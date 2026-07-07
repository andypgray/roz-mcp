using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Constants;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Models;
using Zphil.Roz.Symbols;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Services;

/// <summary>
///     Computes the blast radius of a proposed change to a symbol: resolves the target, collects
///     its references (exactly as <see cref="ReferenceService" /> does), then classifies each site
///     as <see cref="ImpactSeverity.Compatible" />, <see cref="ImpactSeverity.RequiresUpdate" />,
///     or <see cref="ImpactSeverity.Unsafe" /> against the change.
/// </summary>
/// <remarks>
///     The analysis is one-hop: each reference site is classified by its immediate syntactic
///     context (the consumer it flows into, the assembly/type it sits in), using Roslyn's real
///     conversion and accessibility rules — not a textual guess. Multi-hop propagation (a <c>var</c>
///     local that adopts the new type and then flows onward), reflection, <c>dynamic</c> dispatch,
///     and source-generator output are out of scope, the same blind spots the rest of the server
///     documents. <c>SignatureChange</c> is coarse unless a <c>newSignature</c> descriptor is supplied,
///     which switches on precise per-site classification via <see cref="SignatureImpactAnalyzer" />.
/// </remarks>
internal sealed class ImpactAnalysisService(SymbolResolver symbolResolver)
{
    /// <summary>
    ///     Analyzes the impact of <paramref name="changeKind" /> on the resolved symbol and returns
    ///     a structured blast-radius report.
    /// </summary>
    public async Task<ImpactAnalysisResult> AnalyzeAsync(
        string? filePath, int? line, int? column,
        string? symbolName, string? containingType,
        ChangeKind changeKind, string? newType, AccessibilityLevel? newAccessibility, string? newSignature,
        bool excludeTests,
        int? maxResults, int contextLines, bool includeGenerated,
        SymbolicKind? kind, string? project, CancellationToken ct)
    {
        ListExtensions.ThrowIfMaxResultsInvalid(maxResults);
        ValidateChangeKindDescriptors(changeKind, newType, newAccessibility, newSignature);

        (Func<ISymbol, bool>? filter, string? filterDesc) = kind.BuildKindFilter();

        (Solution solution, string solutionDir, IReadOnlyList<ISymbol> symbols) =
            await symbolResolver.ResolveOverloadsAsync(filePath, line, column, symbolName, containingType, excludeTests,
                filter, filterDesc, project, ct, kind);

        bool projectIgnored = solution.ProjectFilterIgnoredForPositionalResolution(
            project, symbolName, filePath, line, column);

        ISymbol target = symbols[0];

        // Validate inputs and prepare the change target descriptor / resolved new type.
        ITypeSymbol? newTypeSymbol = null;
        string? targetDescriptor = null;
        switch (changeKind)
        {
            case ChangeKind.TypeChange:
                ValidateTypeChangeTarget(target);
                if (String.IsNullOrWhiteSpace(newType))
                {
                    throw new UserErrorException(
                        "changeKind=TypeChange requires newType (the proposed new type, e.g. newType=long).");
                }

                newTypeSymbol = await TypeNameResolver.ResolveAsync(target, newType, solution, ct);
                targetDescriptor = Display(newTypeSymbol);
                break;

            case ChangeKind.AccessibilityNarrow:
                if (newAccessibility is null)
                {
                    throw new UserErrorException(
                        "changeKind=AccessibilityNarrow requires newAccessibility (e.g. newAccessibility=Internal).");
                }

                ValidateNarrowing(target, newAccessibility.Value);
                targetDescriptor = AccessibilityKeyword(newAccessibility.Value);
                break;
        }

        // Precise signature mode: a supplied newSignature classifies each site via real overload
        // resolution against a forked solution, replacing the coarse every-site-RequiresUpdate path.
        SignaturePreciseContext? precise = null;
        if (!String.IsNullOrWhiteSpace(newSignature) && changeKind == ChangeKind.SignatureChange)
        {
            precise = await PrepareSignaturePreciseAsync(symbols, newSignature!, solution, ct);
            targetDescriptor = precise.NormalizedList;
        }

        // Parameter routing: a TypeChange/SignatureChange to a parameter is observed at the
        // containing method's call sites. Precise mode censuses the resolved method target directly.
        IReadOnlyList<ISymbol> referenceTargets = symbols;
        int? parameterOrdinal = null;
        if (precise is not null)
        {
            referenceTargets = [precise.Target];
        }
        else if (changeKind is ChangeKind.TypeChange or ChangeKind.SignatureChange
                 && target is IParameterSymbol { ContainingSymbol: IMethodSymbol containingMethod } parameter)
        {
            referenceTargets = [containingMethod];
            parameterOrdinal = parameter.Ordinal;
        }

        List<ReferenceLocation> allLocations = await CollectReferencesAsync(
            referenceTargets, solution, includeGenerated, ct);

        allLocations = allLocations
            .DistinctBy(l =>
            {
                LinePosition start = l.Location.GetLineSpan().StartLinePosition;
                return (l.Document.FilePath, start.Line, start.Character);
            })
            .ToList();

        (allLocations, int excludedTestCount, int includedTestCount) = allLocations.PartitionByTestProject(
            excludeTests, l => l.Document.Project.IsTestProject());

        List<ClassifiedSite> classified;
        int rippleCount;
        List<string> notes;
        if (precise is not null)
        {
            Solution fork = await SignatureForkBuilder.BuildForkAsync(
                precise.Family, precise.Bound.List, allLocations, false, solution, ct);
            SignatureImpact impact = await SignatureImpactAnalyzer.ClassifyAsync(
                precise.Target, precise.Bound, precise.Delta, precise.Family, allLocations, solution, fork, ct);
            classified = impact.Sites
                .Select(s => new ClassifiedSite(s.Loc, s.Severity, s.Reason))
                .ToList();
            notes = impact.Notes.ToList();
            rippleCount = 0;
        }
        else
        {
            (classified, rippleCount) = await ClassifyAllAsync(
                allLocations, changeKind, target, newTypeSymbol, newAccessibility, parameterOrdinal, ct);
            notes = await BuildNotesAsync(changeKind, target, classified.Count, rippleCount, solution, ct);
        }

        int compatibleCount = classified.Count(c => c.Severity == ImpactSeverity.Compatible);
        int requiresUpdateCount = classified.Count(c => c.Severity == ImpactSeverity.RequiresUpdate);
        int unsafeCount = classified.Count(c => c.Severity == ImpactSeverity.Unsafe);
        int totalCount = classified.Count;

        IReadOnlyList<ProjectDistributionEntry>? distribution = null;
        IReadOnlyList<FileDistributionEntry>? fileDistribution = null;
        if (classified.Count > 0)
        {
            (List<FileDistributionEntry> files, IReadOnlyList<ProjectDistributionEntry> projects) =
                DistributionComputer.Compute(classified, solutionDir,
                    c => (c.Loc.Document.FilePath, ProjectExtensions.StripTfmSuffix(c.Loc.Document.Project.Name)));
            fileDistribution = files;
            distribution = projects;
        }

        // Truncate for display, keeping the most severe sites first so truncation never hides a break.
        List<ClassifiedSite> shown = classified
            .OrderBy(c => SeverityRank(c.Severity))
            .ThenBy(c => c.Loc.Document.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Loc.Location.GetLineSpan().StartLinePosition.Line)
            .Take(maxResults ?? Int32.MaxValue)
            .ToList();

        List<ImpactSite> sites = await BuildSitesAsync(shown, contextLines, ct);

        if (projectIgnored)
        {
            notes.Add(
                "project filter ignored — it scopes name-based resolution only; a cursor/location already " +
                "targets one symbol, so impact is reported across all projects.");
        }

        ISymbol reportSymbol = precise?.Target ?? target;
        return new ImpactAnalysisResult(
            reportSymbol.Name, SymbolQualifiers.For(reportSymbol), changeKind, targetDescriptor, sites, solutionDir,
            totalCount, distribution, fileDistribution,
            compatibleCount, requiresUpdateCount, unsafeCount,
            excludedTestCount, includedTestCount, notes.Count > 0 ? notes : null);
    }

    /// <summary>
    ///     Cross-parameter strictness: each descriptor parameter requires its matching
    ///     <paramref name="changeKind" />. Because <c>SignatureChange</c> is the default kind, this makes
    ///     a bare <c>newType</c>/<c>newAccessibility</c> an actionable error rather than a silent no-op.
    /// </summary>
    private static void ValidateChangeKindDescriptors(
        ChangeKind changeKind, string? newType, AccessibilityLevel? newAccessibility, string? newSignature)
    {
        if (!String.IsNullOrWhiteSpace(newSignature) && changeKind != ChangeKind.SignatureChange)
        {
            throw new UserErrorException("newSignature requires changeKind=SignatureChange.");
        }

        if (!String.IsNullOrWhiteSpace(newType) && changeKind != ChangeKind.TypeChange)
        {
            throw new UserErrorException("newType requires changeKind=TypeChange.");
        }

        if (newAccessibility is not null && changeKind != ChangeKind.AccessibilityNarrow)
        {
            throw new UserErrorException("newAccessibility requires changeKind=AccessibilityNarrow.");
        }
    }

    /// <summary>
    ///     Resolves the single method target for precise-signature mode (routing a parameter target to its
    ///     containing method), parses and binds the new signature, computes the delta, and resolves the
    ///     override/interface slot family — rejecting several-overload targets, non-method targets, and
    ///     metadata-anchored slots.
    /// </summary>
    private async Task<SignaturePreciseContext> PrepareSignaturePreciseAsync(
        IReadOnlyList<ISymbol> symbols, string newSignature, Solution solution, CancellationToken ct)
    {
        List<IMethodSymbol> methods = [];
        foreach (ISymbol symbol in symbols)
        {
            IMethodSymbol method = ValidateSignatureTarget(symbol);
            if (!methods.Any(m => SymbolEqualityComparer.Default.Equals(m, method)))
            {
                methods.Add(method);
            }
        }

        if (methods.Count > 1)
        {
            var overloads = String.Join("\n",
                methods.Select(m => "  " + m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            throw new UserErrorException(
                $"newSignature targets one method, but '{methods[0].Name}' resolved to {methods.Count} overloads:\n{overloads}\n"
                + "Disambiguate with a locations=['path:line:col'] cursor on the specific overload.");
        }

        IMethodSymbol methodTarget = methods[0];
        ParsedSignature parsed = SignatureParser.Parse(newSignature);
        (ParsedSignature bound, SignatureDelta delta) =
            await SignatureDeltaComputer.ComputeAsync(methodTarget, parsed, solution, ct);
        SignatureFamily family = await SignatureForkBuilder.ResolveFamilyAsync(methodTarget, solution, ct);
        if (family.ExtendsIntoMetadata)
        {
            throw new UserErrorException(
                $"'{methodTarget.Name}' overrides/implements a member declared in metadata; its external contract "
                + "can't be modeled — analyze the source member's callers with find_references.");
        }

        return new SignaturePreciseContext(methodTarget, bound, delta, family, parsed.List.ToString());
    }

    /// <summary>
    ///     Validates that <paramref name="symbol" /> (or its containing method, for a parameter target) is
    ///     an ordinary method or constructor — the v1 scope for <c>newSignature</c>.
    /// </summary>
    private static IMethodSymbol ValidateSignatureTarget(ISymbol symbol)
    {
        ISymbol candidate = symbol is IParameterSymbol { ContainingSymbol: IMethodSymbol containing }
            ? containing
            : symbol;

        if (candidate is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.Constructor } method)
        {
            return method;
        }

        throw new UserErrorException(
            $"newSignature applies to a method or constructor, not to {symbol.GetKindString()} '{symbol.Name}'. "
            + "Properties, indexers, operators, delegates, and local functions are out of scope in v1.");
    }

    private static async Task<List<ReferenceLocation>> CollectReferencesAsync(
        IReadOnlyList<ISymbol> referenceTargets, Solution solution, bool includeGenerated, CancellationToken ct)
    {
        List<ReferenceLocation>[] perSymbol = await Task.WhenAll(referenceTargets.Select(async symbol =>
        {
            IEnumerable<ReferencedSymbol> references = await SymbolFinder.FindReferencesAsync(symbol, solution, ct);
            return references
                .SelectMany(r => r.Locations)
                .Where(l => includeGenerated || !ProjectExtensions.IsGeneratedFile(l.Document.FilePath))
                .ToList();
        }));

        return perSymbol.SelectMany(l => l).ToList();
    }

    // ── Classification ──────────────────────────────────────────────────────

    private async Task<(List<ClassifiedSite> Sites, int RippleCount)> ClassifyAllAsync(
        List<ReferenceLocation> locations, ChangeKind changeKind, ISymbol target,
        ITypeSymbol? newType, AccessibilityLevel? newAccessibility, int? parameterOrdinal,
        CancellationToken ct)
    {
        List<ClassifiedSite> results = new(locations.Count);
        var rippleCount = 0;

        // Group by document so each semantic model and syntax root is built once.
        foreach (IGrouping<Document, ReferenceLocation> docGroup in locations.GroupBy(l => l.Document))
        {
            SemanticModel? model = await docGroup.Key.GetSemanticModelAsync(ct);
            SyntaxNode? root = await docGroup.Key.GetSyntaxRootAsync(ct);
            if (model is null || root is null)
            {
                continue;
            }

            foreach (ReferenceLocation loc in docGroup)
            {
                SyntaxNode node = root.FindNode(loc.Location.SourceSpan, getInnermostNodeForTie: true);
                Verdict? verdict = ClassifySite(
                    changeKind, target, newType, newAccessibility, parameterOrdinal, node, model, ct);
                if (verdict is { } v)
                {
                    results.Add(new ClassifiedSite(loc, v.Severity, v.Reason));
                    if (v.Ripple)
                    {
                        rippleCount++;
                    }
                }
            }
        }

        return (results, rippleCount);
    }

    private static Verdict? ClassifySite(
        ChangeKind changeKind, ISymbol target, ITypeSymbol? newType, AccessibilityLevel? newAccessibility,
        int? parameterOrdinal, SyntaxNode node, SemanticModel model, CancellationToken ct) =>
        changeKind switch
        {
            ChangeKind.RemoveSymbol => new Verdict(
                ImpactSeverity.Unsafe, "symbol removed — this reference will not compile"),
            ChangeKind.SignatureChange => ClassifySignatureChange(node),
            ChangeKind.AccessibilityNarrow => ClassifyAccessibility(newAccessibility!.Value, target, node, model, ct),
            ChangeKind.TypeChange => ClassifyTypeChange(target, newType!, parameterOrdinal, node, model, ct),
            _ => null
        };

    private static Verdict? ClassifySignatureChange(SyntaxNode node)
    {
        if (ReferenceKindClassifier.Classify(node) == ReferenceRole.Invocation)
        {
            return new Verdict(ImpactSeverity.RequiresUpdate, "call site must be updated for the new signature");
        }

        // nameof(M) is a compile-time string — a signature change leaves it untouched.
        if (ReferenceKindClassifier.IsNameofOperand(node))
        {
            return null;
        }

        // A method-group conversion (Func<…> f = M, xs.Select(M)) binds to the method's signature, so
        // changing it can break the conversion — flag it (coarse v1: no per-delegate check).
        return new Verdict(ImpactSeverity.RequiresUpdate,
            "method-group reference — verify it still binds under the new signature");
    }

    private static Verdict ClassifyTypeChange(
        ISymbol target, ITypeSymbol newType, int? parameterOrdinal, SyntaxNode node, SemanticModel model,
        CancellationToken ct)
    {
        if (parameterOrdinal is { } ordinal)
        {
            return ClassifyParameterArgument((IParameterSymbol)target, ordinal, newType, node, model);
        }

        ReferenceRole role = ReferenceKindClassifier.Classify(node);

        // A value written into a property/field/event is a consumer; everything else is a producer
        // whose value flows out — except a method referenced without invocation (a method group),
        // which carries no return value to classify.
        if (target is IPropertySymbol or IFieldSymbol or IEventSymbol && role == ReferenceRole.Write)
        {
            return ClassifyConsumerWrite(node, newType, model);
        }

        if (target is IMethodSymbol && role != ReferenceRole.Invocation)
        {
            return MethodGroupVerdict();
        }

        return ClassifyProducer(node, newType, model, ct);
    }

    /// <summary>
    ///     Producer site: the symbol's value flows out into a consumer context <c>C</c>; the site
    ///     survives iff <paramref name="newType" /> still converts to <c>C</c>.
    /// </summary>
    private static Verdict ClassifyProducer(SyntaxNode node, ITypeSymbol newType, SemanticModel model, CancellationToken ct)
    {
        ExpressionSyntax? value = FindProducedValueExpression(node);
        if (value is null)
        {
            return RippleVerdict();
        }

        (ITypeSymbol? context, bool determinable) = GetConsumerContext(value, model, ct);
        if (!determinable || context is null || context.TypeKind == TypeKind.Error)
        {
            return RippleVerdict();
        }

        Conversion conversion = model.Compilation.ClassifyConversion(newType, context);
        string newName = Display(newType);
        string contextName = Display(context);

        if (conversion.IsImplicit)
        {
            return new Verdict(ImpactSeverity.Compatible, $"{newName} converts implicitly to {contextName}");
        }

        return conversion.Exists
            ? new Verdict(ImpactSeverity.RequiresUpdate, $"needs cast: ({contextName}) — {newName} converts only explicitly to {contextName}")
            : new Verdict(ImpactSeverity.Unsafe, $"{newName} does not convert to {contextName}");
    }

    /// <summary>
    ///     Consumer site: a value is written into the symbol; the site survives iff the supplied
    ///     value still converts to <paramref name="newType" />.
    /// </summary>
    private static Verdict ClassifyConsumerWrite(SyntaxNode node, ITypeSymbol newType, SemanticModel model)
    {
        SyntaxNode written = UnwrapToValueExpression(node);
        string newName = Display(newType);

        if (written.Parent is AssignmentExpressionSyntax assignment && assignment.Left == written)
        {
            if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                Conversion conversion = model.ClassifyConversion(assignment.Right, newType);
                if (conversion.IsImplicit)
                {
                    return new Verdict(ImpactSeverity.Compatible, $"assigned value converts implicitly to {newName}");
                }

                return conversion.Exists
                    ? new Verdict(ImpactSeverity.RequiresUpdate, $"assigned value needs an explicit cast to {newName}")
                    : new Verdict(ImpactSeverity.Unsafe, $"assigned value does not convert to {newName}");
            }

            return new Verdict(ImpactSeverity.RequiresUpdate, $"compound assignment — verify the operator against {newName}");
        }

        return new Verdict(ImpactSeverity.RequiresUpdate, $"written here (out/ref/increment) — verify against {newName}");
    }

    /// <summary>
    ///     Parameter site (routed to the containing method's call sites): the argument at the
    ///     parameter position must still convert to <paramref name="newType" />.
    /// </summary>
    private static Verdict ClassifyParameterArgument(
        IParameterSymbol parameter, int ordinal, ITypeSymbol newType, SyntaxNode node, SemanticModel model)
    {
        if (ReferenceKindClassifier.Classify(node) != ReferenceRole.Invocation)
        {
            return MethodGroupVerdict();
        }

        BaseArgumentListSyntax? argumentList = FindArgumentList(node);
        if (argumentList is null)
        {
            return new Verdict(ImpactSeverity.RequiresUpdate, "call site must be updated for the new parameter type");
        }

        ArgumentSyntax? argument =
            argumentList.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == parameter.Name)
            ?? (ordinal < argumentList.Arguments.Count && argumentList.Arguments[ordinal].NameColon is null
                ? argumentList.Arguments[ordinal]
                : null);

        if (argument is null)
        {
            return new Verdict(ImpactSeverity.Compatible, "argument omitted (optional) — default value applies");
        }

        Conversion conversion = model.ClassifyConversion(argument.Expression, newType);
        string newName = Display(newType);
        if (conversion.IsImplicit)
        {
            return new Verdict(ImpactSeverity.Compatible, $"argument converts implicitly to {newName}");
        }

        return conversion.Exists
            ? new Verdict(ImpactSeverity.RequiresUpdate, $"argument needs an explicit cast to {newName}")
            : new Verdict(ImpactSeverity.Unsafe, $"argument does not convert to {newName}");
    }

    private static Verdict ClassifyAccessibility(
        AccessibilityLevel level, ISymbol target, SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        INamedTypeSymbol? enclosingType = EnclosingNamedType(model.GetEnclosingSymbol(node.SpanStart, ct));
        IAssemblySymbol siteAssembly = model.Compilation.Assembly;

        return IsAccessibleUnder(level, target, enclosingType, siteAssembly)
            ? new Verdict(ImpactSeverity.Compatible, $"reference stays in scope under {AccessibilityKeyword(level)}")
            : new Verdict(ImpactSeverity.Unsafe, $"reference loses access under {AccessibilityKeyword(level)}");
    }

    // ── Syntactic value-flow helpers ────────────────────────────────────────

    /// <summary>
    ///     From a reference identifier, climbs to the expression that represents the produced
    ///     value: the enclosing member access, then the invocation if the member is invoked.
    /// </summary>
    private static ExpressionSyntax? FindProducedValueExpression(SyntaxNode node)
    {
        SyntaxNode current = UnwrapToValueExpression(node);
        if (current.Parent is InvocationExpressionSyntax invocation && invocation.Expression == current)
        {
            current = invocation;
        }

        return current as ExpressionSyntax;
    }

    /// <summary>
    ///     If <paramref name="node" /> is the name portion of a member access (<c>obj.Member</c>) or
    ///     member binding (<c>obj?.Member</c>), returns the whole access expression; otherwise returns
    ///     <paramref name="node" /> unchanged.
    /// </summary>
    private static SyntaxNode UnwrapToValueExpression(SyntaxNode node) => node.Parent switch
    {
        MemberAccessExpressionSyntax ma when ma.Name == node => ma,
        MemberBindingExpressionSyntax mb when mb.Name == node => mb,
        _ => node
    };

    /// <summary>
    ///     Determines the type the produced value must convert to at <paramref name="value" />'s
    ///     immediate context, returning <c>determinable: false</c> for <c>var</c>, discards, and
    ///     contexts the one-hop model deliberately does not trace.
    /// </summary>
    private static (ITypeSymbol? Type, bool Determinable) GetConsumerContext(
        ExpressionSyntax value, SemanticModel model, CancellationToken ct)
    {
        switch (value.Parent)
        {
            case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } } equals
                when equals.Value == value:
                return declaration.Type.IsVar ? (null, false) : (model.GetTypeInfo(declaration.Type, ct).Type, true);

            case AssignmentExpressionSyntax assignment
                when assignment.Right == value && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression):
                return (model.GetTypeInfo(assignment.Left, ct).Type, true);

            case ReturnStatementSyntax returnStatement when returnStatement.Expression == value:
                return (EnclosingReturnType(value, model, ct), true);

            case ArrowExpressionClauseSyntax arrow when arrow.Expression == value:
                return (EnclosingReturnType(value, model, ct), true);

            case ArgumentSyntax argument when argument.Expression == value:
                return (ParameterTypeForArgument(argument, model, ct), true);

            case CastExpressionSyntax cast when cast.Expression == value:
                return (model.GetTypeInfo(cast.Type, ct).Type, true);

            default:
                return (null, false);
        }
    }

    private static ITypeSymbol? EnclosingReturnType(SyntaxNode value, SemanticModel model, CancellationToken ct) =>
        model.GetEnclosingSymbol(value.SpanStart, ct) switch
        {
            IMethodSymbol method => method.ReturnType,
            IPropertySymbol property => property.Type,
            _ => null
        };

    private static ITypeSymbol? ParameterTypeForArgument(ArgumentSyntax argument, SemanticModel model, CancellationToken ct)
    {
        if (argument.Parent is not BaseArgumentListSyntax argumentList || argumentList.Parent is not { } invocationLike)
        {
            return null;
        }

        SymbolInfo info = model.GetSymbolInfo(invocationLike, ct);
        IMethodSymbol? method = info.Symbol as IMethodSymbol
                                ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        if (method is null)
        {
            return null;
        }

        if (argument.NameColon?.Name.Identifier.Text is { } argumentName)
        {
            return method.Parameters.FirstOrDefault(p => p.Name == argumentName)?.Type;
        }

        int index = argumentList.Arguments.IndexOf(argument);
        if (index >= 0 && index < method.Parameters.Length)
        {
            return method.Parameters[index].Type;
        }

        IParameterSymbol? last = method.Parameters.LastOrDefault();
        return last is { IsParams: true } && last.Type is IArrayTypeSymbol array ? array.ElementType : null;
    }

    private static BaseArgumentListSyntax? FindArgumentList(SyntaxNode node)
    {
        for (SyntaxNode? current = node; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case InvocationExpressionSyntax invocation:
                    return invocation.ArgumentList;
                case ObjectCreationExpressionSyntax objectCreation:
                    return objectCreation.ArgumentList;
                case ImplicitObjectCreationExpressionSyntax implicitCreation:
                    return implicitCreation.ArgumentList;
            }
        }

        return null;
    }

    // ── Accessibility helpers ───────────────────────────────────────────────

    private static bool IsAccessibleUnder(
        AccessibilityLevel level, ISymbol target, INamedTypeSymbol? enclosingType, IAssemblySymbol? siteAssembly)
    {
        INamedTypeSymbol? declaringType = target.ContainingType;

        // Internal access spans the declaring assembly AND any friend it grants [InternalsVisibleTo],
        // so a cross-assembly reference from a friend stays in scope when narrowing to internal.
        bool internalAccess = AssembliesMatch(siteAssembly, target.ContainingAssembly)
                              || (siteAssembly is not null && target.ContainingAssembly is { } targetAssembly
                                                           && targetAssembly.GivesAccessTo(siteAssembly));
        bool within = declaringType is not null && IsWithinType(enclosingType, declaringType);
        bool derived = declaringType is not null && DerivesFromOrSame(enclosingType, declaringType);

        return level switch
        {
            AccessibilityLevel.Private => within,
            AccessibilityLevel.Protected => derived,
            AccessibilityLevel.Internal => internalAccess,
            AccessibilityLevel.ProtectedInternal => internalAccess || derived,
            AccessibilityLevel.PrivateProtected => internalAccess && derived,
            _ => true
        };
    }

    private static bool IsWithinType(INamedTypeSymbol? enclosing, INamedTypeSymbol declaring)
    {
        for (INamedTypeSymbol? type = enclosing; type is not null; type = type.ContainingType)
        {
            if (SameType(type, declaring))
            {
                return true;
            }
        }

        return false;
    }

    private static bool DerivesFromOrSame(INamedTypeSymbol? enclosing, INamedTypeSymbol declaring)
    {
        for (INamedTypeSymbol? outer = enclosing; outer is not null; outer = outer.ContainingType)
        {
            for (ITypeSymbol? baseType = outer; baseType is not null; baseType = baseType.BaseType)
            {
                if (baseType is INamedTypeSymbol named && SameType(named, declaring))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The same type can surface as symbols that are not reference-equal — metadata vs source, or two
    // compilations — which SymbolEqualityComparer.Default treats as distinct; comparing the
    // fully-qualified display string bridges that.
    private static bool SameType(INamedTypeSymbol a, INamedTypeSymbol b) =>
        SymbolEqualityComparer.Default.Equals(a.OriginalDefinition, b.OriginalDefinition)
        || a.OriginalDefinition.ToDisplayString() == b.OriginalDefinition.ToDisplayString();

    private static bool AssembliesMatch(IAssemblySymbol? a, IAssemblySymbol? b) =>
        a is not null && b is not null
                      && (SymbolEqualityComparer.Default.Equals(a, b) || String.Equals(a.Name, b.Name, StringComparison.Ordinal));

    private static INamedTypeSymbol? EnclosingNamedType(ISymbol? symbol)
    {
        for (ISymbol? current = symbol; current is not null; current = current.ContainingSymbol)
        {
            if (current is INamedTypeSymbol type)
            {
                return type;
            }
        }

        return null;
    }

    // ── Validation ──────────────────────────────────────────────────────────

    private static void ValidateTypeChangeTarget(ISymbol target)
    {
        bool hasType = target is IPropertySymbol or IFieldSymbol or IEventSymbol or IParameterSymbol
                       || target is IMethodSymbol
                       {
                           MethodKind: not (MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor)
                       };

        if (!hasType)
        {
            throw new UserErrorException(
                $"changeKind=TypeChange applies to a typed member (method return, property, field, event, parameter), " +
                $"not to {target.GetKindString()} '{target.Name}'. Use changeKind=RemoveSymbol or AccessibilityNarrow instead.");
        }
    }

    private static void ValidateNarrowing(ISymbol target, AccessibilityLevel level)
    {
        AccessibilityLevel current = MapAccessibility(target.DeclaredAccessibility);
        if (!IsStrictlyNarrower(level, current))
        {
            throw new UserErrorException(
                $"newAccessibility={level} is not strictly narrower than '{target.Name}' (currently {AccessibilityKeyword(current)}). " +
                "AccessibilityNarrow only models reductions in visibility.");
        }

        if (target is INamedTypeSymbol { ContainingType: null } && level != AccessibilityLevel.Internal)
        {
            throw new UserErrorException(
                $"Top-level type '{target.Name}' can only be narrowed to internal.");
        }
    }

    private static bool IsStrictlyNarrower(AccessibilityLevel target, AccessibilityLevel current) => current switch
    {
        AccessibilityLevel.Public => target is not AccessibilityLevel.Public,
        AccessibilityLevel.ProtectedInternal => target is AccessibilityLevel.Internal or AccessibilityLevel.Protected
            or AccessibilityLevel.PrivateProtected or AccessibilityLevel.Private,
        AccessibilityLevel.Internal => target is AccessibilityLevel.PrivateProtected or AccessibilityLevel.Private,
        AccessibilityLevel.Protected => target is AccessibilityLevel.PrivateProtected or AccessibilityLevel.Private,
        AccessibilityLevel.PrivateProtected => target is AccessibilityLevel.Private,
        _ => false
    };

    private static AccessibilityLevel MapAccessibility(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => AccessibilityLevel.Public,
        Accessibility.ProtectedOrInternal => AccessibilityLevel.ProtectedInternal,
        Accessibility.Internal => AccessibilityLevel.Internal,
        Accessibility.Protected => AccessibilityLevel.Protected,
        Accessibility.ProtectedAndInternal => AccessibilityLevel.PrivateProtected,
        Accessibility.Private => AccessibilityLevel.Private,
        _ => AccessibilityLevel.Public
    };

    private static string AccessibilityKeyword(AccessibilityLevel level) => level switch
    {
        AccessibilityLevel.Public => "public",
        AccessibilityLevel.ProtectedInternal => "protected internal",
        AccessibilityLevel.Internal => "internal",
        AccessibilityLevel.Protected => "protected",
        AccessibilityLevel.PrivateProtected => "private protected",
        AccessibilityLevel.Private => "private",
        _ => level.ToString()
    };

    // ── Output assembly ─────────────────────────────────────────────────────

    private static async Task<List<ImpactSite>> BuildSitesAsync(
        List<ClassifiedSite> shown, int contextLines, CancellationToken ct)
    {
        int clampedContextLines = Math.Clamp(contextLines, 0, ToolDescriptions.MaxContextLines);
        List<ImpactSite> sites = new(shown.Count);

        foreach (IGrouping<Document, ClassifiedSite> docGroup in shown.GroupBy(c => c.Loc.Document))
        {
            SourceText text = await docGroup.Key.GetTextAsync(ct);
            string projectName = ProjectExtensions.StripTfmSuffix(docGroup.Key.Project.Name);
            foreach (ClassifiedSite site in docGroup)
            {
                int lineIndex = site.Loc.Location.GetLineSpan().StartLinePosition.Line;
                string[] lines = SourceTextUtility.GetSurroundingLines(text, lineIndex, clampedContextLines);
                int startLineNumber = SourceTextUtility.GetDisplayStartLine(lineIndex, clampedContextLines);
                sites.Add(new ImpactSite(
                    site.Loc, lines, startLineNumber, projectName, site.Severity, site.Reason));
            }
        }

        return sites;
    }

    private static async Task<List<string>> BuildNotesAsync(
        ChangeKind changeKind, ISymbol target, int totalCount, int rippleCount, Solution solution, CancellationToken ct)
    {
        List<string> notes = new();

        switch (changeKind)
        {
            case ChangeKind.SignatureChange when totalCount > 0:
                notes.Add(
                    "SignatureChange without newSignature is coarse: every call site is flagged RequiresUpdate. "
                    + "Pass newSignature=(…) for per-argument Compatible/RequiresUpdate/Unsafe classification.");
                break;

            case ChangeKind.TypeChange when rippleCount > 0:
                notes.Add(
                    $"{rippleCount} site(s) flow into an undeterminable context (var/discard); one-hop analysis cannot trace these — review manually.");
                break;

            case ChangeKind.RemoveSymbol:
                int orphans = await CountOrphanedMembersAsync(target, solution, ct);
                if (orphans > 0)
                {
                    notes.Add(
                        $"{orphans} override(s)/implementation(s) of '{target.Name}' would be orphaned and must be removed or retargeted.");
                }

                break;
        }

        return notes;
    }

    private static async Task<int> CountOrphanedMembersAsync(ISymbol target, Solution solution, CancellationToken ct)
    {
        if (target is not (IMethodSymbol or IPropertySymbol or IEventSymbol))
        {
            return 0;
        }

        IEnumerable<ISymbol> overrides = await SymbolFinder.FindOverridesAsync(target, solution, cancellationToken: ct);
        IEnumerable<ISymbol> implementations = target.ContainingType?.TypeKind == TypeKind.Interface
            ? await SymbolFinder.FindImplementationsAsync(target, solution, cancellationToken: ct)
            : [];

        return overrides
            .Concat(implementations)
            .WhereNotGenerated()
            .Distinct(SymbolEqualityComparer.Default)
            .Count();
    }

    private static Verdict RippleVerdict() =>
        new(ImpactSeverity.Compatible, "no determinable consumer context — var/discard adoption may ripple downstream", true);

    private static Verdict MethodGroupVerdict() =>
        new(ImpactSeverity.Compatible, "method-group or non-invocation reference — verify delegate compatibility manually", true);

    private static int SeverityRank(ImpactSeverity severity) => severity switch
    {
        ImpactSeverity.Unsafe => 0,
        ImpactSeverity.RequiresUpdate => 1,
        _ => 2
    };

    private static string Display(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    private readonly record struct Verdict(ImpactSeverity Severity, string Reason, bool Ripple = false);

    private sealed record ClassifiedSite(ReferenceLocation Loc, ImpactSeverity Severity, string Reason);

    /// <summary>
    ///     The resolved inputs for precise-signature classification: the single method target, the bound
    ///     new signature, the delta, the slot family, and the normalized parameter-list descriptor rendered
    ///     into the report header.
    /// </summary>
    private sealed record SignaturePreciseContext(
        IMethodSymbol Target,
        ParsedSignature Bound,
        SignatureDelta Delta,
        SignatureFamily Family,
        string NormalizedList);
}
