using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;
using Zphil.Roz.Symbols;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Services;

/// <summary>
///     Service for literal and regex text find-and-replace operations in source files.
/// </summary>
internal sealed class TextReplacementService(WorkspaceManager workspaceManager, DiagnosticBaselineManager baselineManager, EditVerificationService verificationService)
{
    private const int MaxRegexMatchLineSpan = 50;
    private const int MaxMatchCount = 100;

    private static readonly Regex EscapeSequencePattern = new(@"\\(.)");

    /// <summary>
    ///     Runs a batch of replacement operations sequentially. Per-op failures are captured into
    ///     <see cref="ReplaceContentResult.Error" /> rather than aborting the batch; later ops still execute.
    /// </summary>
    /// <remarks>
    ///     With <see cref="VerifyMode.None" /> (default) the code path is byte-identical to before this
    ///     feature. With <see cref="VerifyMode.Delta" />/<see cref="VerifyMode.DryRun" />, ops read and
    ///     stage through an <see cref="EditSession" /> fork and
    ///     <see cref="EditVerificationService.FinalizeAsync" /> reports the batch-level compiler-error delta.
    ///     Session batches commit atomically at the end — a call cancelled mid-batch writes nothing.
    /// </remarks>
    public async Task<ReplaceContentBatchOutcome> ReplaceContentBatchAsync(
        IReadOnlyList<ReplaceContentRequest> edits, VerifyMode verify = VerifyMode.None,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        EditSession? session = verify == VerifyMode.None ? null : await EditSession.BeginAsync(workspaceManager, ct);

        List<ReplaceContentResult> results = new(edits.Count);
        foreach (ReplaceContentRequest req in edits)
        {
            ct.ThrowIfCancellationRequested();
            string displayPath = req.FilePath;
            try
            {
                FileLocation loc = LocationParser.ParseFile(req.FilePath, "replace_content");
                displayPath = workspaceManager.GetDisplayPath(loc.FilePath);
                ReplaceContentResult r = await ReplaceContentAsync(
                    loc.FilePath, req.Search, req.Replace, req.IsRegex, req.Singleline, session, ct);
                results.Add(r);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new ReplaceContentResult(
                    0,
                    displayPath,
                    null,
                    false,
                    ex.Message,
                    req.Search));
            }
        }

        // Delta computation runs outside the per-op try/catch: a faulting delta faults the call.
        EditVerification? verification = await verificationService.FinalizeAsync(session, verify, progress, ct);
        return new ReplaceContentBatchOutcome(results, verification);
    }

    /// <summary>
    ///     Finds and replaces text in a file with CRLF-safe matching.
    /// </summary>
    private async Task<ReplaceContentResult> ReplaceContentAsync(
        string filePath, string search, string replace, bool isRegex, bool singleline,
        EditSession? session, CancellationToken ct)
    {
        if (singleline && !isRegex)
        {
            throw new UserErrorException("The singleline option requires isRegex to be true.");
        }

        if (String.IsNullOrEmpty(search))
        {
            throw new UserErrorException("search must not be empty.");
        }

        baselineManager.ScheduleBaselineCaptureIfNeeded();
        string resolvedPath = await FilePathResolver.ResolveAgainstSolutionAsync(filePath, workspaceManager, ct);

        // Skip the mid-batch freshness sync under a session — it would reload from disk (lacking a
        // prior op's un-committed edit) and diverge from the fork. Reads come from the session (staged
        // content wins) so op N still sees op N-1's change on the same file.
        if (session is null)
        {
            await workspaceManager.EnsureFilesFreshAsync([resolvedPath], ct);
        }

        (string fileContent, Encoding encoding) = await EditIo.ReadOriginalAsync(session, resolvedPath, ct);

        string normalized = fileContent.Replace("\r\n", "\n");
        string normalizedSearch = search.Replace("\r\n", "\n");
        string normalizedReplace = replace.Replace("\r\n", "\n");

        // In regex mode, unescape \n, \t, and \\ in the replacement string so they produce
        // actual newlines/tabs/backslashes. .NET's match.Result() only interprets $1/$2
        // substitutions, not C-style escape sequences — but users expect \n to mean newline
        // since the search pattern documents "Use \n for newlines in multi-line patterns."
        // A single left-to-right pass over \\(.) is required: chained .Replace calls would let
        // an earlier \n→newline rewrite consume the backslash of a literal \\n before \\→\ runs.
        if (isRegex)
        {
            normalizedReplace = EscapeSequencePattern.Replace(normalizedReplace, static m => m.Groups[1].Value switch
            {
                "n" => "\n",
                "t" => "\t",
                "\\" => "\\",
                _ => m.Value // unknown escape (e.g. \d): leave verbatim — preserves prior behavior
            });
        }

        // Early exit for literal no-op (search == replace) — skip counting, formatter ignores it
        if (!isRegex && normalizedSearch == normalizedReplace)
        {
            string noOpRelPath = workspaceManager.GetRelativePath(resolvedPath);
            return new ReplaceContentResult(0, noOpRelPath, IsNoOp: true, Search: search);
        }

        string result;
        int matchCount;
        List<int> matchLineNumbers;

        if (isRegex)
        {
            Regex regex;
            try
            {
                RegexOptions options = RegexOptions.Compiled | RegexOptions.Multiline;
                if (singleline)
                {
                    options |= RegexOptions.Singleline;
                }

                regex = new Regex(normalizedSearch, options, TimeSpan.FromSeconds(5));
            }
            catch (RegexParseException ex)
            {
                throw new UserErrorException($"Invalid regex pattern: {ex.Message}", ex);
            }

            // Pre-validate matches before applying replacements
            MatchCollection matches = regex.Matches(normalized);
            matchCount = matches.Count;

            if (matchCount > MaxMatchCount)
            {
                throw new UserErrorException(
                    $"Pattern matched {matchCount} times (limit: {MaxMatchCount}). " +
                    "Verify the pattern is correct, or perform replacements in smaller batches.");
            }

            var longestMatchLines = 0;
            foreach (Match match in matches)
            {
                int matchLines = TextUtility.CountLines(match.Value);
                if (matchLines > longestMatchLines)
                {
                    longestMatchLines = matchLines;
                }
            }

            if (longestMatchLines > MaxRegexMatchLineSpan)
            {
                throw new UserErrorException(
                    $"A regex match spanned {longestMatchLines} lines (limit: {MaxRegexMatchLineSpan}). " +
                    "This likely indicates an overly greedy pattern. Refine the pattern or use singleline=false.");
            }

            matchLineNumbers = GetRegexMatchLineNumbers(normalized, matches);
            result = regex.Replace(normalized, match => match.Result(normalizedReplace));
        }
        else
        {
            matchCount = CountOccurrences(normalized, normalizedSearch);

            if (matchCount > MaxMatchCount)
            {
                throw new UserErrorException(
                    $"Pattern matched {matchCount} times (limit: {MaxMatchCount}). " +
                    "Verify the pattern is correct, or perform replacements in smaller batches.");
            }

            matchLineNumbers = GetMatchLineNumbers(normalized, normalizedSearch);
            result = normalized.Replace(normalizedSearch, normalizedReplace);
        }

        if (matchCount == 0)
        {
            throw new UserErrorException($"No matches found for the search text in {filePath}. Verify the exact text — whitespace and casing must match (line endings are normalized automatically). Read the file first to confirm the content.");
        }

        // Regex no-op: pattern matched but replacement produced identical content
        if (result == normalized)
        {
            string noOpRelPath = workspaceManager.GetRelativePath(resolvedPath);
            return new ReplaceContentResult(matchCount, noOpRelPath, matchLineNumbers, true, Search: search);
        }

        if (String.IsNullOrWhiteSpace(result) && !String.IsNullOrWhiteSpace(normalized))
        {
            string hint = isRegex
                ? "This usually indicates an overly greedy regex pattern. Refine the pattern."
                : "The search text matched the entire file content.";
            throw new UserErrorException($"Replacement would leave {filePath} empty. {hint}");
        }

        result = FileUtility.NormalizeLineEndings(result, fileContent);

        await EditIo.WriteOrStageContentAsync(session, resolvedPath, result, encoding, workspaceManager, ct);

        string relPath = workspaceManager.GetRelativePath(resolvedPath);
        return new ReplaceContentResult(matchCount, relPath, matchLineNumbers, Search: search);
    }

    private static int CountOccurrences(string text, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += search.Length;
        }

        return count;
    }

    private static List<int> GetMatchLineNumbers(string content, string search)
    {
        List<int> lines = new();
        var lineNumber = 1;
        var lastIndex = 0;
        var index = 0;
        while ((index = content.IndexOf(search, index, StringComparison.Ordinal)) != -1)
        {
            lineNumber += content.AsSpan(lastIndex, index - lastIndex).Count('\n');
            lines.Add(lineNumber);
            index += search.Length;
            lastIndex = index;
        }

        return lines;
    }

    private static List<int> GetRegexMatchLineNumbers(string content, MatchCollection matches)
    {
        List<int> lines = new(matches.Count);
        var lineNumber = 1;
        var lastIndex = 0;
        foreach (Match match in matches)
        {
            lineNumber += content.AsSpan(lastIndex, match.Index - lastIndex).Count('\n');
            lines.Add(lineNumber);
            lastIndex = match.Index + match.Length;
        }

        return lines;
    }
}
