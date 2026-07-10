using Serilog;

namespace Zphil.Roz.Infrastructure;

/// <summary>
///     Shared exit path for both watchdogs. Runs registered async disposers (with a per-disposer
///     timeout) and flushes Serilog before terminating the process.
/// </summary>
/// <remarks>
///     <para>
///         Solves the orphaned-BuildHost issue where direct <c>Environment.Exit(0)</c> from
///         either watchdog skipped <see cref="WorkspaceManager.DisposeAsync" />, leaking the
///         <see cref="Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace" /> + its BuildHost child.
///     </para>
///     <para>
///         <see cref="Program" /> registers the workspace disposer between
///         <c>builder.Build()</c> and <c>host.RunAsync()</c>; both
///         <see cref="IdleTimeoutWatchdog" /> and <see cref="ParentProcessWatcher" /> route
///         through <see cref="ExitWith(string)" /> instead of calling <c>Environment.Exit</c>
///         directly. A 5-second bound on each disposer keeps shutdown deterministic even if a
///         disposer hangs (cold-load <c>OpenSolutionAsync</c>, BuildHost RPC stuck, etc.).
///     </para>
/// </remarks>
internal static class ServerShutdown
{
    // 12s, not 5s (N6): WorkspaceManager.DisposeAsync's worst case is ~10s — a 5s bound on draining
    // pending updates plus a 5s bound on acquiring the mutation gate — before it even disposes the
    // MSBuildWorkspace and its BuildHost child. A 5s per-disposer bound here would abandon that dispose
    // partway and risk leaking the BuildHost; 12s gives that worst case headroom. Keep in sync with the
    // two 5s timeouts in WorkspaceManager.DisposeAsync.
    private static readonly TimeSpan DefaultDisposalTimeout = TimeSpan.FromSeconds(12);

    // How long ExitWith waits for in-flight tool calls to finish before running disposers (N2). Bounds
    // the window that lets a mid-flight edit batch commit; 5s comfortably covers an atomic File.Move swap
    // loop while keeping shutdown from being held hostage by a stuck call.
    private static readonly TimeSpan DefaultInFlightDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InFlightPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly Lock disposersLock = new();
    private static readonly List<Func<ValueTask>> disposers = [];
    private static int s_hasExited;

    /// <summary>
    ///     True once <see cref="ExitWith(string)" /> has been accepted by the gate. Subsequent
    ///     callers return silently — disposal runs at most once, no matter how many watchdogs
    ///     race to exit.
    /// </summary>
    internal static bool HasExited => Volatile.Read(ref s_hasExited) != 0;

    /// <summary>
    ///     Adds <paramref name="dispose" /> to the list of disposers run by
    ///     <see cref="ExitWith(string)" />. Multiple registrations run in registration order.
    /// </summary>
    /// <param name="dispose">
    ///     Asynchronous callback invoked during shutdown to release a resource. Faults and
    ///     timeouts are logged at Warning but never block exit.
    /// </param>
    public static void RegisterDisposer(Func<ValueTask> dispose)
    {
        ArgumentNullException.ThrowIfNull(dispose);
        lock (disposersLock)
        {
            disposers.Add(dispose);
        }
    }

    /// <summary>
    ///     Production entry: runs registered disposers with a 5-second bound each, flushes
    ///     Serilog, then calls <see cref="Environment.Exit" /> with code 0.
    /// </summary>
    /// <param name="reason">
    ///     Short label describing why the server is shutting down (e.g. "idle timeout").
    ///     Logged at Warning so post-mortems can identify the trigger.
    /// </param>
    public static void ExitWith(string reason) =>
        ExitWith(reason, static () => Environment.Exit(0), DefaultDisposalTimeout, DefaultInFlightDrainTimeout);

    /// <summary>
    ///     Test seam: lets tests substitute the terminal action and shrink the disposal / in-flight-drain
    ///     timeouts so they can verify the gate, in-flight drain, disposal-then-exit order, and timeout
    ///     fallback without killing the test host. <paramref name="inFlightDrainTimeout" /> defaults to
    ///     <see cref="DefaultInFlightDrainTimeout" /> when null.
    /// </summary>
    internal static void ExitWith(string reason, Action exit, TimeSpan disposalTimeout, TimeSpan? inFlightDrainTimeout = null)
    {
        if (Interlocked.CompareExchange(ref s_hasExited, 1, 0) != 0)
        {
            return;
        }

        Log.Warning("Shutting down: {Reason}", reason);

        // Let an in-flight tool call finish before tearing the process down (N2). This matters most for a
        // mid-flight edit batch: Environment.Exit skips AtomicFileWriter's rollback catch, so exiting
        // during its phase-2 File.Move swap loop would leave a partially-written batch on disk. Bounded so
        // a stuck call can't hold shutdown hostage.
        //
        // Residual (accepted): this covers the *executing* call, not a second edit parked on the edit gate.
        // The idle-count ExitCall (inner GlobalCallToolFilter) runs before editGate.Release (outer
        // EditSerializationFilter), so a parked edit can begin just as the count hits 0 and the drain
        // returns. It then still has the disposer-loop window (DefaultDisposalTimeout) to finish before
        // Environment.Exit. Fully closing this would need the edit gate itself drained here — a second
        // shutdown seam deliberately not added for this narrow (two-concurrent-edits-at-exit) case.
        WaitForInFlightCalls(inFlightDrainTimeout ?? DefaultInFlightDrainTimeout);

        List<Func<ValueTask>> snapshot;
        lock (disposersLock)
        {
            snapshot = [.. disposers];
        }

        foreach (Func<ValueTask> disposer in snapshot)
        {
            RunDisposerBounded(disposer, disposalTimeout);
        }

        Log.CloseAndFlush();
        exit();
    }

    /// <summary>
    ///     Spins (50 ms poll) until no tool call is in flight or <paramref name="timeout" /> elapses.
    ///     Synchronous by design — this is the terminal shutdown path, so blocking the caller is fine.
    /// </summary>
    private static void WaitForInFlightCalls(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        long deadlineTicks = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (IdleTimeoutWatchdog.InFlightCount > 0 && Environment.TickCount64 < deadlineTicks)
        {
            Thread.Sleep(InFlightPollInterval);
        }
    }

    private static void RunDisposerBounded(Func<ValueTask> disposer, TimeSpan timeout)
    {
        try
        {
            Task disposeTask = disposer().AsTask();
            if (!disposeTask.Wait(timeout))
            {
                Log.Warning("Disposer timed out after {Timeout}; continuing exit", timeout);
            }
        }
        catch (AggregateException ae)
        {
            // Task.Wait wraps disposer faults; unwrap so the log shows the real exception.
            Log.Warning(ae.GetBaseException(), "Disposer faulted; continuing exit");
        }
        catch (Exception ex)
        {
            // Synchronous throw from the disposer itself (before it returned a Task).
            Log.Warning(ex, "Disposer threw synchronously; continuing exit");
        }
    }

    internal static void ResetForTests()
    {
        lock (disposersLock)
        {
            disposers.Clear();
        }

        Interlocked.Exchange(ref s_hasExited, 0);
    }
}
