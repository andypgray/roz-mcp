using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Constants;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Symbols;

/// <summary>
///     Shared symbol resolution: resolves a symbol by either position (parsed from a
///     <c>location</c> string) or name (symbolName + containingType).
/// </summary>
internal sealed class SymbolResolver(WorkspaceManager workspaceManager)
{
    /// <summary>
    ///     Resolves a symbol by position OR name.
    /// </summary>
    /// <remarks>
    ///     Valid combinations (parsed from the tool's <c>location</c> string before reaching here):
    ///     <list type="bullet">
    ///         <item><c>filePath + line</c> (line-level: resolves the member declared on that line)</item>
    ///         <item><c>filePath + line + column</c> — exact positional lookup</item>
    ///         <item><c>symbolName</c> (+ optional containingType) — name-based lookup</item>
    ///         <item><c>symbolName + filePath</c> (no line/column) — name-based lookup scoped to file</item>
    ///         <item>
    ///             <c>symbolName + filePath + line</c> (+ optional column) — positional lookup with name
    ///             validation. Resolves by position, then verifies the result matches symbolName.
    ///         </item>
    ///     </list>
    ///     Optional <c>matchFilter</c> predicate is applied to name-based matches before ambiguity
    ///     checking — use to restrict resolution to specific symbol kinds (e.g. types only).
    ///     <c>matchFilterDescription</c> is used in error messages when the filter eliminates all matches.
    /// </remarks>
    public async Task<(Solution Solution, string SolutionDir, ISymbol Symbol)> ResolveSymbolAsync(
        string? filePath, int? line, int? column,
        string? symbolName, string? containingType,
        bool excludeTests = false,
        Func<ISymbol, bool>? matchFilter = null, string? matchFilterDescription = null,
        string? project = null,
        CancellationToken ct = default,
        SymbolicKind? kind = null)
    {
        bool hasFilePath = filePath is not null;
        bool hasName = symbolName is not null;
        bool hasLineOrColumn = line.HasValue || column.HasValue;

        // symbolName + filePath + line/column: resolve by position, validate name matches
        if (hasName && hasFilePath && hasLineOrColumn)
        {
            if (!line.HasValue)
            {
                throw new UserErrorException("location must include :line — e.g. 'Foo.cs:42' or 'Foo.cs:42:18'.");
            }

            (Solution sol, string dir, ISymbol sym) = column.HasValue
                ? await ResolveSymbolAtPositionAsync(filePath!, line.Value, column.Value, ct)
                : await ResolveSymbolOnLineAsync(filePath!, line.Value, ct);

            if (!NameMatchesSymbol(sym, symbolName!))
            {
                string resolvedKind = sym.GetKindString();
                throw new UserErrorException(
                    $"Position {LocationFormat.Format(filePath!, line)} resolved to '{sym.Name}' ({resolvedKind}) but expected '{symbolName}'. " +
                    "Verify the line/column, or drop symbolName to resolve by position alone.");
            }

            return (sol, dir, sym);
        }

        // Reject: nothing provided
        if (!hasFilePath && !hasName)
        {
            throw new UserErrorException(
                "Provide one of: locations=['path:line:col',...] (cursors) or symbolNames=['A','B',...] (names). Both batch — pass many items per call.");
        }

        // symbolName (+ optional filePath for file-scoped search)
        if (hasName)
        {
            string? filterFilePath = hasFilePath ? filePath : null;
            return await ResolveSymbolByNameAsync(symbolName!, containingType, excludeTests, filterFilePath, matchFilter, matchFilterDescription, project, kind, ct);
        }

        // filePath + line (+ optional column)
        if (!line.HasValue)
        {
            throw new UserErrorException("location must include :line — e.g. 'Foo.cs:42' or 'Foo.cs:42:18'.");
        }

        // When column is explicitly provided, use exact position resolution
        if (column.HasValue)
        {
            return await ResolveSymbolAtPositionAsync(filePath!, line.Value, column.Value, ct);
        }

        // Line-only: prefer the member DECLARED on this line over whatever symbol is at column 1
        return await ResolveSymbolOnLineAsync(filePath!, line.Value, ct);
    }

    /// <summary>
    ///     Resolves all overloads matching the given name criteria.
    /// </summary>
    /// <remarks>
    ///     When all matches are overloads of the same member in the same type, returns them all
    ///     instead of throwing an ambiguity error. Use for read-only tools (find_references,
    ///     find_implementations) that can merge results across overloads.
    /// </remarks>
    public async Task<(Solution Solution, string SolutionDir, IReadOnlyList<ISymbol> Symbols)> ResolveOverloadsAsync(
        string? filePath, int? line, int? column,
        string? symbolName, string? containingType,
        bool excludeTests = false,
        Func<ISymbol, bool>? matchFilter = null, string? matchFilterDescription = null,
        string? project = null,
        CancellationToken ct = default,
        SymbolicKind? kind = null)
    {
        // Positional resolution or missing args — delegate to single-symbol resolution (handles validation)
        if (IsPositionalResolution(symbolName, filePath, line, column))
        {
            (Solution sol, string dir, ISymbol sym) =
                await ResolveSymbolAsync(filePath, line, column, symbolName, containingType, excludeTests,
                    matchFilter, matchFilterDescription, project, ct, kind);
            return (sol, dir, [sym]);
        }

        // Name-based: collect matches and allow overload merging. The IsPositionalResolution
        // [NotNullWhen(false)] contract guarantees symbolName is non-null in this branch.
        (Solution solution, string solutionDir, List<ISymbol> matches) =
            await CollectNameMatchesAsync(symbolName, containingType, excludeTests, filePath, matchFilter, matchFilterDescription, project, kind, ct);

        if (matches.Count <= 1)
        {
            return (solution, solutionDir, matches);
        }

        // All matches in same containing type → overloads, return all
        if (AreAllOverloadsInSameType(matches))
        {
            return (solution, solutionDir, matches);
        }

        // Different containing types → genuinely ambiguous
        ThrowAmbiguityError(matches, symbolName, containingType, solutionDir);
        return default; // unreachable
    }

    /// <summary>
    ///     True when resolution is driven by a cursor position rather than a symbol name — either no
    ///     <paramref name="symbolName" />, or a name paired with an explicit position (the position
    ///     wins). In this mode the <c>project</c> filter plays no part in resolution, so callers that
    ///     accept <c>project</c> must treat it as a no-op rather than silently post-filtering results.
    /// </summary>
    /// <remarks>
    ///     <c>[NotNullWhen(false)]</c> on <paramref name="symbolName" /> propagates the real invariant
    ///     to callers: a <c>false</c> result means a name was supplied without a winning position, so
    ///     the name-based branch can use it without a null-forgiving operator.
    /// </remarks>
    internal static bool IsPositionalResolution(
        [NotNullWhen(false)] string? symbolName, string? filePath, int? line, int? column) =>
        symbolName is null || (filePath is not null && (line.HasValue || column.HasValue));

    private static bool AreAllOverloadsInSameType(IReadOnlyList<ISymbol> matches)
    {
        if (matches[0].ContainingType is not { } first)
        {
            return false;
        }

        // Extract first type's location once to avoid repeated lookups in the loop
        (string, int)? firstKey = SymbolDeduplication.GetLocationKey(first);

        return matches.All(m =>
            m.ContainingType is not null &&
            (SymbolEqualityComparer.Default.Equals(m.ContainingType, first) ||
             (firstKey is not null && firstKey == SymbolDeduplication.GetLocationKey(m.ContainingType))));
    }

    /// <summary>
    ///     Throws a user-facing ambiguity error if <paramref name="matches" /> span multiple
    ///     distinct containing types (by source location). Same containing type → caller can
    ///     safely pick any match and look up siblings.
    /// </summary>
    internal static void ThrowIfSpansMultipleContainingTypes(
        IEnumerable<ISymbol> matches, string symbolName, string? containingType, string solutionDir)
    {
        List<ISymbol> list = matches.ToList();
        if (list.Count <= 1 || AreAllOverloadsInSameType(list))
        {
            return;
        }

        ThrowAmbiguityError(list, symbolName, containingType, solutionDir);
    }

    private async Task<(Solution Solution, string SolutionDir, ISymbol Symbol)> ResolveSymbolByNameAsync(
        string symbolName, string? containingType, bool excludeTests, string? filterFilePath,
        Func<ISymbol, bool>? matchFilter, string? matchFilterDescription, string? project, SymbolicKind? kind,
        CancellationToken ct)
    {
        (Solution solution, string solutionDir, List<ISymbol> matches) =
            await CollectNameMatchesAsync(symbolName, containingType, excludeTests, filterFilePath, matchFilter, matchFilterDescription, project, kind, ct);

        if (matches.Count > 1)
        {
            ThrowAmbiguityError(matches, symbolName, containingType, solutionDir);
        }

        return (solution, solutionDir, matches[0]);
    }

    /// <summary>
    ///     Collects all name-matched symbols, applying filters and throwing on zero matches.
    /// </summary>
    /// <remarks>
    ///     Returns the match list without checking for ambiguity — callers decide how to handle
    ///     multiple matches.
    /// </remarks>
    private async Task<(Solution Solution, string SolutionDir, List<ISymbol> Matches)> CollectNameMatchesAsync(
        string symbolName, string? containingType, bool excludeTests, string? filterFilePath,
        Func<ISymbol, bool>? matchFilter, string? matchFilterDescription, string? project, SymbolicKind? kind,
        CancellationToken ct)
    {
        string solutionDir = await workspaceManager.GetRequiredSolutionDirectoryAsync(ct);
        Solution solution = await workspaceManager.GetSolutionAsync(ct);
        string? resolvedFilterPath = filterFilePath is not null
            ? await FilePathResolver.ResolveAgainstSolutionAsync(filterFilePath, workspaceManager, ct)
            : null;

        List<Project> projects = solution.FilterByProjectName(project)
            .Where(p => !excludeTests || !p.IsTestProject())
            .ToList();

        NameMatchDispatchResult dispatch = await NameMatchPipeline.DispatchAsync(
            solution, projects, symbolName, containingType,
            filterFilePath,
            resolvedFilterPath,
            true, ct);

        string searchName = dispatch.SearchName;
        int? requestedArity = dispatch.RequestedArity;
        containingType = dispatch.ResolvedContainingType;

        if (dispatch.IsFqnMatched)
        {
            // FqnResolver already deduplicates by source location
            List<ISymbol> fqnMatches = dispatch.Candidates.WhereNotGenerated().ToList();

            // FqnResolver additively returns both arity-0 and arity-N candidates (Concat), so an
            // open-generic FQN like "Ns.Processor<>" must apply the arity filter here — mirrors the
            // non-FQN path below and NavigationService's own FQN path. Without it, the dual matches
            // surface as a spurious ambiguity error. (CR-4)
            if (requestedArity.HasValue)
            {
                fqnMatches = GenericArityParser.FilterByArity(fqnMatches, requestedArity.Value);
            }

            return await ApplyPostFiltersAsync(fqnMatches, solution, solutionDir, searchName, containingType,
                resolvedFilterPath, filterFilePath, matchFilter, matchFilterDescription, kind, projects,
                SearchKind.Fqn, ct);
        }

        List<ISymbol> matches;
        if (dispatch.IsSpecialMember)
        {
            matches = SymbolDeduplication.DeduplicateByLocation(
                dispatch.Candidates
                    .WhereNotGenerated()
                    .Where(m => String.Equals(m.ContainingType?.Name, containingType, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            matches = SymbolDeduplication.DeduplicateByLocation(
                dispatch.Candidates
                    .WhereNotGenerated()
                    .Where(s => NameMatchesSymbol(s, searchName))
                    .Where(s => containingType is null ||
                                String.Equals(s.ContainingType?.Name, containingType, StringComparison.OrdinalIgnoreCase)));
        }

        // Source fallback: SymbolFinder.FindSourceDeclarationsWithPatternAsync misses types whose
        // compilation context has errors (e.g. CS0315). Walk GlobalNamespace as a supplement.
        if (matches.Count == 0 && containingType is null)
        {
            List<INamedTypeSymbol> sourceTypes =
                await SymbolSearch.FindSourceTypesByNameAsync(projects, searchName, ct);
            matches = SymbolDeduplication.DeduplicateByLocation(
                sourceTypes.WhereNotGenerated().Cast<ISymbol>());
        }

        // Metadata fallback: when no source matches found and containingType is specified,
        // search referenced assemblies (BCL, NuGet) for the containing type and extract members.
        // This enables e.g. find_implementations with symbolName="Dispose", containingType="IDisposable".
        // containingType is a SIMPLE enclosing-type name by contract; a dotted value is a namespace-like
        // mistake that must fall through to the "not by namespace" hint (ThrowNoMatchesAsync), so it is
        // excluded here rather than exact-resolved by FindMetadataTypesAsync's namespace-qualified branch.
        if (matches.Count == 0 && containingType is not null && !containingType.Contains('.'))
        {
            List<INamedTypeSymbol> metadataTypes = await SymbolSearch.FindMetadataTypesAsync(projects, containingType, ct);
            if (metadataTypes.Count > 0)
            {
                List<ISymbol> metadataMembers = metadataTypes
                    .SelectMany(t => t.GetMembers()
                        .Where(m => NameMatchesSymbol(m, searchName)))
                    .ToList();

                matches = SymbolDeduplication.DeduplicateByLocation(metadataMembers);
            }
        }

        // Metadata type fallback: when no source matches found and no containingType specified,
        // search referenced assemblies for the type itself. Enables find_implementations
        // (type dispatch) with external types like IDisposable.
        if (matches.Count == 0 && containingType is null)
        {
            List<INamedTypeSymbol> metadataTypes = await SymbolSearch.FindMetadataTypesAsync(projects, searchName, ct);
            if (metadataTypes.Count > 0)
            {
                matches = SymbolDeduplication.DeduplicateByLocation(metadataTypes.Cast<ISymbol>());
            }
        }

        // Apply generic arity filter before file/kind filters
        if (requestedArity.HasValue)
        {
            matches = GenericArityParser.FilterByArity(matches, requestedArity.Value);
        }

        // When bare name (no angle brackets) matches types differing only by generic arity
        // and a non-generic variant exists, auto-select it. "Grain" → non-generic Grain.
        // Does not apply when no arity-0 match exists (e.g. only Widget<T> and Widget<T,U>),
        // or when matches differ by kind (e.g. interface Metric + class Metric<T>).
        if (!requestedArity.HasValue && matches.Count > 1)
        {
            List<INamedTypeSymbol> typeMatches = matches.OfType<INamedTypeSymbol>().ToList();
            if (typeMatches.Count == matches.Count &&
                typeMatches.Select(t => t.TypeKind).Distinct().Count() == 1 &&
                typeMatches.Select(t => t.TypeParameters.Length).Distinct().Count() > 1)
            {
                List<ISymbol> nonGeneric = matches.Where(s => GenericArityParser.GetGenericArity(s) == 0).ToList();
                if (nonGeneric.Count > 0)
                {
                    matches = nonGeneric;
                }
            }
        }

        return await ApplyPostFiltersAsync(matches, solution, solutionDir, searchName, containingType,
            resolvedFilterPath, filterFilePath, matchFilter, matchFilterDescription, kind, projects,
            SearchKind.Name, ct);
    }

    /// <summary>
    ///     Applies file-path filtering and <paramref name="matchFilter" /> to a pre-resolved match list,
    ///     throwing a <see cref="SearchKind" />-appropriate "no symbol found" error when the filters
    ///     eliminate every candidate. Shared by the FQN and simple-name resolution paths.
    /// </summary>
    private async Task<(Solution Solution, string SolutionDir, List<ISymbol> Matches)> ApplyPostFiltersAsync(
        List<ISymbol> matches, Solution solution, string solutionDir,
        string searchName, string? containingType,
        string? resolvedFilterPath, string? filterFilePath,
        Func<ISymbol, bool>? matchFilter, string? matchFilterDescription, SymbolicKind? kind,
        IReadOnlyList<Project> projects, SearchKind searchKind, CancellationToken ct)
    {
        if (resolvedFilterPath is not null)
        {
            matches = matches
                .Where(s => s.Locations.Any(l =>
                    l.IsInSource &&
                    String.Equals(Path.GetFullPath(l.SourceTree!.FilePath), resolvedFilterPath,
                        StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (matches.Count == 0)
            {
                List<string> fileSuggestions = await GetFileSuggestionsAsync(solution, resolvedFilterPath, searchName, ct);
                string didYouMean = FuzzySuggestionHelper.FormatSuggestionSuffix(fileSuggestions);
                throw new UserErrorException(
                    $"No symbol named '{searchName}' found in '{filterFilePath}'.{didYouMean} " +
                    "Check spelling — use find_symbol to search by substring, " +
                    "or omit location to search the entire solution.");
            }
        }

        if (matchFilter is not null && matches.Count > 0)
        {
            List<ISymbol> filtered = matches.Where(matchFilter).ToList();
            if (filtered.Count == 0)
            {
                string desc = matchFilterDescription ?? "symbol";
                string lead =
                    $"No {desc} named '{searchName}' found. " +
                    "Drop symbolName and use locations=['path:line:col'] to target a specific declaration.";

                SymbolicKind[]? droppedKinds = KindFilterBlame.GetDroppedKinds(matches, 0, kind);
                string blame = droppedKinds is { Length: > 0 }
                    ? "\n" + KindFilterBlame.FormatHint(searchName, droppedKinds)
                    : $" Matched {matches.Count} non-{desc} symbol(s) (" +
                      String.Join(", ", matches.Select(s => s.GetKindString()).Distinct()) + ").";

                throw new UserErrorException(lead + blame);
            }

            matches = filtered;
        }

        if (matches.Count == 0)
        {
            await ThrowNoMatchesAsync(solution, projects, searchName, containingType, searchKind, ct);
        }

        return (solution, solutionDir, matches);
    }

    private static async Task ThrowNoMatchesAsync(
        Solution solution, IReadOnlyList<Project> projects,
        string searchName, string? containingType, SearchKind searchKind, CancellationToken ct)
    {
        if (searchKind == SearchKind.Fqn)
        {
            string didYouMean = await FuzzySuggestionHelper.SolutionSuggestionSuffixAsync(searchName, projects, null, null, ct);
            throw new UserErrorException(
                $"No symbol found with FQN '{searchName}'.{didYouMean} Check spelling — use find_symbol to search by substring.");
        }

        // Name path: FqnResolver already rejects namespaces up front, so this check is name-only.
        await NamespaceDetection.ThrowIfNamespaceAsync(solution, containingType, ct);

        string nameDidYouMean = containingType is not null
            ? await FuzzySuggestionHelper.ContainingTypeMemberSuggestionSuffixAsync(projects, searchName, containingType, ct)
            : await FuzzySuggestionHelper.SolutionSuggestionSuffixAsync(searchName, projects, null, null, ct);

        string suffix = containingType is not null ? $" in type '{containingType}'" : "";
        var hint = "";

        // When symbolName == containingType, the user likely wants the type itself, not a member
        if (containingType is not null &&
            String.Equals(searchName, containingType, StringComparison.OrdinalIgnoreCase))
        {
            bool typeExists = (await SymbolSearch.SearchSourceTypesAsync(projects, searchName, ct))
                .Any(t => !t.IsInGeneratedFile());

            if (typeExists)
            {
                hint = $" Did you mean to look up the type '{searchName}' itself? " +
                       "Omit containingType when the symbol IS the type.";
            }
        }
        else if (containingType is not null && containingType.Contains('.'))
        {
            hint = " containingType filters by enclosing type (for nested types/members), not by namespace. " +
                   "For top-level types, omit containingType and use locations=['path:line:col'] to disambiguate.";
        }

        throw new UserErrorException(
            $"No symbol found with name '{searchName}'{suffix}.{nameDidYouMean} Check spelling — use find_symbol to search by substring.{hint}");
    }

    internal static void ThrowAmbiguityError(
        IReadOnlyList<ISymbol> matches, string symbolName, string? containingType, string solutionDir)
    {
        const int maxCandidates = 10;

        List<ISymbol> sorted = matches
            .OrderBy(s => s.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan().Path,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan().StartLinePosition.Line ?? 0)
            .ToList();

        List<string> candidateLines = sorted.Take(maxCandidates).Select(s =>
        {
            string kind = s.GetKindString();
            Location? loc = s.Locations.FirstOrDefault(l => l.IsInSource);
            if (loc is null)
            {
                return $"  [{kind}] {s.ToDisplayString()} — (no source)";
            }

            string relPath = loc.GetRelativePath(solutionDir);
            int candidateLine = loc.GetLineSpan().StartLinePosition.Line + 1;
            return $"  [{kind}] {s.ToDisplayString()} — {LocationFormat.Format(relPath, candidateLine)}";
        }).ToList();

        if (matches.Count > maxCandidates)
        {
            candidateLines.Add($"  ... and {matches.Count - maxCandidates} more");
        }

        string disambiguationHint = containingType is not null
            ? $"Ambiguous: {matches.Count} overloads of '{symbolName}' in '{containingType}'. " +
              "Drop symbolName and use locations=['path:line:col'] to select a specific overload:"
            : $"Ambiguous: {matches.Count} symbols match '{symbolName}'. " +
              "Use containingType to narrow, or drop symbolName and use locations=['path:line:col']:";

        string arityHint = FormatArityHint(matches, symbolName);

        throw new UserErrorException(
            $"{disambiguationHint}\n{String.Join("\n", candidateLines)}{arityHint}");
    }

    /// <summary>
    ///     When ambiguous matches differ by generic arity, suggests open generic syntax to disambiguate.
    /// </summary>
    private static string FormatArityHint(IReadOnlyList<ISymbol> matches, string symbolName)
    {
        List<INamedTypeSymbol> typeMatches = matches.OfType<INamedTypeSymbol>().ToList();
        if (typeMatches.Count < 2)
        {
            return "";
        }

        List<int> distinctArities = typeMatches.Select(t => t.TypeParameters.Length).Distinct().OrderBy(a => a).ToList();
        if (distinctArities.Count < 2)
        {
            return "";
        }

        List<string> suggestions = distinctArities
            .Select(a => a == 0 ? $"'{symbolName}' (non-generic)" : $"'{symbolName}<{new string(',', a - 1)}>' (arity {a})")
            .ToList();

        return $"\nThese differ by generic arity. Use open generic syntax to disambiguate: {String.Join(", ", suggestions)}.";
    }

    /// <summary>
    ///     Line-level resolution: finds the member declared on the given line rather than
    ///     resolving whatever symbol happens to be at column 1. Falls back to column-1
    ///     snap-to-nearest if no declaration is found on the line.
    /// </summary>
    private async Task<(Solution Solution, string SolutionDir, ISymbol Symbol)> ResolveSymbolOnLineAsync(
        string filePath, int line, CancellationToken ct)
    {
        string solutionDir = await workspaceManager.GetRequiredSolutionDirectoryAsync(ct);
        string resolvedPath = await FilePathResolver.ResolveAgainstSolutionAsync(filePath, workspaceManager, ct);
        await workspaceManager.EnsureFilesFreshAsync([resolvedPath], ct);
        Solution solution = await workspaceManager.GetSolutionAsync(ct);

        Document? document = solution.GetDocumentByPath(resolvedPath);
        if (document is null)
        {
            throw new UserErrorException(ErrorMessages.FileNotInSolution(filePath));
        }

        SourceText text = await document.GetTextAsync(ct);
        SemanticModel? model = await document.GetSemanticModelAsync(ct);
        SyntaxTree? tree = await document.GetSyntaxTreeAsync(ct);

        if (model is not null && tree is not null)
        {
            ISymbol? declared = FindDeclaredSymbolOnLine(text, tree, model, line, ct);
            if (declared is not null)
            {
                return (solution, solutionDir, declared);
            }
        }

        // Fallback: column 1 + existing snap-to-nearest behavior
        SymbolResolution resolution = await solution.ResolveSymbolAtPositionAsync(resolvedPath, line, 1, ct: ct);
        if (resolution.Symbol is null)
        {
            throw new UserErrorException(resolution.FormatNoSymbolError(filePath, line, 1));
        }

        return (solution, solutionDir, resolution.Symbol);
    }

    /// <summary>
    ///     Finds the symbol declared on the given 1-based line number by scanning for member
    ///     declarations whose identifier token falls on that line.
    /// </summary>
    private static ISymbol? FindDeclaredSymbolOnLine(
        SourceText text, SyntaxTree tree, SemanticModel model, int line, CancellationToken ct)
    {
        int lineIndex = line - 1;
        if (lineIndex < 0 || lineIndex >= text.Lines.Count)
        {
            return null;
        }

        TextLine targetLine = text.Lines[lineIndex];
        TextSpan lineSpan = targetLine.Span;
        SyntaxNode root = tree.GetRoot(ct);

        SyntaxToken startToken = root.FindToken(lineSpan.Start);
        SyntaxNode? node = startToken.Parent;

        while (node is not null)
        {
            // Fields and event fields have their identifiers on child VariableDeclaratorSyntax nodes
            if (node is BaseFieldDeclarationSyntax fieldDecl)
            {
                VariableDeclaratorSyntax? declarator = fieldDecl.Declaration.Variables
                    .FirstOrDefault(v => lineSpan.Contains(v.Identifier.Span.Start));
                if (declarator is not null)
                {
                    ISymbol? fieldSymbol = model.GetDeclaredSymbol(declarator, ct);
                    if (fieldSymbol is not null)
                    {
                        return fieldSymbol;
                    }
                }
            }

            SyntaxToken? identifier = GetDeclarationIdentifier(node);
            if (identifier is not null && lineSpan.Contains(identifier.Value.Span.Start))
            {
                ISymbol? declared = model.GetDeclaredSymbol(node, ct);
                if (declared is not null)
                {
                    return declared;
                }
            }

            // Stop walking once we hit a type declaration — don't resolve to a parent type
            // when the line is inside a method body with no declaration
            if (node is BaseTypeDeclarationSyntax)
            {
                break;
            }

            node = node.Parent;
        }

        return null;
    }

    /// <summary>
    ///     Returns the identifier token for a declaration syntax node, or null for nodes
    ///     that don't represent declarations we want to resolve at the line level.
    /// </summary>
    private static SyntaxToken? GetDeclarationIdentifier(SyntaxNode node)
    {
        return node switch
        {
            MethodDeclarationSyntax m => m.Identifier,
            PropertyDeclarationSyntax p => p.Identifier,
            EventDeclarationSyntax e => e.Identifier,
            BaseTypeDeclarationSyntax t => t.Identifier,
            DelegateDeclarationSyntax d => d.Identifier,
            EnumMemberDeclarationSyntax em => em.Identifier,
            ConstructorDeclarationSyntax c => c.Identifier,
            DestructorDeclarationSyntax dt => dt.Identifier,
            IndexerDeclarationSyntax i => i.ThisKeyword,
            OperatorDeclarationSyntax op => op.OperatorToken,
            ConversionOperatorDeclarationSyntax co => co.OperatorKeyword,
            VariableDeclaratorSyntax v => v.Identifier,
            LocalFunctionStatementSyntax lf => lf.Identifier,
            _ => null
        };
    }

    private async Task<(Solution Solution, string SolutionDir, ISymbol Symbol)> ResolveSymbolAtPositionAsync(
        string filePath, int line, int column, CancellationToken ct)
    {
        string solutionDir = await workspaceManager.GetRequiredSolutionDirectoryAsync(ct);
        string resolvedPath = await FilePathResolver.ResolveAgainstSolutionAsync(filePath, workspaceManager, ct);
        await workspaceManager.EnsureFilesFreshAsync([resolvedPath], ct);
        Solution solution = await workspaceManager.GetSolutionAsync(ct);

        SymbolResolution resolution = await solution.ResolveSymbolAtPositionAsync(resolvedPath, line, column, ct: ct);
        if (resolution.Symbol is null)
        {
            throw new UserErrorException(resolution.FormatNoSymbolError(filePath, line, column));
        }

        return (solution, solutionDir, resolution.Symbol);
    }

    /// <summary>
    ///     Returns fuzzy suggestions from symbols declared in a specific file.
    /// </summary>
    private static async Task<List<string>> GetFileSuggestionsAsync(
        Solution solution, string resolvedPath, string symbolName, CancellationToken ct)
    {
        Document? document = solution.GetDocumentByPath(resolvedPath);
        if (document is null)
        {
            return [];
        }

        SemanticModel? model = await document.GetSemanticModelAsync(ct);
        if (model is null)
        {
            return [];
        }

        SyntaxNode root = await model.SyntaxTree.GetRootAsync(ct);
        List<string> names = root.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Select(n => model.GetDeclaredSymbol(n, ct))
            .Where(s => s is not null && s.CanBeReferencedByName && !s.IsImplicitlyDeclared)
            .Select(s => s!.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return FuzzyMatcher.GetSuggestions(symbolName, names);
    }

    /// <summary>
    ///     Returns <c>true</c> if the resolved symbol's name matches the expected name,
    ///     accounting for special members (constructors, accessors, explicit implementations).
    /// </summary>
    internal static bool NameMatchesSymbol(ISymbol symbol, string expectedName)
    {
        // Direct name match covers most symbols including operators (Name == MetadataName for op_*)
        if (String.Equals(symbol.Name, expectedName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Constructor: accept when expectedName is the containing type name
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor })
        {
            if (symbol.ContainingType is not null &&
                String.Equals(symbol.ContainingType.Name, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Accessor (get_Prop, set_Prop) or event accessor: match against associated symbol name
        if (symbol is IMethodSymbol { AssociatedSymbol: not null } accessor &&
            String.Equals(accessor.AssociatedSymbol.Name, expectedName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Explicit interface implementation: check simple name after last dot
        if (symbol.Name.Contains('.') &&
            String.Equals(FqnParser.SimpleName(symbol.Name), expectedName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private enum SearchKind { Fqn, Name }
}
