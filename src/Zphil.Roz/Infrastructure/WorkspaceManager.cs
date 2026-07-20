using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using Zphil.Roz.Extensions;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Infrastructure;

/// <summary>
///     Manages the MSBuildWorkspace lifecycle. Singleton — injected into all tool classes.
///     Solution loading starts eagerly in the background so the workspace is typically
///     warm by the time the first tool call arrives.
/// </summary>
/// <remarks>
///     <para>
///         <b>Concurrency model:</b> The MCP protocol may invoke different tools concurrently on the
///         same server. All edit/mutation tools are serialized by <see cref="Pipeline.EditSerializationFilter" />
///         at the MCP filter layer so only one edit runs at a time. Read-only tools run concurrently and are safe
///         because Roslyn <see cref="Solution" /> objects are immutable snapshots.
///     </para>
///     <para>
///         Within a single edit operation, the internal <c>gate</c> semaphore serializes workspace mutations
///         (<see cref="NotifyFileChangedAsync(string,string?,System.Text.Encoding?,System.Threading.CancellationToken)" />
///         and <see cref="ScheduleReloadAsync" />), while <see cref="ScheduleFileChanged" /> chains fire-and-forget
///         updates via <c>pendingUpdate</c>. The next <see cref="GetSolutionAsync" /> call drains pending updates
///         before returning.
///     </para>
///     <para>
///         <b>External-edit detection:</b> A two-layer scheme keeps the workspace in sync with edits made
///         outside the server (other editors, <c>dotnet format</c>, branch switches, etc.).
///         <see cref="ReconcileAllExternalEditsAsync" /> runs at every tool-call entry — that's the
///         correctness guarantee for modifications and deletions. A background
///         <see cref="System.IO.FileSystemWatcher" /> opportunistically schedules per-file reloads via
///         <see cref="ScheduleFileChanged" /> for modifications and triggers a coalesced solution reload
///         when adds, deletes, or renames are detected (state-at-flush classification handles atomic-rename
///         and delete-recreate ambiguity). Adds are detected only by the watcher; the entry-time sweep would
///         have to walk the solution tree on every tool call. Both layers can be disabled with
///         <c>ROZ_DISABLE_AUTO_REFRESH=true</c>.
///     </para>
/// </remarks>
internal sealed class WorkspaceManager : IAsyncDisposable
{
    private const int ExternalEditDebounceMs = 1000;

    /// <summary>
    ///     Filesystem-mtime resolution slop. An entry recorded within this window of the file's own
    ///     mtime can't be trusted on mtime-equality alone — a same-tick external write could share the
    ///     mtime — so it stays content-verified until the gap widens. 2s covers the FAT/exFAT 2-second
    ///     tick floor; NTFS resolution (~100ns) is far finer, so this is conservative on the common case.
    /// </summary>
    private static readonly TimeSpan RacyWindow = TimeSpan.FromSeconds(2);

    private readonly bool autoRefreshDisabled;

    /// <summary>
    ///     Cancelled exactly once, on <see cref="DisposeAsync" />, to unwind any in-flight compilation
    ///     warmup promptly. Long-lived and never reset: each load derives its warmup token from a fresh
    ///     linked CTS off this one, so a reload after a dispose still starts with a live token.
    /// </summary>
    private readonly CancellationTokenSource disposeCts = new();

    private readonly string? explicitPath;
    private readonly Lock fieldLock = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<string, FileTimestamp> knownTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<WorkspaceManager> logger;

    /// <summary>
    ///     Test-only async hook awaited inside the load just before the snapshot-ready signal fires,
    ///     while the load still holds <see cref="gate" />. Lets a test park the load in its pre-ready
    ///     window to exercise a dispose-mid-load. Null (a no-op) in production.
    /// </summary>
    private readonly Func<Task>? onBeforeReadySignal;

    private readonly ConcurrentDictionary<string, long> pendingExternalEdits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Test-only diagnostic counter: the number of on-disk content reads performed by the
    ///     external-edit reconcile path (the <see cref="FileUtility.ReadFileWithEncodingAsync" />
    ///     call in <see cref="NotifyFileChangedAsync(string,string?,Encoding?,CancellationToken)" />).
    ///     Lets tests assert the steady-state sweep is O(stat) — zero content reads when nothing on
    ///     disk has changed. Never consulted in production. Incremented under <see cref="gate" />, so
    ///     the <see cref="Interlocked" /> use is belt-and-suspenders rather than a correctness need.
    /// </summary>
    internal long FilesReadDuringReconcile;

    private int disposed;

    private Lazy<Dictionary<string, List<DocumentPathEntry>>>? documentPathIndex;
    private Solution? documentPathIndexSolution;
    private Timer? externalEditFlushTimer;
    private FileSystemWatcher? externalEditWatcher;
    private Task? loadReadyTask;
    private Task pendingUpdate = Task.CompletedTask;

    private volatile string? solutionPath;

    /// <summary>
    ///     Completes (successfully) the moment the loaded <see cref="Solution" /> snapshot is usable —
    ///     after open + unresolved-reference strip + timestamp recording, but <em>before</em> compilation
    ///     warmup. <see cref="GetSolutionAsync" />/<see cref="GetSolutionIfLoaded" /> gate on this rather
    ///     than the full <see cref="loadReadyTask" />, so the first tool call no longer blocks behind every
    ///     project compiling. Reset alongside <see cref="loadReadyTask" /> on reload.
    ///     <c>RunContinuationsAsynchronously</c> keeps a reader's continuation off the loader thread that
    ///     completes it while holding <see cref="gate" />.
    /// </summary>
    private TaskCompletionSource solutionReadyTask = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private MSBuildWorkspace? workspace;

    /// <param name="logger">Diagnostic logger for solution-load and workspace events.</param>
    /// <param name="explicitPath">
    ///     Path to a specific <c>.sln</c>/<c>.slnx</c> file. Falls back to <c>ROZ_SOLUTION_PATH</c>
    ///     and CWD-walk discovery when null.
    /// </param>
    /// <param name="autoRefreshDisabled">
    ///     When non-null, overrides the <c>ROZ_DISABLE_AUTO_REFRESH</c> env var. Tests pass <c>true</c>
    ///     to keep the background watcher from racing with fixture file operations.
    /// </param>
    public WorkspaceManager(ILogger<WorkspaceManager> logger, string? explicitPath = null, bool? autoRefreshDisabled = null)
        : this(logger, explicitPath, autoRefreshDisabled, null) { }

    /// <summary>
    ///     Test seam: the public constructor delegates here passing <c>null</c>. <paramref name="onBeforeReadySignal" />
    ///     is the test-only hook awaited just before the snapshot-ready signal (see <see cref="onBeforeReadySignal" />),
    ///     set here — before the background load starts — so a test can park the load pre-ready without a race.
    /// </summary>
    internal WorkspaceManager(
        ILogger<WorkspaceManager> logger, string? explicitPath, bool? autoRefreshDisabled, Func<Task>? onBeforeReadySignal)
    {
        this.logger = logger;
        this.explicitPath = explicitPath;
        this.autoRefreshDisabled = autoRefreshDisabled ?? RozEnvVars.DisableAutoRefresh.Read();
        this.onBeforeReadySignal = onBeforeReadySignal;

        // Fire-and-forget: start loading immediately in the background
        loadReadyTask = Task.Run(() => LoadSolutionInternalAsync());
    }

    /// <summary>
    ///     The absolute path to the loaded .sln file. Null until discovery completes.
    /// </summary>
    /// <remarks>
    ///     Volatile-backed: written under <see cref="gate" /> in <see cref="LoadSolutionInternalAsync" />
    ///     but read lock-free on the FileSystemWatcher thread (via <see cref="SolutionDirectory" /> in
    ///     <see cref="IsBuildOutputFullPath" />), so the field guarantees cross-thread visibility.
    /// </remarks>
    public string? SolutionPath
    {
        get => solutionPath;
        private set => solutionPath = value;
    }

    /// <summary>
    ///     The directory containing the solution file. Null until discovery completes.
    /// </summary>
    public string? SolutionDirectory
    {
        get
        {
            string? path = solutionPath; // single volatile read; avoids a torn double property read
            return path is not null ? Path.GetDirectoryName(path) : null;
        }
    }

    /// <summary>
    ///     Test-only: the in-flight (or completed) background load task, which completes only after full
    ///     compilation warmup. The cold-start stress test awaits it to time total warmup and checks
    ///     <see cref="Task.IsCompleted" /> at the first-read point to confirm the read returned before
    ///     warmup finished. Captured under <see cref="fieldLock" /> like every other access. Not used in
    ///     production — reads gate on <see cref="solutionReadyTask" /> instead.
    /// </summary>
    internal Task? LoadReadyTask
    {
        get
        {
            lock (fieldLock)
            {
                return loadReadyTask;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Idempotent: tests dispose explicitly and the fixture disposes again. A second call returns
        // here, which also keeps the disposeCts.Cancel() below safe against double-dispose.
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        // Cancel in-flight warmup first — before the drain/gate waits below — so it unwinds and
        // releases the gate promptly instead of running past the 5-second acquisition window.
        disposeCts.Cancel();

        // Null the workspace and complete the ready signal, both under fieldLock and before the waits,
        // so a GetSolutionAsync parked on solutionReadyTask resumes, re-reads workspace == null, and
        // throws UserErrorException instead of hanging on a dispose mid-load. The watcher start in the
        // loader observes `disposed` (set above) and won't publish a watcher we've already stopped. The
        // actual Dispose() of the workspace stays behind the gate so it never races a TryApplyChanges.
        MSBuildWorkspace? toDispose;
        lock (fieldLock)
        {
            toDispose = workspace;
            workspace = null;
            loadReadyTask = null;
            solutionReadyTask.TrySetResult();
        }

        // Stop the watcher/timer so no new external-edit work is scheduled while we drain.
        await StopExternalEditWatcherAsync();

        // Drain any in-flight scheduled update so we don't dispose the workspace mid-TryApplyChanges
        // (half-applied edit) or dispose the gate while NotifyFileChangedAsync sits between its
        // WaitAsync and Release. Bounded + best-effort: a hung update must not block process exit.
        try
        {
            await DrainPendingUpdatesAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // Pending update hung; proceed — disposing is the safer outcome on shutdown.
        }

        // Acquire the gate so we serialize against any update that slipped in just after the drain.
        // The disposed-guard above means only one DisposeAsync ever reaches here, so the gate is live.
        bool acquired = await gate.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            toDispose?.Dispose();
        }
        finally
        {
            if (acquired)
            {
                gate.Release();
            }

            gate.Dispose(); // idempotent; always runs
            disposeCts.Dispose(); // idempotent; safe after Cancel
        }
    }

    private event Action? BeforeReload;

    /// <summary>
    ///     Registers a callback invoked before each workspace reload. Returns an
    ///     <see cref="IDisposable" /> that unsubscribes when disposed.
    /// </summary>
    /// <remarks>
    ///     Multiple callers can subscribe; all registered handlers fire on each reload, <em>after</em> the
    ///     workspace swap (B1). Handlers are invoked independently — a throwing handler is logged, not
    ///     propagated, so it can neither skip a sibling handler's cache invalidation nor abort the reload
    ///     (which has already committed by the time handlers run).
    /// </remarks>
    internal IDisposable RegisterBeforeReload(Action callback)
    {
        BeforeReload += callback;
        return new BeforeReloadSubscription(this, callback);
    }

    private void InvokeBeforeReloadCallbacks()
    {
        // Invoked after the reload's swap (B1), so the reload is already committed when handlers fire — a
        // throwing handler can no longer abort it. Invoke each independently and log rather than propagate,
        // so one handler's failure can't skip another's cache invalidation (e.g. a throwing ClearBaseline
        // must not leave FixerCatalog's cache stale across the reload).
        Delegate[]? handlers = BeforeReload?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate handler in handlers)
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Before-reload handler threw (reload already committed); continuing");
            }
        }
    }

    /// <summary>
    ///     Returns the solution directory, awaiting the solution load if necessary.
    /// </summary>
    public async Task<string> GetRequiredSolutionDirectoryAsync(CancellationToken ct = default)
    {
        await GetSolutionAsync(ct);
        return SolutionDirectory!;
    }

    /// <summary>
    ///     Returns the path relative to the solution directory.
    /// </summary>
    /// <remarks>
    ///     Only call after the solution has been loaded (e.g. after <see cref="GetSolutionAsync" />
    ///     or <see cref="GetRequiredSolutionDirectoryAsync" />).
    /// </remarks>
    public string GetRelativePath(string absolutePath) =>
        Path.GetRelativePath(
            SolutionDirectory ?? throw new InvalidOperationException(
                "GetRelativePath called before solution loaded — await GetRequiredSolutionDirectoryAsync first."),
            absolutePath);

    /// <summary>
    ///     Returns a display-friendly path relative to the solution directory when possible,
    ///     otherwise the input unchanged. Safe to call with user-supplied paths from failing
    ///     ops — never throws — so batch error-reporting paths can produce consistent output
    ///     regardless of whether the caller passed absolute or relative input.
    /// </summary>
    public string GetDisplayPath(string filePath)
    {
        if (SolutionDirectory is null || String.IsNullOrWhiteSpace(filePath))
        {
            return filePath;
        }

        try
        {
            string absolute = PathExtensions.ResolveFilePath(filePath, SolutionDirectory);
            return Path.GetRelativePath(SolutionDirectory, absolute);
        }
        catch
        {
            return filePath;
        }
    }

    /// <summary>
    ///     Returns the current solution. If still loading, awaits the background task.
    ///     Drains any pending file-change notification first so the returned solution is up-to-date.
    /// </summary>
    public async Task<Solution> GetSolutionAsync(CancellationToken ct = default)
    {
        await DrainPendingUpdatesAsync();

        TaskCompletionSource readyTcs;
        Task? loadTask;
        lock (fieldLock)
        {
            // Capture both from one snapshot so a reload (which swaps both atomically) is never seen half-applied.
            readyTcs = solutionReadyTask;
            loadTask = loadReadyTask;
        }

        if (loadTask is null)
        {
            throw new UserErrorException("Workspace has been disposed. The MCP server may need to be restarted.");
        }

        // Wait for the snapshot to become usable, NOT for full compilation warmup — warmup runs on in the
        // background and the specific compilations a tool touches are forced (and shared) on demand.
        await readyTcs.Task.WaitAsync(ct);

        MSBuildWorkspace? ws;
        lock (fieldLock)
        {
            ws = workspace;
        }

        if (ws is null)
        {
            throw new UserErrorException("Workspace has been disposed. The MCP server may need to be restarted.");
        }

        return ws.CurrentSolution;
    }

    /// <summary>
    ///     Returns the current solution if already loaded, or null if still loading.
    /// </summary>
    public Solution? GetSolutionIfLoaded()
    {
        TaskCompletionSource readyTcs;
        MSBuildWorkspace? ws;
        lock (fieldLock)
        {
            readyTcs = solutionReadyTask;
            ws = workspace;
        }

        // The snapshot is usable once the ready signal fires (pre-warmup); callers that force
        // compilation share the in-flight warmup's work. After dispose, ws is null → null here.
        if (readyTcs.Task.IsCompletedSuccessfully && ws is not null)
        {
            return ws.CurrentSolution;
        }

        return null;
    }

    /// <summary>
    ///     Returns all indexed documents whose final path segment matches <paramref name="filename" />.
    ///     Used by <see cref="FilePathResolver" /> for suffix-match resolution.
    /// </summary>
    /// <remarks>
    ///     The index is built lazily on first access per workspace load and invalidated by
    ///     <see cref="ScheduleReloadAsync" />. Build-output paths (<c>obj/</c>, <c>bin/</c>) are
    ///     excluded so generated <c>.g.cs</c> files don't pollute matches.
    /// </remarks>
    internal async Task<IReadOnlyList<DocumentPathEntry>> GetDocumentsByFilenameAsync(
        string filename, CancellationToken ct = default)
    {
        Solution solution = await GetSolutionAsync(ct);

        Lazy<Dictionary<string, List<DocumentPathEntry>>> idx;
        lock (fieldLock)
        {
            // `solution` was captured before this lock, so a reload may have published a new
            // CurrentSolution in between. Rebuild when the cached index was built for a different
            // solution (identity check), not only when it was null'd — otherwise a build racing a
            // reload could leave an index for a stale solution published to later callers.
            if (documentPathIndex is null || !ReferenceEquals(documentPathIndexSolution, solution))
            {
                documentPathIndexSolution = solution;
                documentPathIndex = new Lazy<Dictionary<string, List<DocumentPathEntry>>>(
                    () => BuildDocumentPathIndex(solution, SolutionDirectory!),
                    LazyThreadSafetyMode.ExecutionAndPublication);
            }

            idx = documentPathIndex;
        }

        return idx.Value.TryGetValue(filename, out List<DocumentPathEntry>? list)
            ? list
            : [];
    }

    private static Dictionary<string, List<DocumentPathEntry>> BuildDocumentPathIndex(
        Solution solution, string solutionDir)
    {
        Dictionary<string, List<DocumentPathEntry>> index = new(StringComparer.OrdinalIgnoreCase);
        foreach (DocumentPathEntry entry in solution.EnumerateSourceDocumentPaths(solutionDir))
        {
            string fname = Path.GetFileName(entry.AbsolutePath);
            if (!index.TryGetValue(fname, out List<DocumentPathEntry>? list))
            {
                list = [];
                index[fname] = list;
            }

            list.Add(entry);
        }

        return index;
    }

    /// <summary>
    ///     Checks whether any of the given files have been modified on disk since the workspace
    ///     last loaded them, reloading stale files into the workspace.
    /// </summary>
    public async Task EnsureFilesFreshAsync(IEnumerable<string> filePaths, CancellationToken ct = default)
    {
        List<string> stale = [];
        foreach (string filePath in filePaths)
        {
            string fullPath = Path.GetFullPath(filePath);

            // Roslyn's MSBuildWorkspace exposes no per-document removal API, so a missing
            // tracked file short-circuits to a full reload. Untracked missing paths (typos)
            // are skipped — the caller's resolver surfaces a clearer error than a stray reload.
            if (!File.Exists(fullPath))
            {
                if (knownTimestamps.ContainsKey(fullPath))
                {
                    logger.LogInformation("File deleted externally, scheduling reload: {Path}", fullPath);
                    // Direct reload, not TriggerAutoReloadAsync — see the reload-vs-warmup coalescing note
                    // in ReconcileAllExternalEditsAsync. A delete must reload against disk, not coalesce
                    // against an in-flight load that opened the file before the deletion.
                    await ScheduleReloadAsync(ct: ct);
                    return;
                }

                continue;
            }

            if (IsMtimeUnchanged(fullPath))
            {
                continue;
            }

            stale.Add(fullPath);
        }

        await ReloadStaleFilesAsync(stale, ct);
    }

    /// <summary>
    ///     Reconciles every workspace document against disk: missing files trigger a full reload;
    ///     surviving files whose on-disk mtime or length no longer match the recorded fingerprint
    ///     (or whose fingerprint is still within the racy window) are content-checked and reloaded
    ///     individually. See <see cref="IsMtimeUnchanged" /> for the freshness predicate.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Called at tool entry to absorb edits made outside the server (other editors,
    ///         <c>dotnet format</c>, branch switches, etc.) before a tool runs.
    ///         No-op when <c>ROZ_DISABLE_AUTO_REFRESH=true</c>.
    ///     </para>
    ///     <para>
    ///         The background <see cref="FileSystemWatcher" /> is a latency optimization that fires
    ///         <see cref="ScheduleFileChanged" /> (and triggers reloads for adds/deletes) so this sweep
    ///         is usually a no-op. <see cref="GetSolutionAsync" /> drains those pending updates first,
    ///         so any watcher-scheduled reloads complete before the sweep enumerates documents.
    ///         The sweep does not detect newly-added files; that path is the watcher only.
    ///     </para>
    /// </remarks>
    public async Task ReconcileAllExternalEditsAsync(CancellationToken ct = default)
    {
        if (autoRefreshDisabled)
        {
            return;
        }

        Solution solution = await GetSolutionAsync(ct);

        List<string> stale = [];
        foreach (Document doc in solution.Projects.SelectMany(p => p.Documents))
        {
            if (doc.FilePath is null)
            {
                continue;
            }

            string fullPath = doc.FilePath;

            if (!File.Exists(fullPath))
            {
                logger.LogInformation("Document deleted externally, scheduling reload: {Path}", fullPath);
                // Call ScheduleReloadAsync directly rather than coalescing through TriggerAutoReloadAsync:
                // the coalescing check keys on "a load is in flight", but an initial/warmup load already
                // opened the solution BEFORE this deletion, so coalescing against it would leave the
                // deleted document in the snapshot (a stale read). A direct reload re-reads disk and is
                // the correctness guarantee the entry-time sweep owes its caller. The residual
                // concurrent-sweep double-reload is a known, bounded waste.
                await ScheduleReloadAsync(ct: ct);
                return;
            }

            if (IsMtimeUnchanged(fullPath))
            {
                continue;
            }

            stale.Add(fullPath);
        }

        await ReloadStaleFilesAsync(stale, ct);
    }

    /// <summary>
    ///     Reloads the collected externally-modified files into the workspace: one Information
    ///     summary line plus per-file detail at Debug. Logging every path at Information floods
    ///     the sink on whole-solution sweeps (branch switch, <c>git reset --hard</c> of a large
    ///     tree), so the per-file lines live at Debug.
    /// </summary>
    private async Task ReloadStaleFilesAsync(List<string> stale, CancellationToken ct)
    {
        if (stale.Count == 0)
        {
            return;
        }

        logger.LogInformation("Reloading {Count} externally-modified file(s) into the workspace", stale.Count);
        foreach (string path in stale)
        {
            logger.LogDebug("File modified externally, reloading: {Path}", path);
            try
            {
                await NotifyFileChangedAsync(path, ct);
            }
            catch (UnsupportedEncodingException ex)
            {
                // The sweep must tolerate a non-UTF-8 file on disk. Rethrowing here would poison-pill the
                // whole reconcile (the entry-time ReconcileAllExternalEditsAsync AND the edit-time
                // EnsureFilesFreshAsync both funnel through here), leaving the file permanently stale and
                // re-throwing on every later tool call — bricking the server for that solution.
                // Deliberately do NOT RecordTimestamp: the file stays stale and is cheaply re-skipped
                // (one failed read) until re-saved as UTF-8, rather than masking a file that later becomes
                // valid. For a UTF-16 file this is strictly more correct than reloading — MSBuildWorkspace
                // already decoded it on load, so keeping that version beats overwriting it with a mojibake
                // UTF-8 re-read.
                logger.LogWarning(ex, "Skipping reconcile of file with unsupported encoding: {Path}", path);
            }
        }
    }

    /// <summary>
    ///     Disposes the current workspace and reloads from disk.
    ///     Use after git checkout, branch switch, or NuGet restore.
    /// </summary>
    public async Task<Solution> ReloadAsync(
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
    {
        await ScheduleReloadAsync(progress, ct);
        return await GetSolutionAsync(ct);
    }

    /// <summary>
    ///     Disposes the current workspace and starts a background reload from disk.
    /// </summary>
    public async Task ScheduleReloadAsync(
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
    {
        await StopExternalEditWatcherAsync();

        await gate.WaitAsync(ct);
        try
        {
            // LoadSolutionInternalAsync's first statement is `await gate.WaitAsync()`. Since we
            // already hold gate, that await suspends and a hot Task is returned synchronously
            // back to us — safe to call from inside fieldLock without blocking the lock.
            MSBuildWorkspace? toDispose;
            lock (fieldLock)
            {
                toDispose = workspace;
                workspace = null;
                documentPathIndex = null;
                // Fresh ready signal for this load generation, swapped atomically with loadReadyTask so a
                // concurrent GetSolutionAsync captures a consistent (readyTcs, loadTask) pair.
                solutionReadyTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                loadReadyTask = LoadSolutionInternalAsync(progress);
            }

            // Dispose the old workspace (and clear timestamps) immediately after the swap — the swap already
            // detached toDispose, so this is independent of the before-reload callbacks below. Doing it here,
            // BEFORE those callbacks, means a throwing handler can't skip this and leak the old
            // MSBuildWorkspace + its BuildHost child (the very orphan ServerShutdown exists to prevent).
            toDispose?.Dispose();
            knownTimestamps.Clear();

            // Invalidate before-reload state (diagnostic baseline, fixer cache) AFTER the swap, not
            // before it (B1). Once swapped, workspace is null and solutionReadyTask is a fresh
            // (incomplete) signal, so GetSolutionIfLoaded() returns null until the new generation is
            // ready. Clearing here therefore closes the window where a baseline capture scheduled during
            // the StopExternalEditWatcher / gate-acquire awaits above could read the still-current
            // pre-swap solution and write a baseline that outlives the reload (ScheduleBaselineCaptureIfNeeded
            // no-ops forever once a baseline exists). Both subscribers (ClearBaseline,
            // FixerCatalog.InvalidateCache) only clear cached state; neither needs the old solution current,
            // and post-swap is strictly safer — nothing can re-capture/re-cache the stale generation while
            // workspace is null. A reload cancelled at the gate above simply leaves the still-valid baseline
            // in place, which is correct.
            InvokeBeforeReloadCallbacks();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    ///     Coalesced auto-reload trigger for external add/delete detection from the watcher path.
    /// </summary>
    /// <remarks>
    ///     If a reload is already in flight, no-ops — the running reload will pick up everything
    ///     currently on disk. Errors are logged but never surface to the watcher timer thread.
    /// </remarks>
    internal async Task TriggerAutoReloadAsync()
    {
        Task? current;
        lock (fieldLock)
        {
            current = loadReadyTask;
        }

        // Only a completed load warrants a fresh reload. A null loadReadyTask means DisposeAsync has run
        // (or is unwinding) — proceeding would resurrect a reload on a disposed workspace (A1); the prior
        // `is { IsCompleted: false }` check let null through. A non-null-but-incomplete task means a
        // load/reload is already in flight and will pick up current disk state, so coalesce (N3).
        if (current is not { IsCompleted: true })
        {
            return;
        }

        try
        {
            await ScheduleReloadAsync();
        }
        catch (ObjectDisposedException)
        {
            // Server is shutting down; auto-reload is best-effort.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-reload triggered by external add/delete failed");
        }
    }

    /// <summary>
    ///     Notifies the workspace that a single file has been modified on disk.
    ///     Updates the in-memory Solution without a full reload.
    /// </summary>
    public Task NotifyFileChangedAsync(string filePath, CancellationToken ct = default) => NotifyFileChangedAsync(filePath, null, null, ct);

    /// <summary>
    ///     Syncs the in-memory workspace after a file has already been written to disk.
    ///     Avoids re-reading the file from disk when the caller already has the content.
    ///     Pass <paramref name="encoding" /> to preserve BOM status when TryApplyChanges writes back to disk.
    /// </summary>
    /// <remarks>
    ///     This is the workspace-sync step, not the primary write path. Edit tools write to disk
    ///     via <see cref="FileUtility" /> first (for BOM/line-ending control), then call
    ///     <see cref="ScheduleFileChanged" /> which chains into this method to update Roslyn's
    ///     in-memory solution. See <see cref="FileUtility" /> class remarks for the full rationale.
    /// </remarks>
    public async Task NotifyFileChangedAsync(string filePath, string? content, Encoding? encoding = null, CancellationToken ct = default)
    {
        // Await loadReadyTask directly (not GetSolutionAsync) to avoid circular drain
        // when GetSolutionAsync awaits pendingUpdate which is this very method.
        Task? loadTask;
        lock (fieldLock)
        {
            loadTask = loadReadyTask;
        }

        if (loadTask is null)
        {
            return;
        }

        await loadTask.WaitAsync(ct);

        await gate.WaitAsync(ct);
        try
        {
            MSBuildWorkspace? ws;
            lock (fieldLock)
            {
                ws = workspace;
            }

            if (ws is null)
            {
                return;
            }

            Solution currentSolution = ws.CurrentSolution;
            DocumentId? docId = currentSolution
                .GetDocumentIdsWithFilePath(filePath)
                .FirstOrDefault();

            if (docId is null)
            {
                logger.LogDebug("File not part of solution, skipping update: {Path}", filePath);
                return;
            }

            // Layer A: when the watcher path supplies no content, skip the disk read entirely if the
            // file is provably unchanged (equal mtime+length, recorded outside the racy window) — this
            // absorbs spurious watcher double-fires and paths the entry-time sweep already reconciled.
            // Snapshot mtime+length BEFORE the read so the fingerprint we record reflects the version
            // actually loaded; an external write interleaved with the read then can't be masked.
            bool readFromDisk = content is null;
            DateTime mtimeBeforeRead = default;
            long lengthBeforeRead = 0;
            // Branch on `content is null` (not the readFromDisk alias) so flow analysis still proves
            // content non-null after the read for the SourceText.From below.
            if (content is null)
            {
                string fullPath = Path.GetFullPath(filePath);
                if (IsMtimeUnchanged(fullPath))
                {
                    return;
                }

                var info = new FileInfo(fullPath);
                if (info.Exists)
                {
                    mtimeBeforeRead = info.LastWriteTimeUtc;
                    lengthBeforeRead = info.Length;
                }

                (content, encoding) = await FileUtility.ReadFileWithEncodingAsync(filePath, ct);
                Interlocked.Increment(ref FilesReadDuringReconcile);
            }

            var text = SourceText.From(content, encoding ?? FileUtility.Utf8NoBom);

            // Layer B: short-circuit when the new content is byte-equivalent to what Roslyn
            // already holds. ReSharper-style touch-loops bump mtime without changing bytes;
            // without this, every fire builds a new Solution snapshot and pushes native-side
            // state that GC can't reclaim. Recording the new fingerprint (mtime/size/verification
            // time) lets Layer A skip the subsequent fires from the same touch.
            SourceText currentText = await currentSolution.GetDocument(docId)!.GetTextAsync(ct);
            if (currentText.ContentEquals(text))
            {
                RecordObservedFingerprint();
                return;
            }

            Solution newSolution = currentSolution.WithDocumentText(docId, text);

            if (!ws.TryApplyChanges(newSolution))
            {
                logger.LogWarning("TryApplyChanges failed for: {Path}", filePath);
            }

            RecordObservedFingerprint();

            // Disk-read paths record the pre-read snapshot (the version actually loaded);
            // content-supplied paths re-stat the file.
            void RecordObservedFingerprint()
            {
                if (readFromDisk)
                {
                    RecordTimestamp(filePath, mtimeBeforeRead, lengthBeforeRead);
                }
                else
                {
                    RecordTimestamp(filePath);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    ///     Awaits all pending file-change notifications queued by <see cref="ScheduleFileChanged" />.
    ///     Call before operations (like File.Move) that must not overlap with TryApplyChanges writes.
    /// </summary>
    public async Task DrainPendingUpdatesAsync()
    {
        Task update;
        lock (fieldLock)
        {
            update = pendingUpdate;
        }

        await update;
    }

    /// <summary>
    ///     Schedules a file-change notification to run in the background.
    ///     The update is drained automatically by the next <see cref="GetSolutionAsync" /> call,
    ///     so callers can return their result immediately without waiting for the workspace refresh.
    /// </summary>
    /// <remarks>
    ///     Uses <see cref="CancellationToken.None" /> internally because the caller's request-scoped
    ///     token may be cancelled after the edit tool returns.
    /// </remarks>
    public void ScheduleFileChanged(string filePath, string? content, Encoding? encoding = null)
    {
        lock (fieldLock)
        {
            Task previous = pendingUpdate;

            // Not wrapped in Task.Run: the sync preamble of RunScheduledUpdateAsync (and its
            // inner NotifyFileChangedAsync → TryApplyChanges) may run on the caller's thread
            // while fieldLock is held. This is intentional — TryApplyChanges writes to disk,
            // so offloading to the thread pool would race with subsequent File.Move (rename)
            // and file reads by the caller or the next tool invocation.
            pendingUpdate = RunScheduledUpdateAsync(previous, filePath, content, encoding);
        }
    }

    private async Task RunScheduledUpdateAsync(
        Task previous, string filePath, string? content, Encoding? encoding)
    {
        await previous;
        try
        {
            await NotifyFileChangedAsync(filePath, content, encoding);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scheduled file-change notification failed for: {Path}", filePath);
        }
    }

    /// <summary>
    ///     Records the fingerprint for a file whose content we just read from disk, using the
    ///     mtime/length snapshotted <em>before</em> the read. Recording the pre-read stat (rather than
    ///     re-statting after) means an external write that lands during the read isn't masked: its
    ///     newer mtime won't match this record, so the next sweep re-reads it.
    /// </summary>
    private void RecordTimestamp(string filePath, DateTime mtime, long length)
    {
        string fullPath = Path.GetFullPath(filePath);
        knownTimestamps[fullPath] = new FileTimestamp(mtime, length, DateTime.UtcNow);
    }

    /// <summary>
    ///     Records the fingerprint for a file the caller has already written to disk (the edit path):
    ///     the just-written file is the authority, so re-stat it now.
    /// </summary>
    private void RecordTimestamp(string filePath)
    {
        string fullPath = Path.GetFullPath(filePath);
        var info = new FileInfo(fullPath);
        knownTimestamps[fullPath] = new FileTimestamp(info.LastWriteTimeUtc, info.Length, DateTime.UtcNow);
    }

    /// <summary>
    ///     Returns true only when <paramref name="fullPath" /> is provably unchanged since we last
    ///     loaded it: the on-disk mtime <em>and</em> length still match the recorded fingerprint, and
    ///     that fingerprint was recorded comfortably outside the <see cref="RacyWindow" />.
    /// </summary>
    /// <remarks>
    ///     Any mtime difference in <em>either</em> direction forces a reload — a backwards mtime
    ///     (timestamp-preserving restore: <c>robocopy /COPY:T</c>, some git tooling, archive extraction)
    ///     is a change, not proof of freshness. A length difference under an equal/preserved mtime is
    ///     likewise caught. A freshly-loaded file is recorded with a zero racy gap, so it stays
    ///     unverified (returns false) until its first content check promotes it — conservative, because
    ///     "unchanged since load" can't be told from "rewritten in the load tick with a preserved mtime"
    ///     without one verification. Residual blind spot: equal mtime <em>and</em> equal length
    ///     <em>and</em> changed content on an already-promoted file — recovered by the FileSystemWatcher.
    ///     Missing and untracked files return false. One <see cref="FileInfo" /> stat answers all of this.
    /// </remarks>
    private bool IsMtimeUnchanged(string fullPath)
    {
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            return false;
        }

        if (!knownTimestamps.TryGetValue(fullPath, out FileTimestamp known))
        {
            return false;
        }

        if (info.LastWriteTimeUtc != known.Mtime || info.Length != known.Length)
        {
            return false;
        }

        return known.RecordedAtUtc - known.Mtime > RacyWindow;
    }

    private void RecordAllTimestamps(Solution solution)
    {
        knownTimestamps.Clear();
        foreach (Document doc in solution.Projects.SelectMany(p => p.Documents))
        {
            if (doc.FilePath is null)
            {
                continue;
            }

            var info = new FileInfo(doc.FilePath);
            if (info.Exists)
            {
                // RecordedAtUtc == Mtime (zero gap) => racy => content-verified once on the first
                // sweep, then promoted. See IsMtimeUnchanged remarks for why this conservatism is required.
                knownTimestamps[doc.FilePath] = new FileTimestamp(info.LastWriteTimeUtc, info.Length, info.LastWriteTimeUtc);
            }
        }
    }

    private async Task LoadSolutionInternalAsync(IProgress<ProgressNotificationValue>? mcpProgress = null)
    {
        await gate.WaitAsync();

        // Capture this load generation's ready signal up front so the catch below faults the same
        // instance even if a reload later swaps the field (reloads are gate-serialised, so under the
        // gate this is always the current generation).
        TaskCompletionSource readyTcs;
        lock (fieldLock)
        {
            readyTcs = solutionReadyTask;
        }

        try
        {
            string path = FileUtility.DiscoverSolution(explicitPath);
            SolutionPath = path;

            var ws = MSBuildWorkspace.Create();
            ws.RegisterWorkspaceFailedHandler(e =>
            {
                if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                {
                    // Failure messages embed BuildHost subprocess stderr verbatim — surface at WRN
                    // so they reach the file log at the default min-level for post-mortem.
                    logger.LogWarning("Workspace failure: {Message}", e.Diagnostic.Message);
                }
                else
                {
                    logger.LogDebug("Workspace diagnostic: {Message}", e.Diagnostic.Message);
                }
            });

            lock (fieldLock)
            {
                workspace = ws;
            }

            logger.LogInformation("Loading solution: {Path}", path);
            mcpProgress?.Report(new ProgressNotificationValue
            {
                Progress = 0,
                Message = $"Loading solution: {Path.GetFileName(path)}"
            });

            Progress<ProjectLoadProgress> progress = new(p =>
                logger.LogInformation("Loading: {File} ({Operation})",
                    Path.GetFileName(p.FilePath), p.Operation));

            Solution solution = await ws.OpenSolutionAsync(path, progress);

            (Solution stripped, int analyzerCount, int metadataCount, HashSet<ProjectId> affectedProjects) =
                solution.StripUnresolvedReferences();
            if (stripped != solution)
            {
                // Snapshot affected .csproj files before TryApplyChanges rewrites them.
                // TryApplyChanges is the only public API to update workspace.CurrentSolution,
                // but MSBuildWorkspace re-serializes project XML as a side effect (adding BOM,
                // stripping blank lines). We restore the originals immediately after.
                Dictionary<string, byte[]> projectFileSnapshots = [];
                foreach (ProjectId pid in affectedProjects)
                {
                    string? filePath = solution.GetProject(pid)?.FilePath;
                    if (filePath is not null)
                    {
                        projectFileSnapshots[filePath] = File.ReadAllBytes(filePath);
                    }
                }

                try
                {
                    ws.TryApplyChanges(stripped);
                }
                finally
                {
                    foreach ((string filePath, byte[] originalBytes) in projectFileSnapshots)
                    {
                        File.WriteAllBytes(filePath, originalBytes);
                    }
                }

                logger.LogInformation(
                    "Stripped {AnalyzerCount} unresolved analyzer and {MetadataCount} unresolved metadata references",
                    analyzerCount, metadataCount);
            }

            solution = ws.CurrentSolution;
            RecordAllTimestamps(solution);

            // The snapshot is usable now: read tools can resolve symbols and force only the specific
            // compilations they touch. Signal readiness BEFORE warmup so the first tool call no longer
            // blocks behind every project compiling. (The test hook parks the load here, pre-signal.)
            if (onBeforeReadySignal is not null)
            {
                await onBeforeReadySignal();
            }

            readyTcs.TrySetResult();

            int projectCount = solution.Projects.Count();
            int documentCount = solution.Projects.Sum(p => p.Documents.Count());
            logger.LogInformation(
                "Solution loaded: {Projects} projects, {Documents} documents",
                projectCount, documentCount);

            // Warmup runs after the ready signal, so cancelling it (dispose/reload) never affects readers.
            // The linked token cancels with disposeCts; a fresh link per load keeps a post-dispose reload
            // starting live. WarmUpCompilationsAsync tolerates the cancellation internally.
            using var warmupCts = CancellationTokenSource.CreateLinkedTokenSource(disposeCts.Token);
            await WarmUpCompilationsAsync(solution, warmupCts.Token, mcpProgress);

            string? solutionDir = SolutionDirectory;
            if (solutionDir is not null)
            {
                await StartExternalEditWatcherAsync(solutionDir);
            }
        }
        catch (Exception ex)
        {
            // A failure before the ready signal (discover/open/strip) would otherwise leave readers
            // awaiting solutionReadyTask forever; fault it so they observe the load error. A no-op once
            // the signal has fired, so a warmup-phase throw can't re-fault an already-served snapshot.
            readyTcs.TrySetException(ex);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task StartExternalEditWatcherAsync(string solutionDir)
    {
        if (autoRefreshDisabled)
        {
            return;
        }

        try
        {
            FileSystemWatcher w = new(solutionDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,

                // The default 8 KB buffer overflows on a large burst (branch switch, git reset --hard
                // of a big tree), dropping events. 64 KB is the documented practical maximum; the Error
                // handler below recovers anything still dropped past it.
                InternalBufferSize = 64 * 1024
            };
            w.Filters.Add("*.cs");
            w.Changed += OnExternalFileChanged;
            w.Created += OnExternalFileChanged;
            w.Deleted += OnExternalFileChanged;
            w.Renamed += OnExternalFileRenamed;
            w.Error += OnExternalWatcherError;
            Timer t = new(FlushExternalEdits, null, ExternalEditDebounceMs, ExternalEditDebounceMs);

            bool started;
            FileSystemWatcher? oldWatcher;
            Timer? oldTimer;
            lock (fieldLock)
            {
                // Warmup now completes before this runs, so a dispose can land between cancelling warmup
                // and here. If it has, StopExternalEditWatcherAsync already ran — don't publish a
                // watcher/timer it will never stop. `disposed` is set before that stop and republished
                // via this same fieldLock, so the check is reliable.
                started = disposed == 0;

                // Capture any predecessor so we can dispose it after publishing (N3 leak fix). A reload's
                // StopExternalEditWatcherAsync runs before it acquires the gate; a watcher that a prior
                // in-flight load published *after* that stop would otherwise be silently overwritten here
                // and leaked. Disposing it keeps exactly one watcher/timer live at a time.
                oldWatcher = externalEditWatcher;
                oldTimer = externalEditFlushTimer;

                if (started)
                {
                    externalEditWatcher = w;
                    externalEditFlushTimer = t;
                }
            }

            // Dispose the predecessor (if any) outside the lock. Timer.DisposeAsync drains any in-flight
            // FlushExternalEdits, which never synchronously takes the gate (it offloads reloads via
            // Task.Run), so awaiting it while this load holds the gate cannot deadlock.
            oldWatcher?.Dispose();
            if (oldTimer is not null)
            {
                await oldTimer.DisposeAsync();
            }

            if (!started)
            {
                w.Dispose();
                await t.DisposeAsync();
                return;
            }

            // Enable events only after publishing, so a watcher we end up discarding never fired any.
            w.EnableRaisingEvents = true;
            logger.LogInformation("External edit watcher started on {Dir}", solutionDir);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "External edit watcher failed to start on {Dir}; falling back to mtime sweep only", solutionDir);
        }
    }

    /// <summary>
    ///     Stops the external-edit watcher and drains any in-flight flush callback.
    /// </summary>
    /// <remarks>
    ///     Disposes the <see cref="FileSystemWatcher" /> first so no new flushes can be scheduled
    ///     while we await the timer drain. <see cref="Timer.DisposeAsync" /> guarantees no
    ///     in-flight callback survives the await.
    /// </remarks>
    private async Task StopExternalEditWatcherAsync()
    {
        Timer? timer;
        FileSystemWatcher? watcher;
        lock (fieldLock)
        {
            timer = externalEditFlushTimer;
            externalEditFlushTimer = null;
            watcher = externalEditWatcher;
            externalEditWatcher = null;
        }

        watcher?.Dispose();
        if (timer is not null)
        {
            await timer.DisposeAsync();
        }

        pendingExternalEdits.Clear();
    }

    private void OnExternalFileChanged(object? sender, FileSystemEventArgs e)
    {
        if (IsBuildOutputFullPath(e.FullPath))
        {
            return;
        }

        pendingExternalEdits[e.FullPath] = Environment.TickCount64;
    }

    private void OnExternalFileRenamed(object? sender, RenamedEventArgs e)
    {
        if (!IsBuildOutputFullPath(e.OldFullPath))
        {
            pendingExternalEdits[e.OldFullPath] = Environment.TickCount64;
        }

        if (!IsBuildOutputFullPath(e.FullPath))
        {
            pendingExternalEdits[e.FullPath] = Environment.TickCount64;
        }
    }

    /// <summary>
    ///     Handles <see cref="FileSystemWatcher.Error" /> — raised when the watcher's internal buffer
    ///     overflows (an event burst exceeds <see cref="FileSystemWatcher.InternalBufferSize" />) or the
    ///     watch otherwise fails. Dropped <c>Created</c> events are unrecoverable by the entry-time
    ///     sweep (adds are watcher-only), so trigger a coalesced full reload to resync from disk.
    /// </summary>
    /// <remarks>
    ///     Fire-and-forget like the add/delete path in <see cref="FlushExternalEdits" /> — the
    ///     <c>Error</c> event is a synchronous void callback and must not block. A genuine buffer
    ///     overflow can't be forced deterministically, so the recovery is exercised through its target,
    ///     <see cref="TriggerAutoReloadAsync" />; the <c>Error</c> wiring itself is verified by inspection.
    /// </remarks>
    private void OnExternalWatcherError(object? sender, ErrorEventArgs e)
    {
        logger.LogWarning(e.GetException(),
            "External edit watcher error (buffer overflow?); triggering full reload to recover dropped events");
        _ = Task.Run(TriggerAutoReloadAsync);
    }

    private void FlushExternalEdits(object? state)
    {
        if (pendingExternalEdits.IsEmpty)
        {
            return;
        }

        Task? loadTask;
        MSBuildWorkspace? ws;
        lock (fieldLock)
        {
            loadTask = loadReadyTask;
            ws = workspace;
        }

        if (loadTask is not { IsCompletedSuccessfully: true } || ws is null)
        {
            return;
        }

        // ws can be disposed concurrently by ScheduleReloadAsync or DisposeAsync between the
        // snapshot above and the CurrentSolution dereference below — the timer fires on a
        // background thread independent of those paths. Catch ObjectDisposedException so the
        // unhandled-exception escalation policy never crashes the process from this thread.
        try
        {
            Solution solution = ws.CurrentSolution;
            long now = Environment.TickCount64;
            var needsReload = false;

            foreach (KeyValuePair<string, long> kvp in pendingExternalEdits)
            {
                if (now - kvp.Value < ExternalEditDebounceMs)
                {
                    continue;
                }

                // Value-based remove: if a new event re-stamped this entry between iteration
                // and remove, the remove fails and we'll re-evaluate on the next tick.
                if (!pendingExternalEdits.TryRemove(kvp))
                {
                    continue;
                }

                bool onDisk = File.Exists(kvp.Key);
                bool inSolution = solution.GetDocumentIdsWithFilePath(kvp.Key).Length > 0;

                if (onDisk && inSolution)
                {
                    ScheduleFileChanged(kvp.Key, null);
                }
                else if (onDisk != inSolution)
                {
                    logger.LogInformation(
                        "External {Kind} detected, reload pending: {Path}",
                        onDisk ? "add" : "delete",
                        kvp.Key);
                    needsReload = true;
                }
                // else: !onDisk && !inSolution — file came and went, no-op
            }

            if (needsReload)
            {
                _ = Task.Run(TriggerAutoReloadAsync);
            }
        }
        catch (ObjectDisposedException)
        {
            // Workspace disposed mid-flush; the next reload picks up the current state.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "External edit flush failed");
        }
    }

    private bool IsBuildOutputFullPath(string fullPath)
    {
        if (SolutionDirectory is null)
        {
            return false;
        }

        string relative = Path.GetRelativePath(SolutionDirectory, fullPath).Replace('\\', '/');
        return IsBuildOutputPath(relative);
    }

    private async Task WarmUpCompilationsAsync(
        Solution solution, CancellationToken ct, IProgress<ProgressNotificationValue>? mcpProgress = null)
    {
        List<Project> projects = solution.Projects.ToList();
        if (projects.Count == 0)
        {
            return;
        }

        logger.LogInformation("Warming up compilations for {Count} projects...", projects.Count);
        mcpProgress?.Report(new ProgressNotificationValue
        {
            Progress = 0,
            Total = projects.Count,
            Message = $"Compiling {projects.Count} projects..."
        });

        var completed = 0;
        IEnumerable<Task> tasks = projects.Select(async project =>
        {
            await project.GetCompilationAsync(ct);
            int done = Interlocked.Increment(ref completed);
            logger.LogInformation("Compiled {Done}/{Total}: {Name}", done, projects.Count, project.Name);
            mcpProgress?.Report(new ProgressNotificationValue
            {
                Progress = done,
                Total = projects.Count,
                Message = $"Compiled {done}/{projects.Count}: {project.Name}"
            });
        });

        try
        {
            await Task.WhenAll(tasks);
            logger.LogInformation("All compilations warm.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Dispose/reload cancelled warmup (or disposed the workspace) mid-flight. Task.WhenAll has
            // already observed every constituent task, so swallowing here leaves no unobserved fault and
            // lets the loader complete cleanly — readers were already served at the ready signal.
            logger.LogDebug("Compilation warmup cancelled before all projects compiled.");
        }
    }

    // ── Glob expansion ──────────────────────────────────────────────────

    /// <summary>
    ///     Expands file paths that contain glob patterns against the workspace's documents.
    ///     Non-glob paths pass through unchanged.
    /// </summary>
    /// <remarks>
    ///     Throws if a glob matches more than <paramref name="maxFiles" />. When
    ///     <paramref name="tooManyMatchesHint" /> is supplied, it is appended to the over-cap error
    ///     message — use to offer a tool-specific recovery (e.g. a <c>project=</c> alternative).
    /// </remarks>
    public async Task<string[]> ExpandGlobPatternsAsync(
        string[] filePaths, int maxFiles, string? tooManyMatchesHint = null, CancellationToken ct = default)
    {
        bool hasGlobs = filePaths.Any(PathExtensions.IsGlobPattern);
        if (!hasGlobs)
        {
            return filePaths;
        }

        string solutionDir = await GetRequiredSolutionDirectoryAsync(ct);
        Solution solution = await GetSolutionAsync(ct);

        List<DocumentPathEntry> allDocPaths = solution.EnumerateSourceDocumentPaths(solutionDir).ToList();

        List<string> expanded = [];
        foreach (string pattern in filePaths)
        {
            if (!PathExtensions.IsGlobPattern(pattern))
            {
                expanded.Add(pattern);
                continue;
            }

            Regex globRegex = PathExtensions.CompileFilePathGlobRegex(pattern);
            List<DocumentPathEntry> matched = allDocPaths
                .Where(d => globRegex.IsMatch(d.NormalizedRelativePath))
                .ToList();

            if (matched.Count == 0)
            {
                string prefix = ExtractGlobLiteralPrefix(pattern);
                string hint = BuildNoMatchHint(prefix, allDocPaths);
                throw new UserErrorException(
                    $"Glob pattern '{pattern}' matched no files in the solution.{hint}");
            }

            if (matched.Count > maxFiles)
            {
                List<string> relativePaths = matched.Select(m => m.NormalizedRelativePath).ToList();
                string examples = FormatExamplePaths(relativePaths, matched.Count);
                string hintLine = tooManyMatchesHint is null ? "" : $"\n{tooManyMatchesHint}";
                throw new UserErrorException(
                    $"Glob pattern '{pattern}' matched {matched.Count} files, exceeding maxFiles={maxFiles}. " +
                    $"Use a narrower pattern or increase maxFiles.{hintLine}\n{examples}");
            }

            expanded.AddRange(matched.Select(m => m.AbsolutePath));
        }

        return expanded.ToArray();
    }

    /// <summary>
    ///     Filters relative paths by glob patterns, returning the union of all matches.
    /// </summary>
    public static string[] FilterByGlobPatterns(string[] relativePaths, string[] globPatterns)
    {
        HashSet<string> matched = new(StringComparer.OrdinalIgnoreCase);

        foreach (string pattern in globPatterns)
        {
            Regex globRegex = PathExtensions.CompileFilePathGlobRegex(pattern);
            string[] hits = relativePaths.Where(p => globRegex.IsMatch(p)).ToArray();

            if (hits.Length == 0)
            {
                throw new UserErrorException(
                    $"Glob pattern '{pattern}' matched no files in the project.");
            }

            matched.UnionWith(hits);
        }

        return matched.ToArray();
    }

    internal static bool IsBuildOutputPath(string relativePath)
        => PathExtensions.ContainsDirectorySegment(relativePath, "obj")
           || PathExtensions.ContainsDirectorySegment(relativePath, "bin");

    private static List<string> SelectDiverseExamples(List<string> relativePaths, int count)
    {
        List<List<string>> byDirectory = relativePaths
            .GroupBy(p => Path.GetDirectoryName(p)?.Replace('\\', '/') ?? "")
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList())
            .ToList();

        List<string> result = [];
        var dirIndex = 0;
        while (result.Count < count && byDirectory.Count > 0)
        {
            List<string> dir = byDirectory[dirIndex % byDirectory.Count];
            result.Add(dir[0]);
            dir.RemoveAt(0);
            if (dir.Count == 0)
            {
                byDirectory.RemoveAt(dirIndex % byDirectory.Count);
            }
            else
            {
                dirIndex++;
            }
        }

        return result;
    }

    /// <summary>
    ///     Formats a list of example relative paths with a "... and N more" trailer.
    /// </summary>
    private static string FormatExamplePaths(List<string> relativePaths, int total, int maxExamples = 5)
    {
        List<string> examples = SelectDiverseExamples(relativePaths, maxExamples);
        return FormatPathList("Example matches (1 per directory):", examples, total);
    }

    /// <summary>
    ///     Renders an indented path list with a header and an optional "... and N more" trailer.
    /// </summary>
    private static string FormatPathList(string header, List<string> paths, int total)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);
        foreach (string path in paths)
        {
            sb.AppendLine($"  {path}");
        }

        int remaining = total - paths.Count;
        if (remaining > 0)
        {
            sb.Append($"  ... and {remaining} more");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Extracts the literal directory prefix from a glob pattern (everything before the first wildcard).
    /// </summary>
    private static string ExtractGlobLiteralPrefix(string pattern)
    {
        string normalized = pattern.Replace('\\', '/');
        int firstWild = normalized.IndexOfAny(['*', '?', '[']);
        if (firstWild < 0)
        {
            return normalized;
        }

        string prefix = normalized[..firstWild];
        int lastSlash = prefix.LastIndexOf('/');
        return lastSlash >= 0 ? prefix[..(lastSlash + 1)] : "";
    }

    /// <summary>
    ///     Builds a hint showing nearby files when a glob matches nothing.
    /// </summary>
    private static string BuildNoMatchHint(
        string prefix, IReadOnlyList<DocumentPathEntry> allDocPaths)
    {
        if (prefix.Length == 0)
        {
            return "";
        }

        List<string> nearby = allDocPaths
            .Where(d => d.NormalizedRelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.NormalizedRelativePath)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (nearby.Count == 0)
        {
            return $"\nNo files found under '{prefix}'. Use get_workspace_info to explore solution structure.";
        }

        List<string> examples = nearby.Take(5).ToList();
        return "\n" + FormatPathList($"Files under '{prefix}':", examples, nearby.Count);
    }

    /// <summary>
    ///     A file's recorded freshness fingerprint: its last-write time and byte length at the moment we
    ///     last loaded its content, plus the wall-clock time we recorded it. <see cref="RecordedAtUtc" />
    ///     minus <see cref="Mtime" /> is the "racy gap" — only once it exceeds <see cref="RacyWindow" />
    ///     can an equal mtime be trusted as proof the file is unchanged (see <see cref="IsMtimeUnchanged" />).
    /// </summary>
    private readonly record struct FileTimestamp(DateTime Mtime, long Length, DateTime RecordedAtUtc);

    private sealed class BeforeReloadSubscription(WorkspaceManager owner, Action callback) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.BeforeReload -= callback;
            }
        }
    }
}
