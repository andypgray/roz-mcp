using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Services;

/// <summary>
///     Holds the in-memory <see cref="Solution" /> fork for a single verified edit batch.
///     <see cref="BaseSolution" /> is the snapshot captured once at batch start; <see cref="Fork" /> is
///     advanced by <see cref="Stage" /> as each op computes its final normalized content. A staged map
///     keyed by absolute path lets op N read op N-1's result (staged content wins) so mid-batch no-op
///     detection and line-ending normalization stay exact.
/// </summary>
/// <remarks>
///     Used only by <c>edit_symbol</c> and <c>replace_content</c>. <c>rename_symbol</c> produces its own
///     complete fork via <c>Renamer.RenameSymbolAsync</c> and verifies against that directly.
///     Not thread-safe: a batch runs its ops sequentially, and edits are serialized across the server by
///     <see cref="Pipeline.EditSerializationFilter" />.
/// </remarks>
internal sealed class EditSession
{
    private readonly Dictionary<string, StagedFile> staged = new(StringComparer.OrdinalIgnoreCase);
    private readonly WorkspaceManager workspaceManager;

    private EditSession(WorkspaceManager workspaceManager, Solution baseSolution)
    {
        this.workspaceManager = workspaceManager;
        BaseSolution = baseSolution;
        Fork = baseSolution;
    }

    /// <summary>The immutable snapshot captured at batch start — the delta's "before" side.</summary>
    public Solution BaseSolution { get; }

    /// <summary>The fork advanced by <see cref="Stage" /> — the delta's "after" side.</summary>
    public Solution Fork { get; private set; }

    /// <summary>True once any op has staged a real content change.</summary>
    public bool HasEffectiveChanges => staged.Count > 0;

    /// <summary>Absolute paths of every staged file (all had their content changed).</summary>
    public IReadOnlyCollection<string> ChangedPaths => staged.Keys;

    /// <summary>
    ///     Absolute paths of staged files that map to no workspace document (e.g. a
    ///     <c>replace_content</c> target outside the loaded solution). No compilation covers them, so
    ///     the delta cannot report on them.
    /// </summary>
    public IReadOnlyList<string> UncoveredPaths =>
        staged.Where(kv => !kv.Value.InWorkspace).Select(kv => kv.Key).ToList();

    /// <summary>
    ///     Begins a session, capturing the current solution as the base snapshot. The entry-time
    ///     external-edit reconcile has already run (<see cref="Pipeline.GlobalCallToolFilter" />) and
    ///     <see cref="WorkspaceManager.GetSolutionAsync" /> drains pending updates, so the base is fresh.
    /// </summary>
    public static async Task<EditSession> BeginAsync(WorkspaceManager workspaceManager, CancellationToken ct)
    {
        Solution solution = await workspaceManager.GetSolutionAsync(ct);
        return new EditSession(workspaceManager, solution);
    }

    /// <summary>
    ///     Reads a file's current content for this batch: the staged content if a prior op touched the
    ///     same path, otherwise the on-disk bytes.
    /// </summary>
    public async Task<(string Content, Encoding Encoding)> ReadFileAsync(string absPath, CancellationToken ct)
    {
        string fullPath = Path.GetFullPath(absPath);
        return staged.TryGetValue(fullPath, out StagedFile existing)
            ? (existing.Content, existing.Encoding)
            : await FileUtility.ReadFileWithEncodingAsync(absPath, ct);
    }

    /// <summary>
    ///     Stages an op's final normalized content: records it in the staged map and applies it to the
    ///     fork for <em>every</em> <see cref="DocumentId" /> at that path (multi-TFM files map to several,
    ///     and missing even one would hide the second target's breakage). A path with zero document ids
    ///     is still staged (so <see cref="CommitAsync" /> writes it) but flagged not-in-workspace.
    /// </summary>
    public void Stage(string absPath, string content, Encoding encoding)
    {
        string fullPath = Path.GetFullPath(absPath);
        var text = SourceText.From(content, encoding);

        DocumentId[] ids = Fork.GetDocumentIdsWithFilePath(fullPath).ToArray();
        foreach (DocumentId id in ids)
        {
            Fork = Fork.WithDocumentText(id, text);
        }

        staged[fullPath] = new StagedFile(content, encoding, ids.Length > 0);
    }

    /// <summary>
    ///     Writes every staged file to disk atomically (all-or-nothing) and syncs the workspace —
    ///     exactly the pattern <c>RenameService.SaveSolutionChangesAsync</c> uses for its own commit.
    /// </summary>
    public async Task CommitAsync(CancellationToken ct)
    {
        List<(string FilePath, string Content, Encoding Encoding)> fileChanges = staged
            .Select(kv => (kv.Key, kv.Value.Content, kv.Value.Encoding))
            .ToList();

        await AtomicFileWriter.WriteBatchAtomicAsync(fileChanges, ct);

        foreach ((string filePath, string content, Encoding encoding) in fileChanges)
        {
            workspaceManager.ScheduleFileChanged(filePath, content, encoding);
        }
    }

    private readonly record struct StagedFile(string Content, Encoding Encoding, bool InWorkspace);
}

/// <summary>
///     Null-tolerant read/stage seam shared by <c>SymbolEditService</c> and <c>TextReplacementService</c>.
///     A null session is the <see cref="Enums.VerifyMode.None" /> path: every call forwards verbatim to
///     the <see cref="FileUtility" /> method the code used before, so <c>verify=None</c> stays
///     byte-identical. A non-null session stages the same content instead of writing it.
/// </summary>
internal static class EditIo
{
    /// <summary>Reads the "original" content an op edits: staged (session) or on-disk (no session).</summary>
    public static Task<(string Content, Encoding Encoding)> ReadOriginalAsync(
        EditSession? session, string absPath, CancellationToken ct) =>
        session is null
            ? FileUtility.ReadFileWithEncodingAsync(absPath, ct)
            : session.ReadFileAsync(absPath, ct);

    /// <summary>
    ///     Writes pre-normalized content to disk (no session) or stages it (session). The content must
    ///     already be the final normalized string, so a session's fork text is byte-identical to the
    ///     bytes a commit writes.
    /// </summary>
    public static async Task WriteOrStageContentAsync(
        EditSession? session, string absPath, string content, Encoding encoding,
        WorkspaceManager workspaceManager, CancellationToken ct)
    {
        if (session is null)
        {
            await FileUtility.WriteContentAsync(absPath, content, encoding, workspaceManager, ct);
        }
        else
        {
            session.Stage(absPath, content, encoding);
        }
    }

    /// <summary>
    ///     Writes a formatted <see cref="Document" /> to disk (no session) or stages its normalized text
    ///     (session). The no-session branch calls <see cref="FileUtility.WriteDocumentAsync" /> verbatim;
    ///     the session branch reproduces the same normalization so both sides agree byte-for-byte.
    /// </summary>
    public static async Task WriteOrStageDocumentAsync(
        EditSession? session, string absPath, Document document, string originalContent, Encoding encoding,
        WorkspaceManager workspaceManager, CancellationToken ct)
    {
        if (session is null)
        {
            await FileUtility.WriteDocumentAsync(absPath, document, originalContent, encoding, workspaceManager, ct);
        }
        else
        {
            SourceText text = await document.GetTextAsync(ct);
            string content = FileUtility.NormalizeLineEndings(text.ToString(), originalContent);
            session.Stage(absPath, content, encoding);
        }
    }
}
