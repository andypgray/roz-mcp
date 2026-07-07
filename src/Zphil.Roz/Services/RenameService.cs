using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;
using Zphil.Roz.Symbols;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Services;

/// <summary>
///     Service for solution-wide symbol renaming with file rename and disabled branch fixup support.
/// </summary>
internal sealed class RenameService(WorkspaceManager workspaceManager, DiagnosticBaselineManager baselineManager, EditSymbolResolver symbolResolution, EditVerificationService verificationService)
{
    private static readonly string[] AssociatedFileSuffixes = [".Designer.cs", ".resx", ".Designer.resx"];

    private static readonly string[] StrayScanExtensions = [".cs", ".razor"];

    private static readonly string[] StrayScanSuffixExclusions = [".g.cs", ".g.i.cs", ".Designer.cs"];

    private static readonly HashSet<string> StrayScanDirectoryExclusions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".vs", "node_modules", "packages", "TestResults", "artifacts"
        };

    /// <summary>
    ///     Renames a symbol across the entire solution, updating all references.
    /// </summary>
    /// <remarks>
    ///     <paramref name="verify" /> reports the compiler-error delta the rename introduces:
    ///     <see cref="VerifyMode.Delta" /> commits (as <see cref="VerifyMode.None" /> does) then diffs;
    ///     <see cref="VerifyMode.DryRun" /> collects the rename in memory, writes nothing, and defers the
    ///     file rename to a note. The fork already exists — <c>Renamer.RenameSymbolAsync</c> produces the
    ///     complete post-rename <see cref="Solution" /> — so persist + verify route through the shared
    ///     <see cref="EditVerificationService.FinalizeForkAsync" /> contract (over
    ///     <see cref="SolutionChangeWriter" />) rather than an <see cref="EditSession" />. Inactive
    ///     preprocessor-branch fixups are folded into that same fork before it is finalized, so they ride
    ///     the one atomic commit.
    /// </remarks>
    public async Task<RenameSymbolResult> RenameSymbolAsync(
        string filePath, string symbolName, int? line, int? column, string newName, SymbolRenameOptions options,
        VerifyMode verify = VerifyMode.None, IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default, string? containingType = null, SymbolicKind? kind = null)
    {
        if (!IsValidSymbolName(newName))
        {
            throw new UserErrorException($"'{newName}' is not a valid C# identifier.");
        }

        baselineManager.ScheduleBaselineCaptureIfNeeded();

        // Rename intentionally keeps strict position resolution (preferName defaulted false):
        // a rename rewrites every reference, renames files, and fixes #if branches solution-wide,
        // so the position↔symbolName cross-check is a safety interlock against a typo'd target
        // renaming the wrong symbol. Unlike edit_symbol, a single rename has no earlier batch op
        // to shift its cursor, so the EDIT-2 staleness trigger cannot occur here.
        ResolvedSymbolContext context = await symbolResolution.ResolveAsync(filePath, symbolName, line, column, ct, containingType, kind);
        SemanticModel? model = await context.Document.GetSemanticModelAsync(ct);

        // Field/event-field declarations: GetDeclaredSymbol returns null for FieldDeclarationSyntax;
        // the symbol lives on the VariableDeclaratorSyntax child (same invariant as EditSymbolResolver.FindDeclarationByName).
        // A fieldless declaration (int ;) has no declarator — leave symbol null so the friendly
        // "could not resolve" error below fires instead of crashing on First().
        ISymbol? symbol;
        if (context.TargetNode is BaseFieldDeclarationSyntax fieldDecl)
        {
            VariableDeclaratorSyntax? declarator = fieldDecl.Declaration.Variables.FirstOrDefault();
            symbol = declarator is null ? null : model?.GetDeclaredSymbol(declarator, ct);
        }
        else
        {
            symbol = model?.GetDeclaredSymbol(context.TargetNode, ct);
        }

        if (symbol is null)
        {
            throw new UserErrorException(
                $"Could not resolve symbol '{symbolName}' in {filePath}. Provide line and column to disambiguate, or verify the symbol name with get_symbols_overview.");
        }

        if (symbol is INamespaceSymbol)
        {
            throw new UserErrorException(
                $"Cannot rename namespace '{symbol.Name}' via rename_symbol — namespace renames have solution-wide blast radius. "
                + "Use replace_content with regex mode to rename namespace segments in specific files.");
        }

        if (symbol is IMethodSymbol method)
        {
            string? rejection = method.MethodKind switch
            {
                MethodKind.Destructor => "Destructors cannot be renamed — the destructor name is derived from the containing class name. To rename the destructor, rename the class instead.",
                MethodKind.Constructor => "Constructors cannot be renamed — the constructor name must match the containing class name. To rename the constructor, rename the class instead.",
                MethodKind.StaticConstructor => "Static constructors cannot be renamed — the name must match the containing class name. To rename the static constructor, rename the class instead.",
                MethodKind.UserDefinedOperator or MethodKind.Conversion => $"Operator '{symbol.Name}' cannot be renamed — operator names are determined by the operator keyword.",
                _ => null
            };

            if (rejection is not null)
            {
                throw new UserErrorException(rejection);
            }
        }

        if (symbol is IPropertySymbol { IsIndexer: true })
        {
            throw new UserErrorException("Indexers cannot be renamed — the indexer name 'this[]' is fixed by the language.");
        }

        string oldName = symbol.Name;

        if (String.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return new RenameSymbolResult(oldName, newName, []);
        }

        // Strip RenameFile from options — Roslyn doesn't handle it with MSBuildWorkspace,
        // so we perform the file rename manually after saving.
        bool renameFile = options.RenameFile;
        SymbolRenameOptions roslynOptions = options with { RenameFile = false };

        Solution solution = context.Document.Project.Solution;
        Solution newSolution = await Renamer.RenameSymbolAsync(solution, symbol, roslynOptions, newName, ct);
        bool isLocallyScoped = symbol.IsLocallyScopedSymbol();

        // Roslyn's rename only touches the active preprocessor branch. Inactive #if/#else/#elif branches
        // are DisabledTextTrivia (raw text) and are missed — fold whole-word replacement into those spans
        // on the fork itself, so the fixups ride the single commit FinalizeForkAsync issues below.
        (newSolution, int disabledBranchFixups) = await FoldDisabledBranchFixupsAsync(solution, newSolution, oldName, newName, ct);

        // Persist + verify via the shared fork contract. Delta commits first, then diffs the two immutable
        // snapshots BEFORE the file-rename reload below can dispose the workspace out from under the fork's
        // compilation services. (Disabled-branch fixups are trivia and the file rename doesn't change
        // compiled content, so newSolution is a faithful "after" snapshot for the delta.)
        ForkFinalizeOutcome outcome = await verificationService.FinalizeForkAsync(solution, newSolution, verify, progress, ct);
        List<string> changedDocs = outcome.ChangedDocs;

        bool committed = verify != VerifyMode.DryRun;
        string resolvedPath = context.ResolvedPath;
        string? deferredFileRenameNote = null;
        if (renameFile && symbol is INamedTypeSymbol && resolvedPath.EndsWith($"{oldName}.cs", StringComparison.OrdinalIgnoreCase))
        {
            if (committed)
            {
                // Drain all pending ScheduleFileChanged notifications before File.Move.
                // FinalizeForkAsync's commit (SolutionChangeWriter.SaveAsync) queued TryApplyChanges calls
                // that write to disk; moving the file or reloading the workspace while those are in-flight
                // causes spurious IOExceptions ("file being used by another process").
                await workspaceManager.DrainPendingUpdatesAsync();

                string directory = Path.GetDirectoryName(resolvedPath)!;
                string newFilePath = Path.Combine(directory, $"{newName}.cs");

                // Fail fast on a target collision. Without this, File.Move's "already exists" IOException
                // matches the transient-I/O retry filter, so it is retried ~350 ms before surfacing as a
                // crash-classified raw IOException instead of a friendly, actionable error.
                if (File.Exists(newFilePath))
                {
                    throw new UserErrorException(
                        $"Cannot rename file to '{newName}.cs' — '{Path.GetFileName(newFilePath)}' already exists in {directory}.");
                }

                await FileUtility.RunWithIoRetryAsync(() => File.Move(resolvedPath, newFilePath), ct);
                changedDocs.Remove(workspaceManager.GetRelativePath(resolvedPath));
                changedDocs.Add(workspaceManager.GetRelativePath(newFilePath));

                // Rename associated files (Designer.cs, .resx) that follow the {TypeName}.* naming convention.
                // These are common in WinForms projects and would be orphaned without this step.
                await RenameAssociatedFilesAsync(directory, oldName, newName, changedDocs, ct);

                // File was physically moved — workspace must reload to pick up the new path.
                // Schedule in background; next GetSolutionAsync drains it.
                // ClearBaseline is handled automatically by WorkspaceService.onBeforeReload callback.
                await workspaceManager.ScheduleReloadAsync(ct: ct);
            }
            else
            {
                deferredFileRenameNote =
                    $"File rename to '{newName}.cs' (and any associated .Designer.cs/.resx) applies on commit only — verify=DryRun writes nothing.";
            }
        }

        IReadOnlyList<StrayReference>? strays = null;
        if (!isLocallyScoped)
        {
            string solutionDir = await workspaceManager.GetRequiredSolutionDirectoryAsync(ct);
            HashSet<string> excludedPaths = BuildExcludedPathSet(solutionDir, newSolution, changedDocs);
            strays = await FindStrayReferencesAsync(solutionDir, excludedPaths, oldName, ct);
        }

        return new RenameSymbolResult(
            oldName, newName, changedDocs, disabledBranchFixups, strays, outcome.Verification, deferredFileRenameNote);
    }

    /// <summary>
    ///     Folds the disabled-branch fixups into the rename fork. Roslyn's semantic rename only reaches the
    ///     active preprocessor branch; inactive <c>#if</c>/<c>#else</c>/<c>#elif</c> branches are
    ///     <see cref="SyntaxKind.DisabledTextTrivia" /> (raw text) it cannot see. For each changed document
    ///     — the same <see cref="SolutionChanges.GetProjectChanges" /> enumeration + first-wins path dedupe
    ///     that <see cref="SolutionChangeWriter.CollectFileChangesAsync" /> uses, so the written content is
    ///     unchanged — this whole-word-replaces the old name inside those spans and applies the fixed text
    ///     to <em>every</em> <see cref="DocumentId" /> at the path (multi-TFM files map to several). Text and
    ///     spans now come from the same fork document, and the result rides the single commit
    ///     <see cref="EditVerificationService.FinalizeForkAsync" /> issues.
    /// </summary>
    /// <returns>The updated fork and the total number of text replacements made across all files.</returns>
    private static async Task<(Solution Fork, int FixupCount)> FoldDisabledBranchFixupsAsync(
        Solution baseSolution, Solution fork, string oldName, string newName, CancellationToken ct)
    {
        var totalFixups = 0;
        HashSet<string> processedPaths = new(StringComparer.OrdinalIgnoreCase);

        List<DocumentId> changedDocIds = fork.GetChanges(baseSolution)
            .GetProjectChanges()
            .SelectMany(pc => pc.GetChangedDocuments())
            .ToList();

        foreach (DocumentId docId in changedDocIds)
        {
            Document? doc = fork.GetDocument(docId);
            string? filePath = doc?.FilePath;
            if (doc is null || filePath is null || !processedPaths.Add(filePath))
            {
                continue;
            }

            SyntaxNode? root = await doc.GetSyntaxRootAsync(ct);
            if (root is null)
            {
                continue;
            }

            List<TextSpan> disabledSpans = FindDisabledTextSpans(root);
            if (disabledSpans.Count == 0)
            {
                continue;
            }

            SourceText text = await doc.GetTextAsync(ct);
            (string fixedText, int count) = ReplaceInDisabledSpans(text.ToString(), disabledSpans, oldName, newName);
            if (count == 0)
            {
                continue;
            }

            // Apply the fixed text to every DocumentId at this path (multi-TFM files map to several — the
            // EditSession.Stage rule). A null encoding propagates so CollectFileChangesAsync's Utf8NoBom
            // fallback fires exactly as it did when this ran as a separate post-commit write.
            var fixedSource = SourceText.From(fixedText, text.Encoding);
            foreach (DocumentId pathId in fork.GetDocumentIdsWithFilePath(filePath))
            {
                fork = fork.WithDocumentText(pathId, fixedSource);
            }

            totalFixups += count;
        }

        return (fork, totalFixups);
    }

    private static HashSet<string> BuildExcludedPathSet(string solutionDir, Solution solution, IEnumerable<string> relativePaths)
    {
        HashSet<string> excluded = new(StringComparer.OrdinalIgnoreCase);

        // changedDocs carries every path Roslyn just rewrote — including a file-rename's NEW path,
        // which is not yet present in the in-memory solution enumerated below. Keep adding them.
        foreach (string relPath in relativePaths)
        {
            string abs = Path.GetFullPath(relPath, solutionDir);
            excluded.Add(abs);
        }

        // Every document Roslyn already has loaded is, by definition, inside the solution — even an
        // unchanged file that textually matches the old name only through an unrelated same-named
        // member. Excluding all loaded docs keeps the stray scan to genuinely-external files.
        // AdditionalDocuments covers loaded .razor, which fall in the same false-positive class.
        foreach (TextDocument doc in solution.Projects.SelectMany(p => p.Documents.Concat(p.AdditionalDocuments)))
        {
            if (doc.FilePath is not null)
            {
                excluded.Add(Path.GetFullPath(doc.FilePath));
            }
        }

        return excluded;
    }

    /// <summary>
    ///     Scans the solution directory for <c>.cs</c>/<c>.razor</c> files outside Roslyn's loaded
    ///     workspace that still textually reference <paramref name="oldName" />. Used to surface
    ///     unloaded projects and excluded tests/plugins that a semantic rename cannot reach, so the
    ///     caller can flag them in the response rather than silently leaving the build broken.
    /// </summary>
    private async Task<IReadOnlyList<StrayReference>> FindStrayReferencesAsync(
        string solutionDir, HashSet<string> excludedPaths, string oldName, CancellationToken ct)
    {
        var pattern = $@"\b{Regex.Escape(oldName)}\b";
        var regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        int oldNameLength = oldName.Length;

        List<StrayReference> strays = [];
        foreach (string absPath in EnumerateStrayScanCandidates(solutionDir))
        {
            ct.ThrowIfCancellationRequested();

            if (excludedPaths.Contains(absPath))
            {
                continue;
            }

            long fileLength;
            try
            {
                fileLength = new FileInfo(absPath).Length;
            }
            catch (IOException)
            {
                continue;
            }

            if (fileLength < oldNameLength)
            {
                continue;
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(absPath, ct);
            }
            catch (IOException)
            {
                continue;
            }

            if (content.IndexOf(oldName, StringComparison.Ordinal) < 0)
            {
                continue;
            }

            MatchCollection matches = regex.Matches(content);
            if (matches.Count == 0)
            {
                continue;
            }

            int firstLine = GetOneBasedLineNumber(content, matches[0].Index);
            strays.Add(new StrayReference(
                workspaceManager.GetRelativePath(absPath), matches.Count, firstLine));
        }

        return strays;
    }

    /// <summary>
    ///     Walks <paramref name="root" /> skipping common noise directories (bin, obj, .git, etc.)
    ///     so excluded trees don't pay the per-file filter cost.
    /// </summary>
    private static IEnumerable<string> EnumerateStrayScanCandidates(string root)
    {
        Stack<string> dirs = new();
        dirs.Push(root);

        while (dirs.Count > 0)
        {
            string current = dirs.Pop();

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(current);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string sub in subDirs)
            {
                string name = Path.GetFileName(sub);
                if (!StrayScanDirectoryExclusions.Contains(name))
                {
                    dirs.Push(sub);
                }
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                if (!StrayScanExtensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (StrayScanSuffixExclusions.Any(suffix => file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static int GetOneBasedLineNumber(string content, int charIndex)
    {
        var line = 1;
        int limit = Math.Min(charIndex, content.Length);
        for (var i = 0; i < limit; i++)
        {
            if (content[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>
    ///     Validates that a name is a valid C# identifier, rejecting reserved keywords
    ///     unless they use the verbatim <c>@</c> prefix.
    /// </summary>
    private static bool IsValidSymbolName(string name)
    {
        if (String.IsNullOrEmpty(name))
        {
            return false;
        }

        // Verbatim identifiers: strip @ prefix, then check the remainder is a valid identifier
        if (name.StartsWith('@'))
        {
            string bare = name[1..];
            return SyntaxFacts.IsValidIdentifier(bare);
        }

        // Must be a valid identifier AND not a reserved keyword
        return SyntaxFacts.IsValidIdentifier(name) && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None;
    }

    /// <summary>
    ///     Finds all <see cref="SyntaxKind.DisabledTextTrivia" /> spans in a syntax tree.
    ///     These represent code inside inactive preprocessor branches (#if/#else/#elif).
    /// </summary>
    internal static List<TextSpan> FindDisabledTextSpans(SyntaxNode root)
    {
        List<TextSpan> spans = [];
        foreach (SyntaxTrivia trivia in root.DescendantTrivia())
        {
            if (trivia.IsKind(SyntaxKind.DisabledTextTrivia))
            {
                spans.Add(trivia.Span);
            }
        }

        return spans;
    }

    /// <summary>
    ///     Performs whole-word replacement of <paramref name="oldName" /> with <paramref name="newName" />
    ///     only within the specified disabled text spans. Processes spans in reverse order to preserve offsets.
    /// </summary>
    internal static (string NewText, int ReplacementCount) ReplaceInDisabledSpans(
        string sourceText, IReadOnlyList<TextSpan> disabledSpans, string oldName, string newName)
    {
        if (disabledSpans.Count == 0)
        {
            return (sourceText, 0);
        }

        // Escape the old name for use in regex, then wrap with word boundaries.
        var pattern = $@"\b{Regex.Escape(oldName)}\b";
        var regex = new Regex(pattern);
        var totalReplacements = 0;

        // Process in reverse order so earlier span offsets remain valid.
        var sb = new StringBuilder(sourceText);
        for (int i = disabledSpans.Count - 1; i >= 0; i--)
        {
            TextSpan span = disabledSpans[i];

            // Guard against spans that extend beyond the source text (e.g. stale syntax tree).
            if (span.Start >= sourceText.Length)
            {
                continue;
            }

            int end = Math.Min(span.End, sourceText.Length);
            string fragment = sourceText.Substring(span.Start, end - span.Start);
            var count = 0;
            string replaced = regex.Replace(fragment, _ =>
            {
                count++;
                return newName;
            });

            if (count > 0)
            {
                sb.Remove(span.Start, end - span.Start);
                sb.Insert(span.Start, replaced);
                totalReplacements += count;
            }
        }

        return (sb.ToString(), totalReplacements);
    }

    /// <summary>
    ///     Renames files associated with a type (Designer.cs, .resx) when the primary file is renamed.
    ///     Common in WinForms projects where types have paired designer and resource files.
    /// </summary>
    private async Task RenameAssociatedFilesAsync(
        string directory, string oldName, string newName, List<string> changedDocs, CancellationToken ct)
    {
        foreach (string suffix in AssociatedFileSuffixes)
        {
            string oldPath = Path.Combine(directory, $"{oldName}{suffix}");
            string newPath = Path.Combine(directory, $"{newName}{suffix}");

            // Only a real collision when there is a source to move AND the target is taken; if the
            // source is absent there is nothing to rename, so let the missing-source case below
            // (FileNotFoundException) skip silently rather than throwing on an unrelated target.
            if (File.Exists(oldPath) && File.Exists(newPath))
            {
                throw new UserErrorException(
                    $"Cannot rename '{Path.GetFileName(oldPath)}' to '{Path.GetFileName(newPath)}' — the target already exists in {directory}.");
            }

            try
            {
                await FileUtility.RunWithIoRetryAsync(() => File.Move(oldPath, newPath), ct);
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            changedDocs.Remove(workspaceManager.GetRelativePath(oldPath));
            changedDocs.Add(workspaceManager.GetRelativePath(newPath));
        }
    }
}
