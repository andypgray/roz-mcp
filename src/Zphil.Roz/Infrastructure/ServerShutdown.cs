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
    private static readonly TimeSpan DefaultDisposalTimeout = TimeSpan.FromSeconds(5);
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
        ExitWith(reason, static () => Environment.Exit(0), DefaultDisposalTimeout);

    /// <summary>
    ///     Test seam: lets tests substitute the terminal action and shrink the disposal timeout
    ///     so they can verify the gate, disposal-then-exit order, and timeout fallback without
    ///     killing the test host.
    /// </summary>
    internal static void ExitWith(string reason, Action exit, TimeSpan disposalTimeout)
    {
        if (Interlocked.CompareExchange(ref s_hasExited, 1, 0) != 0)
        {
            return;
        }

        Log.Warning("Shutting down: {Reason}", reason);

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
