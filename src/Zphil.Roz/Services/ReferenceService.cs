using Microsoft.CodeAnalysis;
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
///     Service encapsulating symbol reference, implementation, and caller lookups.
/// </summary>
internal sealed class ReferenceService(SymbolResolver symbolResolver, DiRegistrationScanner diScanner)
{
    /// <summary>
    ///     Finds references to a symbol, optionally narrowed to invocations, reads, or writes.
    /// </summary>
    /// <remarks>
    ///     When <paramref name="kinds" /> is <see cref="ReferenceKind.Invocations" /> the result is a
    ///     <see cref="FindCallersResult" /> with caller-symbol grouping; otherwise a
    ///     <see cref="FindReferencesResult" /> with flat locations.
    /// </remarks>
    public async Task<ReferenceSearchResult> FindReferencesAsync(
        string? filePath, int? line, int? column,
        string? symbolName = null, string? containingType = null,
        ReferenceKind kinds = ReferenceKind.All,
        bool includeOverloads = false, bool excludeBaseCalls = false,
        bool excludeTests = false,
        int? maxResults = null, int contextLines = 0, bool includeGenerated = false,
        SymbolicKind? kind = null, string? project = null, CancellationToken ct = default)
    {
        if (kinds == ReferenceKind.Invocations)
        {
            return await FindInvocationsCoreAsync(filePath, line, column, symbolName, containingType,
                excludeBaseCalls, excludeTests, maxResults, contextLines, includeGenerated, kind, project,
                includeOverloads, ct);
        }

        return await FindLocationsCoreAsync(filePath, line, column, symbolName, containingType, kinds,
            excludeTests, maxResults, contextLines, includeGenerated, kind, project, ct);
    }

    private async Task<FindReferencesResult> FindLocationsCoreAsync(
        string? filePath, int? line, int? column,
        string? symbolName, string? containingType,
        ReferenceKind kinds,
        bool excludeTests,
        int? maxResults, int contextLines, bool includeGenerated,
        SymbolicKind? kind, string? project, CancellationToken ct)
    {
        ListExtensions.ThrowIfMaxResultsInvalid(maxResults);

        (Func<ISymbol, bool>? filter, string? filterDesc) = kind.BuildKindFilter();

        (Solution solution, string solutionDir, IReadOnlyList<ISymbol> symbols) =
            await symbolResolver.ResolveOverloadsAsync(filePath, line, column, symbolName, containingType, excludeTests,
                filter, filterDesc, project, ct, kind);

        bool projectIgnored = solution.ProjectFilterIgnoredForPositionalResolution(
            project, symbolName, filePath, line, column);

        // Collect references for all overloads in parallel (I/O-bound Roslyn scans) and deduplicate by location.
        List<ReferenceLocation>[] perSymbol = await Task.WhenAll(symbols.Select(async symbol =>
        {
            IEnumerable<ReferencedSymbol> references = await SymbolFinder.FindReferencesAsync(symbol, solution, ct);
            return references
                .SelectMany(r => r.Locations)
                .Where(l => includeGenerated || !ProjectExtensions.IsGeneratedFile(l.Document.FilePath))
                .ToList();
        }));

        List<ReferenceLocation> allLocations = perSymbol.SelectMany(l => l).ToList();

        // Deduplicate by file + line + column (different overloads may share call sites)
        allLocations = allLocations
            .DistinctBy(l =>
            {
                LinePosition start = l.Location.GetLineSpan().StartLinePosition;
                return (l.Document.FilePath, start.Line, start.Character);
            })
            .ToList();

        // Apply kind filter after dedup so distribution counts reflect the filtered set.
        if (kinds != ReferenceKind.All)
        {
            allLocations = ReferenceKindClassifier.Filter(allLocations, kinds, ct);
        }

        (allLocations, int excludedTestCount, int includedTestCount) = allLocations.PartitionByTestProject(
            excludeTests, l => l.Document.Project.IsTestProject());

        int totalCount = allLocations.Count;
        bool isTruncated = maxResults.HasValue && allLocations.Count > maxResults.Value;

        // Compute per-project and per-file distribution before truncation so the summary covers all results
        IReadOnlyList<ProjectDistributionEntry>? distribution = null;
        List<FileDistributionEntry>? fileDistribution = null;
        if (isTruncated)
        {
            (fileDistribution, distribution) = DistributionComputer.Compute(allLocations, solutionDir,
                l => (l.Document.FilePath, ProjectExtensions.StripTfmSuffix(l.Document.Project.Name)));

            allLocations = allLocations
                .OrderBy(l => l.Document.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.Location.GetLineSpan().StartLinePosition.Line)
                .Take(maxResults!.Value).ToList();
        }

        int clampedContextLines = Math.Clamp(contextLines, 0, ToolDescriptions.MaxContextLines);

        // Fetch source context for each reference, grouped by document for efficiency
        List<ReferenceLocationWithContext> locationsWithContext = new();
        foreach (IGrouping<Document, ReferenceLocation> docGroup in allLocations.GroupBy(l => l.Document))
        {
            SourceText text = await docGroup.Key.GetTextAsync(ct);
            string projectName = ProjectExtensions.StripTfmSuffix(docGroup.Key.Project.Name);
            foreach (ReferenceLocation loc in docGroup)
            {
                int lineIndex = loc.Location.GetLineSpan().StartLinePosition.Line;
                string[] lines = SourceTextUtility.GetSurroundingLines(text, lineIndex, clampedContextLines);
                int startLineNumber = SourceTextUtility.GetDisplayStartLine(lineIndex, clampedContextLines);
                locationsWithContext.Add(new ReferenceLocationWithContext(loc, lines, startLineNumber, projectName));
            }
        }

        IReadOnlyList<DiRegistration>? diRegistrations = await FindCtorDiRegistrationsAsync(
            symbols, locationsWithContext.Count, solution, ct);

        return new FindReferencesResult(symbols[0].Name, SymbolQualifiers.For(symbols[0]), locationsWithContext, solutionDir, totalCount, kinds,
            distribution, fileDistribution, excludedTestCount, includedTestCount, diRegistrations, projectIgnored);
    }

    /// <summary>
    ///     Finds all implementations of an interface member, overrides of a virtual/abstract class
    ///     member, or — when the resolved symbol is a type — all derived classes / implementing types.
    /// </summary>
    public async Task<FindImplementationsResult> FindImplementationsAsync(
        string? filePath, int? line, int? column,
        string? symbolName = null, string? containingType = null,
        bool excludeTests = false,
        int? maxResults = null, bool includeGenerated = false,
        SymbolicKind? kind = null, bool excludeMetadata = true,
        string? project = null, CancellationToken ct = default)
    {
        (Func<ISymbol, bool>? filter, string? filterDesc) = kind.BuildKindFilter();

        (Solution solution, string solutionDir, IReadOnlyList<ISymbol> symbols) =
            await symbolResolver.ResolveOverloadsAsync(filePath, line, column, symbolName, containingType, excludeTests,
                filter, filterDesc, project, ct, kind);

        // Type dispatch: resolved to a type → find derived classes / implementing types
        if (symbols.Count == 1 && symbols[0] is INamedTypeSymbol namedType)
        {
            if (namedType.TypeKind == TypeKind.Delegate)
            {
                throw new UserErrorException(
                    $"'{namedType.Name}' is a delegate. Delegates have no implementations or derived types. " +
                    "Use find_references to locate delegate target methods or conversions.");
            }

            return await FindDerivedOrImplementingTypesCoreAsync(
                namedType, solution, solutionDir, excludeTests, maxResults,
                includeGenerated, excludeMetadata, ct);
        }

        if (symbols.Count > 1)
        {
            // All static (operators, static methods) — implementations are impossible
            if (symbols.All(s => s.IsStatic))
            {
                return new FindImplementationsResult(symbols[0].Name, SymbolQualifiers.For(symbols[0]), [], solutionDir, 0,
                    TargetSymbol: symbols[0]);
            }

            // Non-static overloads still need disambiguation
            SymbolResolver.ThrowAmbiguityError(symbols, symbolName ?? symbols[0].Name, containingType, solutionDir);
        }

        ISymbol symbol = symbols[0];

        IEnumerable<ISymbol> implementations = await SymbolFinder.FindImplementationsAsync(
            symbol, solution, cancellationToken: ct);

        if (IsOverridableMember(symbol))
        {
            IEnumerable<ISymbol> overrides = await SymbolFinder.FindOverridesAsync(
                symbol, solution, cancellationToken: ct);
            implementations = implementations.Concat(overrides);
        }

        // Supplement: SymbolFinder misses implementations in types with compilation errors.
        if (symbol.ContainingType is { } sourceType)
        {
            List<INamedTypeSymbol> fallbackTypes =
                await SymbolSearch.FindDerivedSourceTypesAsync(sourceType, solution, ct);

            if (fallbackTypes.Count > 0)
            {
                IEnumerable<ISymbol> fallbackMembers = fallbackTypes
                    .SelectMany(t => t.GetMembers()
                        .Where(m => String.Equals(m.MetadataName, symbol.MetadataName, StringComparison.Ordinal)));
                implementations = implementations.Concat(fallbackMembers);
            }
        }

        List<ISymbol> candidates = SymbolDeduplication.DeduplicateByLocation(
            implementations.WhereNotGenerated(includeGenerated));

        return await FinalizeImplementationResultAsync(
            candidates, static s => s.ContainingType ?? s as INamedTypeSymbol,
            symbol, solution, solutionDir, maxResults, excludeTests, excludeMetadata, ct);
    }

    private static bool IsOverridableMember(ISymbol symbol) =>
        symbol is IMethodSymbol or IPropertySymbol or IEventSymbol
        && symbol.ContainingType?.TypeKind is TypeKind.Class or TypeKind.Struct
        && (symbol.IsAbstract || symbol.IsVirtual || symbol.IsOverride);

    /// <summary>
    ///     Shared implementation of <see cref="FindImplementationsAsync" /> when the resolved
    ///     symbol is a type: returns derived classes (for classes) or implementing types
    ///     (for interfaces). Supplements Roslyn's SymbolFinder with a GlobalNamespace walk
    ///     to catch types whose compilation context has errors.
    /// </summary>
    private async Task<FindImplementationsResult> FindDerivedOrImplementingTypesCoreAsync(
        INamedTypeSymbol typeSymbol, Solution solution, string solutionDir,
        bool excludeTests, int? maxResults, bool includeGenerated, bool excludeMetadata,
        CancellationToken ct)
    {
        List<ISymbol> candidates = (await FindDerivedOrImplementingTypesAsync(typeSymbol, solution, ct))
            .WhereNotGenerated(includeGenerated)
            .Cast<ISymbol>()
            .ToList();

        return await FinalizeImplementationResultAsync(
            candidates, static s => s as INamedTypeSymbol,
            typeSymbol, solution, solutionDir, maxResults, excludeTests, excludeMetadata, ct);
    }

    /// <summary>
    ///     Runs the standard exclude → distribute → truncate → DI-scan pipeline and materialises a
    ///     <see cref="FindImplementationsResult" />.
    /// </summary>
    /// <remarks>
    ///     <paramref name="typeProjector" /> selects the types fed into the DI scan — project to
    ///     each member's containing type for member-level dispatch, or to the symbol itself for
    ///     type-level dispatch.
    /// </remarks>
    private async Task<FindImplementationsResult> FinalizeImplementationResultAsync(
        List<ISymbol> candidates,
        Func<ISymbol, INamedTypeSymbol?> typeProjector,
        ISymbol targetSymbol,
        Solution solution, string solutionDir,
        int? maxResults, bool excludeTests, bool excludeMetadata,
        CancellationToken ct)
    {
        List<ISymbol> implList = candidates.ExcludeMetadataSymbols(excludeMetadata, out int excludedMetadataCount);
        (implList, int excludedTestCount, int includedTestCount) = implList.PartitionByTestProject(
            excludeTests, s => s.IsInTestProject(solution));

        IReadOnlyList<ProjectDistributionEntry>? distribution;
        int totalCount;
        (implList, totalCount, distribution) = implList.TruncateWithDistribution(maxResults, solution);

        IReadOnlyDictionary<string, IReadOnlyList<DiRegistration>>? diByType = null;
        if (implList.Count > 0)
        {
            List<INamedTypeSymbol> diTypes = SymbolDeduplication.DeduplicateByLocation(
                    implList.Select(typeProjector).OfType<INamedTypeSymbol>())
                .ToList();

            if (diTypes.Count > 0)
            {
                diByType = await diScanner.FindRegistrationsForTypesAsync(diTypes, solution, ct);
            }
        }

        return new FindImplementationsResult(targetSymbol.Name, SymbolQualifiers.For(targetSymbol), implList, solutionDir, totalCount,
            distribution, targetSymbol, excludedTestCount, includedTestCount, excludedMetadataCount, diByType);
    }

    private static async Task<List<INamedTypeSymbol>> FindDerivedOrImplementingTypesAsync(
        INamedTypeSymbol typeSymbol, Solution solution, CancellationToken ct)
    {
        List<INamedTypeSymbol> primary;
        if (typeSymbol.TypeKind == TypeKind.Interface)
        {
            IEnumerable<ISymbol> implementations = await SymbolFinder.FindImplementationsAsync(
                typeSymbol, solution, cancellationToken: ct);
            primary = SymbolDeduplication.DeduplicateByLocation(implementations.OfType<INamedTypeSymbol>());
        }
        else
        {
            IEnumerable<INamedTypeSymbol> derivedTypes = await SymbolFinder.FindDerivedClassesAsync(
                typeSymbol, solution, cancellationToken: ct);
            primary = SymbolDeduplication.DeduplicateByLocation(derivedTypes);
        }

        // Supplement: SymbolFinder misses types whose compilation context has errors (e.g. CS0315).
        // Always run — we can't know which types were missed without actually checking.
        List<INamedTypeSymbol> fallback = await SymbolSearch.FindDerivedSourceTypesAsync(typeSymbol, solution, ct);
        if (fallback.Count == 0)
        {
            return primary;
        }

        HashSet<(string, int)?> seen = primary.Select(SymbolDeduplication.GetLocationKey).ToHashSet();
        primary.AddRange(fallback.Where(s => !seen.Contains(SymbolDeduplication.GetLocationKey(s))));
        return primary;
    }

    /// <summary>
    ///     Finds invocation sites of a method, constructor, or indexer.
    /// </summary>
    /// <remarks>
    ///     Shared implementation of <see cref="FindReferencesAsync" /> when <c>referenceKinds=invocations</c>:
    ///     caller-symbol grouping, receiver-type filtering, base-call exclusion, overload expansion,
    ///     interface-dispatch tip, and DI-registration fallback.
    /// </remarks>
    private async Task<FindCallersResult> FindInvocationsCoreAsync(
        string? filePath, int? line, int? column,
        string? symbolName, string? containingType,
        bool excludeBaseCalls,
        bool excludeTests, int? maxResults, int contextLines,
        bool includeGenerated,
        SymbolicKind? kind, string? project,
        bool includeOverloads, CancellationToken ct)
    {
        ListExtensions.ThrowIfMaxResultsInvalid(maxResults);

        (Func<ISymbol, bool>? filter, string? filterDesc) = kind.BuildKindFilter();

        (Solution solution, string solutionDir, IReadOnlyList<ISymbol> symbols) =
            await symbolResolver.ResolveOverloadsAsync(filePath, line, column, symbolName, containingType, excludeTests,
                filter, filterDesc, project, ct, kind);

        bool projectIgnored = solution.ProjectFilterIgnoredForPositionalResolution(
            project, symbolName, filePath, line, column);

        // When includeOverloads is requested and we resolved a single symbol, expand to all overloads
        // in the same containing type. Name-based resolution already returns all overloads via
        // ResolveOverloadsAsync, so this primarily helps positional resolution.
        if (includeOverloads && symbols.Count == 1)
        {
            INamedTypeSymbol? type = symbols[0].ContainingType;
            if (type is not null)
            {
                string memberName = symbols[0].Name;
                List<ISymbol> allOverloads = symbols[0] switch
                {
                    IMethodSymbol => type.GetMembers(memberName).OfType<IMethodSymbol>()
                        .WhereNotGenerated(includeGenerated)
                        .Cast<ISymbol>().ToList(),
                    IPropertySymbol { IsIndexer: true } => type.GetMembers().OfType<IPropertySymbol>()
                        .Where(p => p.IsIndexer)
                        .WhereNotGenerated(includeGenerated)
                        .Cast<ISymbol>().ToList(),
                    _ => [symbols[0]]
                };

                if (allOverloads.Count > 1)
                {
                    symbols = allOverloads;
                }
            }
        }

        // Collect callers for all overloads in parallel (I/O-bound Roslyn scans).
        List<SymbolCallerInfo>[] perSymbolCallers = await Task.WhenAll(symbols.Select(async symbol =>
        {
            IEnumerable<SymbolCallerInfo> callers = await SymbolFinder.FindCallersAsync(symbol, solution, ct);
            return callers
                .Where(c => includeGenerated || !c.CallingSymbol.IsInGeneratedFile())
                .ToList();
        }));

        List<SymbolCallerInfo> allCallers = perSymbolCallers.SelectMany(c => c).ToList();

        if (excludeBaseCalls)
        {
            allCallers = allCallers
                .Where(c => !symbols.Any(s => IsOverrideOf(c.CallingSymbol, s)))
                .ToList();
        }

        // Merge callers with the same CallingSymbol (can happen when one method calls multiple overloads,
        // or when the same caller appears from different TFM compilations).
        // Group by calling symbol's source location to handle cross-TFM duplicates.
        List<(ISymbol CallingSymbol, List<Location> Locations)> mergedCallers = allCallers
            .GroupBy(c => SymbolDeduplication.GetLocationKey(c.CallingSymbol) ?? (c.CallingSymbol.ToDisplayString(), 0))
            .Select(g => (
                g.First().CallingSymbol,
                Locations: g
                    .SelectMany(c => c.Locations)
                    .Where(l => l.IsInSource)
                    .DistinctBy(l => (l.SourceTree?.FilePath, l.GetLineSpan().StartLinePosition.Line))
                    .ToList()))
            .ToList();

        // When containingType is specified, filter out call sites where the receiver expression's
        // type is unrelated to the target type. Without this, inherited/interface members return
        // callers from ALL types that share the same base member (e.g. all IDictionary indexer users).
        // The symbol resolver has already resolved containingType to members whose ContainingType IS
        // the correct target — reuse it rather than re-resolving by name (which is non-deterministic
        // across cross-compilation results).
        if (containingType is not null && symbols[0].ContainingType is { } targetType)
        {
            mergedCallers = await FilterByReceiverTypeAsync(mergedCallers, targetType, solution, ct);
        }

        (mergedCallers, int excludedTestCount, int includedTestCount) = mergedCallers.PartitionByTestProject(
            excludeTests, c => c.CallingSymbol.IsInTestProject(solution));

        int totalCount = mergedCallers.Count;
        bool isTruncated = maxResults.HasValue && mergedCallers.Count > maxResults.Value;

        // Compute per-project and per-file distribution before truncation
        IReadOnlyList<ProjectDistributionEntry>? distribution = null;
        List<FileDistributionEntry>? fileDistribution = null;
        if (isTruncated)
        {
            (fileDistribution, distribution) = DistributionComputer.Compute(mergedCallers, solutionDir, c =>
            {
                Location? loc = c.CallingSymbol.Locations.FirstOrDefault(l => l.IsInSource);
                Document? doc = loc?.SourceTree is { } tree ? solution.GetDocument(tree) : null;
                return (doc?.FilePath, doc is not null ? ProjectExtensions.StripTfmSuffix(doc.Project.Name) : "Unknown");
            });

            mergedCallers = mergedCallers
                .OrderBy(c => c.CallingSymbol.Locations.FirstOrDefault()?.GetLineSpan().Path,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.CallingSymbol.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line ?? 0)
                .Take(maxResults!.Value)
                .ToList();
        }

        int clampedContextLines = Math.Clamp(contextLines, 0, ToolDescriptions.MaxContextLines);

        // Fetch source context for each call site, grouped by syntax tree for efficiency
        List<CallerWithLineText> callersWithContext = new(mergedCallers.Count);
        Dictionary<SyntaxTree, (SourceText? Text, string? ProjectName)> treeCache = new();

        foreach ((ISymbol callingSymbol, List<Location> locations) in mergedCallers)
        {
            List<LocationWithContext> locsWithContext = new();
            string? callerProjectName = null;
            foreach (Location loc in locations)
            {
                int lineIndex = loc.GetLineSpan().StartLinePosition.Line;

                SyntaxTree? tree = loc.SourceTree;
                if (tree is null)
                {
                    continue;
                }

                if (!treeCache.TryGetValue(tree, out (SourceText? Text, string? ProjectName) cached))
                {
                    Document? doc = solution.GetDocument(tree);
                    SourceText? text = doc is not null ? await doc.GetTextAsync(ct) : null;
                    cached = (text, doc is not null ? ProjectExtensions.StripTfmSuffix(doc.Project.Name) : null);
                    treeCache[tree] = cached;
                }

                callerProjectName ??= cached.ProjectName;

                if (cached.Text is null)
                {
                    continue;
                }

                string[] lines = SourceTextUtility.GetSurroundingLines(cached.Text, lineIndex, clampedContextLines);
                locsWithContext.Add(new LocationWithContext(loc, lines, SourceTextUtility.GetDisplayStartLine(lineIndex, clampedContextLines)));
            }

            callersWithContext.Add(new CallerWithLineText(callingSymbol, locsWithContext, callerProjectName));
        }

        IReadOnlyList<DiRegistration>? diRegistrations = await FindCtorDiRegistrationsAsync(
            symbols, callersWithContext.Count, solution, ct);

        IReadOnlyList<InterfaceMemberDescriptor>? interfaceMembers = BuildInterfaceMemberDescriptors(symbols[0]);
        string? concreteTypeShort = interfaceMembers is { Count: > 0 }
            ? symbols[0].ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            : null;

        int overloadCount = includeOverloads ? symbols.Count : 0;
        return new FindCallersResult(symbols[0].Name, SymbolQualifiers.For(symbols[0]), callersWithContext, solutionDir, totalCount, distribution, fileDistribution,
            excludedTestCount, includedTestCount, diRegistrations, overloadCount, interfaceMembers, concreteTypeShort, projectIgnored);
    }

    /// <summary>
    ///     Builds descriptors for every interface member that <paramref name="target" /> implements.
    /// </summary>
    /// <remarks>
    ///     Returns <c>null</c> when <paramref name="target" /> is not a concrete method/property/event
    ///     that could implement an interface member (e.g., a type, field, or an interface member itself).
    /// </remarks>
    private static IReadOnlyList<InterfaceMemberDescriptor>? BuildInterfaceMemberDescriptors(ISymbol target)
    {
        if (target is not (IMethodSymbol or IPropertySymbol or IEventSymbol))
        {
            return null;
        }

        if (target.ContainingType?.TypeKind == TypeKind.Interface)
        {
            return null;
        }

        IReadOnlyList<ISymbol> ifaceMembers = InterfaceImplementationLookup.FindInterfaceMembers(target);
        if (ifaceMembers.Count == 0)
        {
            return null;
        }

        List<InterfaceMemberDescriptor> descriptors = new(ifaceMembers.Count);
        foreach (ISymbol ifaceMember in ifaceMembers)
        {
            INamedTypeSymbol? containingType = ifaceMember.ContainingType;
            if (containingType is null)
            {
                continue;
            }

            string shortName = containingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            string fullName = containingType.ToDisplayString(SymbolQualifiers.FullyQualifiedWithoutGlobal);

            Location? sourceLoc = ifaceMember.Locations.FirstOrDefault(l => l.IsInSource);
            string? filePath = sourceLoc?.GetLineSpan().Path;
            int? line = sourceLoc is not null ? sourceLoc.GetLineSpan().StartLinePosition.Line + 1 : null;

            descriptors.Add(new InterfaceMemberDescriptor(shortName, fullName, ifaceMember.Name, filePath, line));
        }

        return descriptors;
    }

    private static bool IsOverrideOf(ISymbol caller, ISymbol target)
    {
        if (caller is IMethodSymbol callerMethod && target is IMethodSymbol targetMethod)
        {
            IMethodSymbol? overridden = callerMethod.OverriddenMethod;
            while (overridden is not null)
            {
                if (AreEquivalentSymbols(overridden, targetMethod))
                {
                    return true;
                }

                overridden = overridden.OverriddenMethod;
            }
        }

        if (caller is IPropertySymbol callerProperty && target is IPropertySymbol targetProperty)
        {
            IPropertySymbol? overridden = callerProperty.OverriddenProperty;
            while (overridden is not null)
            {
                if (AreEquivalentSymbols(overridden, targetProperty))
                {
                    return true;
                }

                overridden = overridden.OverriddenProperty;
            }
        }

        return false;
    }

    /// <summary>
    ///     Cross-compilation-aware symbol equivalence. <see cref="SymbolEqualityComparer.Default" />
    ///     fails when comparing the same symbol from different project compilations (e.g. multi-TFM
    ///     or cross-project references). Falls back to source location, then display string.
    /// </summary>
    private static bool AreEquivalentSymbols(ISymbol a, ISymbol b)
    {
        if (SymbolEqualityComparer.Default.Equals(a, b))
        {
            return true;
        }

        if (SymbolDeduplication.AreSameSourceLocation(a, b))
        {
            return true;
        }

        // Metadata symbols (NuGet/BCL) have no source location — compare by display string
        return a.OriginalDefinition.ToDisplayString() == b.OriginalDefinition.ToDisplayString();
    }

    /// <summary>
    ///     Filters merged callers to only include call sites where the receiver expression's type
    ///     is assignable to <paramref name="targetType" />. Removes callers with no remaining locations.
    /// </summary>
    private static async Task<List<(ISymbol CallingSymbol, List<Location> Locations)>> FilterByReceiverTypeAsync(
        List<(ISymbol CallingSymbol, List<Location> Locations)> mergedCallers,
        INamedTypeSymbol targetType,
        Solution solution,
        CancellationToken ct)
    {
        Dictionary<SyntaxTree, SemanticModel?> modelCache = new();
        List<(ISymbol CallingSymbol, List<Location> Locations)> filtered = new();

        foreach ((ISymbol callingSymbol, List<Location> locations) in mergedCallers)
        {
            List<Location> matchingLocations = new();
            foreach (Location loc in locations)
            {
                SyntaxTree? tree = loc.SourceTree;
                if (tree is null)
                {
                    matchingLocations.Add(loc);
                    continue;
                }

                if (!modelCache.TryGetValue(tree, out SemanticModel? model))
                {
                    Document? doc = solution.GetDocument(tree);
                    model = doc is not null ? await doc.GetSemanticModelAsync(ct) : null;
                    modelCache[tree] = model;
                }

                if (model is null)
                {
                    matchingLocations.Add(loc);
                    continue;
                }

                SyntaxNode root = await tree.GetRootAsync(ct);
                SyntaxNode node = root.FindNode(loc.SourceSpan);

                ITypeSymbol? receiverType = GetReceiverType(node, model, callingSymbol);

                // Conservative: include when receiver type can't be determined (e.g. dynamic, error types)
                if (receiverType is null || IsAssignableTo(receiverType, targetType))
                {
                    matchingLocations.Add(loc);
                }
            }

            if (matchingLocations.Count > 0)
            {
                filtered.Add((callingSymbol, matchingLocations));
            }
        }

        return filtered;
    }

    /// <summary>
    ///     Extracts the compile-time type of the receiver expression at a call site.
    ///     For <c>obj.Method()</c> returns the type of <c>obj</c>; for <c>obj[i]</c> returns
    ///     the type of <c>obj</c>; for implicit <c>this</c> calls returns the enclosing type.
    ///     For constructor invocations (<c>new X()</c> or target-typed <c>new()</c>) returns
    ///     the type being constructed.
    /// </summary>
    private static ITypeSymbol? GetReceiverType(SyntaxNode node, SemanticModel model, ISymbol callingSymbol)
    {
        for (SyntaxNode? current = node; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case ObjectCreationExpressionSyntax objCreation:
                    return model.GetTypeInfo(objCreation).Type;

                case ImplicitObjectCreationExpressionSyntax implicitNew:
                    return model.GetTypeInfo(implicitNew).Type;

                case InvocationExpressionSyntax invocation:
                    return invocation.Expression switch
                    {
                        MemberAccessExpressionSyntax memberAccess =>
                            model.GetTypeInfo(memberAccess.Expression).Type,
                        MemberBindingExpressionSyntax =>
                            GetConditionalAccessReceiverType(current, model),
                        _ => callingSymbol.ContainingType // implicit this
                    };

                case ElementAccessExpressionSyntax elementAccess:
                    return model.GetTypeInfo(elementAccess.Expression).Type;

                case ConditionalAccessExpressionSyntax conditionalAccess
                    when current != node: // only match when we walked UP to it
                    return model.GetTypeInfo(conditionalAccess.Expression).Type;
            }
        }

        return null;
    }

    /// <summary>
    ///     For <c>obj?.Method()</c>, walks up from the <see cref="MemberBindingExpressionSyntax" />
    ///     to the enclosing <see cref="ConditionalAccessExpressionSyntax" /> and returns the type
    ///     of the receiver expression.
    /// </summary>
    private static ITypeSymbol? GetConditionalAccessReceiverType(SyntaxNode node, SemanticModel model)
    {
        ConditionalAccessExpressionSyntax? conditionalAccess =
            node.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>();
        return conditionalAccess is not null
            ? model.GetTypeInfo(conditionalAccess.Expression).Type
            : null;
    }

    /// <summary>
    ///     Checks if <paramref name="type" /> is the same as, derives from, or implements
    ///     <paramref name="target" />. Uses <see cref="ISymbol.OriginalDefinition" /> for
    ///     generic type comparison (e.g. <c>MyList&lt;int&gt;</c> matches <c>MyList&lt;T&gt;</c>).
    /// </summary>
    private static bool IsAssignableTo(ITypeSymbol type, INamedTypeSymbol target)
    {
        INamedTypeSymbol targetOriginal = target.OriginalDefinition;

        // Pre-compute FQN for cross-compilation fallback (multi-TFM projects have
        // equivalent types in separate compilations that SymbolEqualityComparer rejects).
        string targetFqn = targetOriginal.ToDisplayString();

        bool IsSameType(INamedTypeSymbol candidate)
        {
            return SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, targetOriginal) ||
                   candidate.OriginalDefinition.ToDisplayString() == targetFqn;
        }

        // Walk base type chain
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current is INamedTypeSymbol named && IsSameType(named))
            {
                return true;
            }
        }

        // Check all implemented interfaces
        return type.AllInterfaces.Any(IsSameType);
    }

    /// <summary>
    ///     When a .ctor lookup returns no direct results, check for DI registrations as a fallback.
    /// </summary>
    private async Task<IReadOnlyList<DiRegistration>?> FindCtorDiRegistrationsAsync(
        IReadOnlyList<ISymbol> symbols, int resultCount, Solution solution, CancellationToken ct)
    {
        if (resultCount > 0)
        {
            return null;
        }

        IMethodSymbol? ctor = symbols.OfType<IMethodSymbol>()
            .FirstOrDefault(static s => s.MethodKind == MethodKind.Constructor);
        if (ctor is null)
        {
            return null;
        }

        return await diScanner.FindRegistrationsAsync(ctor.ContainingType, solution, ct);
    }
}
