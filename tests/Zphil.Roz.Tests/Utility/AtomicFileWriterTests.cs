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
        await AtomicFileWriter.WriteBatchAtomicAsync(files, CancellationToken.None);

        // Assert
        File.ReadAllText(path1).ShouldBe("new-a");
        File.ReadAllText(path2).ShouldBe("new-b");
        File.ReadAllText(path3).ShouldBe("new-c");
    }

    [Fact]
    public async Task WriteBatchAtomicAsync_EmptyList_Succeeds()
    {
        // Act & Assert — should not throw
        await AtomicFileWriter.WriteBatchAtomicAsync([], CancellationToken.None);
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
        Exception? ex = await Record.ExceptionAsync(() => AtomicFileWriter.WriteBatchAtomicAsync(files, CancellationToken.None));

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
            Exception? ex = await Record.ExceptionAsync(() => AtomicFileWriter.WriteBatchAtomicAsync(files, CancellationToken.None));

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
            Exception? ex = await Record.ExceptionAsync(() => AtomicFileWriter.WriteBatchAtomicAsync(files, CancellationToken.None));

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
        Exception? ex = await Record.ExceptionAsync(() => AtomicFileWriter.WriteBatchAtomicAsync(files, cts.Token));

        // Assert
        ex.ShouldNotBeNull();
        File.ReadAllText(path1).ShouldBe("original");
    }
}
