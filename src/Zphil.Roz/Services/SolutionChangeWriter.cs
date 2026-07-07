using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Services;

/// <summary>
///     Diffs two immutable <see cref="Solution" /> snapshots, atomically writes every changed
///     document to disk, and notifies the workspace. The persist half of the fork-side edit contract:
///     <see cref="RenameService" /> (semantic rename) and <see cref="CodeFixService" /> (FixAll) both
///     reach it through <see cref="EditVerificationService.FinalizeForkAsync" /> rather than calling it
///     directly.
/// </summary>
/// <remarks>
///     Originally extracted from <see cref="RenameService" /> so a second consumer reuses the
///     diff-write-notify path rather than copying it, then hardened to normalize each changed
///     document's line endings back to its pre-edit form: Roslyn's FixAll/Formatter synthesize
///     <see cref="Environment.NewLine" /> (LF on Linux), which written verbatim into a CRLF user
///     file yields mixed endings. The multi-TFM dedupe (one physical file owns several
///     <see cref="DocumentId" />s) and the empty-doc skip are load-bearing — preserve exactly.
/// </remarks>
internal static class SolutionChangeWriter
{
    /// <summary>
    ///     Collects the post-edit content of every changed document, in memory, without touching disk.
    ///     Each document's line endings are normalized to its pre-edit form so synthesized
    ///     <see cref="Environment.NewLine" /> insertions never leave a CRLF file with mixed endings.
    ///     Deduplicates by file path — multi-TFM projects have multiple <see cref="DocumentId" />s per
    ///     physical file. Used by the commit path (<see cref="SaveAsync" />) and by <c>DryRun</c> callers
    ///     that need the same collection but write nothing.
    /// </summary>
    public static async Task<List<(string FilePath, string Content, Encoding Encoding)>> CollectFileChangesAsync(
        Solution oldSolution, Solution newSolution, CancellationToken ct)
    {
        SolutionChanges changes = newSolution.GetChanges(oldSolution);
        List<DocumentId> changedDocIds = changes
            .GetProjectChanges()
            .SelectMany(pc => pc.GetChangedDocuments())
            .ToList();

        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
        List<(string FilePath, string Content, Encoding Encoding)> fileChanges = new();
        foreach (DocumentId docId in changedDocIds)
        {
            Document? newDoc = newSolution.GetDocument(docId);
            if (newDoc?.FilePath is null)
            {
                continue;
            }

            if (!seenPaths.Add(newDoc.FilePath))
            {
                continue;
            }

            SourceText newText = await newDoc.GetTextAsync(ct);
            if (newText.Length == 0)
            {
                continue;
            }

            // Key the new text's line endings to the pre-edit document. Every changed doc exists in
            // both snapshots (GetChangedDocuments), so the null-guard is belt-and-braces.
            var content = newText.ToString();
            Document? oldDoc = oldSolution.GetDocument(docId);
            if (oldDoc is not null)
            {
                SourceText oldText = await oldDoc.GetTextAsync(ct);
                content = FileUtility.NormalizeLineEndings(content, oldText.ToString());
            }

            Encoding encoding = newText.Encoding ?? FileUtility.Utf8NoBom;
            fileChanges.Add((newDoc.FilePath, content, encoding));
        }

        return fileChanges;
    }

    /// <summary>
    ///     Collects the changed documents, atomically writes them all (all succeed or none are
    ///     modified), notifies the workspace of each change, and returns the changed files as paths
    ///     relative to the solution directory.
    /// </summary>
    public static async Task<List<string>> SaveAsync(
        WorkspaceManager workspaceManager, Solution oldSolution, Solution newSolution, CancellationToken ct)
    {
        List<(string FilePath, string Content, Encoding Encoding)> fileChanges =
            await CollectFileChangesAsync(oldSolution, newSolution, ct);

        // Atomic batch write: all files succeed or none are modified.
        await AtomicFileWriter.WriteBatchAtomicAsync(fileChanges, ct);

        // Notify workspace of all changes after successful write.
        foreach ((string FilePath, string Content, Encoding Encoding) change in fileChanges)
        {
            workspaceManager.ScheduleFileChanged(change.FilePath, change.Content, change.Encoding);
        }

        return fileChanges.Select(c => workspaceManager.GetRelativePath(c.FilePath)).ToList();
    }
}
