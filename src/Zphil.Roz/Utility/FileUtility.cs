using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Utility;

/// <summary>
///     Shared file I/O utilities for encoding detection and line-ending normalization.
/// </summary>
/// <remarks>
///     <para>
///         Edit tools write to disk through this helper instead of using
///         <c>workspace.TryApplyChanges(solution)</c> directly. This is intentional — TryApplyChanges
///         also writes to disk, but does not give us control over three things that matter:
///     </para>
///     <list type="number">
///         <item>
///             <strong>BOM preservation</strong> — Roslyn's SourceText round-trip can accumulate
///             duplicate BOMs on sequential writes. We read raw bytes to detect the BOM, skip it
///             when decoding, and write back with the original encoding.
///         </item>
///         <item>
///             <strong>Line-ending normalization</strong> — Roslyn normalizes line endings to <c>\n</c>
///             internally. We restore the original file's CRLF/LF style so diffs stay clean.
///         </item>
///         <item>
///             <strong>Concurrency safety</strong> — Writing to disk first, then syncing the workspace
///             via <see cref="WorkspaceManager.ScheduleFileChanged" />, serializes I/O to prevent races
///             with <c>File.Move</c> (rename operations) and subsequent file reads.
///         </item>
///     </list>
/// </remarks>
internal static class FileUtility
{
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(true);
    internal static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    // Strict UTF-8 (no BOM): GetString throws DecoderFallbackException on invalid bytes instead of
    // emitting U+FFFD. Used to reject legacy-codepage files rather than silently corrupting them.
    private static readonly UTF8Encoding Utf8NoBomStrict = new(false, true);

    private static readonly int[] IoRetryBackoffMs = [50, 100, 200];

    /// <summary>
    ///     Reads a file and detects its encoding (UTF-8 with or without BOM).
    /// </summary>
    /// <remarks>
    ///     Retries transient <see cref="IOException" />s (file locked by another process — VS,
    ///     ReSharper, AV, the writer between flush and handle close) with a 50/100/200ms backoff.
    ///     Genuine missing-file errors (<see cref="FileNotFoundException" />,
    ///     <see cref="DirectoryNotFoundException" />) bypass the retry and surface immediately.
    /// </remarks>
    internal static async Task<(string Content, Encoding Encoding)> ReadFileWithEncodingAsync(
        string filePath, CancellationToken ct)
    {
        byte[] bytes = await RunWithIoRetryAsync(() => File.ReadAllBytesAsync(filePath, ct), ct);

        // UTF-8 BOM (EF BB BF): the supported, round-trippable encoding. Strip the BOM and decode the rest.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (Utf8NoBom.GetString(bytes, 3, bytes.Length - 3), Utf8WithBom);
        }

        // Reject any non-UTF-8 BOM rather than decoding its high bytes as UTF-8 (which yields mojibake
        // and would be written back as corruption).
        string? bomEncoding = DetectNonUtf8Bom(bytes);
        if (bomEncoding is not null)
        {
            throw new UnsupportedEncodingException(
                $"Cannot edit '{filePath}': it is encoded as {bomEncoding} (byte-order mark detected), " +
                $"not UTF-8. Re-save the file as UTF-8 first.");
        }

        // No BOM: decode strictly so genuinely-undecodable bytes reject instead of silently becoming
        // U+FFFD. An empty file decodes to "" and valid multi-byte UTF-8 (e.g. 'é' = C3 A9) succeeds;
        // only a legacy codepage (lone high bytes like Windows-1252 0xE9/0xA9) trips the fallback.
        try
        {
            return (Utf8NoBomStrict.GetString(bytes), Utf8NoBom);
        }
        catch (DecoderFallbackException)
        {
            throw new UnsupportedEncodingException(
                $"Cannot edit '{filePath}': its bytes are not valid UTF-8 (possibly a legacy codepage " +
                $"like Windows-1252). Re-save the file as UTF-8 first.");
        }
    }

    /// <summary>
    ///     Decodes raw file bytes to their content string for the write-time conflict check, using the
    ///     encoding the batch entry will write with — for a session edit the encoding
    ///     <see cref="ReadFileWithEncodingAsync" /> detected (always UTF-8; it rejects everything else),
    ///     for a fork edit the encoding Roslyn loaded the document with, which can be UTF-16 or a legacy
    ///     codepage. Assuming UTF-8 here would turn every fork write to such a file into a deterministic
    ///     false conflict. The encoding's own byte-order mark is stripped so the decoded string matches
    ///     the BOM-free content the "expected" side always carries.
    /// </summary>
    /// <returns>
    ///     The decoded content, or null when the bytes cannot be decoded under
    ///     <paramref name="encoding" /> (a strict encoding hitting an out-of-band re-encode). The only
    ///     caller compares the result for equality against the content an edit was computed from, so
    ///     "cannot decode" must read as a difference (conflict), never surface as an exception.
    /// </returns>
    internal static string? DecodeContentForComparison(byte[] bytes, Encoding encoding)
    {
        byte[] preamble = encoding.GetPreamble();
        int offset = preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;

        // BOM-less UTF-8 entries (codepage 65001) tolerate an on-disk UTF-8 BOM: an external re-save
        // that only toggles the BOM is not a content conflict, and the expected side is BOM-free.
        if (offset == 0 && encoding.CodePage == Utf8NoBom.CodePage
                        && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            offset = 3;
        }

        try
        {
            return encoding.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Detects a non-UTF-8 byte-order mark and returns its encoding name, or null when none is present.
    ///     UTF-32 patterns are tested before UTF-16 because UTF-32 LE (<c>FF FE 00 00</c>) begins with the
    ///     UTF-16 LE mark (<c>FF FE</c>); the ordering only affects which name is reported — all reject.
    /// </summary>
    private static string? DetectNonUtf8Bom(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return "UTF-32 LE";
        }

        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return "UTF-32 BE";
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return "UTF-16 LE";
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return "UTF-16 BE";
        }

        return null;
    }

    /// <summary>
    ///     Runs an async I/O operation, retrying transient I/O faults with a 50/100/200ms backoff
    ///     (4 attempts, ~350 ms worst case). Catches <see cref="IOException" /> (sharing violations
    ///     when reading a locked file) and <see cref="UnauthorizedAccessException" /> (Win32
    ///     <c>ERROR_ACCESS_DENIED</c> when <c>File.Move</c> targets a destination held by another
    ///     process — VS, ReSharper, AV, the writer between flush and handle close).
    ///     <see cref="FileNotFoundException" /> and <see cref="DirectoryNotFoundException" /> bypass
    ///     the retry and surface immediately.
    /// </summary>
    internal static async Task<T> RunWithIoRetryAsync<T>(Func<Task<T>> op, CancellationToken ct)
    {
        for (var attempt = 0; attempt < IoRetryBackoffMs.Length; attempt++)
        {
            try
            {
                return await op();
            }
            catch (Exception ex) when (IsTransientIoFault(ex))
            {
                await Task.Delay(IoRetryBackoffMs[attempt], ct);
            }
        }

        return await op();
    }

    /// <inheritdoc cref="RunWithIoRetryAsync{T}(Func{Task{T}}, CancellationToken)" />
    internal static async Task RunWithIoRetryAsync(Action op, CancellationToken ct)
    {
        for (var attempt = 0; attempt < IoRetryBackoffMs.Length; attempt++)
        {
            try
            {
                op();
                return;
            }
            catch (Exception ex) when (IsTransientIoFault(ex))
            {
                await Task.Delay(IoRetryBackoffMs[attempt], ct);
            }
        }

        op();
    }

    private static bool IsTransientIoFault(Exception ex) =>
        ex is IOException and not FileNotFoundException and not DirectoryNotFoundException
            or UnauthorizedAccessException;

    /// <summary>
    ///     Writes a Roslyn document to disk, preserving original line endings and encoding,
    ///     then notifies the workspace of the change.
    /// </summary>
    /// <remarks>
    ///     This is the standard write path for edit tools. Do not replace with
    ///     <c>workspace.TryApplyChanges</c> — see class remarks for why.
    /// </remarks>
    internal static async Task WriteDocumentAsync(
        string filePath, Document document, string originalContent, Encoding encoding,
        WorkspaceManager workspaceManager, CancellationToken ct)
    {
        SourceText formattedText = await document.GetTextAsync(ct);
        var formattedStr = formattedText.ToString();
        string result = NormalizeLineEndings(formattedStr, originalContent);
        await WriteContentAsync(filePath, result, encoding, workspaceManager, ct);
    }

    /// <summary>
    ///     Writes string content to disk and notifies the workspace of the change.
    /// </summary>
    /// <remarks>
    ///     The content is assumed to already have correct line endings.
    /// </remarks>
    internal static async Task WriteContentAsync(
        string filePath, string content, Encoding encoding,
        WorkspaceManager workspaceManager, CancellationToken ct)
    {
        await AtomicFileWriter.WriteAtomicAsync(filePath, content, encoding, ct);
        workspaceManager.ScheduleFileChanged(filePath, content, encoding);
    }

    /// <summary>
    ///     Normalizes line endings in <paramref name="text" /> to match those in <paramref name="referenceContent" />.
    /// </summary>
    internal static string NormalizeLineEndings(string text, string referenceContent)
    {
        string normalized = text.Replace("\r\n", "\n");
        return referenceContent.Contains("\r\n") ? normalized.Replace("\n", "\r\n") : normalized;
    }

    /// <summary>
    ///     Resolves the solution file path by checking an explicit path, the
    ///     <see cref="RozEnvVars.SolutionPath" /> environment variable, and then walking up
    ///     from the working directory.
    /// </summary>
    public static string DiscoverSolution(string? explicitPath = null, string? workingDirectory = null)
    {
        if (!String.IsNullOrWhiteSpace(explicitPath))
        {
            string resolved = Path.GetFullPath(explicitPath);
            if (!File.Exists(resolved))
            {
                throw new UserErrorException($"Solution file not found: {resolved}");
            }

            return resolved;
        }

        string? envPath = EnvParse.RawString(RozEnvVars.SolutionPath.Name);
        if (envPath is not null)
        {
            string resolved = Path.GetFullPath(envPath);
            if (!File.Exists(resolved))
            {
                throw new UserErrorException(
                    $"{RozEnvVars.SolutionPath.Name} points to a file that does not exist: {resolved}");
            }

            return resolved;
        }

        string cwd = workingDirectory ?? Directory.GetCurrentDirectory();
        (string dir, string[] files)? firstAmbiguous = null;

        string? current = cwd;
        while (current is not null)
        {
            string[] slnFiles = FindSlnFiles(current);
            if (slnFiles.Length == 1)
            {
                return slnFiles[0];
            }

            if (slnFiles.Length > 1 && firstAmbiguous is null)
            {
                firstAmbiguous = (current, slnFiles);
            }

            current = Directory.GetParent(current)?.FullName;
        }

        if (firstAmbiguous is { } ambiguous)
        {
            throw new UserErrorException(
                $"Multiple solution files found in '{ambiguous.dir}':\n" +
                $"  {String.Join("\n  ", ambiguous.files.Select(Path.GetFileName))}\n" +
                $"Set {RozEnvVars.SolutionPath.Name} to the desired solution file.");
        }

        throw new UserErrorException(
            $"No .sln, .slnf or .slnx file found in '{cwd}' or any parent directory.\n" +
            $"Set {RozEnvVars.SolutionPath.Name} environment variable or run 'roz-mcp setup' in your project directory.");
    }

    private static string[] FindSlnFiles(string directory)
    {
        return Directory.EnumerateFiles(directory)
            .Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
