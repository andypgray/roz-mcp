using System.Diagnostics;
using Serilog;

namespace Zphil.Roz.Infrastructure;

/// <summary>
///     Defensive idle-timeout shutdown, complementing <see cref="ParentProcessWatcher" />.
///     When an MCP client drops/recreates a connection abnormally without closing this
///     server's stdin pipe and the watched parent PID stays alive, the SDK's stdin-EOF
///     detection is missed and the server orphans (holding an MSBuildWorkspace +
///     BuildHost child). Tracks time since the last tool call and exits after
///     ROZ_IDLE_TIMEOUT_MINUTES of inactivity.
/// </summary>
/// <remarks>
///     Monotonic clock (Stopwatch) — does not advance during machine sleep, so an
///     overnight suspend does not trip on wake. A call in flight never counts as idle;
///     a server that never receives a call still exits, timed from process start.
///     ROZ_IDLE_TIMEOUT_MINUTES=0 disables; unset/blank/invalid => the 30 min default
///     (a config typo must not silently remove leak protection — 0 is the only opt-out).
/// </remarks>
internal static class IdleTimeoutWatchdog
{
    internal const int DefaultTimeoutMinutes = 30;

    private static long s_lastActivityTicks;
    private static int s_inFlightCount;
    private static Func<long> s_timestampProvider = Stopwatch.GetTimestamp;

    /// <summary>
    ///     The number of tool calls currently executing (bumped by <see cref="EnterCall" /> /
    ///     <see cref="ExitCall" />). Read by <see cref="ServerShutdown" /> so a shutdown can let an
    ///     in-flight edit batch commit before tearing the process down (N2). Edits still parked on the edit
    ///     gate have not yet reached <see cref="EnterCall" /> (the serialization gate is the outer request
    ///     filter, <see cref="Pipeline.GlobalCallToolFilter" /> the inner one), so they are correctly
    ///     uncounted — nothing is written until they run.
    /// </summary>
    internal static int InFlightCount => Volatile.Read(ref s_inFlightCount);

    /// <summary>Records the start of a tool call: re-stamps the idle clock and increments the in-flight count.</summary>
    /// <remarks>
    ///     Pair with <see cref="ExitCall" /> in a <c>finally</c>. The in-flight count this bumps keeps a
    ///     long first call (cold solution load) from tripping the timeout while the call is still running.
    /// </remarks>
    public static void EnterCall()
    {
        Interlocked.Exchange(ref s_lastActivityTicks, s_timestampProvider());
        Interlocked.Increment(ref s_inFlightCount);
    }

    /// <summary>Records the completion of a tool call: re-stamps the idle clock and decrements the in-flight count.</summary>
    /// <remarks>
    ///     Idle is measured from completion, so the timeout window is the gap between responses, not the time since a
    ///     call began.
    /// </remarks>
    public static void ExitCall()
    {
        Interlocked.Exchange(ref s_lastActivityTicks, s_timestampProvider());
        Interlocked.Decrement(ref s_inFlightCount);
    }

    /// <summary>
    ///     Reads <c>ROZ_IDLE_TIMEOUT_MINUTES</c> (or the 30-minute default), then attaches a
    ///     background watcher that exits the process after that long with no tool activity. Returns
    ///     silently when the timeout resolves to zero (explicitly disabled via <c>=0</c>).
    /// </summary>
    public static void Start() => Start(
        ParseTimeoutMinutes(Environment.GetEnvironmentVariable(RozEnvVars.IdleTimeoutMinutes.Name)),
        TimeSpan.FromSeconds(30),
        Stopwatch.GetTimestamp,
        () => ServerShutdown.ExitWith("idle timeout"));

    /// <summary>
    ///     Test seam: injected timeout, poll interval, fake clock, fake exit —
    ///     instant, deterministic, never kills the test host. Mirrors
    ///     <see cref="ParentProcessWatcher" />'s <c>Start(Func,Action,bool)</c> seam.
    /// </summary>
    internal static void Start(
        TimeSpan timeout, TimeSpan pollInterval, Func<long> clock, Action onIdleTimeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            Log.Information("Idle-timeout watchdog disabled (ROZ_IDLE_TIMEOUT_MINUTES=0)");
            return;
        }

        s_timestampProvider = clock;
        Interlocked.Exchange(ref s_lastActivityTicks, clock());
        // Warning so a post-mortem at default min-level confirms attach (cf. ParentProcessWatcher.cs:79).
        Log.Warning("Idle-timeout watchdog attached: {Minutes} min timeout", timeout.TotalMinutes);
        _ = Task.Run(() => WatchAsync(timeout, pollInterval, clock, onIdleTimeout));
    }

    /// <summary>Test seam: pure synchronous predicate — true iff it would exit now.</summary>
    internal static bool IsIdleExpired(TimeSpan timeout, long nowTicks)
    {
        if (Volatile.Read(ref s_inFlightCount) > 0)
        {
            return false;
        }

        long last = Interlocked.Read(ref s_lastActivityTicks);
        return Stopwatch.GetElapsedTime(last, nowTicks) > timeout;
    }

    private static async Task WatchAsync(
        TimeSpan timeout, TimeSpan pollInterval, Func<long> clock, Action onIdleTimeout)
    {
        try
        {
            using var timer = new PeriodicTimer(pollInterval);
            while (await timer.WaitForNextTickAsync())
            {
                if (IsIdleExpired(timeout, clock()))
                {
                    Log.Warning("No tool activity for over {Minutes} min; shutting down idle server",
                        timeout.TotalMinutes);
                    onIdleTimeout();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Idle-timeout watchdog loop failed; watchdog disabled");
        }
    }

    /// <summary>
    ///     null/blank/invalid/negative => default (watchdog stays ON; a typo must
    ///     not silently disable leak protection). "0" => Zero (explicit, documented opt-out).
    /// </summary>
    internal static TimeSpan ParseTimeoutMinutes(string? raw)
    {
        if (String.IsNullOrWhiteSpace(raw))
        {
            return TimeSpan.FromMinutes(DefaultTimeoutMinutes);
        }

        if (Int32.TryParse(raw, out int minutes) && minutes >= 0)
        {
            return TimeSpan.FromMinutes(minutes); // includes "0" => disabled
        }

        Log.Warning("Invalid ROZ_IDLE_TIMEOUT_MINUTES '{Raw}'; using {Default} min default",
            raw, DefaultTimeoutMinutes);
        return TimeSpan.FromMinutes(DefaultTimeoutMinutes);
    }

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref s_inFlightCount, 0);
        Interlocked.Exchange(ref s_lastActivityTicks, 0);
        s_timestampProvider = Stopwatch.GetTimestamp;
    }
}
