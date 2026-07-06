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
    ///     Guarantees the original is either fully updated or untouched.
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
    ///     Writes multiple files atomically using a two-phase temp-then-swap strategy.
    /// </summary>
    /// <remarks>
    ///     Phase 1 writes all temp files in parallel — originals are untouched. If any temp
    ///     write fails, all temps are cleaned up and the exception propagates.
    ///     Phase 2 swaps temps into place sequentially. If any swap fails, already-swapped
    ///     files are restored from their captured original content.
    /// </remarks>
    internal static async Task WriteBatchAtomicAsync(
        IReadOnlyList<(string FilePath, string Content, Encoding Encoding)> files,
        CancellationToken ct)
    {
        if (files.Count == 0)
        {
            return;
        }

        // Resolve temp paths upfront so cleanup always knows what to delete
        List<(string FilePath, string Content, Encoding Encoding, string TempPath)> entries = files
            .Select(f => (f.FilePath, f.Content, f.Encoding, TempPath: GetTempPath(f.FilePath)))
            .ToList();

        // Capture originals as raw bytes before writing anything — minimizes TOCTOU window
        // and avoids encoding assumptions (BOM fidelity preserved on rollback)
        byte[]?[] originals = await Task.WhenAll(entries.Select(async e =>
            File.Exists(e.FilePath)
                ? await FileUtility.RunWithIoRetryAsync(() => File.ReadAllBytesAsync(e.FilePath, ct), ct)
                : null));

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
                (string FilePath, string Content, Encoding Encoding, string TempPath) entry = entries[i];
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
