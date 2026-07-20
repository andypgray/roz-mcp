using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;
using Zphil.Roz.Resources;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Services;

/// <summary>
///     Applies the deterministic subset of a signature change — add-with-default, remove-unused, and
///     reorder-with-named-args — to a method's whole override/interface slot family and all of its call
///     sites, then persists and (optionally) verifies via the shared fork contract. Backs the
///     <c>change_signature</c> tool.
/// </summary>
/// <remarks>
///     <para>
///         Extremely conservative: the tool reuses the Phase-1 <see cref="SignatureImpactAnalyzer" /> to
///         classify every reference site against the proposed signature and refuses (writing nothing)
///         unless <em>every</em> site is <see cref="ImpactSeverity.Compatible" /> or a
///         <see cref="ImpactSeverity.RequiresUpdate" /> that <see cref="CallSiteRewritePlanner" /> can
///         mechanically rewrite. Any unsafe site, un-rewritable site (attribute usage, receiver-touch,
///         params expansion), removed parameter still used in a body, side-effectful dropped argument, or
///         reference in a generated file becomes a <see cref="UserErrorException" /> that lists every
///         blocker as <c>file:line — reason</c>.
///     </para>
///     <para>
///         The apply gate censuses with <c>excludeTests=false, includeGenerated=true</c>: a test call
///         site must be rewritten too (the build would break otherwise), and a generated-file reference is
///         a blocker (rewriting generated output is wrong; leaving it stale breaks the build). Persist and
///         verify reuse the same <see cref="SolutionChangeWriter" /> + <see cref="EditVerificationService" />
///         path as <c>rename_symbol</c> / <c>apply_code_fix</c>. The delta is compiler-only, and the Razor
///         blind spot applies.
///     </para>
/// </remarks>
internal sealed class ChangeSignatureService(
    WorkspaceManager workspaceManager,
    EditSymbolResolver symbolResolution,
    DiagnosticBaselineManager baselineManager,
    EditVerificationService verificationService)
{
    /// <summary>
    ///     Changes the signature of the method resolved from <paramref name="location" /> /
    ///     <paramref name="symbolName" /> to <paramref name="newSignature" />, across its slot family and
    ///     all call sites, under the conservative apply gate. Throws <see cref="UserErrorException" /> for
    ///     any recoverable condition (bad target, non-deterministic change, or an unsafe/un-rewritable site).
    /// </summary>
    public async Task<ChangeSignatureResult> ChangeSignatureAsync(
        string location, string symbolName, string newSignature, string? containingType,
        SymbolicKind? kind, VerifyMode verify, IProgress<ProgressNotificationValue>? progress,
        CancellationToken ct)
    {
        // Keep get_diagnostics incremental independent — capture in every mode, like the other mutators.
        baselineManager.ScheduleBaselineCaptureIfNeeded();

        // Edit-tool contract: symbolName (+ containingType/kind) is authoritative; a bare path:line is
        // normalized to path-only, a :line:col cursor only tie-breaks same-name overloads.
        LocationArg loc = LocationParser.ParseFileOrCursor(location, "change_signature", true);
        int? line = (loc as CursorLocation)?.Line;
        int? column = (loc as CursorLocation)?.Column;

        ResolvedSymbolContext context = await symbolResolution.ResolveAsync(
            loc.FilePath, symbolName, line, column, ct, containingType, kind, true);
        SemanticModel? model = await context.Document.GetSemanticModelAsync(ct);
        ISymbol? resolved = model?.GetDeclaredSymbol(context.TargetNode, ct);
        IMethodSymbol methodTarget = ValidateTarget(resolved, symbolName);

        Solution solution = context.Document.Project.Solution;

        ParsedSignature parsed = SignatureParser.Parse(newSignature);
        (ParsedSignature bound, SignatureDelta delta) =
            await SignatureDeltaComputer.ComputeAsync(methodTarget, parsed, solution, ct);

        string oldSignature = OldSignatureText(methodTarget);
        var newSignatureText = bound.List.ToString();

        if (IsEmpty(delta))
        {
            return Skipped(methodTarget, oldSignature, newSignatureText,
                "the new signature matches the current one");
        }

        RejectIfNotDeterministic(delta);

        SignatureFamily family = await SignatureForkBuilder.ResolveFamilyAsync(methodTarget, solution, ct);
        if (family.ExtendsIntoMetadata)
        {
            throw new UserErrorException(
                $"'{methodTarget.Name}' overrides/implements a member declared in metadata; its external "
                + "contract can't be changed — edit the source member's callers by hand.");
        }

        // Apply-gate census: keep test AND generated sites. Test sites must be rewritten (the build breaks
        // otherwise); generated sites become blockers.
        List<ReferenceLocation> census = await CollectCensusAsync(methodTarget, solution, ct);

        // syncXmlDoc: true — the declaration rewrite reorders/removes/placeholder-adds <param> docs.
        Solution fork = await SignatureForkBuilder.BuildForkAsync(family, bound.List, census, true, solution, ct);

        SignatureImpact impact = await SignatureImpactAnalyzer.ClassifyAsync(
            methodTarget, bound, delta, family, census, solution, fork, ct);

        List<string> blockers = [];
        blockers.AddRange(impact.Sites
            .Where(s => s.Severity == ImpactSeverity.Unsafe)
            .Select(s => FormatSite(s.Loc, s.Reason)));
        blockers.AddRange(GeneratedFileBlockers(census));
        blockers.AddRange(await RemovedParameterUsageBlockersAsync(family, delta, solution, ct));

        Dictionary<(DocumentId Doc, int Start), ImpactSeverity> severityBySite = new();
        foreach (SiteVerdict verdict in impact.Sites)
        {
            severityBySite[(verdict.Loc.Document.Id, verdict.Loc.Location.SourceSpan.Start)] = verdict.Severity;
        }

        (List<PlannedRewrite> rewrites, List<string> planBlockers) =
            await PlanCallSiteRewritesAsync(census, severityBySite, delta, solution, fork, ct);
        blockers.AddRange(planBlockers);

        if (blockers.Count > 0)
        {
            throw Refusal(methodTarget, blockers);
        }

        Solution applied = await ApplyRewritesAsync(solution, fork, family, rewrites, ct);

        ForkFinalizeOutcome outcome = await verificationService.FinalizeForkAsync(solution, applied, verify, progress, ct);

        return new ChangeSignatureResult(
            methodTarget.Name, oldSignature, newSignatureText,
            family.SourceMembers.Count, rewrites.Count, outcome.ChangedDocs,
            outcome.Verification, impact.Notes.Count > 0 ? impact.Notes : null);
    }

    // ── Target & delta validation ───────────────────────────────────────────

    private static IMethodSymbol ValidateTarget(ISymbol? symbol, string symbolName)
    {
        if (symbol is null)
        {
            throw new UserErrorException(
                $"Could not resolve '{symbolName}'. Provide a 'path:line:col' cursor on the method name, "
                + "or verify the name with get_symbols_overview.");
        }

        ISymbol candidate = symbol is IParameterSymbol { ContainingSymbol: IMethodSymbol containing }
            ? containing
            : symbol;

        if (candidate is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.Constructor } method)
        {
            return method;
        }

        throw new UserErrorException(
            $"change_signature applies to a method or constructor, not to {symbol.GetKindString()} '{symbol.Name}'. "
            + "Properties, indexers, operators, delegates, and local functions are out of scope in v1.");
    }

    private static bool IsEmpty(SignatureDelta delta) =>
        delta.Removed.Count == 0 && delta.Added.Count == 0 && delta.Retyped.Count == 0 && !delta.Reordered;

    private static void RejectIfNotDeterministic(SignatureDelta delta)
    {
        if (delta.IsDeterministicSubset)
        {
            return;
        }

        List<string> reasons = [];
        if (delta.Retyped.Count > 0)
        {
            reasons.Add("changing a parameter's type or ref-kind");
        }

        if (delta.TouchesParams)
        {
            reasons.Add("adding, removing, or reordering a params parameter");
        }

        if (delta.Added.Any(a => !a.HasExplicitDefault))
        {
            reasons.Add("adding a required parameter (no default value)");
        }

        throw new UserErrorException(
            $"change_signature applies only the mechanical subset (add-with-default, remove-unused, reorder). "
            + $"This change requires {String.Join("; ", reasons)} — analysis-only in v1. "
            + "Preview it with analyze_change_impact newSignature, then edit by hand.");
    }

    // ── Census ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     Collects the target's references for the apply gate — <em>including</em> test and generated
    ///     sites (unlike the read-only reporting path), deduped by (path, line, character).
    /// </summary>
    private static async Task<List<ReferenceLocation>> CollectCensusAsync(
        IMethodSymbol target, Solution solution, CancellationToken ct)
    {
        IEnumerable<ReferencedSymbol> references = await SymbolFinder.FindReferencesAsync(target, solution, ct);
        return references
            .SelectMany(r => r.Locations)
            .DistinctBy(l =>
            {
                LinePosition start = l.Location.GetLineSpan().StartLinePosition;
                return (l.Document.FilePath, start.Line, start.Character);
            })
            .ToList();
    }

    private IEnumerable<string> GeneratedFileBlockers(IReadOnlyList<ReferenceLocation> census) =>
        census
            .Where(l => ProjectExtensions.IsGeneratedFile(l.Document.FilePath))
            .Select(l => l.Document.FilePath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => $"{workspaceManager.GetRelativePath(path)} — reference(s) in a generated file; "
                            + "regenerate its source (change_signature does not edit generated output).");

    /// <summary>
    ///     A removed parameter that is still read in any family body is a blocker — dropping it would
    ///     leave a dangling reference. Binding (not name text) is checked, so a shadowing local is ignored.
    /// </summary>
    private static async Task<List<string>> RemovedParameterUsageBlockersAsync(
        SignatureFamily family, SignatureDelta delta, Solution solution, CancellationToken ct)
    {
        if (delta.Removed.Count == 0)
        {
            return [];
        }

        HashSet<string> removedNames = delta.Removed.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        List<string> blockers = [];

        foreach (IMethodSymbol member in family.SourceMembers)
        {
            foreach (SyntaxReference reference in member.DeclaringSyntaxReferences)
            {
                if (await reference.GetSyntaxAsync(ct) is not BaseMethodDeclarationSyntax declaration)
                {
                    continue;
                }

                Document? document = solution.GetDocument(reference.SyntaxTree);
                SemanticModel? model = document is null ? null : await document.GetSemanticModelAsync(ct);
                if (model is null)
                {
                    continue;
                }

                foreach (IdentifierNameSyntax identifier in declaration.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    if (!removedNames.Contains(identifier.Identifier.ValueText)
                        || model.GetSymbolInfo(identifier, ct).Symbol is not IParameterSymbol parameter
                        || !SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, member))
                    {
                        continue;
                    }

                    blockers.Add(
                        $"parameter '{parameter.Name}' is used in the body of {Display(member)} — "
                        + "remove its uses before removing the parameter.");
                    break;
                }
            }
        }

        return blockers.Distinct().ToList();
    }

    // ── Call-site rewrite planning ──────────────────────────────────────────

    /// <summary>
    ///     Re-runs <see cref="CallSiteRewritePlanner" /> for every site the classifier marked
    ///     <see cref="ImpactSeverity.RequiresUpdate" /> (Compatible sites need no rewrite; Unsafe sites are
    ///     already blockers), producing one <see cref="PlannedRewrite" /> per rewritable site and a blocker
    ///     string per site that cannot be mechanically rewritten. Overlapping (nested) planned rewrites are
    ///     refused as blockers: each plan derives from the base tree, so applying an outer plan would
    ///     silently discard the rewrite nested inside it and commit non-compiling code.
    /// </summary>
    private async Task<(List<PlannedRewrite> Rewrites, List<string> Blockers)> PlanCallSiteRewritesAsync(
        IReadOnlyList<ReferenceLocation> census,
        IReadOnlyDictionary<(DocumentId Doc, int Start), ImpactSeverity> severityBySite,
        SignatureDelta delta, Solution baseSolution, Solution fork, CancellationToken ct)
    {
        List<(ReferenceLocation Loc, PlannedRewrite Rewrite)> planned = [];
        List<string> blockers = [];

        IEnumerable<IGrouping<DocumentId, (ReferenceLocation Loc, int Index)>> byDocument = census
            .Select((loc, index) => (Loc: loc, Index: index))
            .GroupBy(x => x.Loc.Document.Id);

        foreach (IGrouping<DocumentId, (ReferenceLocation Loc, int Index)> group in byDocument)
        {
            Document? baseDoc = baseSolution.GetDocument(group.Key);
            Document? forkDoc = fork.GetDocument(group.Key);
            SemanticModel? baseModel = baseDoc is null ? null : await baseDoc.GetSemanticModelAsync(ct);
            SyntaxNode? baseRoot = baseDoc is null ? null : await baseDoc.GetSyntaxRootAsync(ct);
            SyntaxNode? forkRoot = forkDoc is null ? null : await forkDoc.GetSyntaxRootAsync(ct);
            if (baseModel is null || baseRoot is null || forkRoot is null)
            {
                blockers.AddRange(group.Select(x =>
                    FormatSite(x.Loc, "could not classify this reference (its document failed to load)")));
                continue;
            }

            Dictionary<int, SyntaxNode> forkByIndex = forkRoot
                .GetAnnotatedNodes(SignatureForkBuilder.SiteMarkerKind)
                .Select(node => (Node: node, node.GetAnnotations(SignatureForkBuilder.SiteMarkerKind).First().Data))
                .Where(x => x.Data is not null)
                .ToDictionary(x => Int32.Parse(x.Data!), x => x.Node);

            foreach ((ReferenceLocation loc, int index) in group)
            {
                if (!severityBySite.TryGetValue((loc.Document.Id, loc.Location.SourceSpan.Start), out ImpactSeverity severity))
                {
                    blockers.Add(FormatSite(loc, "could not classify this reference"));
                    continue;
                }

                if (severity != ImpactSeverity.RequiresUpdate)
                {
                    // Compatible → no rewrite; Unsafe → already a blocker.
                    continue;
                }

                string? blocker = TryPlanSite(
                    baseRoot, forkRoot, forkByIndex, baseModel, delta, loc, index, out PlannedRewrite? rewrite);
                if (blocker is not null)
                {
                    blockers.Add(FormatSite(loc, blocker));
                }
                else if (rewrite is not null)
                {
                    planned.Add((loc, rewrite));
                }
            }
        }

        List<PlannedRewrite> rewrites = [];
        foreach ((ReferenceLocation loc, PlannedRewrite rewrite) in planned)
        {
            bool overlapsAnother = planned.Any(other =>
                !ReferenceEquals(other.Rewrite, rewrite)
                && String.Equals(other.Rewrite.Path, rewrite.Path, StringComparison.OrdinalIgnoreCase)
                && other.Rewrite.ForkArgumentSpan.OverlapsWith(rewrite.ForkArgumentSpan));
            if (overlapsAnother)
            {
                blockers.Add(FormatSite(loc, "nests inside another rewritten call site — rewrite these by hand"));
            }
            else
            {
                rewrites.Add(rewrite);
            }
        }

        return (rewrites, blockers);
    }

    /// <summary>
    ///     Plans one RequiresUpdate site: returns a blocker reason when it cannot be mechanically rewritten,
    ///     otherwise sets <paramref name="rewrite" /> and returns null.
    /// </summary>
    private static string? TryPlanSite(
        SyntaxNode baseRoot, SyntaxNode forkRoot, IReadOnlyDictionary<int, SyntaxNode> forkByIndex,
        SemanticModel baseModel, SignatureDelta delta, ReferenceLocation loc, int index,
        out PlannedRewrite? rewrite)
    {
        rewrite = null;

        SyntaxNode baseNode = baseRoot.FindNode(loc.Location.SourceSpan, true, true);
        SyntaxNode forkNode = forkByIndex.TryGetValue(index, out SyntaxNode? annotated)
            ? annotated
            : forkRoot.FindNode(loc.Location.SourceSpan, true, true);

        SyntaxNode? baseCall = SignatureCallSite.ResolveCallNode(baseNode);
        SyntaxNode? forkCall = SignatureCallSite.ResolveCallNode(forkNode);
        if (baseCall is null or AttributeSyntax || forkCall is null)
        {
            return "attribute or non-argument usage cannot be rewritten mechanically in v1 — update it by hand";
        }

        if (SignatureCallSite.ArgumentListOf(baseCall) is not { } baseArgs
            || SignatureCallSite.ArgumentListOf(forkCall) is not ArgumentListSyntax forkArgs)
        {
            return "call site has no rewritable argument list — update it by hand";
        }

        if (baseModel.GetSymbolInfo(baseCall).Symbol is not IMethodSymbol baseBound)
        {
            return "call site does not bind to a single method — update it by hand";
        }

        bool reducedForm = baseBound.ReducedFrom is not null;
        IMethodSymbol boundUnreduced = baseBound.ReducedFrom ?? baseBound;

        RewritePlan plan = CallSiteRewritePlanner.Plan(baseArgs, boundUnreduced, delta, reducedForm);
        if (plan.Blocker is { } blocker)
        {
            return BlockerReason(blocker);
        }

        if (plan.NewArgs is null)
        {
            return "call site cannot be rewritten mechanically — update it by hand";
        }

        ArgumentSyntax? sideEffecting = plan.DroppedArgs.FirstOrDefault(a => !IsSideEffectFree(a));
        if (sideEffecting is not null)
        {
            return $"dropping argument '{sideEffecting.Expression}' may have a side effect — remove it by hand";
        }

        rewrite = new PlannedRewrite(loc.Document.FilePath!, forkArgs.Span, plan.NewArgs, forkArgs.Arguments.Count);
        return null;
    }

    private static string BlockerReason(RewriteBlocker blocker) => blocker switch
    {
        RewriteBlocker.NeedsCallerValue => "a required parameter was added — supply the argument by hand",
        RewriteBlocker.TouchesParamsArray => "a params parameter is affected — rewrite the call by hand",
        RewriteBlocker.TouchesReceiverParam => "a reduced extension call touches the receiver — rewrite by hand",
        RewriteBlocker.ExpandedParamsForm => "the call passes a params argument in expanded form — rewrite by hand",
        _ => "the call cannot be rewritten mechanically — update it by hand"
    };

    /// <summary>
    ///     The side-effect-free whitelist for a dropped argument: literal, identifier, member access
    ///     without invocation, <c>this</c>/<c>base</c>, a predefined-type reference, <c>typeof</c>,
    ///     <c>nameof</c>, or <c>default</c>. A <c>ref</c>/<c>out</c>/<c>in</c> argument never qualifies.
    /// </summary>
    private static bool IsSideEffectFree(ArgumentSyntax argument) =>
        argument.RefKindKeyword.IsKind(SyntaxKind.None) && IsSideEffectFreeExpression(argument.Expression);

    private static bool IsSideEffectFreeExpression(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax => true,
        IdentifierNameSyntax => true,
        PredefinedTypeSyntax => true,
        ThisExpressionSyntax => true,
        BaseExpressionSyntax => true,
        DefaultExpressionSyntax => true,
        TypeOfExpressionSyntax => true,
        ParenthesizedExpressionSyntax parenthesized => IsSideEffectFreeExpression(parenthesized.Expression),
        MemberAccessExpressionSyntax member when member.IsKind(SyntaxKind.SimpleMemberAccessExpression) =>
            IsSideEffectFreeExpression(member.Expression),
        InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } } => true,
        _ => false
    };

    // ── Apply ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Builds the final solution from <paramref name="baseSolution" /> by re-rooting only the documents
    ///     that genuinely change: the declaration files (already rewritten in <paramref name="fork" />) and
    ///     the call-site files (rewritten here). Census-only documents that <see cref="SignatureForkBuilder" />
    ///     merely annotated are left untouched, so they don't show up as spurious byte-identical writes.
    ///     Each rewrite is applied to every <see cref="DocumentId" /> at its path (multi-TFM siblings).
    /// </summary>
    private static async Task<Solution> ApplyRewritesAsync(
        Solution baseSolution, Solution fork, SignatureFamily family,
        IReadOnlyList<PlannedRewrite> rewrites, CancellationToken ct)
    {
        HashSet<string> touchedPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (IMethodSymbol member in family.SourceMembers)
        {
            foreach (SyntaxReference reference in member.DeclaringSyntaxReferences)
            {
                if (reference.SyntaxTree.FilePath is { Length: > 0 } declarationPath)
                {
                    touchedPaths.Add(declarationPath);
                }
            }
        }

        ILookup<string, PlannedRewrite> rewritesByPath = rewrites.ToLookup(r => r.Path, StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, PlannedRewrite> pathGroup in rewritesByPath)
        {
            touchedPaths.Add(pathGroup.Key);
        }

        Solution applied = baseSolution;
        foreach (string path in touchedPaths)
        {
            List<PlannedRewrite> pathRewrites = rewritesByPath[path].ToList();
            foreach (DocumentId docId in fork.GetDocumentIdsWithFilePath(path))
            {
                SyntaxNode? forkRoot = fork.GetDocument(docId) is { } forkDoc ? await forkDoc.GetSyntaxRootAsync(ct) : null;
                if (forkRoot is null)
                {
                    continue;
                }

                SyntaxNode newRoot = ApplyPathRewrites(forkRoot, pathRewrites)
                    .WithoutAnnotations(SignatureForkBuilder.SiteMarkerKind);
                applied = applied.WithDocumentSyntaxRoot(docId, newRoot);
            }
        }

        return applied;
    }

    private static SyntaxNode ApplyPathRewrites(SyntaxNode root, IReadOnlyList<PlannedRewrite> pathRewrites)
    {
        Dictionary<SyntaxNode, ArgumentListSyntax> map = new();
        foreach (PlannedRewrite rewrite in pathRewrites)
        {
            // The span comes from the census document's fork root; in a non-divergent sibling (the common
            // multi-TFM case) it aligns. Guard on argument count so a preprocessor-divergent sibling that
            // maps the span elsewhere is skipped rather than mis-rewritten (verify catches the rare miss).
            if (root.FindNode(rewrite.ForkArgumentSpan).FirstAncestorOrSelf<BaseArgumentListSyntax>()
                    is ArgumentListSyntax argumentList
                && argumentList.Arguments.Count == rewrite.OriginalArgumentCount)
            {
                map.TryAdd(argumentList, rewrite.NewArguments);
            }
        }

        return map.Count == 0 ? root : root.ReplaceNodes(map.Keys, (original, _) => map[original]);
    }

    // ── Output ──────────────────────────────────────────────────────────────

    private string FormatSite(ReferenceLocation loc, string reason)
    {
        string relativePath = loc.Document.FilePath is { } path ? workspaceManager.GetRelativePath(path) : "(unknown)";
        int lineNumber = loc.Location.GetLineSpan().StartLinePosition.Line + 1;
        return $"{relativePath}:{lineNumber} — {reason}";
    }

    private static UserErrorException Refusal(IMethodSymbol target, IReadOnlyList<string> blockers)
    {
        List<string> distinct = blockers.Distinct().ToList();
        var list = String.Join("\n", distinct.Select(b => "  " + b));
        return new UserErrorException(
            $"change_signature refused '{target.Name}': {distinct.Count} site(s)/condition(s) cannot be changed "
            + "safely and automatically. Nothing was written. Blockers:\n" + list
            + "\nchange_signature applies only when every site is safe (add-with-default, remove-unused, "
            + "reorder-with-named-args). Use analyze_change_impact newSignature to see the full classification, "
            + $"then edit by hand. The apply-gate rules are documented in the {RozResources.EditingGuideUri} MCP resource.");
    }

    private static ChangeSignatureResult Skipped(
        IMethodSymbol target, string oldSignature, string newSignature, string reason) =>
        new(target.Name, oldSignature, newSignature, 0, 0, [], SkippedReason: reason);

    private static string OldSignatureText(IMethodSymbol method) =>
        "(" + String.Join(", ", method.Parameters.Select(FormatParameter)) + ")";

    private static string FormatParameter(IParameterSymbol parameter)
    {
        string prefix = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => ""
        };
        string modifier = parameter.IsParams ? "params " : "";
        return $"{modifier}{prefix}{Display(parameter.Type)} {parameter.Name}";
    }

    private static string Display(ISymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    /// <summary>
    ///     One planned call-site rewrite: the physical file path, the fork span of the argument list to
    ///     replace, the replacement argument list, and the original argument count (a cross-TFM guard).
    /// </summary>
    private sealed record PlannedRewrite(
        string Path,
        TextSpan ForkArgumentSpan,
        ArgumentListSyntax NewArguments,
        int OriginalArgumentCount);
}
