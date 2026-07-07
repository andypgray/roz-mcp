using System.Diagnostics;
using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Tests.Infrastructure;

/// <summary>
///     Verifies <see cref="ParentProcessWatcher" /> opt-out, parent-resolution failure paths,
///     immediate-exit on dead parent, and live-parent watch via the internal test seam.
/// </summary>
public sealed class ParentProcessWatcherTests
{
    [Fact]
    public void Start_Disabled_DoesNotInvokeOnExit()
    {
        // Arrange / Act / Assert — disabled flag short-circuits before parent lookup.
        ParentProcessWatcher.Start(
            () => throw new InvalidOperationException("lookup must not run when disabled"),
            () => throw new InvalidOperationException("onExit must not run when disabled"),
            true);
    }

    [Fact]
    public void Start_ParentLookupReturnsNull_DoesNotInvokeOnExit()
    {
        // Arrange / Act / Assert — null PID resolution is treated as "skip", not "exit immediately".
        ParentProcessWatcher.Start(
            () => null,
            () => throw new InvalidOperationException("onExit must not run when PID is null"),
            false);
    }

    [Fact]
    public void Start_ParentAlreadyExited_InvokesOnExitImmediately()
    {
        // The watcher bails on OperatingSystem.IsWindows() before reaching the synchronous
        // already-exited branch, so this scenario is only reachable on Windows.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange — spawn a short-lived helper, wait for it to die, then point the watcher at its
        // dead PID. Process.GetProcessById should throw ArgumentException, triggering the
        // synchronous onParentExited path.
        int deadPid = SpawnAndWaitForExit();
        ManualResetEventSlim exited = new();

        // Act
        ParentProcessWatcher.Start(
            () => deadPid,
            () => exited.Set(),
            false);

        // Assert — synchronous: should already be set, but allow tiny slack.
        exited.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ShouldBeTrue();
    }

    [Fact]
    public void Start_ParentExitsLater_InvokesOnExitWithinTimeout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange — spawn a helper that lives ~1 second, point watcher at its live PID.
        using Process helper = SpawnLiveHelper();
        int helperPid = helper.Id;
        ManualResetEventSlim exited = new();

        // Act
        ParentProcessWatcher.Start(
            () => helperPid,
            () => exited.Set(),
            false);

        // Assert — the helper exits quickly; allow generous slack for CI/scheduler.
        exited.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue();
    }

    [Fact]
    public void GetParentProcessId_OnWindows_ReturnsNonNull()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // testhost is spawned by the test runner (vstest.console / dotnet test), so it always
        // has a parent. A non-null result confirms the P/Invoke shape compiles and dispatches.
        int? parentPid = ParentProcessWatcher.GetParentProcessId();

        parentPid.ShouldNotBeNull();
        parentPid.Value.ShouldBeGreaterThan(0);
    }

    /// <summary>
    ///     Spawns <c>cmd /c exit 0</c>, waits for it to terminate, returns its (now-dead) PID.
    /// </summary>
    private static int SpawnAndWaitForExit()
    {
        ProcessStartInfo psi = new("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using Process proc = Process.Start(psi)
                             ?? throw new InvalidOperationException("Failed to start cmd.exe");
        int pid = proc.Id;
        proc.WaitForExit(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        return pid;
    }

    /// <summary>
    ///     Spawns a helper process that lives for roughly one second. Returns the live
    ///     <see cref="Process" /> so the caller can resolve its PID before exit.
    /// </summary>
    private static Process SpawnLiveHelper()
    {
        // ping -n 2 127.0.0.1 sleeps ~1 second between pings, so total runtime is ~1s.
        ProcessStartInfo psi = new("cmd.exe", "/c ping -n 2 127.0.0.1 >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start helper");
    }
}
