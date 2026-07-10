using System.Diagnostics;
using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Tests.Infrastructure;

/// <summary>
///     Exercises the pure <see cref="IdleTimeoutWatchdog.IsIdleExpired" /> and
///     <see cref="IdleTimeoutWatchdog.ParseTimeoutMinutes" /> seams plus the disabled
///     short-circuit and one real-clock end-to-end fire. No real waits in the pure
///     tests: <c>nowTicks</c> is derived from <see cref="Stopwatch.Frequency" /> so the
///     <see cref="Stopwatch.GetElapsedTime(long, long)" /> math is exact. The class
///     resets the watchdog's static state before and after every test.
/// </summary>
[Collection("WatchdogStatics")]
public sealed class IdleTimeoutWatchdogTests : IDisposable
{
    public IdleTimeoutWatchdogTests()
    {
        IdleTimeoutWatchdog.ResetForTests();
    }

    public void Dispose() => IdleTimeoutWatchdog.ResetForTests();

    // Stopwatch-timestamp delta for a given number of seconds, so GetElapsedTime(0, Ticks(n)) == n s.
    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public void Start_TimeoutZero_DoesNotLoopOrExit()
    {
        // Arrange / Act / Assert — timeout <= 0 short-circuits before any task; the
        // throwing exit delegate proves onIdleTimeout is never invoked.
        Should.NotThrow(() => IdleTimeoutWatchdog.Start(
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1),
            () => 0L,
            () => throw new InvalidOperationException("watchdog must not exit when disabled")));
    }

    [Fact]
    public void Start_NegativeTimeout_Disabled()
    {
        // Arrange / Act / Assert — a negative timeout is treated identically to zero.
        Should.NotThrow(() => IdleTimeoutWatchdog.Start(
            TimeSpan.FromMinutes(-1),
            TimeSpan.FromMilliseconds(1),
            () => 0L,
            () => throw new InvalidOperationException("watchdog must not exit when disabled")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseTimeoutMinutes_NullOrBlank_ReturnsDefault(string? raw)
    {
        // Act
        TimeSpan parsed = IdleTimeoutWatchdog.ParseTimeoutMinutes(raw);

        // Assert
        parsed.ShouldBe(TimeSpan.FromMinutes(IdleTimeoutWatchdog.DefaultTimeoutMinutes));
    }

    [Fact]
    public void ParseTimeoutMinutes_Zero_ReturnsZeroDisabled()
    {
        // Act / Assert — "0" is the only explicit, documented opt-out.
        IdleTimeoutWatchdog.ParseTimeoutMinutes("0").ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void ParseTimeoutMinutes_ValidPositive()
    {
        // Act / Assert
        IdleTimeoutWatchdog.ParseTimeoutMinutes("45").ShouldBe(TimeSpan.FromMinutes(45));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("5m")]
    [InlineData("1.5")]
    public void ParseTimeoutMinutes_InvalidOrNegative_ReturnsDefault(string raw)
    {
        // Act
        TimeSpan parsed = IdleTimeoutWatchdog.ParseTimeoutMinutes(raw);

        // Assert — locks the config-safety decision: a typo must fail toward "protected",
        // never silently disable leak protection (only "0" disables).
        parsed.ShouldBe(TimeSpan.FromMinutes(IdleTimeoutWatchdog.DefaultTimeoutMinutes));
    }

    [Fact]
    public void IsIdleExpired_NeverCalled_FiresFromStartBaseline()
    {
        // Arrange — no EnterCall ever; after reset the baseline is timestamp 0,
        // standing in for the process-start stamp the real Start() would set.
        var timeout = TimeSpan.FromMinutes(1);

        // Act — two minutes past the baseline with zero activity.
        bool expired = IdleTimeoutWatchdog.IsIdleExpired(timeout, Ticks(120));

        // Assert — a server that never receives a call still exits.
        expired.ShouldBeTrue();
    }

    [Fact]
    public void IsIdleExpired_WithinTimeout_DoesNotFire()
    {
        // Arrange
        var timeout = TimeSpan.FromMinutes(1);

        // Act — 30 s elapsed since the baseline, well inside the 1-min window.
        bool expired = IdleTimeoutWatchdog.IsIdleExpired(timeout, Ticks(30));

        // Assert
        expired.ShouldBeFalse();
    }

    [Fact]
    public void EnterCall_ExitCall_ResetsIdleClock()
    {
        // Arrange
        var timeout = TimeSpan.FromMinutes(1);

        // Act — a completed call re-stamps last-activity to "now".
        IdleTimeoutWatchdog.EnterCall();
        IdleTimeoutWatchdog.ExitCall();
        long afterExit = Stopwatch.GetTimestamp();

        // Assert — 50 s after the ExitCall stamp is still inside the window (clock reset);
        // 120 s after it would expire, proving the anchor moved to the ExitCall, not 0.
        IdleTimeoutWatchdog.IsIdleExpired(timeout, afterExit + Ticks(50)).ShouldBeFalse();
        IdleTimeoutWatchdog.IsIdleExpired(timeout, afterExit + Ticks(120)).ShouldBeTrue();
    }

    [Fact]
    public void InFlightCall_SuppressesFire()
    {
        // Arrange
        var timeout = TimeSpan.FromMinutes(1);

        // Act / Assert — while a call is in flight, even an hour of elapsed time does
        // not count as idle: a multi-minute cold first call can never self-kill.
        IdleTimeoutWatchdog.EnterCall();
        long whileInFlight = Stopwatch.GetTimestamp() + Ticks(3600);
        IdleTimeoutWatchdog.IsIdleExpired(timeout, whileInFlight).ShouldBeFalse();

        // Once the call completes the same elapsed time does expire.
        IdleTimeoutWatchdog.ExitCall();
        long afterExit = Stopwatch.GetTimestamp() + Ticks(3600);
        IdleTimeoutWatchdog.IsIdleExpired(timeout, afterExit).ShouldBeTrue();
    }

    [Fact]
    public void Start_FastPath_InvokesExitWhenIdle()
    {
        // Arrange — real monotonic clock, 50 ms timeout, 10 ms poll. No EnterCall, so
        // the loop observes idle and invokes the (non-throwing) exit delegate.
        ManualResetEventSlim exited = new();

        // Act
        IdleTimeoutWatchdog.Start(
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(10),
            Stopwatch.GetTimestamp,
            () => exited.Set());

        // Assert — 10 s slack matches ParentProcessWatcherTests' analogous background
        // poll-loop test (Start_ParentExitsLater_InvokesOnExitWithinTimeout). A tighter
        // budget flakes under coverage instrumentation / a saturated thread pool, where
        // the Task.Run + PeriodicTimer first tick can be delayed seconds past the 50 ms
        // timeout; the loop still fires, just not within 2 s.
        exited.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue();
    }
}
