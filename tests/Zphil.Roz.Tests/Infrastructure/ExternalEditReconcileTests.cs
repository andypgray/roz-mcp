using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Models;
using Zphil.Roz.Services;
using Zphil.Roz.Symbols;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Infrastructure;

/// <summary>
///     Tests <see cref="WorkspaceManager.ReconcileAllExternalEditsAsync" /> — the entry-point sweep
///     that absorbs file edits made outside the server before each tool call.
/// </summary>
public class ExternalEditReconcileTests
{
    [Fact]
    public async Task ReconcileAllExternalEditsAsync_FileModifiedExternally_ReloadsContent()
    {
        // Arrange — load workspace, then bypass it to mutate disk directly
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(false, TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        string original = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string modified = original.Replace("Math.PI", "3.14159");
        await File.WriteAllTextAsync(circleFile, modified, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(circleFile, DateTime.UtcNow.AddSeconds(1));

        // Act
        await ws.WorkspaceManager.ReconcileAllExternalEditsAsync(TestContext.Current.CancellationToken);

        // Assert
        Solution solution = await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Document? doc = solution.GetDocumentByPath(circleFile);
        doc.ShouldNotBeNull();
        SourceText text = await doc.GetTextAsync(TestContext.Current.CancellationToken);
        text.ToString().ShouldContain("3.14159");
        text.ToString().ShouldNotContain("Math.PI");
    }

    [Fact]
    public async Task ReconcileAllExternalEditsAsync_NoChanges_DoesNotChangeSolution()
    {
        // Arrange
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(false, TestContext.Current.CancellationToken);
        Solution before = await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Act — call reconcile with no disk changes
        await ws.WorkspaceManager.ReconcileAllExternalEditsAsync(TestContext.Current.CancellationToken);

        // Assert — workspace.CurrentSolution unchanged when no files are stale
        Solution after = await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        after.ShouldBeSameAs(before);
    }

    [Fact]
    public async Task NotifyFileChangedAsync_MtimeBumpedSameContent_DoesNotCreateNewSolution()
    {
        // Arrange — watcher off so the only NotifyFileChangedAsync call is the one we make below
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        Solution before = await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Bump mtime without changing bytes (simulates a ReSharper-style touch loop)
        File.SetLastWriteTimeUtc(circleFile, DateTime.UtcNow.AddSeconds(1));

        // Act — drive the watcher path directly (content == null forces the disk read + content compare)
        await ws.WorkspaceManager.NotifyFileChangedAsync(circleFile, TestContext.Current.CancellationToken);

        // Assert — content unchanged → WithDocumentText skipped → same Solution instance
        Solution after = await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        after.ShouldBeSameAs(before);
    }

    [Fact]
    public async Task NotifyFileChangedAsync_TransientFileLock_RetriesAndSucceeds()
    {
        // Arrange — watcher off; write modified content to disk, then briefly lock the file
        // to simulate a transient external hold (VS, ReSharper, AV mid-write).
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        string original = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string modified = original.Replace("Math.PI", "3.14159");
        await File.WriteAllTextAsync(circleFile, modified, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(circleFile, DateTime.UtcNow.AddSeconds(1));

        var lockHandle = new FileStream(circleFile, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            // 75ms encodes the production IO retry backoff ([50,100,200]ms): it lands after the
            // first 50ms retry, so the lock clears while the reconcile loop is still retrying.
            await Task.Delay(75);
            lockHandle.Dispose();
        }, TestContext.Current.CancellationToken);

        try
        {
            // Act — content == null forces the disk read; retry loop should absorb the lock
            await ws.WorkspaceManager.NotifyFileChangedAsync(circleFile, null, ct: TestContext.Current.CancellationToken);
        }
        finally
        {
            await release;
        }

        // Assert — workspace reflects the on-disk content
        Solution solution = await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Document? doc = solution.GetDocumentByPath(circleFile);
        doc.ShouldNotBeNull();
        (await doc.GetTextAsync(TestContext.Current.CancellationToken)).ToString().ShouldContain("3.14159");
    }

    [Fact]
    public async Task ReconcileAllExternalEditsAsync_ConcurrentCallers_AllSucceed()
    {
        // Arrange — one external edit, then ten concurrent reconciles
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(false, TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        string original = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string modified = original.Replace("Math.PI", "3.14159");
        await File.WriteAllTextAsync(circleFile, modified, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(circleFile, DateTime.UtcNow.AddSeconds(1));

        // Act
        Task[] tasks = Enumerable.Range(0, 10)
            .Select(_ => ws.WorkspaceManager.ReconcileAllExternalEditsAsync(TestContext.Current.CancellationToken))
            .ToArray();
        await Task.WhenAll(tasks);

        // Assert — no exceptions, content is fresh
        Solution solution = await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Document? doc = solution.GetDocumentByPath(circleFile);
        doc.ShouldNotBeNull();
        (await doc.GetTextAsync(TestContext.Current.CancellationToken)).ToString().ShouldContain("3.14159");
    }

    [Fact]
    public async Task ReconcileAllExternalEditsAsync_FollowedByFindSymbol_ToolSeesFreshContent()
    {
        // Arrange — externally add a brand-new top-level type, then run a Roslyn-backed tool
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(false, TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        string original = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        const string addedClass = "\npublic class ExternallyAddedType\n{\n    public int Value { get; set; }\n}\n";
        await File.WriteAllTextAsync(circleFile, original + addedClass, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(circleFile, DateTime.UtcNow.AddSeconds(1));

        // Act — simulate what the GlobalCallToolFilter does before each tool call
        await ws.WorkspaceManager.ReconcileAllExternalEditsAsync(TestContext.Current.CancellationToken);

        var navigationService = new NavigationService(ws.WorkspaceManager, new SymbolResolver(ws.WorkspaceManager));
        FindSymbolResult result = await navigationService.FindSymbolAsync("ExternallyAddedType", matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert — the externally added class is discoverable through the Roslyn-backed search
        result.Symbols.ShouldNotBeEmpty();
        result.Symbols.ShouldContain(s => s.Name == "ExternallyAddedType");
    }

    [Fact]
    public async Task EnsureFilesFreshAsync_DiskMtimeEqualsRecorded_ContentDifferent_ReloadsFile()
    {
        // Arrange — load workspace so knownTimestamps is populated for circleFile.
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        DateTime recordedMtime = File.GetLastWriteTimeUtc(circleFile);

        // Rewrite content out-of-band, then force the on-disk mtime back to the recorded value
        // — simulates a same-tick filesystem collision between an MCP edit (which recorded the
        // mtime) and an immediate external rewrite (NTFS ticks at ~15ms, FAT/exFAT at 2s).
        string original = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string modified = original.Replace("Math.PI", "3.14159");
        await File.WriteAllTextAsync(circleFile, modified, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(circleFile, recordedMtime);

        // Act
        await ws.WorkspaceManager.EnsureFilesFreshAsync([circleFile], TestContext.Current.CancellationToken);

        // Assert — Layer A must fall through to Layer B (ContentEquals) on a tie rather than
        // treating diskMtime == known as "definitely unchanged" and skipping the reload.
        Solution solution = await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Document doc = solution.GetDocumentByPath(circleFile).ShouldNotBeNull();
        SourceText text = await doc.GetTextAsync(TestContext.Current.CancellationToken);
        text.ToString().ShouldContain("3.14159");
        text.ToString().ShouldNotContain("Math.PI");
    }

    [Fact]
    public async Task NotifyFileChangedAsync_DiskMtimeEqualsRecorded_ContentDifferent_UpdatesSolution()
    {
        // Arrange — symmetric coverage of the Layer A short-circuit inside NotifyFileChangedAsync.
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        DateTime recordedMtime = File.GetLastWriteTimeUtc(circleFile);

        string original = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string modified = original.Replace("Math.PI", "3.14159");
        await File.WriteAllTextAsync(circleFile, modified, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(circleFile, recordedMtime);

        // Act — content == null drives the watcher path through Layer A.
        await ws.WorkspaceManager.NotifyFileChangedAsync(circleFile, null, ct: TestContext.Current.CancellationToken);

        // Assert — workspace must reflect the on-disk change despite the mtime tie.
        Solution solution = await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Document doc = solution.GetDocumentByPath(circleFile).ShouldNotBeNull();
        SourceText text = await doc.GetTextAsync(TestContext.Current.CancellationToken);
        text.ToString().ShouldContain("3.14159");
        text.ToString().ShouldNotContain("Math.PI");
    }

    [Fact]
    public async Task EnsureFilesFreshAsync_SteadyStateUnchangedFile_PerformsZeroContentReads()
    {
        // F1: the steady-state sweep must be O(stat) — a provably-unchanged file is trusted by
        // mtime+length and never re-read. Watcher OFF (factory default) drives the same
        // IsMtimeUnchanged -> NotifyFileChangedAsync read path the entry-time sweep uses, deterministically.
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Backdate the mtime to create a deterministic, large racy-window gap, then reload so the
        // backdated value is baked into the timestamp record in BOTH eras. A plain backdate without
        // the reload flakes: today's bug skips a file whose disk mtime is below the recorded value
        // (the backwards-mtime half of F1), so the reload re-baselines known.Mtime to (now-60).
        File.SetLastWriteTimeUtc(circleFile, DateTime.UtcNow.AddSeconds(-60));
        await ws.WorkspaceManager.ReloadAsync(ct: TestContext.Current.CancellationToken);

        // Warmup sweep: a freshly-loaded file is racy (unverified) until its first content check,
        // which (after the fix) promotes it to trusted. Disk untouched, so content compares equal.
        await ws.WorkspaceManager.EnsureFilesFreshAsync([circleFile], TestContext.Current.CancellationToken);
        long readsAfterWarmup = ws.WorkspaceManager.FilesReadDuringReconcile;

        // Act — measure: second sweep with disk untouched.
        await ws.WorkspaceManager.EnsureFilesFreshAsync([circleFile], TestContext.Current.CancellationToken);

        // Assert — zero additional content reads.
        // Today: disk == known, strict `<` is false -> stale -> re-read -> delta >= 1 (FAILS, quantifies F1).
        // Green: mtime+length match and the gap exceeds the racy window -> trusted -> 0 reads.
        (ws.WorkspaceManager.FilesReadDuringReconcile - readsAfterWarmup).ShouldBe(0);
    }

    [Fact]
    public async Task EnsureFilesFreshAsync_DiskMtimeOlderThanRecorded_ContentDifferent_ReloadsFile()
    {
        // F1 second half: a timestamp-preserving restore (robocopy /COPY:T, some git tooling,
        // archive extraction) can leave the on-disk mtime OLDER than what we recorded. The change
        // must still be absorbed — an older mtime is a difference, not proof of freshness.
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        DateTime recorded = File.GetLastWriteTimeUtc(circleFile);

        string original = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string modified = original.Replace("Math.PI", "3.14159");
        await File.WriteAllTextAsync(circleFile, modified, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(circleFile, recorded.AddSeconds(-5));

        // Act
        await ws.WorkspaceManager.EnsureFilesFreshAsync([circleFile], TestContext.Current.CancellationToken);

        // Assert — today: disk(recorded-5) < known(recorded) is true -> "unchanged" -> skipped ->
        // still Math.PI (FAILS). Green: disk != known.Mtime -> changed -> content-check -> reload.
        Solution solution = await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Document doc = solution.GetDocumentByPath(circleFile).ShouldNotBeNull();
        SourceText text = await doc.GetTextAsync(TestContext.Current.CancellationToken);
        text.ToString().ShouldContain("3.14159");
        text.ToString().ShouldNotContain("Math.PI");
    }

    [Fact]
    public async Task WatcherErrorRecovery_SchedulesSingleCoalescedReload()
    {
        // The FileSystemWatcher.Error handler (buffer overflow) routes to TriggerAutoReloadAsync to
        // recover dropped Created/add events the entry-time sweep can't see. A real overflow can't be
        // forced deterministically and the handler is fire-and-forget (Error is a sync void event), so
        // drive its reload target directly and await full completion — which also avoids racing the
        // fixture teardown (a background reload still reopening the solution as the temp dir is deleted).
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: TestContext.Current.CancellationToken);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        var reloadCount = 0;
        using IDisposable sub = ws.WorkspaceManager.RegisterBeforeReload(() => Interlocked.Increment(ref reloadCount));

        // Act — invoke the recovery action and await it to completion.
        await ws.WorkspaceManager.TriggerAutoReloadAsync();
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Assert — exactly one coalesced reload.
        reloadCount.ShouldBe(1);
    }

    [Fact]
    public async Task EnsureFilesFreshAsync_PreservedMtimeChangedLength_AfterPromotion_ReloadsFile()
    {
        // Pins the Length field of the timestamp record. Once a file is promoted to "trusted"
        // (gap > racy window), an external write that changes content but restores the exact recorded
        // mtime would be trusted on mtime alone — the length guard catches it. This is a design guard:
        // it passes today (the strict `<` always content-checks on an equal mtime) AND after the
        // 3-field fix, but would FAIL the rejected 2-field (mtime-only) scheme.
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Backdate + reload + warmup so the file is promoted to trusted (gap ~60s > window).
        File.SetLastWriteTimeUtc(circleFile, DateTime.UtcNow.AddSeconds(-60));
        await ws.WorkspaceManager.ReloadAsync(ct: TestContext.Current.CancellationToken);
        DateTime recorded = File.GetLastWriteTimeUtc(circleFile);
        await ws.WorkspaceManager.EnsureFilesFreshAsync([circleFile], TestContext.Current.CancellationToken);

        // Rewrite to a DIFFERENT length, then restore the exact recorded mtime.
        string original = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string modified = original.Replace("Math.PI", "3.14159") + "\n// length-changing marker\n";
        await File.WriteAllTextAsync(circleFile, modified, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(circleFile, recorded);

        // Act
        await ws.WorkspaceManager.EnsureFilesFreshAsync([circleFile], TestContext.Current.CancellationToken);

        // Assert — length differs from the recorded length -> change forced even on an equal mtime.
        Solution solution = await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Document doc = solution.GetDocumentByPath(circleFile).ShouldNotBeNull();
        SourceText text = await doc.GetTextAsync(TestContext.Current.CancellationToken);
        text.ToString().ShouldContain("3.14159");
        text.ToString().ShouldNotContain("Math.PI");
    }

    [Fact]
    public async Task GetDocumentsByFilenameAsync_AfterReload_ReflectsNewSolution()
    {
        // Guards the lazy filename-index rebuild on reload: the index now keys off solution identity,
        // not just a null-out. The concurrent reload-vs-build race the fix closes can't be reproduced
        // deterministically; this pins the observable "index reflects the post-reload solution" behavior.
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: TestContext.Current.CancellationToken);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Prime the lazy index — the probe filename isn't in the solution yet.
        IReadOnlyList<DocumentPathEntry> before =
            await ws.WorkspaceManager.GetDocumentsByFilenameAsync("IndexProbe.cs", TestContext.Current.CancellationToken);
        before.ShouldBeEmpty();

        // Add a new source file to the SDK-style project (auto-included) and reload.
        string newFile = Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Shapes", "IndexProbe.cs");
        await File.WriteAllTextAsync(newFile, "namespace TestFixture.Shapes;\npublic class IndexProbe { }\n", TestContext.Current.CancellationToken);
        await ws.WorkspaceManager.ReloadAsync(ct: TestContext.Current.CancellationToken);

        // Act — query again after the reload.
        IReadOnlyList<DocumentPathEntry> after =
            await ws.WorkspaceManager.GetDocumentsByFilenameAsync("IndexProbe.cs", TestContext.Current.CancellationToken);

        // Assert — the rebuilt index reflects the new solution's documents.
        after.ShouldNotBeEmpty();
    }
}

/// <summary>
///     Verifies the <c>ROZ_DISABLE_AUTO_REFRESH</c> opt-out wires through to skip the reconcile sweep.
/// </summary>
public sealed class ExternalEditAutoRefreshDisabledTests
{
    [Fact]
    public async Task ReconcileAllExternalEditsAsync_OptOutEnabled_LeavesContentStale()
    {
        // Arrange — opt-out enabled (mirrors what ROZ_DISABLE_AUTO_REFRESH=true does at startup).
        // The factory's default is autoRefreshDisabled: true, which is what we want here.
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        string original = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string modified = original.Replace("Math.PI", "3.14159");
        await File.WriteAllTextAsync(circleFile, modified, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(circleFile, DateTime.UtcNow.AddSeconds(1));

        // Act — reconcile is a no-op when opt-out is set
        await ws.WorkspaceManager.ReconcileAllExternalEditsAsync(TestContext.Current.CancellationToken);

        // Assert — workspace still sees the pre-edit content
        Solution solution = await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Document? doc = solution.GetDocumentByPath(circleFile);
        doc.ShouldNotBeNull();
        (await doc.GetTextAsync(TestContext.Current.CancellationToken)).ToString().ShouldContain("Math.PI");
    }

    [Fact]
    public async Task ReconcileAllExternalEditsAsync_OptOutEnabled_DoesNotReloadOnDelete()
    {
        // Arrange — opt-out enabled; delete a file from disk
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(ct: TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        File.Delete(circleFile);

        // Act — reconcile is a no-op when opt-out is set
        await ws.WorkspaceManager.ReconcileAllExternalEditsAsync(TestContext.Current.CancellationToken);

        // Assert — workspace still sees the deleted file as a document
        Solution solution = await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        solution.GetDocumentIdsWithFilePath(circleFile).ShouldNotBeEmpty();
    }
}

/// <summary>
///     Tests <see cref="WorkspaceManager.ReconcileAllExternalEditsAsync" /> reactions to external file
///     deletions — the sweep short-circuits to a full solution reload on the first missing document.
/// </summary>
public sealed class ExternalEditDeleteReconcileTests
{
    [Fact]
    public async Task ReconcileAllExternalEditsAsync_FileDeletedExternally_TriggersReloadAndDocGone()
    {
        // Arrange
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(false, TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        File.Delete(circleFile);

        // Act
        await ws.WorkspaceManager.ReconcileAllExternalEditsAsync(TestContext.Current.CancellationToken);

        // Assert — post-reload solution no longer contains the deleted document
        Solution solution = await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        solution.GetDocumentIdsWithFilePath(circleFile).ShouldBeEmpty();
    }

    [Fact]
    public async Task ReconcileAllExternalEditsAsync_FileDeletedExternally_FollowedByFindSymbol_NoStaleSymbol()
    {
        // Arrange
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(false, TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        File.Delete(circleFile);

        // Act — the GlobalCallToolFilter does this before each tool call
        await ws.WorkspaceManager.ReconcileAllExternalEditsAsync(TestContext.Current.CancellationToken);

        var navigationService = new NavigationService(ws.WorkspaceManager, new SymbolResolver(ws.WorkspaceManager));
        FindSymbolResult result = await navigationService.FindSymbolAsync("Circle", matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert — Circle is gone (the file containing its declaration was deleted)
        result.Symbols.ShouldNotContain(s => s.Name == "Circle");
    }

    [Fact]
    public async Task ReconcileAllExternalEditsAsync_OnlyOneReloadPerCall()
    {
        // Arrange — delete two files; reconcile should short-circuit to a single reload
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(false, TestContext.Current.CancellationToken);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        var reloadCount = 0;
        using IDisposable sub = ws.WorkspaceManager.RegisterBeforeReload(() => Interlocked.Increment(ref reloadCount));

        File.Delete(TestFileHelper.CircleFile(ws));
        File.Delete(TestFileHelper.TriangleFile(ws));

        // Act
        await ws.WorkspaceManager.ReconcileAllExternalEditsAsync(TestContext.Current.CancellationToken);

        // Assert — single reload covers both deletes
        reloadCount.ShouldBe(1);
        Solution solution = await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        solution.GetDocumentIdsWithFilePath(TestFileHelper.CircleFile(ws)).ShouldBeEmpty();
        solution.GetDocumentIdsWithFilePath(TestFileHelper.TriangleFile(ws)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ReconcileAllExternalEditsAsync_DeleteThenSecondReconcile_NoSecondReload()
    {
        // Arrange
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(false, TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        var reloadCount = 0;
        using IDisposable sub = ws.WorkspaceManager.RegisterBeforeReload(() => Interlocked.Increment(ref reloadCount));

        File.Delete(circleFile);
        await ws.WorkspaceManager.ReconcileAllExternalEditsAsync(TestContext.Current.CancellationToken);
        reloadCount.ShouldBe(1);

        // Act — a second reconcile with no further disk changes
        await ws.WorkspaceManager.ReconcileAllExternalEditsAsync(TestContext.Current.CancellationToken);

        // Assert — no additional reload triggered
        reloadCount.ShouldBe(1);
    }

    [Fact]
    public async Task ReconcileAllExternalEditsAsync_DeleteAndModify_DeleteShortCircuits()
    {
        // Arrange — delete one file, modify another. The reload should pick up both changes
        // (the modification persists on disk and is re-read by the fresh load).
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(false, TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        string shapeFile = TestFileHelper.ShapeFile(ws);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        var reloadCount = 0;
        using IDisposable sub = ws.WorkspaceManager.RegisterBeforeReload(() => Interlocked.Increment(ref reloadCount));

        string original = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        string modified = original + "\n// external-edit-marker";
        await File.WriteAllTextAsync(shapeFile, modified, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(shapeFile, DateTime.UtcNow.AddSeconds(1));
        File.Delete(circleFile);

        // Act
        await ws.WorkspaceManager.ReconcileAllExternalEditsAsync(TestContext.Current.CancellationToken);

        // Assert — exactly one reload
        reloadCount.ShouldBe(1);

        Solution solution = await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        solution.GetDocumentIdsWithFilePath(circleFile).ShouldBeEmpty();
        Document? shapeDoc = solution.GetDocumentByPath(shapeFile);
        shapeDoc.ShouldNotBeNull();
        (await shapeDoc.GetTextAsync(TestContext.Current.CancellationToken)).ToString().ShouldContain("external-edit-marker");
    }

    [Fact]
    public async Task ReconcileAllExternalEditsAsync_DeletedFileNotInSolution_DoesNotTriggerReload()
    {
        // Arrange — write a .cs file in solutionDir but not part of any project, then delete it.
        // The sweep iterates documents (which never included the file), so reconcile is a no-op.
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(false, TestContext.Current.CancellationToken);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        string strayFile = Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "Stray.cs");
        await File.WriteAllTextAsync(strayFile, "// outside any project", TestContext.Current.CancellationToken);
        File.Delete(strayFile);

        var reloadCount = 0;
        using IDisposable sub = ws.WorkspaceManager.RegisterBeforeReload(() => Interlocked.Increment(ref reloadCount));

        // Act
        await ws.WorkspaceManager.ReconcileAllExternalEditsAsync(TestContext.Current.CancellationToken);

        // Assert — no reload triggered
        reloadCount.ShouldBe(0);
    }
}

/// <summary>
///     Best-effort tests for the background <see cref="System.IO.FileSystemWatcher" /> that triggers
///     auto-reloads on add/delete/rename. These polling-based tests can flake under heavy load or
///     on systems where watcher events are coalesced/dropped — they verify the happy path.
/// </summary>
// Flaky: depends on FileSystemWatcher event delivery latency.
public sealed class ExternalEditWatcherTests
{
    private const int PollTimeoutMs = 5000;
    private const int PollIntervalMs = 50;

    [Fact]
    public async Task Watcher_FileDeletedExternally_BackgroundReloadOccurs()
    {
        // Arrange
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(false, TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Act — delete on disk, then poll for the watcher to drive a reload
        File.Delete(circleFile);

        bool gone = await PollAsync(async () =>
        {
            Solution s = await ws.WorkspaceManager.GetSolutionAsync();
            return s.GetDocumentIdsWithFilePath(circleFile).Length == 0;
        });

        // Assert
        gone.ShouldBeTrue("watcher did not drop the deleted document within the poll window");
    }

    [Fact]
    public async Task Watcher_FileCreatedAndMatchingGlob_BackgroundReloadOccurs()
    {
        // Arrange
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(false, TestContext.Current.CancellationToken);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        string newFile = Path.Combine(
            ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Shapes", "NewlyAdded.cs");
        const string content = "namespace TestFixture.Shapes;\npublic class NewlyAdded { }\n";

        // Act — write a new .cs in the SDK-style project's tree
        await File.WriteAllTextAsync(newFile, content, TestContext.Current.CancellationToken);

        bool found = await PollAsync(async () =>
        {
            Solution s = await ws.WorkspaceManager.GetSolutionAsync();
            return s.GetDocumentIdsWithFilePath(newFile).Length > 0;
        });

        // Assert
        found.ShouldBeTrue("watcher did not pick up the new file within the poll window");
    }

    [Fact]
    public async Task Watcher_FileRenamed_BackgroundReloadOccurs()
    {
        // Arrange
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(false, TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        string renamedFile = Path.Combine(Path.GetDirectoryName(circleFile)!, "Circle.Renamed.cs");
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Act — File.Move triggers a Renamed event
        File.Move(circleFile, renamedFile);

        bool converged = await PollAsync(async () =>
        {
            Solution s = await ws.WorkspaceManager.GetSolutionAsync();
            return s.GetDocumentIdsWithFilePath(circleFile).Length == 0
                   && s.GetDocumentIdsWithFilePath(renamedFile).Length > 0;
        });

        // Assert
        converged.ShouldBeTrue("watcher did not converge to the renamed path within the poll window");
    }

    [Fact]
    public async Task Watcher_TransientCreateAndDelete_NoReloadIfDebounceCovers()
    {
        // Arrange — write+delete inside the debounce window so the state-at-flush is !onDisk && !inSolution
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(false, TestContext.Current.CancellationToken);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        var reloadCount = 0;
        using IDisposable sub = ws.WorkspaceManager.RegisterBeforeReload(() => Interlocked.Increment(ref reloadCount));

        string transient = Path.Combine(
            ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Shapes", "Transient.cs");
        await File.WriteAllTextAsync(transient, "// transient", TestContext.Current.CancellationToken);
        File.Delete(transient);

        // Act — wait past the second flush tick (first tick at ~1000 ms may skip if entry age
        // is just under the debounce; second tick at ~2000 ms guarantees the transient is processed).
        await Task.Delay(2500, TestContext.Current.CancellationToken);

        // Assert — no reload occurred
        reloadCount.ShouldBe(0);
    }

    [Fact]
    public async Task Watcher_FileModified_BackgroundUpdateOccurs()
    {
        // Arrange — modify an existing solution file. State-at-flush sees onDisk && inSolution,
        // so the watcher routes to ScheduleFileChanged (not a full reload).
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(false, TestContext.Current.CancellationToken);
        string circleFile = TestFileHelper.CircleFile(ws);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        var reloadCount = 0;
        using IDisposable sub = ws.WorkspaceManager.RegisterBeforeReload(() => Interlocked.Increment(ref reloadCount));

        string original = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string modified = original.Replace("Math.PI", "3.14159");
        await File.WriteAllTextAsync(circleFile, modified, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(circleFile, DateTime.UtcNow.AddSeconds(1));

        // Act — poll until the in-memory solution reflects the disk content
        bool converged = await PollAsync(async () =>
        {
            Solution s = await ws.WorkspaceManager.GetSolutionAsync();
            Document? doc = s.GetDocumentByPath(circleFile);
            if (doc is null)
            {
                return false;
            }

            return (await doc.GetTextAsync()).ToString().Contains("3.14159");
        });

        // Assert — content updated, and no full reload was triggered (modify routes to ScheduleFileChanged)
        converged.ShouldBeTrue("watcher did not pick up the modification within the poll window");
        reloadCount.ShouldBe(0);
    }

    [Fact]
    public async Task Watcher_BurstAddsAcrossFiles_SingleReload()
    {
        // Arrange — create five files in rapid sequence; the coalesced reload should fire exactly once
        await using TempWorkspace ws = await TempWorkspaceFactory.CreateAsync(false, TestContext.Current.CancellationToken);
        await ws.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        var reloadCount = 0;
        using IDisposable sub = ws.WorkspaceManager.RegisterBeforeReload(() => Interlocked.Increment(ref reloadCount));

        string shapesDir = Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Shapes");
        string[] paths = Enumerable.Range(0, 5)
            .Select(i => Path.Combine(shapesDir, $"Burst{i}.cs"))
            .ToArray();

        // Act
        foreach (string path in paths)
        {
            await File.WriteAllTextAsync(path, $"namespace TestFixture.Shapes; public class Burst{Path.GetFileNameWithoutExtension(path)} {{ }}", TestContext.Current.CancellationToken);
        }

        bool found = await PollAsync(async () =>
        {
            Solution s = await ws.WorkspaceManager.GetSolutionAsync();
            return paths.All(p => s.GetDocumentIdsWithFilePath(p).Length > 0);
        });

        // Assert
        found.ShouldBeTrue("watcher did not pick up all burst-created files within the poll window");
        reloadCount.ShouldBe(1);
    }

    private static async Task<bool> PollAsync(Func<Task<bool>> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(PollTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
            {
                return true;
            }

            await Task.Delay(PollIntervalMs);
        }

        return await predicate();
    }
}
