using System.Text;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Tests.Utility;

public class AtomicFileWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"atomic-test-{Guid.NewGuid():N}");

    public AtomicFileWriterTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private string CreateFile(string name, string content = "original content")
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    // Adapts the mechanics-focused batch tests (write/rollback) to WriteBatchAtomicAsync's production
    // signature without opting them into write-time conflict detection: a null ExpectedOriginal per entry
    // skips the check. The B2 conflict path is covered by WriteConflictDetectionTests.
    private static Task WriteBatch(List<(string Path, string Content, Encoding Encoding)> files, CancellationToken ct) =>
        AtomicFileWriter.WriteBatchAtomicAsync(
            files.Select(f => (f.Path, f.Content, f.Encoding, (string?)null)).ToList(),
            static p => p,
            ct);

    // ── WriteAtomicAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAtomicAsync_WritesContent()
    {
        // Arrange
        string path = CreateFile("test.cs");

        // Act
        await AtomicFileWriter.WriteAtomicAsync(path, "new content", Encoding.UTF8, CancellationToken.None);

        // Assert
        File.ReadAllText(path).ShouldBe("new content");
    }

    [Fact]
    public async Task WriteAtomicAsync_CreatesNewFile()
    {
        // Arrange
        string path = Path.Combine(_tempDir, "new-file.cs");

        // Act
        await AtomicFileWriter.WriteAtomicAsync(path, "created", Encoding.UTF8, CancellationToken.None);

        // Assert
        File.ReadAllText(path).ShouldBe("created");
    }

    [Fact]
    public async Task WriteAtomicAsync_PreservesEncoding()
    {
        // Arrange
        string path = CreateFile("encoded.cs");
        var utf8Bom = new UTF8Encoding(true);

        // Act
        await AtomicFileWriter.WriteAtomicAsync(path, "bom content", utf8Bom, CancellationToken.None);

        // Assert — file should have a UTF-8 BOM
        await path.ShouldHaveBomAsync();
    }

    [Fact]
    public async Task WriteAtomicAsync_LeavesNoTempFiles()
    {
        // Arrange
        string path = CreateFile("clean.cs");

        // Act
        await AtomicFileWriter.WriteAtomicAsync(path, "updated", Encoding.UTF8, CancellationToken.None);

        // Assert — no .tmp sibling left behind
        File.Exists(path + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public async Task WriteAtomicAsync_OriginalUntouched_WhenTargetDirectoryMissing()
    {
        // Arrange — target in a non-existent directory
        string path = Path.Combine(_tempDir, "missing-dir", "file.cs");

        // Act & Assert — should throw (can't create temp in missing dir)
        await Should.ThrowAsync<DirectoryNotFoundException>(() => AtomicFileWriter.WriteAtomicAsync(path, "content", Encoding.UTF8, CancellationToken.None));
    }

    [Fact]
    public async Task WriteAtomicAsync_DestinationLocked_RetriesAndSucceeds()
    {
        // Arrange — file briefly held with FileShare.None so File.Move overwrite fails;
        // release fires inside the 50/100/200 ms retry window.
        string path = CreateFile("locked.cs", "original");
        var lockHandle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            // 75ms encodes the production retry backoff (AtomicFileWriter's [50,100,200]ms window):
            // it lands after the first 50ms retry, so the lock clears while the loop is still retrying.
            await Task.Delay(75);
            lockHandle.Dispose();
        }, TestContext.Current.CancellationToken);

        try
        {
            // Act
            await AtomicFileWriter.WriteAtomicAsync(path, "updated", Encoding.UTF8, CancellationToken.None);
        }
        finally
        {
            await release;
        }

        // Assert — content swapped in once the lock cleared
        File.ReadAllText(path).ShouldBe("updated");
    }

    [Fact]
    public async Task WriteAtomicAsync_DestinationLockedPersistently_Throws()
    {
        // FileShare.None / read-only enforcement is Windows-only — POSIX rename replaces the
        // destination regardless, so there is no failure path to assert.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange — lock held for the full duration; retry budget exhausts and rethrows.
        // Windows surfaces a held destination as UnauthorizedAccessException (Win32
        // ERROR_ACCESS_DENIED) when File.Move tries to overwrite it.
        string path = CreateFile("perma-locked.cs", "original");
        var lockHandle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Exception? ex;
        try
        {
            // Act
            ex = await Record.ExceptionAsync(() =>
                AtomicFileWriter.WriteAtomicAsync(path, "updated", Encoding.UTF8, CancellationToken.None));
        }
        finally
        {
            lockHandle.Dispose();
        }

        // Assert — surfaces as IOException (sharing violation) or UnauthorizedAccessException
        // depending on which OS-level error the lock produces; original file unchanged either way.
        ex.ShouldNotBeNull();
        (ex is IOException or UnauthorizedAccessException).ShouldBeTrue(
            $"expected IOException or UnauthorizedAccessException, got {ex.GetType().Name}");
        File.ReadAllText(path).ShouldBe("original");
    }

    // ── WriteBatchAtomicAsync ───────────────────────────────────────────────

    [Fact]
    public async Task WriteBatchAtomicAsync_WritesAllFiles()
    {
        // Arrange
        string path1 = CreateFile("a.cs", "old-a");
        string path2 = CreateFile("b.cs", "old-b");
        string path3 = CreateFile("c.cs", "old-c");

        List<(string, string, Encoding)> files = new()
        {
            (path1, "new-a", Encoding.UTF8),
            (path2, "new-b", Encoding.UTF8),
            (path3, "new-c", Encoding.UTF8)
        };

        // Act
        await WriteBatch(files, CancellationToken.None);

        // Assert
        File.ReadAllText(path1).ShouldBe("new-a");
        File.ReadAllText(path2).ShouldBe("new-b");
        File.ReadAllText(path3).ShouldBe("new-c");
    }

    [Fact]
    public async Task WriteBatchAtomicAsync_EmptyList_Succeeds()
    {
        // Act & Assert — should not throw
        await WriteBatch([], CancellationToken.None);
    }

    [Fact]
    public async Task WriteBatchAtomicAsync_OriginalsUntouched_WhenOneFileCannotBeWritten()
    {
        // Arrange — three files, but make the third target an invalid directory
        string path1 = CreateFile("x.cs", "original-x");
        string path2 = CreateFile("y.cs", "original-y");
        string invalidPath = Path.Combine(_tempDir, "no-such-dir", "z.cs");

        List<(string, string, Encoding)> files = new()
        {
            (path1, "new-x", Encoding.UTF8),
            (path2, "new-y", Encoding.UTF8),
            (invalidPath, "new-z", Encoding.UTF8)
        };

        // Act
        Exception? ex = await Record.ExceptionAsync(() => WriteBatch(files, CancellationToken.None));

        // Assert — originals unchanged
        ex.ShouldNotBeNull();
        File.ReadAllText(path1).ShouldBe("original-x");
        File.ReadAllText(path2).ShouldBe("original-y");
    }

    [Fact]
    public async Task WriteBatchAtomicAsync_OriginalsUntouched_WhenSwapFails()
    {
        // FileShare.None / read-only enforcement is Windows-only — POSIX rename replaces the
        // destination regardless, so there is no failure path to assert.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange — write succeeds but swap fails because target is read-only
        string path1 = CreateFile("r1.cs", "original-r1");
        string path2 = CreateFile("r2.cs", "original-r2");

        // Make the second file read-only so File.Move overwrite fails
        File.SetAttributes(path2, FileAttributes.ReadOnly);

        List<(string, string, Encoding)> files = new()
        {
            (path1, "new-r1", Encoding.UTF8),
            (path2, "new-r2", Encoding.UTF8)
        };

        try
        {
            // Act
            Exception? ex = await Record.ExceptionAsync(() => WriteBatch(files, CancellationToken.None));

            // Assert — should have thrown, and first file should be rolled back
            ex.ShouldNotBeNull();
            File.ReadAllText(path1).ShouldBe("original-r1");
        }
        finally
        {
            // Cleanup — remove read-only so Dispose can delete the directory
            File.SetAttributes(path2, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task WriteBatchAtomicAsync_NewlyCreatedFilesDeleted_WhenSwapFails()
    {
        // FileShare.None / read-only enforcement is Windows-only — POSIX rename replaces the
        // destination regardless, so there is no failure path to assert.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange — first file is new (doesn't exist), second is read-only (swap fails)
        string newPath = Path.Combine(_tempDir, "brand-new.cs");
        string readOnlyPath = CreateFile("locked.cs", "original-locked");
        File.SetAttributes(readOnlyPath, FileAttributes.ReadOnly);

        List<(string, string, Encoding)> files = new()
        {
            (newPath, "new-content", Encoding.UTF8),
            (readOnlyPath, "new-locked", Encoding.UTF8)
        };

        try
        {
            // Act
            Exception? ex = await Record.ExceptionAsync(() => WriteBatch(files, CancellationToken.None));

            // Assert — newly created file should be cleaned up on rollback
            ex.ShouldNotBeNull();
            File.Exists(newPath).ShouldBeFalse();
        }
        finally
        {
            File.SetAttributes(readOnlyPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task WriteBatchAtomicAsync_Cancellation_OriginalsUntouched()
    {
        // Arrange
        string path1 = CreateFile("cancel1.cs", "original");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        List<(string, string, Encoding)> files = new()
        {
            (path1, "should-not-write", Encoding.UTF8)
        };

        // Act
        Exception? ex = await Record.ExceptionAsync(() => WriteBatch(files, cts.Token));

        // Assert
        ex.ShouldNotBeNull();
        File.ReadAllText(path1).ShouldBe("original");
    }

    // ── Write-time conflict detection: encoding fidelity ────────────────────
    // The expected-original comparison must decode the on-disk bytes with the entry's encoding.
    // The session tools are UTF-8-gated (ReadFileWithEncodingAsync rejects everything else), but the
    // fork tools (rename_symbol / apply_code_fix / change_signature) carry whatever encoding Roslyn
    // loaded the document with — UTF-16 and legacy codepages included — and wrote such files
    // correctly before conflict detection existed. Assuming UTF-8 here made every fork write to a
    // non-UTF-8 file a deterministic false conflict.

    private static Task WriteBatchExpecting(
        string path, string content, Encoding encoding, string? expectedOriginal, CancellationToken ct) =>
        AtomicFileWriter.WriteBatchAtomicAsync([(path, content, encoding, expectedOriginal)], static p => p, ct);

    [Fact]
    public async Task WriteBatchAtomicAsync_Utf16TargetUnchanged_WritesWithoutConflict()
    {
        // Arrange — a UTF-16 target whose on-disk content still matches the expected original.
        const string Original = "class Utf16Target { }\n";
        string path = Path.Combine(_tempDir, "utf16.cs");
        File.WriteAllText(path, Original, Encoding.Unicode);

        // Act
        await WriteBatchExpecting(path, Original + "// edited\n", Encoding.Unicode, Original, CancellationToken.None);

        // Assert — the write landed (no false conflict) and round-tripped as UTF-16.
        File.ReadAllText(path).ShouldBe(Original + "// edited\n");
        byte[] bytes = File.ReadAllBytes(path);
        bytes.Take(2).ShouldBe(Encoding.Unicode.GetPreamble());
    }

    [Fact]
    public async Task WriteBatchAtomicAsync_LegacyCodepageTargetUnchanged_WritesWithoutConflict()
    {
        // Arrange — a Windows-1252 target with bytes that are invalid UTF-8 (© = A9, é = E9), the
        // encoding Roslyn falls back to when a BOM-less file fails strict UTF-8 decoding.
        Encoding windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
        const string Original = "// © déjà vu\nclass LegacyTarget { }\n";
        string path = Path.Combine(_tempDir, "legacy1252.cs");
        File.WriteAllText(path, Original, windows1252);

        // Act
        await WriteBatchExpecting(path, Original + "// edited\n", windows1252, Original, CancellationToken.None);

        // Assert
        File.ReadAllText(path, windows1252).ShouldBe(Original + "// edited\n");
    }

    [Fact]
    public async Task WriteBatchAtomicAsync_Utf16TargetChangedOnDisk_ThrowsConflictAndWritesNothing()
    {
        // Arrange — the same UTF-16 target, but disk no longer matches the expected original: the
        // encoding-aware decode must still detect a genuine out-of-band change.
        const string External = "class RewrittenOutOfBand { }\n";
        string path = Path.Combine(_tempDir, "utf16-conflict.cs");
        File.WriteAllText(path, External, Encoding.Unicode);

        // Act / Assert
        await Should.ThrowAsync<FileConflictException>(() =>
            WriteBatchExpecting(path, "// must not land\n", Encoding.Unicode, "class Stale { }\n", CancellationToken.None));
        File.ReadAllText(path).ShouldBe(External);
    }

    [Fact]
    public async Task WriteBatchAtomicAsync_ExternalRewriteOnlyAddedUtf8Bom_WritesWithoutConflict()
    {
        // Arrange — a BOM-less UTF-8 entry whose target gained a UTF-8 BOM out-of-band with identical
        // content. The expected side is always BOM-free, so a BOM-only toggle is not a content
        // conflict (pins the leniency the original UTF-8-only check had).
        const string Original = "class BomToggled { }\n";
        string path = CreateFile("bom-added.cs", Original);
        File.WriteAllText(path, Original, new UTF8Encoding(true));

        // Act
        await WriteBatchExpecting(path, Original + "// edited\n", new UTF8Encoding(false), Original, CancellationToken.None);

        // Assert
        File.ReadAllText(path).ShouldBe(Original + "// edited\n");
    }

    [Fact]
    public async Task WriteBatchAtomicAsync_EmptyExpectedTargetDeleted_ThrowsConflictAndDoesNotResurrect()
    {
        // Arrange — the target was EMPTY when the edit read it, then deleted out-of-band. The missing
        // file must read as a conflict outright: comparing a ""-sentinel against the empty expected
        // content would wave the write through and resurrect the file the user just deleted.
        string path = CreateFile("deleted-empty.cs", "");
        File.Delete(path);

        // Act / Assert
        await Should.ThrowAsync<FileConflictException>(() =>
            WriteBatchExpecting(path, "// resurrected\n", Encoding.UTF8, "", CancellationToken.None));
        File.Exists(path).ShouldBeFalse();
    }
}
