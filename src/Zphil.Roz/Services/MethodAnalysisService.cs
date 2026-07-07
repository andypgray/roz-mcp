using Microsoft.CodeAnalysis;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Models;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Services;

/// <summary>
///     Orchestrates the compound <c>analyze_method</c> tool: resolves a method/constructor, then
///     composes its signature, inbound callers (reusing <see cref="ReferenceService" />'s
///     <c>referenceKinds=invocations</c> path, DI fallback and interface tip included), outbound in-solution
///     callees (via <see cref="OutboundCallExtractor" />), an external-call summary, and — when
///     requested — the overload aggregate (via <see cref="NavigationService" />).
/// </summary>
internal sealed class MethodAnalysisService(
    SymbolResolver symbolResolver,
    ReferenceService referenceService,
    NavigationService navigationService)
{
    /// <summary>
    ///     Analyzes a single method/constructor, resolved by position or by name.
    /// </summary>
    public async Task<AnalyzeMethodResult> AnalyzeMethodAsync(
        string? filePath, int? line, int? column,
        string? symbolName, string? containingType,
        bool includeOverloads, bool includeExternalCalls,
        bool excludeTests, int? maxResults, int contextLines,
        bool includeBody, bool includeGenerated,
        SymbolicKind? kind, string? project, CancellationToken ct)
    {
        ListExtensions.ThrowIfMaxResultsInvalid(maxResults);

        (Func<ISymbol, bool>? filter, string? filterDesc) = kind.BuildKindFilter();

        (Solution solution, string solutionDir, IReadOnlyList<ISymbol> symbols) =
            await symbolResolver.ResolveOverloadsAsync(filePath, line, column, symbolName, containingType,
                excludeTests, filter, filterDesc, project, ct, kind);

        if (symbols[0] is not IMethodSymbol)
        {
            throw new UserErrorException(
                $"'{symbols[0].Name}' is a {symbols[0].GetKindString()}, not a method. " +
                "analyze_method targets methods and constructors.");
        }

        // Inbound — reuse find_references referenceKinds=invocations verbatim (DI fallback + interface tip free).
        // The Invocations branch always returns a FindCallersResult; guard so a future refactor fails loudly.
        ReferenceSearchResult inboundResult = await referenceService.FindReferencesAsync(
            filePath, line, column, symbolName, containingType, ReferenceKind.Invocations,
            includeOverloads, false, excludeTests, maxResults, contextLines,
            includeGenerated, kind, project, ct);

        if (inboundResult is not FindCallersResult inbound)
        {
            throw new InvalidOperationException(
                $"referenceKinds=Invocations must yield a {nameof(FindCallersResult)}; got {inboundResult.GetType().Name}.");
        }

        // Outbound — extract callees from each overload body, merge by target identity.
        List<IMethodSymbol> outboundMethods = SelectOutboundMethods(symbols, includeOverloads, includeGenerated);

        Dictionary<string, OutboundCallGroup> mergedByTarget = new();
        var externalCount = 0;
        List<string> externalTypeNames = [];

        foreach (IMethodSymbol method in outboundMethods)
        {
            (List<OutboundCallGroup> groups, int extCount, List<string> extTypes) =
                await OutboundCallExtractor.ExtractAsync(method, solution, contextLines, includeExternalCalls, ct);

            foreach (OutboundCallGroup group in groups)
            {
                string key = OutboundCallExtractor.GroupKey(group.Target);
                if (mergedByTarget.TryGetValue(key, out OutboundCallGroup? existing))
                {
                    existing.Sites.AddRange(group.Sites);
                }
                else
                {
                    mergedByTarget[key] = group;
                }
            }

            externalCount += extCount;
            externalTypeNames.AddRange(extTypes);
        }

        // Outbound is body-bounded — a method can only call so many distinct targets — unlike inbound
        // callers, which scan the whole solution and legitimately run into the hundreds. maxResults is an
        // inbound concern and must NOT cap outbound: sharing the knob silently dropped a god-method's tail
        // collaborators whenever an agent dialed maxResults down for caller volume (the analyze_method
        // A/B's task-05 outbound-recall regression). ResponseTruncator remains the backstop for a
        // pathologically large body.
        List<OutboundCallGroup> outbound = mergedByTarget.Values
            .OrderBy(g => g.Sites.Min(s => s.Loc.GetLineSpan().StartLinePosition.Line))
            .ToList();

        // Overloads — append the overload signature list when requested.
        FindOverloadsResult? overloads = includeOverloads
            ? await navigationService.FindOverloadsAsync(filePath, line, column, symbolName, containingType,
                kind, project, excludeTests, includeGenerated, ct)
            : null;

        return new AnalyzeMethodResult(
            symbols[0], SymbolQualifiers.For(symbols[0]), solutionDir, inbound,
            outbound, externalCount, externalTypeNames.Distinct().ToList(),
            includeBody, overloads);
    }

    /// <summary>
    ///     The set of method bodies to scan for outbound calls. Name-based resolution already returns
    ///     every overload (same containing type), so they aggregate without a flag. Positional
    ///     resolution returns one symbol — expand it to the overload set only when
    ///     <paramref name="includeOverloads" /> is set, mirroring <see cref="ReferenceService" /> so
    ///     inbound and outbound stay aligned.
    /// </summary>
    private static List<IMethodSymbol> SelectOutboundMethods(
        IReadOnlyList<ISymbol> symbols, bool includeOverloads, bool includeGenerated)
    {
        List<IMethodSymbol> methods = symbols.OfType<IMethodSymbol>().ToList();

        if (includeOverloads && methods.Count == 1 && methods[0].ContainingType is { } type)
        {
            List<IMethodSymbol> allOverloads = type.GetMembers(methods[0].Name)
                .OfType<IMethodSymbol>()
                .WhereNotGenerated(includeGenerated)
                .ToList();

            if (allOverloads.Count > 1)
            {
                return allOverloads;
            }
        }

        return methods;
    }
}
