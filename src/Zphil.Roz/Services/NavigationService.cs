using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Constants;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;
using Zphil.Roz.Symbols;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Services;

/// <summary>
///     Service encapsulating symbol search, file overview, and go-to-definition logic.
/// </summary>
internal sealed class NavigationService(WorkspaceManager workspaceManager, SymbolResolver symbolResolver)
{
    /// <summary>
    ///     Searches for symbols by name substring with optional filters.
    /// </summary>
    public async Task<FindSymbolResult> FindSymbolAsync(
        string name, SymbolicKind? kind = null, int depth = 0,
        string? excludePattern = null, string? containingType = null,
        string? project = null, SymbolMatchMode matchMode = SymbolMatchMode.Contains, bool includeBody = false,
        bool excludeTests = false, int? maxResults = null, SymbolicKind[]? memberKinds = null,
        bool includeGenerated = false, int? maxMembers = null, string[]? filePaths = null,
        CancellationToken ct = default)
    {
        string solutionDir = await workspaceManager.GetRequiredSolutionDirectoryAsync(ct);
        Solution solution = await workspaceManager.GetSolutionAsync(ct);

        IReadOnlyList<Project> allProjects = solution.FilterByProjectName(project);

        IReadOnlyList<Project> projects = excludeTests
            ? allProjects.Where(p => !p.IsTestProject()).ToList()
            : allProjects;

        // Count distinct stripped names so multi-TFM test projects don't inflate the hint.
        int excludedTestProjectCount = excludeTests ? ProjectExtensions.CountDistinctTestProjects(allProjects) : 0;

        NameMatchDispatchResult dispatch = await NameMatchPipeline.DispatchAsync(
            solution, projects, name, containingType,
            null, null,
            false, ct);

        string searchName = dispatch.SearchName;
        int? requestedArity = dispatch.RequestedArity;
        bool isSpecialMemberName = dispatch.IsSpecialMember;
        bool isIndexerName = searchName is "this[]" or "this";
        bool fqnResolved = dispatch.IsFqnMatched;
        List<ISymbol> allMatches = dispatch.Candidates.ToList();

        if (isSpecialMemberName && isIndexerName)
        {
            matchMode = SymbolMatchMode.Contains;
        }

        // Fallback: SymbolFinder.FindSourceDeclarationsWithPatternAsync misses types whose
        // compilation context has errors (e.g. CS0315). Walk GlobalNamespace as a supplement.
        if (!isSpecialMemberName && !fqnResolved
                                 && !allMatches.Any(s => s is INamedTypeSymbol
                                                         && String.Equals(s.Name, searchName, StringComparison.OrdinalIgnoreCase)))
        {
            List<INamedTypeSymbol> fallbackTypes =
                await SymbolSearch.FindSourceTypesByNameAsync(projects, searchName, ct);
            allMatches.AddRange(fallbackTypes);
        }

        // When searching by kind alone (no special name), pivot: find the type by name,
        // then extract matching members. These members have internal Roslyn names
        // (e.g. "op_Addition", "Finalize") that users wouldn't know to search for.
        bool isMemberExtractionSearch = !isSpecialMemberName && kind is SymbolicKind.Constructor or
            SymbolicKind.Operator or SymbolicKind.Destructor or SymbolicKind.Indexer;
        if (isMemberExtractionSearch)
        {
            allMatches = allMatches
                .OfType<INamedTypeSymbol>()
                .SelectMany(t => t.GetMembers()
                    .Where(m => m.IsUserVisibleMember() && m.MatchesKindFilter(kind)))
                .ToList();
            matchMode = SymbolMatchMode.Contains;
        }

        bool skipKindFilter = isSpecialMemberName || isMemberExtractionSearch;
        bool isGlob = PathExtensions.IsGlobPattern(searchName);
        string sortName = isGlob ? searchName.Trim('*', '?') : searchName;
        IEnumerable<ISymbol> filtered = DeduplicateAndSort(allMatches, sortName)
            .WhereNotGenerated(includeGenerated);

        if (excludePattern is not null)
        {
            Regex exclude = PathExtensions.CompileGlobRegex(excludePattern);
            filtered = filtered.Where(s => !exclude.IsMatch(s.Name));
        }

        if (containingType is not null)
        {
            filtered = filtered
                .Where(s => String.Equals(s.ContainingType?.Name, containingType, StringComparison.OrdinalIgnoreCase));
        }

        // Skip name-based matchMode filtering when FQN resolved — the symbol's Name
        // won't match the full FQN string (e.g. "Circle" vs "TestFixture.Shapes.Circle")
        if (!fqnResolved)
        {
            if (isGlob)
            {
                // Glob wildcards in the name override matchMode — "*Director" means
                // "ends with Director" regardless of matchMode setting
                Regex globRegex = PathExtensions.CompileGlobRegex(searchName);
                filtered = filtered.Where(s => globRegex.IsMatch(s.Name));
            }
            else
            {
                // Exact is case-sensitive (Ordinal) by design: an exact-name lookup means that
                // precise identifier. StartsWith/EndsWith are case-insensitive — they're fuzzy
                // substring affordances where requiring matching case would mostly surprise. The
                // asymmetry is intentional; don't "align" Exact to OrdinalIgnoreCase.
                filtered = matchMode switch
                {
                    SymbolMatchMode.Exact => filtered.Where(s => String.Equals(s.Name, searchName, StringComparison.Ordinal)),
                    SymbolMatchMode.StartsWith => filtered.Where(s => s.Name.StartsWith(searchName, StringComparison.OrdinalIgnoreCase)),
                    SymbolMatchMode.EndsWith => filtered.Where(s => s.Name.EndsWith(searchName, StringComparison.OrdinalIgnoreCase)),
                    _ => filtered
                };
            }
        }

        // Apply generic arity filter when open generic syntax was used
        if (requestedArity.HasValue)
        {
            filtered = filtered.Where(s => GenericArityParser.GetGenericArity(s) == requestedArity.Value);
        }

        if (filePaths is { Length: > 0 })
        {
            string[] expandedPaths = await workspaceManager.ExpandGlobPatternsAsync(filePaths, 50, ct: ct);
            HashSet<string> resolvedPaths = expandedPaths
                .Select(fp => PathExtensions.ResolveFilePath(fp, solutionDir))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(s => s.DeclaringSyntaxReferences
                .Any(r => resolvedPaths.Contains(r.SyntaxTree.FilePath)));
        }

        // Apply kind filter LAST so we can detect when it alone is responsible for zero results.
        List<ISymbol> preKindMatches = filtered.ToList();
        allMatches = skipKindFilter
            ? preKindMatches
            : preKindMatches.Where(s => s.MatchesKindFilter(kind)).ToList();

        SymbolicKind[]? filteredOutKinds = skipKindFilter
            ? null
            : KindFilterBlame.GetDroppedKinds(preKindMatches, allMatches.Count, kind);

        (allMatches, _, int includedTestCount) = allMatches.PartitionByTestProject(
            excludeTests, s => s.IsInTestProject(solution));

        IReadOnlyList<ProjectDistributionEntry>? distribution;
        int totalCount;
        (allMatches, totalCount, distribution) = allMatches.TruncateWithDistribution(maxResults, solution);

        List<string>? suggestions = null;
        var containingTypeIsNamespace = false;
        if (totalCount == 0 && !isSpecialMemberName)
        {
            suggestions = await FuzzySuggestionHelper.GetSolutionSuggestionsAsync(searchName, projects, kind, containingType, ct);

            if (containingType is not null)
            {
                containingTypeIsNamespace = await NamespaceDetection.IsNamespaceInSolutionAsync(solution, containingType, ct);
            }
        }

        return new FindSymbolResult(searchName, allMatches, solutionDir, depth, totalCount, kind, excludePattern,
            containingType, project, matchMode, includeBody, distribution,
            suggestions, memberKinds,
            containingTypeIsNamespace, maxMembers, filePaths,
            excludedTestProjectCount, includedTestCount, requestedArity, filteredOutKinds);
    }

    /// <summary>
    ///     Gets all top-level symbols defined in a file.
    /// </summary>
    public async Task<SymbolsOverviewResult> GetSymbolsOverviewAsync(
        string filePath, int depth = 1, SymbolicKind[]? memberKinds = null, int? maxMembers = null, int maxTypes = 50, CancellationToken ct = default)
    {
        string solutionDir = await workspaceManager.GetRequiredSolutionDirectoryAsync(ct);

        // Surface resolution failures as per-file errors — callers aggregate them via GetMultiFileOverviewAsync.
        string resolvedPath;
        try
        {
            resolvedPath = await FilePathResolver.ResolveAgainstSolutionAsync(filePath, workspaceManager, ct);
        }
        catch (UserErrorException ex)
        {
            return new SymbolsOverviewResult(filePath, [], solutionDir, depth, ex.Message);
        }

        await workspaceManager.EnsureFilesFreshAsync([resolvedPath], ct);
        Solution solution = await workspaceManager.GetSolutionAsync(ct);

        Document? document = solution.GetDocumentByPath(resolvedPath);
        if (document is null)
        {
            return new SymbolsOverviewResult(filePath, [], solutionDir, depth, ErrorMessages.FileNotInSolution(filePath));
        }

        SyntaxTree? tree = await document.GetSyntaxTreeAsync(ct);
        SemanticModel? model = await document.GetSemanticModelAsync(ct);
        if (tree is null || model is null)
        {
            return new SymbolsOverviewResult(filePath, [], solutionDir, depth, ErrorMessages.CouldNotAnalyze(filePath));
        }

        SyntaxNode root = await tree.GetRootAsync(ct);

        IEnumerable<SyntaxNode> typeDeclarations = root.DescendantNodes()
            .Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);

        List<ISymbol> symbols = new();
        HashSet<ISymbol> seen = new(SymbolEqualityComparer.Default);
        var totalTypeCount = 0;
        foreach (SyntaxNode declaration in typeDeclarations)
        {
            ISymbol? symbol = model.GetDeclaredSymbol(declaration, ct);
            if (symbol is not null && seen.Add(symbol))
            {
                // When depth > 0, skip nested types — they appear as members of their parent via depth expansion
                if (depth > 0 && symbol is INamedTypeSymbol { ContainingType: not null })
                {
                    continue;
                }

                totalTypeCount++;
                if (symbols.Count < maxTypes)
                {
                    symbols.Add(symbol);
                }
            }
        }

        bool hasTopLevelStatements = symbols.Count == 0 && totalTypeCount == 0
                                                        && root.ChildNodes().OfType<GlobalStatementSyntax>().Any();

        string relPath = workspaceManager.GetRelativePath(resolvedPath);
        return new SymbolsOverviewResult(relPath, symbols, solutionDir, depth,
            HasTopLevelStatements: hasTopLevelStatements, AbsolutePath: resolvedPath, MemberKinds: memberKinds, MaxMembers: maxMembers,
            TotalTypeCount: totalTypeCount);
    }

    /// <summary>
    ///     Gets symbol overviews for multiple files with global type budget enforcement.
    ///     Handles project-vs-filePaths resolution, glob expansion, and cross-file truncation.
    /// </summary>
    public async Task<MultiFileOverviewResult> GetMultiFileOverviewAsync(
        string[]? filePaths, string? project, int depth,
        SymbolicKind[]? memberKinds, int? maxMembers, int maxTypes, int maxFiles,
        CancellationToken ct)
    {
        string[] paths;
        if (project is not null)
        {
            paths = await GetProjectFilePathsAsync(project, ct);
            if (paths.Length == 0)
            {
                return new MultiFileOverviewResult([], 0, 0);
            }

            if (filePaths is { Length: > 0 })
            {
                paths = WorkspaceManager.FilterByGlobPatterns(paths, filePaths);
            }
        }
        else if (filePaths is { Length: > 0 })
        {
            paths = await workspaceManager.ExpandGlobPatternsAsync(filePaths, maxFiles, ct: ct);
        }
        else
        {
            throw new UserErrorException("Either filePaths or project must be specified.");
        }

        string[] distinctPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        SymbolsOverviewResult[] results = await Task.WhenAll(
            distinctPaths.Select(path => GetSymbolsOverviewAsync(path, depth, memberKinds, maxMembers, maxTypes, ct)));

        // Enforce maxTypes globally across all files (per-file caps are applied in the service,
        // but the global budget must be enforced here to avoid returning maxTypes × fileCount types)
        var globalTypeCount = 0;
        var globalTotalTypes = 0;
        var trimmed = new bool[results.Length];
        for (var i = 0; i < results.Length; i++)
        {
            SymbolsOverviewResult result = results[i];
            globalTotalTypes += result.TotalTypeCount;
            int remaining = maxTypes - globalTypeCount;
            if (remaining <= 0 && result.Symbols.Count > 0)
            {
                result.Symbols.Clear();
                trimmed[i] = true;
            }
            else if (result.Symbols.Count > remaining)
            {
                result.Symbols.RemoveRange(remaining, result.Symbols.Count - remaining);
            }

            globalTypeCount += result.Symbols.Count;

            // Suppress per-file truncation hints — global hint is appended by the tool layer
            results[i] = result with { TotalTypeCount = result.Symbols.Count };
        }

        // Remove files that were emptied by the global budget (keep files that naturally have no types)
        SymbolsOverviewResult[] visibleResults = results
            .Where((_, i) => !trimmed[i])
            .ToArray();

        return new MultiFileOverviewResult(visibleResults, globalTotalTypes, distinctPaths.Length);
    }

    /// <summary>
    ///     Returns relative file paths for all documents in projects whose names contain
    ///     <paramref name="projectName" /> as a case-insensitive substring.
    /// </summary>
    /// <remarks>
    ///     Throws <see cref="UserErrorException" /> when no project name matches.
    /// </remarks>
    private async Task<string[]> GetProjectFilePathsAsync(string projectName, CancellationToken ct = default)
    {
        string solutionDir = await workspaceManager.GetRequiredSolutionDirectoryAsync(ct);
        Solution solution = await workspaceManager.GetSolutionAsync(ct);

        return solution.FilterByProjectName(projectName)
            .EnumerateSourceDocumentPaths(solutionDir)
            .Select(e => e.NormalizedRelativePath)
            .ToArray();
    }

    /// <summary>
    ///     Gets detailed information about the symbol at a specific position.
    /// </summary>
    public async Task<SymbolAtPositionResult> GoToDefinitionAsync(
        string filePath, int line, int? column, bool includeBody = false, int? maxMembers = 30, CancellationToken ct = default)
    {
        string resolvedPath = await FilePathResolver.ResolveAgainstSolutionAsync(filePath, workspaceManager, ct);

        (Solution solution, string solutionDir, ISymbol symbol) =
            await symbolResolver.ResolveSymbolAsync(filePath, line, column, null, null, ct: ct);

        // When the cursor is on the 'override' keyword, navigate to the overridden base member
        // instead of the override's own declaration. Skipped for line-only resolution because we
        // can't tell which keyword the cursor is on without a column.
        if (column.HasValue)
        {
            ISymbol? overriddenBase = await TryResolveOverrideKeywordAsync(solution, resolvedPath, line, column.Value, symbol, ct);
            if (overriddenBase is not null)
            {
                symbol = overriddenBase;
            }
        }

        // For extension-method call sites, Roslyn returns the reduced form (receiver elided,
        // type params partially substituted). Unwrap to show the original declaration.
        if (symbol is IMethodSymbol { ReducedFrom: not null } reducedExt)
        {
            symbol = reducedExt.ReducedFrom;
        }

        bool isAtDeclaration = IsPositionAtDeclaration(symbol, resolvedPath, line);

        bool isMetadataOnly = symbol.IsMetadataSymbol();
        string? projectOrAssemblyName;
        if (isMetadataOnly)
        {
            projectOrAssemblyName = symbol.ContainingAssembly?.Name;
        }
        else
        {
            SyntaxTree tree = symbol.DeclaringSyntaxReferences[0].SyntaxTree;
            Document? doc = solution.GetDocument(tree);
            projectOrAssemblyName = doc is not null ? ProjectExtensions.StripTfmSuffix(doc.Project.Name) : null;
        }

        return new SymbolAtPositionResult(symbol, solutionDir, 1, includeBody, isAtDeclaration, projectOrAssemblyName, maxMembers);
    }

    /// <summary>
    ///     Finds all overloads of a method at a given position, or by name and containing type.
    /// </summary>
    public async Task<FindOverloadsResult> FindOverloadsAsync(
        string? filePath, int? line, int? column,
        string? symbolName, string? containingType,
        SymbolicKind? kind = null, string? project = null,
        bool excludeTests = false, bool includeGenerated = false, CancellationToken ct = default)
    {
        bool hasPosition = filePath is not null && (line.HasValue || column.HasValue);
        bool hasName = symbolName is not null;

        if (!hasPosition && !hasName)
        {
            throw new UserErrorException("Provide either locations=['path:line:col'] OR symbolName+containingType.");
        }

        string solutionDir = await workspaceManager.GetRequiredSolutionDirectoryAsync(ct);
        bool isIndexerSearch = String.Equals(symbolName, "this[]", StringComparison.Ordinal);

        if (hasPosition)
        {
            // symbolName triggers name validation after positional resolution
            (_, _, ISymbol symbol) =
                await symbolResolver.ResolveSymbolAsync(filePath, line, column, symbolName, null,
                    excludeTests, project: project, ct: ct);

            if (symbol is IPropertySymbol { IsIndexer: true } posIndexer)
            {
                return BuildIndexerResult(posIndexer, solutionDir, includeGenerated);
            }

            if (symbol is not IMethodSymbol posMethod)
            {
                throw new UserErrorException(
                    $"'{symbol.Name}' is a {symbol.Kind.ToString().ToLowerInvariant()}, not a method. " +
                    "find_overloads only works with methods, constructors, and indexers.");
            }

            return BuildMethodResult(posMethod, solutionDir, includeGenerated);
        }

        // Name-based path
        Solution solution = await workspaceManager.GetSolutionAsync(ct);

        IReadOnlyList<Project> scopedProjects = solution.FilterByProjectName(project)
            .Where(p => !excludeTests || !p.IsTestProject())
            .ToList();

        if (isIndexerSearch)
        {
            // Indexer-by-name has its own dedicated resolver; bypass the shared dispatch.
            (string indexerSearchName, _) = GenericArityParser.ExtractArity(symbolName!);
            FqnParser.ThrowIfInvalid(indexerSearchName, containingType);
            return await FindIndexerOverloadsByNameAsync(scopedProjects, containingType, solutionDir, includeGenerated, ct);
        }

        string? inferResolvedPath = filePath is not null
            ? await FilePathResolver.ResolveAgainstSolutionAsync(filePath, workspaceManager, ct)
            : null;

        NameMatchDispatchResult dispatch = await NameMatchPipeline.DispatchAsync(
            solution, scopedProjects, symbolName!, containingType,
            filePath,
            inferResolvedPath,
            true, ct);

        string overloadSearchName = dispatch.SearchName;
        int? overloadArity = dispatch.RequestedArity;
        containingType = dispatch.ResolvedContainingType;

        IMethodSymbol? firstMatch;
        SymbolicKind[]? overloadDroppedKinds = null;

        if (dispatch.IsFqnMatched)
        {
            firstMatch = dispatch.Candidates
                .WhereNotGenerated(includeGenerated)
                .Where(s => s.MatchesKindFilter(kind))
                .Where(s => !overloadArity.HasValue || (s is IMethodSymbol ms && ms.TypeParameters.Length == overloadArity.Value))
                .OfType<IMethodSymbol>()
                .FirstOrDefault();

            // If FQN found a method, use it; otherwise fall through to miss handling below
            if (firstMatch is not null)
            {
                return BuildMethodResult(firstMatch, solutionDir, includeGenerated);
            }
        }
        else if (dispatch.IsSpecialMember)
        {
            List<IMethodSymbol> filteredSpecial = SymbolDeduplication.DeduplicateByLocation(dispatch.Candidates)
                .WhereNotGenerated(includeGenerated)
                .Where(s => String.Equals(s.ContainingType?.Name, containingType, StringComparison.OrdinalIgnoreCase))
                .Where(s => s.MatchesKindFilter(kind))
                .OfType<IMethodSymbol>()
                .ToList();

            SymbolResolver.ThrowIfSpansMultipleContainingTypes(filteredSpecial, overloadSearchName, containingType, solutionDir);
            firstMatch = filteredSpecial.FirstOrDefault();
        }
        else
        {
            // Name-based: overloads are inherently ambiguous within a single containing type,
            // so we don't use SymbolResolver (which throws on ambiguity). Instead, find matches
            // and use ThrowIfSpansMultipleContainingTypes to detect cross-type ambiguity.
            //
            // Apply name + containingType + arity filters first; kind filter goes last so
            // KindFilterBlame can detect when it alone is responsible for an empty result.
            List<ISymbol> nameOnlyMatches = SymbolDeduplication.DeduplicateByLocation(dispatch.Candidates)
                .WhereNotGenerated(includeGenerated)
                .Where(s => String.Equals(s.Name, overloadSearchName, StringComparison.OrdinalIgnoreCase))
                .Where(s => containingType is null ||
                            String.Equals(s.ContainingType?.Name, containingType, StringComparison.OrdinalIgnoreCase))
                .Where(s => !overloadArity.HasValue || (s is IMethodSymbol ms && ms.TypeParameters.Length == overloadArity.Value))
                .ToList();

            // Apply kind filter and OfType<IMethodSymbol> separately so KindFilterBlame
            // doesn't mis-attribute drops to the kind filter when the user passed a
            // non-method kind (e.g. Class) that survives the kind check but loses to OfType.
            List<ISymbol> kindMatched = nameOnlyMatches
                .Where(s => s.MatchesKindFilter(kind))
                .ToList();

            overloadDroppedKinds = KindFilterBlame.GetDroppedKinds(nameOnlyMatches, kindMatched.Count, kind);

            List<IMethodSymbol> filteredName = kindMatched.OfType<IMethodSymbol>().ToList();

            SymbolResolver.ThrowIfSpansMultipleContainingTypes(filteredName, overloadSearchName, containingType, solutionDir);
            firstMatch = filteredName.FirstOrDefault();
        }

        if (firstMatch is null)
        {
            string kindBlame = overloadDroppedKinds is { Length: > 0 }
                ? "\n" + KindFilterBlame.FormatHint(overloadSearchName, overloadDroppedKinds)
                : "";

            await NamespaceDetection.ThrowIfNamespaceAsync(solution, containingType, ct);

            if (containingType is not null)
            {
                // Resolve the containing type once and reuse it for both the base-type scan
                // and fuzzy member suggestions; avoids double-searching on the miss path.
                List<INamedTypeSymbol> typeMatches = await SymbolSearch.SearchSourceTypesAsync(
                    scopedProjects, containingType, ct);
                INamedTypeSymbol? typeSymbol = typeMatches.FirstOrDefault();

                string? baseTypeName = typeSymbol?
                    .BaseTypes()
                    .FirstOrDefault(bt => bt.GetMembers(overloadSearchName).OfType<IMethodSymbol>().Any())?
                    .Name;
                if (baseTypeName is not null)
                {
                    throw new UserErrorException(
                        $"No method '{overloadSearchName}' in type '{containingType}', but it exists in base type '{baseTypeName}'. " +
                        $"Overrides are not overloads — try containingType='{baseTypeName}'.{kindBlame}");
                }

                string didYouMean = typeSymbol is not null
                    ? FuzzySuggestionHelper.MemberSuggestionSuffix(overloadSearchName, typeSymbol)
                    : "";

                throw new UserErrorException(
                    $"No method found with name '{overloadSearchName}' in type '{containingType}'.{didYouMean} " +
                    $"Check spelling — use find_symbol to search for methods by name.{kindBlame}");
            }

            string solutionDidYouMean = await FuzzySuggestionHelper.SolutionSuggestionSuffixAsync(
                overloadSearchName, scopedProjects, SymbolicKind.Method, null, ct);
            throw new UserErrorException(
                $"No method found with name '{overloadSearchName}'.{solutionDidYouMean} Check spelling — use find_symbol to search for methods by name.{kindBlame}");
        }

        return BuildMethodResult(firstMatch, solutionDir, includeGenerated);
    }

    private static FindOverloadsResult BuildMethodResult(IMethodSymbol method, string solutionDir, bool includeGenerated)
    {
        INamedTypeSymbol? type = method.ContainingType;
        if (type is null)
        {
            return new FindOverloadsResult(method.Name, "?", [method], solutionDir);
        }

        List<ISymbol> overloads = type.GetMembers(method.Name)
            .OfType<IMethodSymbol>()
            .WhereNotGenerated(includeGenerated)
            .Cast<ISymbol>()
            .ToList();

        return new FindOverloadsResult(method.Name, type.Name, overloads, solutionDir);
    }

    private static FindOverloadsResult BuildIndexerResult(IPropertySymbol indexer, string solutionDir, bool includeGenerated)
    {
        INamedTypeSymbol? type = indexer.ContainingType;
        if (type is null)
        {
            return new FindOverloadsResult("this[]", "?", [indexer], solutionDir);
        }

        List<ISymbol> indexers = type.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p.IsIndexer)
            .WhereNotGenerated(includeGenerated)
            .Cast<ISymbol>()
            .ToList();

        return new FindOverloadsResult("this[]", type.Name, indexers, solutionDir);
    }

    private static async Task<FindOverloadsResult> FindIndexerOverloadsByNameAsync(
        IReadOnlyList<Project> projects, string? containingType, string solutionDir, bool includeGenerated, CancellationToken ct)
    {
        if (containingType is null)
        {
            throw new UserErrorException(
                "containingType is required when searching for indexers by name (this[]).");
        }

        List<INamedTypeSymbol> typeMatches = await SymbolSearch.SearchSourceTypesAsync(
            projects, containingType, ct);

        List<INamedTypeSymbol> sourceTypes = typeMatches
            .WhereNotGenerated(includeGenerated)
            .ToList();

        if (sourceTypes.Count == 0)
        {
            string didYouMean = await FuzzySuggestionHelper.SolutionSuggestionSuffixAsync(
                containingType, projects, null, null, ct);
            throw new UserErrorException(
                $"No type found with name '{containingType}'.{didYouMean} Check spelling — use find_symbol to search for types by name.");
        }

        List<ISymbol> allIndexers = sourceTypes
            .SelectMany(t => t.GetMembers())
            .OfType<IPropertySymbol>()
            .Where(p => p.IsIndexer)
            .WhereNotGenerated(includeGenerated)
            .Cast<ISymbol>()
            .ToList();

        if (allIndexers.Count == 0)
        {
            throw new UserErrorException(
                $"No indexers found in type '{containingType}'.");
        }

        SymbolResolver.ThrowIfSpansMultipleContainingTypes(allIndexers, "this[]", containingType, solutionDir);

        // All indexers are in one containing type (by source location) — safe to use first
        return new FindOverloadsResult("this[]", allIndexers[0].ContainingType!.Name, allIndexers, solutionDir);
    }


    private static async Task<ISymbol?> TryResolveOverrideKeywordAsync(
        Solution solution, string resolvedPath, int line, int column, ISymbol symbol, CancellationToken ct)
    {
        Document? document = solution.GetDocumentByPath(resolvedPath);
        if (document is null)
        {
            return null;
        }

        SourceText text = await document.GetTextAsync(ct);
        int position = text.GetPosition(line, column, true);
        SyntaxTree? tree = await document.GetSyntaxTreeAsync(ct);
        if (tree is null)
        {
            return null;
        }

        SyntaxToken token = (await tree.GetRootAsync(ct)).FindToken(position);
        if (!token.IsKind(SyntaxKind.OverrideKeyword))
        {
            return null;
        }

        return SymbolClassificationExtensions.GetOverriddenMember(symbol);
    }

    private static bool IsPositionAtDeclaration(ISymbol symbol, string resolvedPath, int line)
    {
        foreach (SyntaxReference syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            if (!String.Equals(syntaxRef.SyntaxTree.FilePath, resolvedPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FileLinePositionSpan lineSpan = syntaxRef.SyntaxTree.GetLineSpan(syntaxRef.Span);
            int declStartLine = lineSpan.StartLinePosition.Line + 1;
            int declEndLine = lineSpan.EndLinePosition.Line + 1;

            if (line >= declStartLine && line <= declEndLine)
            {
                return true;
            }
        }

        return false;
    }

    private static List<ISymbol> DeduplicateAndSort(List<ISymbol> symbols, string searchName)
    {
        return SymbolDeduplication.DeduplicateByLocation(symbols)
            .OrderBy(s => GetMatchTier(s.Name, searchName))
            .ThenBy(s => GetKindTier(s))
            .ThenBy(s => s.Name)
            .ToList();
    }

    private static int GetKindTier(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => 0,
        INamedTypeSymbol => 1,
        _ => 2
    };

    private static int GetMatchTier(string symbolName, string searchName)
    {
        if (String.Equals(symbolName, searchName, StringComparison.OrdinalIgnoreCase))
        {
            return 0; // Exact match
        }

        if (symbolName.StartsWith(searchName, StringComparison.OrdinalIgnoreCase))
        {
            return 1; // Prefix match
        }

        return 2; // Substring match
    }
}
