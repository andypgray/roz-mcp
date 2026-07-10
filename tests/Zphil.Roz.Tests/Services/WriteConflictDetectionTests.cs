using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Services;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Services;

/// <summary>
///     B2 write-time conflict detection: the batch commit paths must refuse to write when a file changed
///     on disk after the edit was computed against it, rather than silently clobbering the external edit
///     (a lost update — the worst bug class for a tool whose pitch is conservative writes). Covers both
///     commit paths: the session batch (<see cref="EditSession.CommitAsync" />, backing
///     <c>edit_symbol</c>/<c>replace_content</c> under <c>verify=Delta</c>) and the fork batch
///     (<see cref="SolutionChangeWriter.SaveAsync" />, backing <c>rename_symbol</c>/<c>apply_code_fix</c>/
///     <c>change_signature</c>).
/// </summary>
/// <remarks>
///     Uses an isolated <see cref="TempWorkspace" /> per test (the sabotage writes to disk) with the
///     background watcher off, so the only disk mutation is the test's own out-of-band write.
/// </remarks>
public sealed class WriteConflictDetectionTests
{
    [Fact]
    public async Task CommitAsync_FileChangedOnDiskAfterStage_ThrowsConflictAndWritesNothing()
    {
        // Arrange
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: ct);
        Solution solution = await ws.WorkspaceManager.GetSolutionAsync(ct);
        string target = SourceFile(solution, "Circle.cs");

        EditSession session = await EditSession.BeginAsync(ws.WorkspaceManager, ct);
        (string original, Encoding encoding) = await session.ReadFileAsync(target, ct);
        session.Stage(target, original + "\n// staged edit that must not land\n", encoding);

        // Sabotage: an external process rewrites the file after the edit read it.
        const string ExternalContent = "// rewritten out-of-band by another process\n";
        await File.WriteAllTextAsync(target, ExternalContent, ct);
        byte[] sabotagedBytes = await File.ReadAllBytesAsync(target, ct);

        // Act / Assert — commit aborts with a conflict rather than overwriting the external edit.
        await Should.ThrowAsync<FileConflictException>(() => session.CommitAsync(ct));

        // Assert — the external edit is intact byte-for-byte; nothing was written.
        (await File.ReadAllBytesAsync(target, ct)).ShouldBe(sabotagedBytes);
    }

    [Fact]
    public async Task SaveAsync_FileChangedOnDiskAfterFork_ThrowsConflictAndWritesNothing()
    {
        // Arrange
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: ct);
        Solution oldSolution = await ws.WorkspaceManager.GetSolutionAsync(ct);

        Document doc = oldSolution.Projects
            .SelectMany(p => p.Documents)
            .First(d => d.FilePath is not null && d.FilePath.EndsWith("Circle.cs", StringComparison.Ordinal));
        SourceText originalText = await doc.GetTextAsync(ct);
        Solution newSolution = oldSolution.WithDocumentText(
            doc.Id, SourceText.From(originalText + "\n// forked edit that must not land\n", originalText.Encoding));

        // Sabotage: an external process rewrites the file after the fork was computed.
        const string ExternalContent = "// rewritten out-of-band by another process\n";
        await File.WriteAllTextAsync(doc.FilePath!, ExternalContent, ct);
        byte[] sabotagedBytes = await File.ReadAllBytesAsync(doc.FilePath!, ct);

        // Act / Assert — the fork commit aborts with a conflict rather than overwriting the external edit.
        await Should.ThrowAsync<FileConflictException>(() =>
            SolutionChangeWriter.SaveAsync(ws.WorkspaceManager, oldSolution, newSolution, ct));

        // Assert — the external edit is intact byte-for-byte; nothing was written.
        (await File.ReadAllBytesAsync(doc.FilePath!, ct)).ShouldBe(sabotagedBytes);
    }

    [Fact]
    public async Task SaveAsync_TargetDeletedOnDiskAfterFork_ThrowsConflictAndDoesNotResurrect()
    {
        // A target present when the edit read it but gone at commit time (an external delete during the
        // compute window) is a conflict, not a silent resurrection: writing would recreate the file the
        // user just deleted.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: ct);
        Solution oldSolution = await ws.WorkspaceManager.GetSolutionAsync(ct);

        Document doc = oldSolution.Projects
            .SelectMany(p => p.Documents)
            .First(d => d.FilePath is not null && d.FilePath.EndsWith("Circle.cs", StringComparison.Ordinal));
        SourceText originalText = await doc.GetTextAsync(ct);
        Solution newSolution = oldSolution.WithDocumentText(
            doc.Id, SourceText.From(originalText + "\n// forked edit\n", originalText.Encoding));

        File.Delete(doc.FilePath!);

        await Should.ThrowAsync<FileConflictException>(() =>
            SolutionChangeWriter.SaveAsync(ws.WorkspaceManager, oldSolution, newSolution, ct));

        // The aborted commit left the file deleted — it did not recreate it.
        File.Exists(doc.FilePath!).ShouldBeFalse();
    }

    private static string SourceFile(Solution solution, string fileName) =>
        solution.Projects
            .SelectMany(p => p.Documents)
            .First(d => d.FilePath is not null && d.FilePath.EndsWith(fileName, StringComparison.Ordinal))
            .FilePath!;
}
