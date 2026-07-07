using Microsoft.CodeAnalysis;

namespace Zphil.Roz.Symbols;

/// <summary>
///     Runs the shared name-resolution dispatch used by <c>find_symbol</c>, <c>find_overloads</c>
///     (name path), and the read-only symbol resolver (<c>find_references</c>,
///     <c>find_implementations</c>, <c>rename</c>, etc.).
/// </summary>
/// <remarks>
///     <para>
///         Dispatch core:
///         <list type="number">
///             <item>
///                 Extracts open-generic arity (e.g. <c>Processor&lt;&gt;</c> →
///                 <c>searchName="Processor"</c>, <c>arity=1</c>).
///             </item>
///             <item>Validates the FQN+containingType combination via <see cref="FqnParser.ThrowIfInvalid" />.</item>
///             <item>
///                 Routes to one of three resolvers:
///                 <list type="bullet">
///                     <item>
///                         Bare special member (<c>.ctor</c>, <c>.cctor</c>, <c>op_*</c>, <c>this[]</c>) →
///                         <see cref="SpecialMemberResolver" />
///                     </item>
///                     <item>
///                         FQN (contains <c>.</c>, not a bare special member) → <see cref="FqnResolver" />, falls
///                         through to <see cref="SymbolSearch" /> on miss
///                     </item>
///                     <item>Otherwise → <see cref="SymbolSearch.SearchProjectsAsync" /></item>
///                 </list>
///             </item>
///         </list>
///         Returns raw candidates plus metadata about which branch ran. Callers apply their own
///         dedup, filter, and ambiguity policies.
///     </para>
/// </remarks>
internal static class NameMatchPipeline
{
    /// <summary>
    ///     Runs the dispatch and returns raw candidates with branch metadata.
    ///     When <paramref name="useFileInference" /> is <c>true</c>, special-member resolution flows
    ///     through <see cref="SpecialMemberResolver.ResolveWithFileInferenceAsync" /> — infers the
    ///     containing type from the supplied file, or throws when neither
    ///     <paramref name="containingType" /> nor a file path is supplied. When <c>false</c>
    ///     (exploration mode), uses plain <see cref="SpecialMemberResolver.ResolveAsync" /> which
    ///     walks all types when <paramref name="containingType" /> is <c>null</c>.
    /// </summary>
    public static async Task<NameMatchDispatchResult> DispatchAsync(
        Solution solution,
        IReadOnlyList<Project> projects,
        string symbolName,
        string? containingType,
        string? filePathForInference,
        string? resolvedFilePathForInference,
        bool useFileInference,
        CancellationToken ct)
    {
        (string searchName, int? requestedArity) = GenericArityParser.ExtractArity(symbolName);
        FqnParser.ThrowIfInvalid(searchName, containingType);

        bool isSpecialMember = SpecialMemberResolver.IsSpecialMemberName(searchName);

        // Order matters: special-member names look like FQNs (".ctor" contains a dot) but
        // FqnParser.IsFqn already excludes them; the explicit guard here is defensive.
        if (!isSpecialMember && FqnParser.IsFqn(searchName))
        {
            List<ISymbol> fqnMatches = await FqnResolver.ResolveAsync(projects, searchName, requestedArity, ct);
            if (fqnMatches.Count > 0)
            {
                return new NameMatchDispatchResult(
                    searchName, requestedArity, containingType, fqnMatches,
                    true, false);
            }
            // Fall through to name search on FQN miss
        }

        if (isSpecialMember)
        {
            if (useFileInference)
            {
                (string resolvedType, List<ISymbol> inferredMatches) =
                    await SpecialMemberResolver.ResolveWithFileInferenceAsync(
                        solution, projects, searchName, containingType,
                        filePathForInference, resolvedFilePathForInference, ct);
                return new NameMatchDispatchResult(
                    searchName, requestedArity, resolvedType, inferredMatches,
                    false, true);
            }

            List<ISymbol> specialMatches = await SpecialMemberResolver.ResolveAsync(
                projects, searchName, containingType, ct);
            return new NameMatchDispatchResult(
                searchName, requestedArity, containingType, specialMatches,
                false, true);
        }

        List<ISymbol> nameMatches = await SymbolSearch.SearchProjectsAsync(projects, searchName, ct);
        return new NameMatchDispatchResult(
            searchName, requestedArity, containingType, nameMatches,
            false, false);
    }
}

/// <summary>
///     Output of <see cref="NameMatchPipeline.DispatchAsync" />: raw candidates plus flags telling
///     callers which dispatch branch ran so they can pick the right post-processing pipeline.
/// </summary>
internal readonly record struct NameMatchDispatchResult(
    string SearchName,
    int? RequestedArity,
    string? ResolvedContainingType,
    IReadOnlyList<ISymbol> Candidates,
    bool IsFqnMatched,
    bool IsSpecialMember);
