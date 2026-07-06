using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Constants;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;
using Zphil.Roz.Symbols;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Services;

/// <summary>
///     Service for adding, removing, and sorting using directives in C# files.
/// </summary>
internal sealed class UsingDirectiveService(WorkspaceManager workspaceManager, DiagnosticBaselineManager baselineManager)
{
    /// <summary>
    ///     Adds using directives to a file in sorted order, skipping duplicates
    ///     and namespaces already available via global/implicit usings.
    /// </summary>
    public async Task<AddUsingsResult> AddUsingsAsync(
        string filePath, string[] usings, bool sortUsings = true, CancellationToken ct = default)
    {
        baselineManager.ScheduleBaselineCaptureIfNeeded();
        ArgumentNullException.ThrowIfNull(usings);
        if (usings.Length == 0)
        {
            throw new UserErrorException("At least one namespace must be provided.");
        }

        string resolvedPath = await FilePathResolver.ResolveAgainstSolutionAsync(filePath, workspaceManager, ct);
        await workspaceManager.EnsureFilesFreshAsync([resolvedPath], ct);

        if (!resolvedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            throw new UserErrorException(ErrorMessages.NotACSharpFile(filePath, "add_usings"));
        }

        (string originalContent, Encoding encoding) = await FileUtility.ReadFileWithEncodingAsync(resolvedPath, ct);

        Solution solution = await workspaceManager.GetSolutionAsync(ct);
        Document? document = solution.GetDocumentByPath(resolvedPath);
        if (document is not null)
        {
            EditSymbolResolver.EnsureCSharpDocument(document, filePath);
        }

        var parseOptions = document?.Project.ParseOptions as CSharpParseOptions;

        SyntaxTree tree = CSharpSyntaxTree.ParseText(originalContent, parseOptions, cancellationToken: ct);
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot(ct);

        SyntaxList<UsingDirectiveSyntax> existingUsings = root.Usings;
        HashSet<string> existingKeys = new(existingUsings.Select(UsingKey), StringComparer.Ordinal);

        HashSet<string> globalNames = await GetGlobalUsingNamesAsync(document, ct);

        List<string> added = new();
        List<UsingDirectiveSyntax> newDirectives = new();
        List<string> alreadyPresent = new();
        List<string> alreadyGloballyImported = new();
        List<string> invalid = new();

        foreach (string ns in usings)
        {
            string trimmed = ns.Trim();
            UsingDirectiveSyntax? directive = TryCreateUsingDirective(trimmed);
            if (directive is null)
            {
                invalid.Add(ns);
                continue;
            }

            // Key dedup off the constructed directive, not the raw input: an alias or static
            // input ("SL = ...", "static System.Math") never matches an existing using's bare
            // NamespaceOrType string, so the raw-input compare appended a duplicate (CS1537/CS0105).
            string key = UsingKey(directive);

            if (existingKeys.Contains(key))
            {
                alreadyPresent.Add(trimmed);
            }
            else if (globalNames.Contains(directive.NamespaceOrType.ToString()))
            {
                alreadyGloballyImported.Add(trimmed);
            }
            else
            {
                added.Add(trimmed);
                newDirectives.Add(directive);
                existingKeys.Add(key);
            }
        }

        if (invalid.Count > 0)
        {
            throw new UserErrorException(
                $"Invalid using directive(s): {String.Join(", ", invalid)}. " +
                "Expected a namespace (e.g. \"System.Linq\") or alias (e.g. \"Json = System.Text.Json\").");
        }

        string relPath = workspaceManager.GetRelativePath(resolvedPath);

        if (added.Count == 0)
        {
            return new AddUsingsResult(added, alreadyPresent, alreadyGloballyImported, relPath);
        }

        List<UsingDirectiveSyntax> merged = sortUsings
            ? MergeInsertUsings(existingUsings, newDirectives)
            : existingUsings.Concat(newDirectives).ToList();

        CompilationUnitSyntax newRoot = root.WithUsings(SyntaxFactory.List(merged));
        string newContent = FileUtility.NormalizeLineEndings(newRoot.ToFullString(), originalContent);

        await FileUtility.WriteContentAsync(resolvedPath, newContent, encoding, workspaceManager, ct);

        return new AddUsingsResult(added, alreadyPresent, alreadyGloballyImported, relPath);
    }

    /// <summary>
    ///     Removes unused using directives from one or more files, sorting the remaining ones.
    /// </summary>
    public async Task<RemoveUnusedUsingsResult> RemoveUnusedUsingsAsync(
        string[] filePaths, bool sortUsings = true, CancellationToken ct = default)
    {
        baselineManager.ScheduleBaselineCaptureIfNeeded();
        ArgumentNullException.ThrowIfNull(filePaths);
        if (filePaths.Length == 0)
        {
            throw new UserErrorException("At least one file path must be provided.");
        }

        filePaths = filePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        List<FileUsingsResult> results = new();
        List<string> resolvedForFreshness = [];
        List<(string Original, string Resolved)> resolvedPaths = [];

        foreach (string fp in filePaths)
        {
            try
            {
                string resolvedPath = await FilePathResolver.ResolveAgainstSolutionAsync(fp, workspaceManager, ct);
                resolvedForFreshness.Add(resolvedPath);
                resolvedPaths.Add((fp, resolvedPath));
            }
            catch (UserErrorException ex)
            {
                results.Add(new FileUsingsResult(fp, [], [], ex.Message));
            }
        }

        if (resolvedForFreshness.Count > 0)
        {
            await workspaceManager.EnsureFilesFreshAsync(resolvedForFreshness, ct);
        }

        Solution solution = await workspaceManager.GetSolutionAsync(ct);

        foreach ((string fp, string resolvedPath) in resolvedPaths)
        {
            if (!resolvedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new FileUsingsResult(fp, [], [], ErrorMessages.NotACSharpFile(fp, "remove_unused_usings")));
                continue;
            }

            Document? document = solution.GetDocumentByPath(resolvedPath);
            if (document is null)
            {
                results.Add(new FileUsingsResult(fp, [], [], ErrorMessages.FileNotInSolution(fp)));
                continue;
            }

            EditSymbolResolver.EnsureCSharpDocument(document, fp);

            FileUsingsResult fileResult = await RemoveUnusedFromDocumentAsync(document, resolvedPath, sortUsings, ct);
            results.Add(fileResult);
        }

        return new RemoveUnusedUsingsResult(results);
    }

    private async Task<FileUsingsResult> RemoveUnusedFromDocumentAsync(
        Document document, string resolvedPath, bool sortUsings, CancellationToken ct)
    {
        SemanticModel? model = await document.GetSemanticModelAsync(ct);
        if (model is null)
        {
            throw new UserErrorException(ErrorMessages.CouldNotAnalyze(resolvedPath));
        }

        SyntaxTree? tree = await document.GetSyntaxTreeAsync(ct);
        if (tree is null)
        {
            throw new UserErrorException($"Could not get syntax tree for {resolvedPath} — the file may not be a valid C# source file.");
        }

        (string originalContent, Encoding encoding) = await FileUtility.ReadFileWithEncodingAsync(resolvedPath, ct);

        SyntaxNode root = await tree.GetRootAsync(ct);
        var compilationUnit = (CompilationUnitSyntax)root;

        // Identify usings protected by preprocessor directives — these must not be
        // removed (could destroy #if/#else blocks) or reordered (sorting strips trivia).
        HashSet<UsingDirectiveSyntax> protectedUsings = FindProtectedUsings(compilationUnit.Usings);
        bool canSort = sortUsings
                       && protectedUsings.Count == 0
                       && !HasNonFirstUsingComment(compilationUnit.Usings);

        const string unusedUsingId = "CS8019";
        const string unresolvedTypeId = "CS0246";

        ImmutableArray<Diagnostic> allDiagnostics = model.GetDiagnostics(cancellationToken: ct);

        HashSet<TextSpan> unresolvedSpans = allDiagnostics
            .Where(d => d.Id == unresolvedTypeId)
            .Select(d => d.Location.SourceSpan)
            .ToHashSet();

        List<UsingDirectiveSyntax> unusedUsings = new();
        List<string> removedNames = new();
        List<string> skippedNames = new();

        foreach (Diagnostic diagnostic in allDiagnostics.Where(d => d.Id == unusedUsingId))
        {
            SyntaxNode node = root.FindNode(diagnostic.Location.SourceSpan);
            if (node is UsingDirectiveSyntax usingDirective && !protectedUsings.Contains(usingDirective))
            {
                // Check if any CS0246 diagnostic overlaps this using's span
                if (unresolvedSpans.Any(s => usingDirective.Span.Contains(s)))
                {
                    skippedNames.Add(usingDirective.NamespaceOrType.ToString());
                }
                else
                {
                    unusedUsings.Add(usingDirective);
                    removedNames.Add(usingDirective.NamespaceOrType.ToString());
                }
            }
        }

        string relPath = workspaceManager.GetRelativePath(resolvedPath);

        if (unusedUsings.Count == 0)
        {
            // Still sort even if nothing was removed
            if (canSort)
            {
                SyntaxList<UsingDirectiveSyntax> existingUsings = compilationUnit.Usings;
                if (existingUsings.Count > 1)
                {
                    List<UsingDirectiveSyntax> sorted = SortUsings(existingUsings.ToList());
                    if (!UsingsMatch(existingUsings, sorted))
                    {
                        sorted[0] = sorted[0].WithLeadingTrivia(existingUsings[0].GetLeadingTrivia());
                        CompilationUnitSyntax newRoot = compilationUnit.WithUsings(SyntaxFactory.List(sorted));
                        await WriteBackAsync(resolvedPath, newRoot, originalContent, encoding, ct);
                    }
                }
            }

            return new FileUsingsResult(relPath, removedNames, skippedNames);
        }

        // Capture leading trivia from the original first using (copyright headers, blank lines)
        // before RemoveNodes drops it — Roslyn attaches file-level trivia to the first token.
        SyntaxTriviaList firstUsingTrivia = compilationUnit.Usings[0].GetLeadingTrivia();
        bool firstUsingRemoved = unusedUsings.Contains(compilationUnit.Usings[0]);

        // Remove unused nodes. KeepDirectives preserves any preprocessor directives attached to a
        // removed using (e.g. a closing #endif in the leading trivia of an unconditional using that
        // follows a #if/#endif block) — dropping them would unbalance #if/#endif and break the file.
        // Non-directive trivia (whitespace, comments) is still dropped, so the first-using copyright
        // handling below is unchanged.
        SyntaxNode cleaned = root.RemoveNodes(unusedUsings, SyntaxRemoveOptions.KeepDirectives)!;
        var cleanedUnit = (CompilationUnitSyntax)cleaned;

        if (cleanedUnit.Usings.Count > 0)
        {
            // Determine the trivia for the new first using. When the first using was removed,
            // its file-level trivia (copyright headers) must be preserved. If the new first
            // using has directive trivia (#if, #region, etc.), merge both to keep the directives.
            SyntaxTriviaList newFirstTrivia = cleanedUnit.Usings[0].GetLeadingTrivia();
            SyntaxTriviaList leadingTrivia;
            if (firstUsingRemoved && newFirstTrivia.Any(t => t.IsDirective))
            {
                leadingTrivia = firstUsingTrivia.AddRange(newFirstTrivia);
            }
            else if (firstUsingRemoved)
            {
                leadingTrivia = firstUsingTrivia;
            }
            else
            {
                leadingTrivia = newFirstTrivia;
            }

            // Sort remaining usings (if requested and safe), then apply leading trivia in a single pass
            List<UsingDirectiveSyntax> finalUsings = canSort
                ? SortUsings(cleanedUnit.Usings.ToList())
                : cleanedUnit.Usings.ToList();
            finalUsings[0] = finalUsings[0].WithLeadingTrivia(leadingTrivia);
            cleanedUnit = cleanedUnit.WithUsings(SyntaxFactory.List(finalUsings));
        }
        else if (firstUsingRemoved && cleanedUnit.Members.Count > 0)
        {
            // All usings removed — reattach file-level trivia to the first member
            MemberDeclarationSyntax firstMember = cleanedUnit.Members[0];
            cleanedUnit = cleanedUnit.WithMembers(
                cleanedUnit.Members.Replace(
                    firstMember,
                    firstMember.WithLeadingTrivia(
                        firstUsingTrivia.AddRange(firstMember.GetLeadingTrivia()))));
        }

        await WriteBackAsync(resolvedPath, cleanedUnit, originalContent, encoding, ct);

        return new FileUsingsResult(relPath, removedNames, skippedNames);
    }

    /// <summary>
    ///     Identifies usings that must not be removed or reordered because they are
    ///     inside <c>#if</c>/<c>#endif</c> preprocessor conditional blocks (layer 1: depth tracking)
    ///     or have any directive trivia attached — <c>#region</c>, <c>#pragma</c>, etc. (layer 2).
    /// </summary>
    private static HashSet<UsingDirectiveSyntax> FindProtectedUsings(SyntaxList<UsingDirectiveSyntax> usings)
    {
        HashSet<UsingDirectiveSyntax> protectedSet = new();
        var conditionalDepth = 0;

        foreach (UsingDirectiveSyntax usingDirective in usings)
        {
            var hasDirectiveTrivia = false;

            foreach (SyntaxTrivia trivia in usingDirective.GetLeadingTrivia())
            {
                switch (trivia.Kind())
                {
                    case SyntaxKind.IfDirectiveTrivia:
                        conditionalDepth++;
                        break;
                    case SyntaxKind.EndIfDirectiveTrivia:
                        // Layer 1 (depth) only. A closing #endif that returns depth to 0
                        // terminates a conditional block; it must NOT mark the following
                        // unconditional using as protected (that disabled sorting file-wide and
                        // left the using unremovable). A nested #endif leaves depth > 0, so its
                        // using stays protected by the depth check below.
                        conditionalDepth--;
                        break;
                    default:
                        // Layer 2: #region, #pragma, #nullable, etc. protect the using they
                        // precede. #if/#endif are excluded here — they're handled above by depth.
                        if (trivia.IsDirective)
                        {
                            hasDirectiveTrivia = true;
                        }

                        break;
                }
            }

            if (conditionalDepth > 0 || hasDirectiveTrivia)
            {
                protectedSet.Add(usingDirective);
            }
        }

        return protectedSet;
    }

    /// <summary>
    ///     True when any using other than the first carries a leading comment — a standalone
    ///     comment that groups or annotates usings (e.g. <c>// Third-party</c>). Sorting strips
    ///     every using's leading trivia, so such a comment must suppress the re-sort to avoid
    ///     deleting it. The first using's leading trivia is excluded: it holds the file header,
    ///     which is captured and reattached separately.
    /// </summary>
    private static bool HasNonFirstUsingComment(SyntaxList<UsingDirectiveSyntax> usings) =>
        usings.Skip(1).Any(u => u.GetLeadingTrivia().Any(IsCommentTrivia));

    private static bool IsCommentTrivia(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
        || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
        || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
        || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

    /// <summary>
    ///     If <paramref name="insertIndex" /> falls inside a <c>#if</c>/<c>#endif</c> conditional
    ///     block, moves it forward to the first position after the block closes. This prevents
    ///     new unconditional usings from being placed inside a TFM-conditional region.
    /// </summary>
    private static int AdjustInsertionForConditionalBlocks(List<UsingDirectiveSyntax> usings, int insertIndex)
    {
        // Compute "entry depth" — the conditional depth BEFORE each using's leading trivia
        // is processed. Inserting at position i places the new node at entry depth i.
        var depth = 0;

        for (var i = 0; i < usings.Count; i++)
        {
            if (i == insertIndex && depth > 0)
            {
                // Insertion point is inside a conditional block — scan forward for exit
                return FindConditionalExit(usings, i, depth);
            }

            foreach (SyntaxTrivia trivia in usings[i].GetLeadingTrivia())
            {
                switch (trivia.Kind())
                {
                    case SyntaxKind.IfDirectiveTrivia:
                        depth++;
                        break;
                    case SyntaxKind.EndIfDirectiveTrivia:
                        depth--;
                        break;
                }
            }
        }

        // insertIndex == usings.Count (appending at end) or depth was 0 — safe
        return insertIndex;
    }

    /// <summary>
    ///     Starting from <paramref name="fromIndex" /> at <paramref name="depth" /> &gt; 0,
    ///     scans forward through remaining usings' leading trivia until depth reaches 0.
    ///     Returns the first safe insertion index after the conditional block.
    /// </summary>
    private static int FindConditionalExit(List<UsingDirectiveSyntax> usings, int fromIndex, int depth)
    {
        for (int i = fromIndex; i < usings.Count; i++)
        {
            foreach (SyntaxTrivia trivia in usings[i].GetLeadingTrivia())
            {
                switch (trivia.Kind())
                {
                    case SyntaxKind.IfDirectiveTrivia:
                        depth++;
                        break;
                    case SyntaxKind.EndIfDirectiveTrivia:
                        depth--;
                        if (depth == 0)
                        {
                            // #endif is in usings[i]'s leading trivia, so inserting at i
                            // would place us before the #endif (still inside). Insert after.
                            return i + 1;
                        }

                        break;
                }
            }
        }

        // Block never closed within usings — append at end as safest option
        return usings.Count;
    }

    private async Task WriteBackAsync(
        string resolvedPath, CompilationUnitSyntax newRoot, string originalContent, Encoding encoding,
        CancellationToken ct)
    {
        string newContent = FileUtility.NormalizeLineEndings(newRoot.ToFullString(), originalContent);
        await FileUtility.WriteContentAsync(resolvedPath, newContent, encoding, workspaceManager, ct);
    }

    /// <summary>
    ///     Collects all global using namespace names from the project's compilation.
    ///     Checks both <see cref="CSharpCompilationOptions.Usings" /> (SDK implicit usings)
    ///     and <c>global using</c> directives in source files.
    /// </summary>
    private static async Task<HashSet<string>> GetGlobalUsingNamesAsync(
        Document? document, CancellationToken ct)
    {
        if (document is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        Compilation? compilation = await document.Project.GetCompilationAsync(ct);
        if (compilation is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        HashSet<string> names = new(StringComparer.Ordinal);

        // From compilation options (implicit usings translated from <Using> MSBuild items)
        if (compilation.Options is CSharpCompilationOptions csharpOptions)
        {
            foreach (string ns in csharpOptions.Usings)
            {
                names.Add(ns);
            }
        }

        // From source files (explicit global using directives, e.g. in GlobalUsings.cs)
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            var root = (CompilationUnitSyntax)await tree.GetRootAsync(ct);
            foreach (UsingDirectiveSyntax u in root.Usings)
            {
                if (u.GlobalKeyword != default && u.Alias is null && u.StaticKeyword == default)
                {
                    names.Add(u.NamespaceOrType.ToString());
                }
            }
        }

        return names;
    }

    private static List<UsingDirectiveSyntax> SortUsings(List<UsingDirectiveSyntax> usings)
    {
        List<UsingDirectiveSyntax> sorted = usings
            .OrderBy(u => GetUsingKind(u))
            .ThenBy(u => IsSystemNamespace(u) ? 0 : 1)
            .ThenBy(u => u.NamespaceOrType.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(u => u
                .WithLeadingTrivia(SyntaxTriviaList.Empty)
                .WithTrailingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.ElasticCarriageReturnLineFeed)))
            .ToList();

        // Insert blank line between groups (System regular → non-System regular → static → alias)
        for (var i = 1; i < sorted.Count; i++)
        {
            if (GetGroupKey(sorted[i - 1]) != GetGroupKey(sorted[i]))
            {
                sorted[i] = sorted[i].WithLeadingTrivia(
                    SyntaxFactory.TriviaList(SyntaxFactory.ElasticCarriageReturnLineFeed));
            }
        }

        return sorted;
    }

    /// <summary>
    ///     Groups usings for blank-line separation: System(0), non-System(1), static(2), alias(3).
    /// </summary>
    private static int GetGroupKey(UsingDirectiveSyntax u) =>
        u.Alias is not null ? 3 :
        u.StaticKeyword != default ? 2 :
        IsSystemNamespace(u) ? 0 : 1;

    private static int GetUsingKind(UsingDirectiveSyntax u)
    {
        if (u.Alias is not null)
        {
            return 2; // alias usings last
        }

        if (u.StaticKeyword != default)
        {
            return 1; // static usings middle
        }

        return 0; // regular usings first
    }

    private static bool IsSystemNamespace(UsingDirectiveSyntax u)
    {
        var name = u.NamespaceOrType.ToString();
        return name == "System" || name.StartsWith("System.", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Canonical dedup key for a using directive. Distinguishes regular, <c>static</c>, and
    ///     alias usings so that re-adding any of them is recognised as already present, and keys
    ///     off the constructed directive's parsed shape rather than the raw input string.
    /// </summary>
    private static string UsingKey(UsingDirectiveSyntax u) =>
        u.Alias is not null ? $"{u.Alias.Name} = {u.NamespaceOrType}" :
        u.StaticKeyword != default ? $"static {u.NamespaceOrType}" :
        u.NamespaceOrType.ToString();

    /// <summary>
    ///     Builds a normalized using directive from a raw input line, or returns <c>null</c> when
    ///     the input does not parse cleanly as exactly one using directive with nothing left over.
    /// </summary>
    /// <remarks>
    ///     The directive comes from a real parse of the full <c>using …;</c> line so alias,
    ///     <c>static</c>, and regular usings are all built correctly (a hand-rolled split +
    ///     <c>ParseName</c> mangled <c>"static System.Math"</c>). Validation and construction share
    ///     the single parse, so they can never disagree on what a given input parses to.
    /// </remarks>
    private static UsingDirectiveSyntax? TryCreateUsingDirective(string value)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText($"using {value};");
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

        if (root.ContainsDiagnostics || root.Usings.Count != 1 || root.Members.Count != 0)
        {
            return null;
        }

        return root.Usings[0]
            .NormalizeWhitespace()
            .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
    }

    /// <summary>
    ///     Inserts new using directives into the existing list at sorted positions,
    ///     preserving the original trivia of existing usings.
    /// </summary>
    private static List<UsingDirectiveSyntax> MergeInsertUsings(
        SyntaxList<UsingDirectiveSyntax> existing, List<UsingDirectiveSyntax> newDirectives)
    {
        List<UsingDirectiveSyntax> result = existing.ToList();

        foreach (UsingDirectiveSyntax newUsing in newDirectives)
        {
            int newKind = GetUsingKind(newUsing);
            bool newIsSystem = IsSystemNamespace(newUsing);
            var newName = newUsing.NamespaceOrType.ToString();

            int insertIndex = result.Count;
            for (var i = 0; i < result.Count; i++)
            {
                int existingKind = GetUsingKind(result[i]);
                if (existingKind > newKind)
                {
                    insertIndex = i;
                    break;
                }

                if (existingKind < newKind)
                {
                    continue;
                }

                // Same kind — sort System before non-System, then alphabetically
                bool existingIsSystem = IsSystemNamespace(result[i]);
                if (!newIsSystem && existingIsSystem)
                {
                    continue;
                }

                if (newIsSystem && !existingIsSystem)
                {
                    insertIndex = i;
                    break;
                }

                int cmp = String.Compare(newName, result[i].NamespaceOrType.ToString(), StringComparison.OrdinalIgnoreCase);
                if (cmp < 0)
                {
                    insertIndex = i;
                    break;
                }
            }

            // Don't insert inside a preprocessor conditional block — move past #endif
            insertIndex = AdjustInsertionForConditionalBlocks(result, insertIndex);

            // Match only the neighbor's end-of-line, not its trailing comment — copying the full
            // trailing trivia would duplicate a neighbor's "// note" onto the new using. Fall back
            // to CRLF at the top (or when the neighbor has no EOL); NormalizeLineEndings reconciles
            // CRLF/LF on write, so line-ending style is still preserved.
            SyntaxTriviaList trailingTrivia = SyntaxFactory.TriviaList(SyntaxFactory.ElasticCarriageReturnLineFeed);
            if (insertIndex > 0)
            {
                SyntaxTrivia eol = result[insertIndex - 1].GetTrailingTrivia()
                    .LastOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
                if (eol != default)
                {
                    trailingTrivia = SyntaxFactory.TriviaList(eol);
                }
            }

            UsingDirectiveSyntax prepared = newUsing
                .WithLeadingTrivia(SyntaxTriviaList.Empty)
                .WithTrailingTrivia(trailingTrivia);

            // When inserting at the very top, the displaced first using carries the file header
            // (copyright comment, blank line) in its leading trivia. Move that header onto the new
            // top using so it stays above everything — unless it's directive trivia (#if/#region),
            // which must stay attached to the using it guards (e.g. an #if-wrapped first using).
            if (insertIndex == 0 && result.Count > 0)
            {
                SyntaxTriviaList displacedLeading = result[0].GetLeadingTrivia();
                if (!displacedLeading.Any(t => t.IsDirective))
                {
                    prepared = prepared.WithLeadingTrivia(displacedLeading);
                    result[0] = result[0].WithLeadingTrivia(SyntaxTriviaList.Empty);
                }
            }

            result.Insert(insertIndex, prepared);
        }

        return result;
    }

    private static bool UsingsMatch(SyntaxList<UsingDirectiveSyntax> existing, List<UsingDirectiveSyntax> sorted)
    {
        if (existing.Count != sorted.Count)
        {
            return false;
        }

        for (var i = 0; i < existing.Count; i++)
        {
            if (existing[i].NamespaceOrType.ToString() != sorted[i].NamespaceOrType.ToString())
            {
                return false;
            }
        }

        return true;
    }
}
