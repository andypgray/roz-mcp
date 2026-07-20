using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;
using Zphil.Roz.Resources;
using Zphil.Roz.Symbols;
using Zphil.Roz.Utility;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace Zphil.Roz.Services;

/// <summary>
///     Service for Roslyn AST-based symbol editing: replace, remove, and insert declarations.
/// </summary>
internal sealed class SymbolEditService(WorkspaceManager workspaceManager, DiagnosticBaselineManager baselineManager, EditSymbolResolver symbolResolution, EditVerificationService verificationService, ILogger<SymbolEditService> logger)
{
    /// <summary>
    ///     Runs a batch of symbol edits sequentially. Per-op failures are captured as an
    ///     <see cref="EditSymbolErrorOp" /> result rather than aborting the batch; later ops still execute.
    /// </summary>
    /// <remarks>
    ///     With <see cref="VerifyMode.None" /> (the default), op N observes op N-1's changes on disk and
    ///     in the workspace (<see cref="WorkspaceManager.GetSolutionAsync" /> drains pending updates
    ///     before returning) — the code path is byte-identical to before this feature. With
    ///     <see cref="VerifyMode.Delta" />/<see cref="VerifyMode.DryRun" />, ops read and stage through an
    ///     <see cref="EditSession" /> fork instead, and <see cref="EditVerificationService.FinalizeAsync" />
    ///     computes the compiler-error delta once over the whole batch (committing first for
    ///     <see cref="VerifyMode.Delta" />). The staged batch commits atomically at the end, so a call
    ///     cancelled mid-batch writes nothing — under <see cref="VerifyMode.None" />, each completed op is
    ///     already on disk.
    /// </remarks>
    public async Task<EditSymbolBatchOutcome> EditSymbolBatchAsync(
        IReadOnlyList<EditSymbolRequest> edits, VerifyMode verify = VerifyMode.None,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        EditSession? session = verify == VerifyMode.None ? null : await EditSession.BeginAsync(workspaceManager, ct);

        List<EditSymbolOpResult> results = new(edits.Count);
        foreach (EditSymbolRequest req in edits)
        {
            ct.ThrowIfCancellationRequested();
            string displayPath = req.Location;
            try
            {
                // EDIT-1: a redundant `path:line` (no column) plus an explicit symbolName is not
                // ambiguous — `treatLineOnlyAsFile` normalizes it to path-only so it resolves by
                // name (preferName) instead of throwing. The parser still rejects bare `path:line`
                // when no symbolName is supplied.
                LocationArg loc = LocationParser.ParseFileOrCursor(
                    req.Location, "edit_symbol",
                    !String.IsNullOrEmpty(req.SymbolName));
                displayPath = workspaceManager.GetDisplayPath(loc.FilePath);
                results.Add(await DispatchAsync(req, loc, session, ct));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UserErrorException uex)
            {
                // Expected per-op input error (bad path, ambiguous overload, missing newDeclaration, etc.).
                // Wrap silently — read-batch fan-out in BatchOrSingle.RunAllAsync does the same.
                results.Add(new EditSymbolErrorOp(
                    displayPath, req.SymbolName, req.Action, uex.Message));
            }
            catch (Exception ex)
            {
                // Approved catch-all: write batches need per-op resilience so one bad edit doesn't
                // fault the whole batch. Log at Warning before wrapping so the "real bug" crash
                // signal isn't lost (ex.Message alone strips the stack trace).
                logger.LogWarning(ex, "edit_symbol op failed unexpectedly for symbol '{SymbolName}' at {Location}", req.SymbolName, req.Location);
                results.Add(new EditSymbolErrorOp(
                    displayPath, req.SymbolName, req.Action, ex.Message));
            }
        }

        // Delta computation runs outside the per-op try/catch: a faulting delta must fault the call so
        // the crash signal is preserved (a failed op stages nothing, so the delta covers only successes).
        EditVerification? verification = await verificationService.FinalizeAsync(session, verify, progress, ct);
        return new EditSymbolBatchOutcome(results, verification);
    }

    private async Task<EditSymbolOpResult> DispatchAsync(EditSymbolRequest req, LocationArg loc, EditSession? session, CancellationToken ct)
    {
        int? line = (loc as CursorLocation)?.Line;
        int? column = (loc as CursorLocation)?.Column;
        switch (req.Action)
        {
            case EditSymbolAction.Replace:
            {
                if (req.NewDeclaration is null)
                {
                    throw new UserErrorException("newDeclaration is required when action=replace.");
                }

                ReplaceSymbolResult r = await ReplaceSymbolAsync(
                    loc.FilePath, req.SymbolName, req.NewDeclaration,
                    line, column, ct, req.ContainingType, req.Kind, session);
                return new EditSymbolReplaceOp(loc.FilePath, req.SymbolName, r);
            }
            case EditSymbolAction.Remove:
            {
                RemoveSymbolResult r = await RemoveSymbolAsync(
                    loc.FilePath, req.SymbolName,
                    line, column, ct, req.ContainingType, req.Kind, session);
                return new EditSymbolRemoveOp(loc.FilePath, req.SymbolName, r);
            }
            case EditSymbolAction.Insert:
            {
                if (req.Content is null)
                {
                    throw new UserErrorException("content is required when action=insert.");
                }

                InsertResult r = await InsertRelativeToSymbolAsync(
                    loc.FilePath, req.SymbolName, req.Content,
                    req.Position == InsertPosition.After,
                    line, column, ct, req.ContainingType, req.Kind, session);
                return new EditSymbolInsertOp(loc.FilePath, req.SymbolName, r);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(req.Action), req.Action, "Unknown edit action.");
        }
    }

    /// <summary>
    ///     Replaces the entire declaration of a named symbol with new content,
    ///     preserving leading trivia (doc comments, blank lines, indentation) and encoding.
    /// </summary>
    public async Task<ReplaceSymbolResult> ReplaceSymbolAsync(
        string filePath, string symbolName, string newDeclaration,
        int? line = null, int? column = null, CancellationToken ct = default, string? containingType = null, SymbolicKind? kind = null,
        EditSession? session = null)
    {
        if (String.IsNullOrWhiteSpace(newDeclaration))
        {
            throw new UserErrorException("New declaration cannot be empty.");
        }

        baselineManager.ScheduleBaselineCaptureIfNeeded();
        ResolvedSymbolContext ctx = await symbolResolution.ResolveAsync(filePath, symbolName, line, column, ct, containingType, kind, true, session?.Fork);

        if (TryGetEnumMemberContext(ctx.TargetNode, out EnumMemberDeclarationSyntax enumMember, out EnumDeclarationSyntax parentEnum))
        {
            return await ReplaceEnumMemberAsync(ctx, parentEnum, enumMember, symbolName, newDeclaration, session, ct);
        }

        GuardMultiDeclaratorField(ctx.TargetNode, symbolName, "replace");

        int oldLineCount = TextUtility.CountLines(ctx.TargetNode.ToString());

        var parseOptions = ctx.Document.Project.ParseOptions as CSharpParseOptions;
        SyntaxNode? newNode = ParseDeclaration(ctx.TargetNode, newDeclaration, parseOptions);

        if (parseOptions is not null)
        {
            ValidateLanguageVersionCompatibility(
                newNode, parseOptions,
                opts => ParseDeclaration(ctx.TargetNode, newDeclaration, opts));
        }

        if (newNode is null)
        {
            throw new UserErrorException("Could not parse the new declaration as valid C#. Ensure the code is a complete, syntactically valid member or type declaration.");
        }

        ValidateStructuralCompatibility(ctx.TargetNode, newNode);

        string targetIndent = GetNodeIndentation(ctx.TargetNode);
        var replacedMarker = new SyntaxAnnotation("ReplacedNode");

        newNode = newNode
            .WithLeadingTrivia(ctx.TargetNode.GetLeadingTrivia())
            .WithTrailingTrivia(ResolveTrailingTrivia(newNode, ctx.TargetNode.GetTrailingTrivia()));

        newNode = newNode.WithAdditionalAnnotations(Formatter.Annotation, replacedMarker);

        SyntaxNode newRoot = ctx.Root.ReplaceNode(ctx.TargetNode, newNode);
        Document updatedDoc = ctx.Document.WithSyntaxRoot(newRoot);

        Document formattedDoc = await FormatAndNormalizeAsync(updatedDoc, replacedMarker, targetIndent, ct);

        (string originalContent, Encoding encoding) = await EditIo.ReadOriginalAsync(session, ctx.ResolvedPath, ct);

        SourceText formattedText = await formattedDoc.GetTextAsync(ct);
        string formattedContent = FileUtility.NormalizeLineEndings(formattedText.ToString(), originalContent);
        if (formattedContent == originalContent)
        {
            SourceText noOpText = await ctx.Document.GetTextAsync(ct);
            int noOpLine = noOpText.Lines.GetLinePosition(ctx.TargetNode.SpanStart).Line + 1;
            string noOpRelPath = workspaceManager.GetRelativePath(ctx.ResolvedPath);
            return new ReplaceSymbolResult(symbolName, oldLineCount, oldLineCount, noOpLine, noOpRelPath, ctx.TotalMatches, true);
        }

        await EditIo.WriteOrStageContentAsync(session, ctx.ResolvedPath, formattedContent, encoding, workspaceManager, ct);

        SourceText sourceText = await ctx.Document.GetTextAsync(ct);
        int startLine = sourceText.Lines.GetLinePosition(ctx.TargetNode.SpanStart).Line + 1;
        int newLineCount = TextUtility.CountLines(newDeclaration);
        string relPath = workspaceManager.GetRelativePath(ctx.ResolvedPath);

        return new ReplaceSymbolResult(symbolName, oldLineCount, newLineCount, startLine, relPath, ctx.TotalMatches);
    }

    /// <summary>
    ///     Removes a named symbol's entire declaration, including leading trivia
    ///     (doc comments, attributes, blank lines) and the trailing line ending.
    /// </summary>
    private async Task<RemoveSymbolResult> RemoveSymbolAsync(
        string filePath, string symbolName,
        int? line = null, int? column = null, CancellationToken ct = default, string? containingType = null, SymbolicKind? kind = null,
        EditSession? session = null)
    {
        baselineManager.ScheduleBaselineCaptureIfNeeded();
        ResolvedSymbolContext ctx = await symbolResolution.ResolveAsync(filePath, symbolName, line, column, ct, containingType, kind, true, session?.Fork);

        SyntaxNode targetNode = ctx.TargetNode;
        SourceText sourceText = await ctx.Document.GetTextAsync(ct);
        int startLine = sourceText.Lines.GetLinePosition(targetNode.SpanStart).Line + 1;

        if (TryGetEnumMemberContext(targetNode, out EnumMemberDeclarationSyntax enumMember, out EnumDeclarationSyntax parentEnum))
        {
            return await RemoveEnumMemberAsync(ctx, parentEnum, enumMember, startLine, session, ct);
        }

        GuardMultiDeclaratorField(targetNode, symbolName, "remove");

        // FullSpan includes leading trivia (doc comments, attributes, blank lines).
        TextSpan removeSpan = targetNode.FullSpan;

        // Shrink start to exclude file-level trivia (copyright headers, comment blocks)
        // that Roslyn attaches to the first token of the first member.
        int memberTriviaStart = GetMemberOwnedTriviaStart(targetNode, sourceText);
        if (memberTriviaStart > removeSpan.Start)
        {
            removeSpan = TextSpan.FromBounds(memberTriviaStart, removeSpan.End);
        }

        // Blank separator line handling: FullSpan includes leading trivia, which may start with
        // a blank line that visually separates this symbol from the previous one.
        // - If the span starts at a blank line AND there's a next sibling: preserve it (shrink past it).
        // - If Stage 2 already shrunk past a blank line that's within FullSpan and there's no
        //   next sibling: re-include it (no following member needs the separator).
        if (removeSpan.Start > 0)
        {
            int lineIndex = sourceText.Lines.GetLineFromPosition(removeSpan.Start).LineNumber;
            TextLine firstLine = sourceText.Lines[lineIndex];
            var firstLineText = sourceText.ToString(firstLine.Span);

            if (String.IsNullOrWhiteSpace(firstLineText))
            {
                // Advance past the blank line (including its line ending)
                removeSpan = TextSpan.FromBounds(firstLine.SpanIncludingLineBreak.End, removeSpan.End);
            }
            else if (lineIndex > 0)
            {
                // Stage 2 may have shrunk past a blank line — check the line before the span
                TextLine prevLine = sourceText.Lines[lineIndex - 1];
                var prevLineText = sourceText.ToString(prevLine.Span);

                if (String.IsNullOrWhiteSpace(prevLineText)
                    && prevLine.Start >= targetNode.FullSpan.Start)
                {
                    // The blank line is part of this member's FullSpan but was excluded by Stage 2.
                    // Include it only if there's no next sibling that needs it as a separator.
                    bool hasNextSibling = targetNode.Parent?.ChildNodes()
                        .SkipWhile(n => n != targetNode).Skip(1)
                        .Any(n => n is MemberDeclarationSyntax) == true;

                    if (!hasNextSibling)
                    {
                        removeSpan = TextSpan.FromBounds(prevLine.Start, removeSpan.End);
                    }
                }
            }
        }

        // Extend past the trailing EOL to avoid leaving a blank line where the symbol was.
        int endPos = removeSpan.End;
        if (endPos < sourceText.Length)
        {
            char ch = sourceText[endPos];
            if (ch == '\r' && endPos + 1 < sourceText.Length && sourceText[endPos + 1] == '\n')
            {
                endPos += 2;
            }
            else if (ch == '\n')
            {
                endPos += 1;
            }
        }

        removeSpan = TextSpan.FromBounds(removeSpan.Start, endPos);

        // Count lines from the actual removal span (includes doc comments, attributes, trailing EOL).
        // Count newline characters rather than using line-number arithmetic, which undercounts
        // by 1 when the span doesn't extend past the trailing EOL (e.g., last symbol in file).
        var removedText = sourceText.ToString(removeSpan);
        int removedLineCount = removedText.Count(c => c == '\n');
        if (removedLineCount == 0 && removeSpan.Length > 0)
        {
            removedLineCount = 1;
        }

        SourceText newText = sourceText.WithChanges(new TextChange(removeSpan, String.Empty));
        var newContent = newText.ToString();

        (string originalContent, Encoding encoding) = await EditIo.ReadOriginalAsync(session, ctx.ResolvedPath, ct);
        string result = FileUtility.NormalizeLineEndings(newContent, originalContent);
        await EditIo.WriteOrStageContentAsync(session, ctx.ResolvedPath, result, encoding, workspaceManager, ct);

        string relPath = workspaceManager.GetRelativePath(ctx.ResolvedPath);
        return new RemoveSymbolResult(symbolName, removedLineCount, startLine, relPath, ctx.TotalMatches);
    }

    /// <summary>
    ///     Rejects Remove/Replace of one declarator that shares a single field declaration with siblings
    ///     (e.g. <c>int _x, _y;</c>). <c>EditSymbolResolver</c> matches per-declarator but returns the whole
    ///     <see cref="BaseFieldDeclarationSyntax" />, so removing/replacing that node would silently take the
    ///     siblings too. Throwing is the conservative cure — per-declarator surgery (initializers, shared
    ///     attributes, comma cleanup) is a deferred feature, not a more-correct version of this fix. Insert
    ///     is intentionally not guarded: inserting near such a field never touches the shared declaration.
    /// </summary>
    private static void GuardMultiDeclaratorField(SyntaxNode targetNode, string symbolName, string action)
    {
        if (targetNode is not BaseFieldDeclarationSyntax field || field.Declaration.Variables.Count <= 1)
        {
            return;
        }

        var all = String.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.ValueText));
        var siblings = String.Join(", ", field.Declaration.Variables
            .Select(v => v.Identifier.ValueText)
            .Where(n => !String.Equals(n, symbolName, StringComparison.Ordinal)));

        throw new UserErrorException(
            $"Cannot {action} '{symbolName}': it shares one field declaration with {siblings} " +
            $"('{field.Declaration.Type} {all};'). Editing one declarator would affect the others. " +
            $"Split the field into separate declarations first, or rewrite the whole declaration with replace_content.");
    }

    /// <summary>
    ///     Inserts content before or after a named symbol's declaration.
    /// </summary>
    private async Task<InsertResult> InsertRelativeToSymbolAsync(
        string filePath, string symbolName, string content, bool after,
        int? line = null, int? column = null, CancellationToken ct = default, string? containingType = null, SymbolicKind? kind = null,
        EditSession? session = null)
    {
        baselineManager.ScheduleBaselineCaptureIfNeeded();
        if (String.IsNullOrEmpty(content))
        {
            throw new UserErrorException("Content cannot be empty.");
        }

        ResolvedSymbolContext ctx = await symbolResolution.ResolveAsync(filePath, symbolName, line, column, ct, containingType, kind, true, session?.Fork);

        if (TryGetEnumMemberContext(ctx.TargetNode, out EnumMemberDeclarationSyntax targetEnumMember, out EnumDeclarationSyntax targetParentEnum))
        {
            return await InsertEnumMemberAsync(ctx, targetParentEnum, targetEnumMember, symbolName, content, after, session, ct);
        }

        (string originalContent, Encoding encoding) = await EditIo.ReadOriginalAsync(session, ctx.ResolvedPath, ct);

        var parseOptions = ctx.Document.Project.ParseOptions as CSharpParseOptions;
        MemberDeclarationSyntax? newMember = TryParseMember(content, parseOptions);

        if (parseOptions is not null)
        {
            ValidateLanguageVersionCompatibility(
                newMember, parseOptions,
                opts => TryParseMember(content, opts));
        }

        Document resultDoc;
        if (newMember is not null)
        {
            resultDoc = await InsertMemberNode(ctx.Document, ctx.Root, ctx.TargetNode, newMember, after, ct);
        }
        else
        {
            SyntaxNode newRoot = InsertAsTrivia(ctx.Root, ctx.TargetNode, content, after);
            resultDoc = ctx.Document.WithSyntaxRoot(newRoot);
        }

        await EditIo.WriteOrStageDocumentAsync(session, ctx.ResolvedPath, resultDoc, originalContent, encoding, workspaceManager, ct);

        string relPath = workspaceManager.GetRelativePath(ctx.ResolvedPath);
        int lineCount = TextUtility.CountLines(content);
        return new InsertResult(symbolName, lineCount, after, relPath, ctx.TotalMatches);
    }

    internal static MemberDeclarationSyntax? TryParseMember(string content, CSharpParseOptions? parseOptions = null)
    {
        string trimmed = content.Trim();
        if (String.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        MemberDeclarationSyntax? member = SyntaxFactory.ParseMemberDeclaration(trimmed, options: parseOptions);
        return member is null || member.ContainsDiagnostics ? null : member;
    }

    internal static SyntaxNode? ParseDeclaration(SyntaxNode targetNode, string body, CSharpParseOptions? parseOptions = null)
    {
        string trimmed = body.Trim();

        if (targetNode is LocalFunctionStatementSyntax)
        {
            StatementSyntax statement = SyntaxFactory.ParseStatement(trimmed, options: parseOptions);
            return statement is LocalFunctionStatementSyntax && !statement.ContainsDiagnostics ? statement : null;
        }

        MemberDeclarationSyntax? member = SyntaxFactory.ParseMemberDeclaration(trimmed, options: parseOptions);
        return member is not null && !member.ContainsDiagnostics ? member : null;
    }

    /// <summary>
    ///     Detects when submitted code uses C# features not available in the target project's language version.
    ///     Catches two failure modes: (1) code that parses successfully with diagnostics in the target version
    ///     but cleanly in latest C#, and (2) code that silently parses as a different syntax node type
    ///     (e.g., <c>record</c> becoming a method name in C# 7.3).
    /// </summary>
    internal static void ValidateLanguageVersionCompatibility(
        SyntaxNode? targetVersionResult, CSharpParseOptions targetParseOptions,
        Func<CSharpParseOptions, SyntaxNode?> reparse)
    {
        CSharpParseOptions latestOptions = targetParseOptions.WithLanguageVersion(LanguageVersion.Latest);
        if (targetParseOptions.LanguageVersion == latestOptions.LanguageVersion)
        {
            // Target is already the latest version — no downlevel risk
            return;
        }

        SyntaxNode? latestResult = reparse(latestOptions);

        if (latestResult is null)
        {
            // Code is invalid even in latest C# — not a version issue
            return;
        }

        string targetVersion = targetParseOptions.LanguageVersion.ToDisplayString();

        if (targetVersionResult is null)
        {
            // Code parses in latest but not in the target version — extract diagnostics
            // by re-parsing with target options (retaining the diagnostic-bearing node).
            string diagnostics = ExtractDiagnosticsFromReparse(reparse, targetParseOptions);
            throw new UserErrorException(
                $"This code uses C# features not available in the target project (C# {targetVersion}):\n{diagnostics}\n"
                + $"Rewrite to be compatible with C# {targetVersion}, or update the project's LangVersion.");
        }

        if (targetVersionResult.GetType() != latestResult.GetType())
        {
            // Both parse but as different node types — silent misinterpretation
            string targetDesc = FriendlyNodeDescription(targetVersionResult);
            string latestDesc = FriendlyNodeDescription(latestResult);
            throw new UserErrorException(
                $"Language version conflict: this code is interpreted as {latestDesc} in latest C# "
                + $"but as {targetDesc} in C# {targetVersion} (target project). "
                + $"Rewrite to be compatible with C# {targetVersion}, or update the project's LangVersion.");
        }
    }

    /// <summary>
    ///     Re-parses using the same delegate as the original parse (respecting local function vs member paths)
    ///     but without the <c>ContainsDiagnostics</c> filter, so we can extract specific error messages.
    /// </summary>
    private static string ExtractDiagnosticsFromReparse(
        Func<CSharpParseOptions, SyntaxNode?> reparse, CSharpParseOptions targetParseOptions)
    {
        // The reparse delegate filters out diagnostic-bearing nodes (returns null).
        // Parse one more time without the filter by going through ParseMemberDeclaration directly.
        // This is only called on the error path so the extra parse is acceptable.
        // Note: we cannot avoid this extra parse because ParseDeclaration/TryParseMember discard
        // the node when ContainsDiagnostics is true, and we need that node for its diagnostics.

        // Try reparse first — if the delegate happens to return a node (shouldn't on this path), use it.
        SyntaxNode? node = reparse(targetParseOptions);
        if (node is not null)
        {
            IEnumerable<Diagnostic> diags = node.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
            var formatted = String.Join("\n", diags.Select(d => $"  {d.Id}: {d.GetMessage()}"));
            return String.IsNullOrEmpty(formatted) ? "  (no specific diagnostics available)" : formatted;
        }

        return "  (parse failed without specific diagnostics)";
    }

    private static string FriendlyNodeDescription(SyntaxNode node) =>
        node switch
        {
            RecordDeclarationSyntax => "a record declaration",
            ClassDeclarationSyntax => "a class declaration",
            StructDeclarationSyntax => "a struct declaration",
            InterfaceDeclarationSyntax => "an interface declaration",
            EnumDeclarationSyntax => "an enum declaration",
            MethodDeclarationSyntax => "a method declaration",
            PropertyDeclarationSyntax => "a property declaration",
            FieldDeclarationSyntax => "a field declaration",
            ConstructorDeclarationSyntax => "a constructor declaration",
            LocalFunctionStatementSyntax => "a local function",
            _ => node.GetType().Name
        };

    private static void ValidateStructuralCompatibility(SyntaxNode targetNode, SyntaxNode newNode)
    {
        if (targetNode is not TypeDeclarationSyntax)
        {
            return;
        }

        if (newNode is ConstructorDeclarationSyntax)
        {
            throw new UserErrorException(
                "The matched symbol is a type declaration but the replacement is a constructor. " +
                "This would replace the entire class/struct with just the constructor. " +
                "To target a constructor, use symbolName \".ctor\" (instance) or \".cctor\" (static). " +
                $"Special symbol names are documented in the {RozResources.EditingGuideUri} MCP resource.");
        }

        if (newNode is MemberDeclarationSyntax && newNode is not TypeDeclarationSyntax)
        {
            throw new UserErrorException(
                "The matched symbol is a type declaration but the replacement is a member declaration. " +
                "This would replace the entire class/struct/record with a single member. " +
                "Verify the symbolName targets the correct symbol.");
        }
    }

    private static async Task<Document> InsertMemberNode(
        Document document, SyntaxNode root, SyntaxNode targetNode,
        MemberDeclarationSyntax newMember, bool after, CancellationToken ct)
    {
        SyntaxTrivia eol = GetEndOfLineTrivia(root);
        string targetIndent = GetNodeIndentation(targetNode);
        var insertedMarker = new SyntaxAnnotation("InsertedNode");
        newMember = newMember
            .WithTrailingTrivia(ResolveTrailingTrivia(newMember, new SyntaxTriviaList(eol)))
            .WithAdditionalAnnotations(Formatter.Annotation, insertedMarker);

        SyntaxNode newRoot;
        if (after)
        {
            newRoot = root.InsertNodesAfter(targetNode, [newMember]);
        }
        else
        {
            (SyntaxTriviaList blankLines, SyntaxTriviaList meaningful) = SplitLeadingTrivia(targetNode);

            if (blankLines.Count > 0)
            {
                newMember = newMember.WithLeadingTrivia(blankLines);
                var marker = new SyntaxAnnotation();
                SyntaxNode modifiedTarget = targetNode.WithLeadingTrivia(meaningful)
                    .WithAdditionalAnnotations(marker);
                SyntaxNode intermediateRoot = root.ReplaceNode(targetNode, modifiedTarget);
                SyntaxNode trackedTarget = intermediateRoot.GetAnnotatedNodes(marker).Single();
                newRoot = intermediateRoot.InsertNodesBefore(trackedTarget, [newMember]);
            }
            else
            {
                newRoot = root.InsertNodesBefore(targetNode, [newMember]);
            }
        }

        Document updatedDoc = document.WithSyntaxRoot(newRoot);
        Document formattedDoc = await Formatter.FormatAsync(
            updatedDoc, Formatter.Annotation, cancellationToken: ct);

        Document indentedDoc = await NormalizeInsertedIndentationAsync(formattedDoc, insertedMarker, targetIndent, ct);
        return await EnsureBlankLineSeparatorAsync(indentedDoc, insertedMarker, after, ct);
    }

    /// <summary>
    ///     Removes an enum member from its parent enum, handling comma separator cleanup.
    /// </summary>
    private async Task<RemoveSymbolResult> RemoveEnumMemberAsync(
        ResolvedSymbolContext ctx, EnumDeclarationSyntax parentEnum,
        EnumMemberDeclarationSyntax enumMember, int startLine, EditSession? session, CancellationToken ct)
    {
        int memberIndex = parentEnum.Members.IndexOf(enumMember);
        SyntaxNodeOrTokenList flat = parentEnum.Members.GetWithSeparators();
        int flatIndex = memberIndex * 2; // elements live at even indices

        flat = flat.RemoveAt(flatIndex);

        // Remove exactly one associated separator, chosen to preserve any trailing comma.
        // - Non-last element: drop the separator that WAS after it (now sits at flatIndex).
        // - Last element (not only): drop the separator BEFORE it — this keeps the original
        //   trailing comma (if any) attached to the new last element.
        // - Only element: sweep any remaining trailing separator tokens.
        if (parentEnum.Members.Count == 1)
        {
            while (flat.Count > 0 && flat[flat.Count - 1].IsToken)
            {
                flat = flat.RemoveAt(flat.Count - 1);
            }
        }
        else if (memberIndex == parentEnum.Members.Count - 1)
        {
            flat = flat.RemoveAt(flatIndex - 1);
        }
        else
        {
            flat = flat.RemoveAt(flatIndex);
        }

        SeparatedSyntaxList<EnumMemberDeclarationSyntax> newMembers =
            SyntaxFactory.SeparatedList<EnumMemberDeclarationSyntax>(flat);
        EnumDeclarationSyntax newEnum = parentEnum.WithMembers(newMembers)
            .WithAdditionalAnnotations(Formatter.Annotation);
        SyntaxNode newRoot = ctx.Root.ReplaceNode(parentEnum, newEnum);

        await FormatAndWriteAsync(ctx, newRoot, session, ct);

        // ToString() (not ToFullString()) so leading/trailing trivia — blank lines, the comma
        // separator's newline — isn't counted as removed source lines (matches the :605 sibling).
        int removedLineCount = TextUtility.CountLines(enumMember.ToString());
        string relPath = workspaceManager.GetRelativePath(ctx.ResolvedPath);
        return new RemoveSymbolResult(enumMember.Identifier.Text, removedLineCount, startLine, relPath, ctx.TotalMatches);
    }

    /// <summary>
    ///     Replaces an enum member with new content, handling comma separator preservation.
    /// </summary>
    private async Task<ReplaceSymbolResult> ReplaceEnumMemberAsync(
        ResolvedSymbolContext ctx, EnumDeclarationSyntax parentEnum,
        EnumMemberDeclarationSyntax oldMember, string symbolName,
        string newDeclaration, EditSession? session, CancellationToken ct)
    {
        List<EnumMemberDeclarationSyntax> parsedMembers = ParseEnumMembers(newDeclaration);
        if (parsedMembers.Count != 1)
        {
            throw new UserErrorException(
                "Could not parse the new declaration as a single enum member. Ensure the content is a valid enum member declaration (e.g., 'MyValue = 1').");
        }

        string targetIndent = GetNodeIndentation(oldMember);
        var replacedMarker = new SyntaxAnnotation("ReplacedEnumMember");

        EnumMemberDeclarationSyntax newMember = parsedMembers[0]
            .WithLeadingTrivia(oldMember.GetLeadingTrivia())
            .WithTrailingTrivia(ResolveTrailingTrivia(parsedMembers[0], oldMember.GetTrailingTrivia()))
            .WithAdditionalAnnotations(replacedMarker);

        int oldLineCount = TextUtility.CountLines(oldMember.ToString());
        SourceText sourceText = await ctx.Document.GetTextAsync(ct);
        int startLine = sourceText.Lines.GetLinePosition(oldMember.SpanStart).Line + 1;

        SeparatedSyntaxList<EnumMemberDeclarationSyntax> newMembers =
            parentEnum.Members.Replace(oldMember, newMember);
        EnumDeclarationSyntax newEnum = parentEnum.WithMembers(newMembers)
            .WithAdditionalAnnotations(Formatter.Annotation);
        SyntaxNode newRoot = ctx.Root.ReplaceNode(parentEnum, newEnum);

        Document updatedDoc = ctx.Document.WithSyntaxRoot(newRoot);
        Document formattedDoc = await FormatAndNormalizeAsync(updatedDoc, replacedMarker, targetIndent, ct);

        (string originalContent, Encoding encoding) = await EditIo.ReadOriginalAsync(session, ctx.ResolvedPath, ct);

        SourceText formattedText = await formattedDoc.GetTextAsync(ct);
        string formattedContent = FileUtility.NormalizeLineEndings(formattedText.ToString(), originalContent);
        if (formattedContent == originalContent)
        {
            string noOpRelPath = workspaceManager.GetRelativePath(ctx.ResolvedPath);
            return new ReplaceSymbolResult(symbolName, oldLineCount, oldLineCount, startLine, noOpRelPath, ctx.TotalMatches, true);
        }

        await EditIo.WriteOrStageContentAsync(session, ctx.ResolvedPath, formattedContent, encoding, workspaceManager, ct);

        int newLineCount = TextUtility.CountLines(newDeclaration);
        string relPath = workspaceManager.GetRelativePath(ctx.ResolvedPath);
        return new ReplaceSymbolResult(symbolName, oldLineCount, newLineCount, startLine, relPath, ctx.TotalMatches);
    }

    /// <summary>
    ///     Inserts content as enum member(s) before or after an existing enum member.
    /// </summary>
    private async Task<InsertResult> InsertEnumMemberAsync(
        ResolvedSymbolContext ctx, EnumDeclarationSyntax parentEnum,
        EnumMemberDeclarationSyntax targetMember, string symbolName, string content,
        bool after, EditSession? session, CancellationToken ct)
    {
        List<EnumMemberDeclarationSyntax> parsedMembers = ParseEnumMembers(content);
        if (parsedMembers.Count == 0)
        {
            throw new UserErrorException(
                "Could not parse the content as enum member(s). Ensure the content is a valid enum member declaration (e.g., 'MyValue = 1').");
        }

        string targetIndent = GetNodeIndentation(targetMember);
        var insertedMarker = new SyntaxAnnotation("InsertedEnumMember");
        parsedMembers = parsedMembers
            .Select(m => m.WithAdditionalAnnotations(insertedMarker))
            .ToList();

        SeparatedSyntaxList<EnumMemberDeclarationSyntax> members = parentEnum.Members;
        int insertIndex = members.IndexOf(targetMember);
        if (after)
        {
            insertIndex++;
        }

        bool hadTrailingSeparator = members.Count > 0 && members.SeparatorCount == members.Count;

        SeparatedSyntaxList<EnumMemberDeclarationSyntax> newMembers =
            members.InsertRange(insertIndex, parsedMembers);

        // When the original enum had a trailing comma and the insert landed at the end,
        // Roslyn's InsertRange reuses the trailing separator as the between-separator for
        // the newly-appended element, dropping the "trailing comma" state. Restore it so
        // insert + remove round-trips preserve the original byte layout.
        if (hadTrailingSeparator && newMembers.SeparatorCount < newMembers.Count)
        {
            SyntaxNodeOrTokenList flat = newMembers.GetWithSeparators();
            flat = flat.Add(SyntaxFactory.Token(SyntaxKind.CommaToken));
            newMembers = SyntaxFactory.SeparatedList<EnumMemberDeclarationSyntax>(flat);
        }

        EnumDeclarationSyntax newEnum = parentEnum.WithMembers(newMembers)
            .WithAdditionalAnnotations(Formatter.Annotation);
        SyntaxNode newRoot = ctx.Root.ReplaceNode(parentEnum, newEnum);

        Document updatedDoc = ctx.Document.WithSyntaxRoot(newRoot);
        Document formattedDoc = await FormatAndNormalizeAsync(updatedDoc, insertedMarker, targetIndent, ct);

        (string originalContent, Encoding encoding) = await EditIo.ReadOriginalAsync(session, ctx.ResolvedPath, ct);
        await EditIo.WriteOrStageDocumentAsync(session, ctx.ResolvedPath, formattedDoc, originalContent, encoding, workspaceManager, ct);

        string relPath = workspaceManager.GetRelativePath(ctx.ResolvedPath);
        int lineCount = TextUtility.CountLines(content);
        return new InsertResult(symbolName, lineCount, after, relPath, ctx.TotalMatches);
    }

    /// <summary>
    ///     Enum members live in a SeparatedSyntaxList where comma separators are separate
    ///     tokens, so they can't be handled by the standard MemberDeclarationSyntax path.
    /// </summary>
    private static bool TryGetEnumMemberContext(
        SyntaxNode targetNode,
        out EnumMemberDeclarationSyntax enumMember,
        out EnumDeclarationSyntax parentEnum)
    {
        if (targetNode is EnumMemberDeclarationSyntax member
            && member.Parent is EnumDeclarationSyntax parent)
        {
            enumMember = member;
            parentEnum = parent;
            return true;
        }

        enumMember = null!;
        parentEnum = null!;
        return false;
    }

    /// <summary>
    ///     Formats a modified syntax root (scoped to Formatter.Annotation) and writes it to disk (or
    ///     stages it into <paramref name="session" />'s fork when a verified edit is in progress).
    /// </summary>
    private async Task FormatAndWriteAsync(ResolvedSymbolContext ctx, SyntaxNode newRoot, EditSession? session, CancellationToken ct)
    {
        Document updatedDoc = ctx.Document.WithSyntaxRoot(newRoot);
        Document formattedDoc = await Formatter.FormatAsync(
            updatedDoc, Formatter.Annotation, cancellationToken: ct);

        (string originalContent, Encoding encoding) = await EditIo.ReadOriginalAsync(session, ctx.ResolvedPath, ct);
        await EditIo.WriteOrStageDocumentAsync(session, ctx.ResolvedPath, formattedDoc, originalContent, encoding, workspaceManager, ct);
    }

    /// <summary>
    ///     Formats annotated nodes (scoped via Formatter.Annotation) and normalizes their indentation
    ///     to match the target symbol's indentation. Returns the formatted document without writing.
    /// </summary>
    private static async Task<Document> FormatAndNormalizeAsync(
        Document document, SyntaxAnnotation marker, string targetIndent, CancellationToken ct)
    {
        Document formattedDoc = await Formatter.FormatAsync(
            document, Formatter.Annotation, cancellationToken: ct);
        return await NormalizeInsertedIndentationAsync(formattedDoc, marker, targetIndent, ct);
    }

    /// <summary>
    ///     Parses content as enum member(s) by wrapping in a temporary enum declaration.
    /// </summary>
    private static List<EnumMemberDeclarationSyntax> ParseEnumMembers(string content)
    {
        string trimmed = content.Trim().TrimEnd(',');

        // Newline after '{' ensures doc comments parse as leading trivia of the member,
        // not trailing trivia of the opening brace.
        var wrapper = $"enum Temp {{\n{trimmed}\n}}";
        MemberDeclarationSyntax? parsed = SyntaxFactory.ParseMemberDeclaration(wrapper);

        if (parsed is not EnumDeclarationSyntax tempEnum || tempEnum.Members.Count == 0)
        {
            return [];
        }

        return tempEnum.Members
            .Select(m => m.WithTrailingTrivia(SyntaxFactory.TriviaList()))
            .ToList();
    }

    /// <summary>
    ///     Ensures a blank line separates the inserted member from its neighbor.
    ///     For insert-after: blank line between the target's closing and the inserted member's start.
    ///     For insert-before: blank line between the inserted member's end and the target's start.
    /// </summary>
    private static async Task<Document> EnsureBlankLineSeparatorAsync(
        Document document, SyntaxAnnotation insertedMarker, bool after, CancellationToken ct)
    {
        SourceText sourceText = await document.GetTextAsync(ct);
        SyntaxNode? root = await document.GetSyntaxRootAsync(ct);
        if (root is null)
        {
            return document;
        }

        SyntaxNode? insertedNode = root.GetAnnotatedNodes(insertedMarker).FirstOrDefault();
        if (insertedNode is null)
        {
            return document;
        }

        // Find the line boundary between the inserted member and its neighbor
        int insertedStart = sourceText.Lines.GetLineFromPosition(insertedNode.FullSpan.Start).LineNumber;
        int insertedEnd = sourceText.Lines.GetLineFromPosition(insertedNode.Span.End).LineNumber;

        // Determine the boundary line to check for a blank separator
        int boundaryLine = after ? insertedStart : insertedEnd;

        // Check the line before (insert-after) or after (insert-before) for blank line
        int checkLine = after ? boundaryLine - 1 : boundaryLine + 1;
        if (checkLine < 0 || checkLine >= sourceText.Lines.Count)
        {
            return document;
        }

        var lineText = sourceText.Lines[checkLine].ToString();
        if (String.IsNullOrWhiteSpace(lineText))
        {
            return document; // blank line already exists
        }

        // Insert a blank line at the boundary
        int insertPosition = after
            ? sourceText.Lines[boundaryLine].Start
            : sourceText.Lines[checkLine].End;

        var eolText = GetEndOfLineTrivia(root).ToString();

        SourceText newText = sourceText.WithChanges(new TextChange(new TextSpan(insertPosition, 0), eolText));
        return document.WithText(newText);
    }

    /// <summary>
    ///     After Roslyn's formatter runs, inserted/replaced nodes may have incorrect indentation
    ///     (e.g., indented 4 spaces in a file-scoped namespace where types are at column 0).
    ///     This method detects the mismatch and normalizes all annotated nodes' indentation
    ///     to match the target symbol's indentation.
    /// </summary>
    private static async Task<Document> NormalizeInsertedIndentationAsync(
        Document document, SyntaxAnnotation insertedMarker, string targetIndent, CancellationToken ct)
    {
        SyntaxNode? formattedRoot = await document.GetSyntaxRootAsync(ct);
        if (formattedRoot is null)
        {
            return document;
        }

        List<SyntaxNode> annotatedNodes = formattedRoot.GetAnnotatedNodes(insertedMarker).ToList();
        if (annotatedNodes.Count == 0)
        {
            return document;
        }

        // Use the first annotated node to detect the formatter's applied indentation
        string insertedIndent = GetNodeIndentation(annotatedNodes[0]);
        if (insertedIndent == targetIndent)
        {
            return document;
        }

        SourceText sourceText = await document.GetTextAsync(ct);
        List<TextChange> changes = new();

        foreach (SyntaxNode insertedNode in annotatedNodes)
        {
            TextSpan fullSpan = insertedNode.FullSpan;
            int startLine = sourceText.Lines.GetLineFromPosition(fullSpan.Start).LineNumber;
            int endLine = sourceText.Lines.GetLineFromPosition(fullSpan.End).LineNumber;

            // If the end position is at the start of a line, don't include that line
            if (fullSpan.End == sourceText.Lines[endLine].Start)
            {
                endLine--;
            }

            for (int i = startLine; i <= endLine; i++)
            {
                TextLine line = sourceText.Lines[i];
                var lineText = line.ToString();
                if (String.IsNullOrWhiteSpace(lineText))
                {
                    continue;
                }

                // Skip lines whose start resolves into a string-literal token: their leading whitespace
                // is the token's content (verbatim @"..." interior) or the dedent-defining closing
                // delimiter of a raw """...""" literal, so prefix-swapping it would change the string
                // value. The resulting cosmetic oddity (signature reindented, literal block keeps the
                // formatter's indent) is valid C# and semantically identical — value > cosmetics.
                SyntaxToken tokenAtLineStart = formattedRoot.FindToken(line.Start);
                if (RoslynExtensions.IsStringLiteralKind(tokenAtLineStart.Kind()))
                {
                    continue;
                }

                if (lineText.StartsWith(insertedIndent))
                {
                    string remainder = lineText[insertedIndent.Length..];
                    string newLineText = targetIndent + remainder;
                    changes.Add(new TextChange(line.Span, newLineText));
                }
            }
        }

        if (changes.Count == 0)
        {
            return document;
        }

        SourceText newText = sourceText.WithChanges(changes);
        return document.WithText(newText);
    }

    /// <summary>
    ///     Returns trailing trivia from <paramref name="newNode" /> when it contains inline comments
    ///     (which are part of the user's new code), otherwise falls back to <paramref name="fallback" />
    ///     (preserving EOL style, #endregion, etc.).
    /// </summary>
    private static SyntaxTriviaList ResolveTrailingTrivia(SyntaxNode newNode, SyntaxTriviaList fallback)
    {
        SyntaxTriviaList newTrailing = newNode.GetTrailingTrivia();
        bool hasComment = newTrailing.Any(t =>
            t.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
            t.IsKind(SyntaxKind.MultiLineCommentTrivia));
        return hasComment ? newTrailing : fallback;
    }

    /// <summary>
    ///     Extracts the whitespace indentation string from a node's leading trivia.
    ///     Returns the whitespace immediately before the node's first token on its line.
    /// </summary>
    private static string GetNodeIndentation(SyntaxNode node)
    {
        SyntaxTriviaList leading = node.GetLeadingTrivia();

        // Walk backwards from the end of leading trivia to find the last whitespace before the node
        for (int i = leading.Count - 1; i >= 0; i--)
        {
            if (leading[i].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                return leading[i].ToString();
            }

            if (leading[i].IsKind(SyntaxKind.EndOfLineTrivia))
            {
                // End of line with no following whitespace means column 0
                return String.Empty;
            }
        }

        // No trivia at all means column 0
        return String.Empty;
    }

    private static SyntaxNode InsertAsTrivia(
        SyntaxNode root, SyntaxNode targetNode, string content, bool after)
    {
        SyntaxTriviaList contentTrivia = SyntaxFactory.ParseLeadingTrivia(content);

        // ParseLeadingTrivia consumes only a leading run of trivia and silently stops at the first
        // real token, so malformed content like "public void Broken( {" (which is not a parseable
        // member either) lexes to zero/partial trivia and would be appended as a byte-identical or
        // truncated edit reported as success. The parsed trivia's full text is always a prefix of the
        // input; if it doesn't cover the whole input, real code was dropped — reject. Ordinal compare
        // because trivia preserves exact CRLF/LF bytes (a CRLF comment block round-trips byte-exactly).
        if (!String.Equals(contentTrivia.ToFullString(), content, StringComparison.Ordinal))
        {
            throw new UserErrorException(
                "Insert content is neither a valid member declaration nor pure trivia " +
                "(comments / whitespace / #region). Provide a complete, syntactically valid member, " +
                "or insert only comments/whitespace.");
        }

        if (contentTrivia.Count > 0 && !content.EndsWith('\n'))
        {
            contentTrivia = contentTrivia.Add(GetEndOfLineTrivia(root));
        }

        if (after)
        {
            SyntaxTriviaList newTrailing = targetNode.GetTrailingTrivia().AddRange(contentTrivia);
            return root.ReplaceNode(targetNode, targetNode.WithTrailingTrivia(newTrailing));
        }

        (SyntaxTriviaList blankLines, SyntaxTriviaList meaningful) = SplitLeadingTrivia(targetNode);
        IEnumerable<SyntaxTrivia> combinedTrivia = blankLines.Concat(contentTrivia).Concat(meaningful);
        SyntaxTriviaList newLeading = SyntaxFactory.TriviaList(combinedTrivia);
        return root.ReplaceNode(targetNode, targetNode.WithLeadingTrivia(newLeading));
    }

    private static SyntaxTrivia GetEndOfLineTrivia(SyntaxNode root)
    {
        SyntaxTrivia existing = root.DescendantTrivia()
            .FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        return existing.Span.Length > 0 ? existing : SyntaxFactory.CarriageReturnLineFeed;
    }

    private static (SyntaxTriviaList blankLines, SyntaxTriviaList meaningful) SplitLeadingTrivia(SyntaxNode node)
    {
        SyntaxTriviaList leading = node.GetLeadingTrivia();
        var splitIndex = 0;

        for (var i = 0; i < leading.Count; i++)
        {
            if (leading[i].IsKind(SyntaxKind.EndOfLineTrivia))
            {
                splitIndex = i + 1;
            }
            else if (!leading[i].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                break;
            }
        }

        SyntaxTriviaList blankLines = SyntaxFactory.TriviaList(leading.Take(splitIndex));
        SyntaxTriviaList meaningful = SyntaxFactory.TriviaList(leading.Skip(splitIndex));
        return (blankLines, meaningful);
    }

    /// <summary>
    ///     Returns the source position where the member's own leading trivia begins.
    ///     Trivia before this point (copyright headers, comment blocks, blank separator
    ///     lines) belongs to the file or enclosing type and must not be removed.
    /// </summary>
    private static int GetMemberOwnedTriviaStart(SyntaxNode node, SourceText sourceText)
    {
        SyntaxTriviaList leading = node.GetLeadingTrivia();

        for (var i = 0; i < leading.Count; i++)
        {
            SyntaxTrivia trivia = leading[i];
            if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                int lineNumber = sourceText.Lines.GetLineFromPosition(trivia.FullSpan.Start).LineNumber;
                return sourceText.Lines[lineNumber].Start;
            }
        }

        int declarationLineNumber = sourceText.Lines.GetLineFromPosition(node.SpanStart).LineNumber;
        return sourceText.Lines[declarationLineNumber].Start;
    }
}
