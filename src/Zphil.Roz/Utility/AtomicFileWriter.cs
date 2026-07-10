using System.Text;

namespace Zphil.Roz.Utility;

/// <summary>
///     Provides atomic file write operations using a temp-file-then-move strategy.
/// </summary>
/// <remarks>
///     Single-file writes guarantee the original is either fully updated or untouched.
///     Batch writes guarantee all-or-nothing semantics across multiple files.
/// </remarks>
internal static class AtomicFileWriter
{
    /// <summary>
    ///     Writes content to a temp file, then moves it over the original.
    /// </summary>
    /// <remarks>
    ///     Guarantees the original is either fully updated or untouched. Unlike
    ///     <see cref="WriteBatchAtomicAsync" />, this per-op path carries no write-time conflict check: it
    ///     backs the <c>verify=None</c> single-file edits, whose read→compute→write window is a few
    ///     milliseconds. That residual lost-update window against a concurrent external write is an
    ///     accepted risk (a batch/verified edit takes the guarded path instead).
    /// </remarks>
    internal static async Task WriteAtomicAsync(
        string filePath, string content, Encoding encoding, CancellationToken ct)
    {
        string tempPath = GetTempPath(filePath);
        try
        {
            await File.WriteAllTextAsync(tempPath, content, encoding, ct);
            await FileUtility.RunWithIoRetryAsync(() => File.Move(tempPath, filePath, true), ct);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    ///     Writes multiple files atomically using a two-phase temp-then-swap strategy, aborting first if
    ///     any target changed on disk since the edit read it.
    /// </summary>
    /// <remarks>
    ///     Each entry may carry the content the edit was computed against (<c>ExpectedOriginal</c>, null to
    ///     skip). Before any write, every such entry is compared against the file's current on-disk
    ///     content — captured here for rollback anyway, so the check costs no extra I/O; a mismatch aborts
    ///     the whole batch with a <see cref="FileConflictException" /> and touches nothing (the lost-update
    ///     guard for the verified-write commit paths). <paramref name="toDisplayPath" /> renders the
    ///     conflicted paths for that message.
    ///     Phase 1 then writes all temp files in parallel — originals are untouched. If any temp
    ///     write fails, all temps are cleaned up and the exception propagates.
    ///     Phase 2 swaps temps into place sequentially. If any swap fails, already-swapped
    ///     files are restored from their captured original content.
    /// </remarks>
    internal static async Task WriteBatchAtomicAsync(
        IReadOnlyList<(string FilePath, string Content, Encoding Encoding, string? ExpectedOriginal)> files,
        Func<string, string> toDisplayPath,
        CancellationToken ct)
    {
        if (files.Count == 0)
        {
            return;
        }

        // Resolve temp paths upfront so cleanup always knows what to delete
        List<(string FilePath, string Content, Encoding Encoding, string? ExpectedOriginal, string TempPath)> entries = files
            .Select(f => (f.FilePath, f.Content, f.Encoding, f.ExpectedOriginal, TempPath: GetTempPath(f.FilePath)))
            .ToList();

        // Capture originals as raw bytes before writing anything — minimizes TOCTOU window
        // and avoids encoding assumptions (BOM fidelity preserved on rollback)
        byte[]?[] originals = await Task.WhenAll(entries.Select(async e =>
        {
            if (!File.Exists(e.FilePath))
            {
                return null;
            }

            try
            {
                return await FileUtility.RunWithIoRetryAsync(() => File.ReadAllBytesAsync(e.FilePath, ct), ct);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // The file vanished between the existence check and the read — an external delete racing
                // this capture. Treat as "missing" (null) rather than letting the raw exception escape as a
                // crash-class error: the conflict check below then reports it as a clean conflict, symmetric
                // with the delete-landed-before-capture case.
                return null;
            }
        }));

        // Write-time conflict detection (lost-update guard): verify each file on disk still holds the
        // content the edit was computed against before touching anything. A mismatch means an external
        // writer changed the file during the batch's compute window — which for the fork tools can span
        // minutes — so writing now would silently discard that change. Reuses the originals just read for
        // rollback (zero extra I/O). Entries with no expected content (a path the edit never read from
        // disk) are skipped.
        List<string> conflicts = [];
        for (var i = 0; i < entries.Count; i++)
        {
            string? expected = entries[i].ExpectedOriginal;
            if (expected is null)
            {
                continue;
            }

            // A file present at read time (expected non-null) but gone now was deleted out-of-band —
            // writing would resurrect it — and bytes undecodable under the entry's encoding mean an
            // out-of-band re-encode; both decode to null, which never matches, so both conflict.
            string? current = originals[i] is { } bytes
                ? FileUtility.DecodeContentForComparison(bytes, entries[i].Encoding)
                : null;
            if (!String.Equals(current, expected, StringComparison.Ordinal))
            {
                conflicts.Add(entries[i].FilePath);
            }
        }

        if (conflicts.Count > 0)
        {
            var list = String.Join("\n", conflicts.Select(p => $"  {toDisplayPath(p)}"));
            throw new FileConflictException(
                "Write aborted: the following file(s) changed on disk after the edit was computed, so " +
                "writing would overwrite those external changes. Nothing was written — re-run the tool to " +
                $"edit against the current file contents:\n{list}");
        }

        // Phase 1: Write all temp files in parallel — originals untouched
        try
        {
            await Task.WhenAll(entries.Select(e =>
                File.WriteAllTextAsync(e.TempPath, e.Content, e.Encoding, ct)));
        }
        catch
        {
            CleanupTemps(entries.Select(e => e.TempPath));
            throw;
        }

        // Phase 2: Swap all temps into place sequentially
        var swappedCount = 0;
        try
        {
            for (var i = 0; i < entries.Count; i++)
            {
                (string FilePath, string Content, Encoding Encoding, string? ExpectedOriginal, string TempPath) entry = entries[i];
                await FileUtility.RunWithIoRetryAsync(() => File.Move(entry.TempPath, entry.FilePath, true), ct);
                swappedCount++;
            }
        }
        catch
        {
            // Restore already-swapped files; delete newly-created files
            for (var i = 0; i < swappedCount; i++)
            {
                try
                {
                    if (originals[i] is not null)
                    {
                        File.WriteAllBytes(entries[i].FilePath, originals[i]!);
                    }
                    else
                    {
                        TryDelete(entries[i].FilePath);
                    }
                }
                catch
                {
                    // Best-effort rollback — if this fails too, the file is already lost
                }
            }

            CleanupTemps(entries.Select(e => e.TempPath));
            throw;
        }
    }

    /// <summary>
    ///     Resolves a temp path on the same volume as <paramref name="filePath" />.
    /// </summary>
    /// <remarks>
    ///     Prefers the system temp directory to avoid polluting the user's working tree.
    ///     Falls back to a same-directory <c>.tmp</c> sibling when volumes differ.
    /// </remarks>
    private static string GetTempPath(string filePath)
    {
        string systemTemp = Path.GetTempPath();
        string? sourceRoot = Path.GetPathRoot(Path.GetFullPath(filePath));
        string? tempRoot = Path.GetPathRoot(systemTemp);

        if (String.Equals(sourceRoot, tempRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(systemTemp, $"roz-mcp-{Guid.NewGuid():N}{Path.GetExtension(filePath)}");
        }

        // Cross-volume fallback: sibling .tmp file (same directory guarantees same volume)
        return filePath + ".tmp";
    }

    private static void CleanupTemps(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup — orphaned temps in %TEMP% are harmless
        }
    }
}
