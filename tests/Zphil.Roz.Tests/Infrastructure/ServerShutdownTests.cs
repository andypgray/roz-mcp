using Serilog;
using Serilog.Core;
using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Tests.Infrastructure;

/// <summary>
///     Exercises <see cref="ServerShutdown" />'s gate, disposal-then-flush-then-exit order,
///     timeout fallback, and registration-order semantics through the injectable exit/timeout
///     test seam — no real process exits during these tests. Mirrors the static-state reset
///     idiom used by <see cref="IdleTimeoutWatchdogTests" />.
/// </summary>
[Collection("WatchdogStatics")]
public sealed class ServerShutdownTests : IDisposable
{
    public ServerShutdownTests()
    {
        ServerShutdown.ResetForTests();
        IdleTimeoutWatchdog.ResetForTests();
    }

    public void Dispose()
    {
        ServerShutdown.ResetForTests();
        IdleTimeoutWatchdog.ResetForTests();
    }

    [Fact]
    public void ExitWith_RegisteredDisposer_RunsBeforeExit()
    {
        // Arrange — track the order in which the disposer and exit callback run.
        var disposerRan = false;
        var exitRanAfterDisposer = false;
        ServerShutdown.RegisterDisposer(() =>
        {
            disposerRan = true;
            return ValueTask.CompletedTask;
        });

        // Act
        ServerShutdown.ExitWith("test", () => exitRanAfterDisposer = disposerRan, TimeSpan.FromSeconds(1));

        // Assert — disposer must have already run by the time exit fires.
        disposerRan.ShouldBeTrue();
        exitRanAfterDisposer.ShouldBeTrue();
        ServerShutdown.HasExited.ShouldBeTrue();
    }

    [Fact]
    public void ExitWith_CalledTwice_OnlyExitsOnce()
    {
        // Arrange — second concurrent caller must be silenced by the Interlocked gate; the
        // disposer runs once (would corrupt state if rerun on a half-disposed workspace).
        var disposerCallCount = 0;
        var exitCount = 0;
        ServerShutdown.RegisterDisposer(() =>
        {
            Interlocked.Increment(ref disposerCallCount);
            return ValueTask.CompletedTask;
        });

        // Act
        ServerShutdown.ExitWith("first", () => Interlocked.Increment(ref exitCount), TimeSpan.FromSeconds(1));
        ServerShutdown.ExitWith("second", () => Interlocked.Increment(ref exitCount), TimeSpan.FromSeconds(1));

        // Assert
        disposerCallCount.ShouldBe(1);
        exitCount.ShouldBe(1);
        ServerShutdown.HasExited.ShouldBeTrue();
    }

    [Fact]
    public void ExitWith_FaultedDisposer_DoesNotBlockExit()
    {
        // Arrange — a disposer that throws must not deadlock or skip exit. The plan's contract:
        // log at Warning, fall through.
        ServerShutdown.RegisterDisposer(() => throw new InvalidOperationException("simulated disposer fault"));
        var exited = false;

        // Act
        ServerShutdown.ExitWith("test", () => exited = true, TimeSpan.FromSeconds(1));

        // Assert
        exited.ShouldBeTrue();
    }

    [Fact]
    public void ExitWith_AsynchronouslyFaultedDisposer_DoesNotBlockExit()
    {
        // Arrange — a disposer that returns a faulted Task (not a sync throw) follows the same
        // contract via the AggregateException branch in Task.Wait.
        ServerShutdown.RegisterDisposer(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("simulated async disposer fault");
        });
        var exited = false;

        // Act
        ServerShutdown.ExitWith("test", () => exited = true, TimeSpan.FromSeconds(1));

        // Assert
        exited.ShouldBeTrue();
    }

    [Fact]
    public void ExitWith_HangingDisposer_ExitsAfterTimeout()
    {
        // Arrange — a disposer that never completes must not stall shutdown past the bound.
        // 200 ms timeout keeps the test fast; we just need to confirm exit happens.
        TaskCompletionSource neverCompletes = new();
        ServerShutdown.RegisterDisposer(() => new ValueTask(neverCompletes.Task));
        var exited = false;

        try
        {
            // Act
            ServerShutdown.ExitWith("test", () => exited = true, TimeSpan.FromMilliseconds(200));

            // Assert
            exited.ShouldBeTrue();
        }
        finally
        {
            // Cleanup — release the suspended task so the worker thread can unwind, even if the assert fails.
            neverCompletes.SetResult();
        }
    }

    [Fact]
    public void ExitWith_MultipleDisposers_RunInRegistrationOrder()
    {
        // Arrange — call site currently registers one, but the API permits more; lock in
        // ordering so future call sites don't get surprised.
        List<int> order = [];
        ServerShutdown.RegisterDisposer(() =>
        {
            order.Add(1);
            return ValueTask.CompletedTask;
        });
        ServerShutdown.RegisterDisposer(() =>
        {
            order.Add(2);
            return ValueTask.CompletedTask;
        });
        ServerShutdown.RegisterDisposer(() =>
        {
            order.Add(3);
            return ValueTask.CompletedTask;
        });

        // Act
        ServerShutdown.ExitWith("test", () => { }, TimeSpan.FromSeconds(1));

        // Assert
        order.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void ExitWith_LoggerFlushed_BeforeExit()
    {
        // Arrange — swap Log.Logger to a real Logger we can observe. Log.CloseAndFlush()
        // disposes the current Logger and swaps Log.Logger to Logger.None, so a reference
        // check inside the exit callback proves CloseAndFlush ran before exit.
        ILogger originalLogger = Log.Logger;
        try
        {
            Logger freshLogger = new LoggerConfiguration().CreateLogger();
            Log.Logger = freshLogger;

            var loggerSwappedBeforeExit = false;
            ServerShutdown.ExitWith(
                "test",
                () => loggerSwappedBeforeExit = !ReferenceEquals(Log.Logger, freshLogger),
                TimeSpan.FromSeconds(1));

            // Assert — Log.Logger must have changed reference by the time exit runs, proving
            // the production order: disposal → CloseAndFlush → exit.
            loggerSwappedBeforeExit.ShouldBeTrue();
        }
        finally
        {
            Log.Logger = originalLogger;
        }
    }

    [Fact]
    public void RegisterDisposer_Null_Throws()
    {
        // Arrange / Act / Assert — guard rail; passing null indicates a wiring bug.
        Should.Throw<ArgumentNullException>(() => ServerShutdown.RegisterDisposer(null!));
    }

    [Fact]
    public async Task ExitWith_InFlightCallInProgress_WaitsForItBeforeDisposingAndExiting()
    {
        // N2 — a mid-flight tool call (e.g. an edit batch mid-write) must finish before shutdown runs
        // disposers or exits, so Environment.Exit can't tear a non-atomic swap. Enter a call, start the
        // shutdown on a background thread, prove it is blocked (nothing disposed, not exited), then
        // complete the call and prove it proceeds.
        IdleTimeoutWatchdog.EnterCall(); // in-flight count = 1

        var disposerRan = false;
        ServerShutdown.RegisterDisposer(() =>
        {
            disposerRan = true;
            return ValueTask.CompletedTask;
        });

        using ManualResetEventSlim exited = new();
        var shutdown = Task.Run(() => ServerShutdown.ExitWith(
            "test", exited.Set, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));

        // While the call is in flight, the drain blocks: no disposer has run and exit hasn't fired.
        exited.Wait(TimeSpan.FromMilliseconds(300)).ShouldBeFalse();
        disposerRan.ShouldBeFalse();

        // Completing the call releases the drain; shutdown proceeds to disposers + exit.
        IdleTimeoutWatchdog.ExitCall(); // in-flight count = 0
        exited.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        disposerRan.ShouldBeTrue();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ExitWith_InFlightCallStuck_ExitsAfterDrainTimeout()
    {
        // N2 bound — a call that never completes must not hold shutdown hostage. With the in-flight count
        // pinned at 1 and a short drain timeout, ExitWith still runs disposers and exits.
        IdleTimeoutWatchdog.EnterCall(); // count stays 1 (no matching ExitCall)

        var exited = false;
        ServerShutdown.ExitWith("test", () => exited = true, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200));

        exited.ShouldBeTrue();
    }
}

/// <summary>
///     Serializes the two suites that share the static in-flight counter — <see cref="ServerShutdown" />
///     reads <see cref="IdleTimeoutWatchdog.InFlightCount" /> during its N2 drain — so their static-state
///     manipulation can't interleave under xUnit's parallel collections.
/// </summary>
[CollectionDefinition("WatchdogStatics", DisableParallelization = true)]
public sealed class WatchdogStaticsCollection;
