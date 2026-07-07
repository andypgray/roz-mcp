using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace Zphil.Roz.Infrastructure;

/// <summary>
///     Defensive shutdown watcher: when the MCP host process dies abnormally (Task Manager kill,
///     host crash, force-close) without cleanly closing this server's stdin pipe, the standard
///     stdin-EOF detection in the MCP SDK can miss the exit and orphan the server. This watcher
///     resolves the parent PID at startup and calls <see cref="Environment.Exit" /> when that
///     parent process terminates.
/// </summary>
/// <remarks>
///     Windows-only. Other hosts skip silently because the Unix MCP SDK reliably observes stdin EOF
///     and the parent-PID lookup uses a Windows-specific NT API. Set
///     <c>ROZ_DISABLE_PARENT_WATCH=true</c> to opt out (for debugging or detached runs).
/// </remarks>
internal static class ParentProcessWatcher
{
    /// <summary>
    ///     Resolves the parent PID, attaches a background waiter, and exits the process when the
    ///     parent dies. Logs and returns silently when disabled, on non-Windows hosts, or when the
    ///     parent PID cannot be resolved.
    /// </summary>
    public static void Start() => Start(
        GetParentProcessId,
        () => ServerShutdown.ExitWith("parent process exited"),
        RozEnvVars.DisableParentWatch.Read());

    /// <summary>
    ///     Test seam: lets tests inject a fake parent-PID lookup, a fake exit action (so
    ///     <see cref="Environment.Exit" /> doesn't kill the test host), and the disabled flag
    ///     directly without env-var leakage between tests.
    /// </summary>
    internal static void Start(Func<int?> parentLookup, Action onParentExited, bool disabled)
    {
        if (disabled)
        {
            Log.Information("Parent process watcher disabled via ROZ_DISABLE_PARENT_WATCH");
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            Log.Debug("Parent process watcher: non-Windows host, skipping");
            return;
        }

        int? parentPid = parentLookup();
        if (parentPid is null)
        {
            Log.Warning("Parent process watcher: could not resolve parent PID, skipping");
            return;
        }

        Process parent;
        try
        {
            parent = Process.GetProcessById(parentPid.Value);
        }
        catch (ArgumentException)
        {
            Log.Warning("Parent process {Pid} already exited at startup, exiting now", parentPid);
            onParentExited();
            return;
        }

        // Logged at Warning so post-mortem at the default min-level can confirm the watcher attached.
        Log.Warning("Parent process watcher attached to PID {Pid}", parentPid);
        _ = Task.Run(() => WatchAsync(parent, onParentExited));
    }

    /// <summary>
    ///     Returns the parent PID via <c>NtQueryInformationProcess</c>, or <c>null</c> on failure
    ///     (non-Windows host, NT call rejected, lookup threw).
    /// </summary>
    internal static int? GetParentProcessId()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var current = Process.GetCurrentProcess();
            ProcessBasicInformation info = default;
            int status = NtQueryInformationProcess(
                current.Handle,
                0,
                ref info,
                Marshal.SizeOf<ProcessBasicInformation>(),
                out _);

            if (status != 0)
            {
                Log.Debug("NtQueryInformationProcess returned non-zero status 0x{Status:X8}", status);
                return null;
            }

            return info.InheritedFromUniqueProcessId.ToInt32();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Parent PID lookup failed");
            return null;
        }
    }

    private static async Task WatchAsync(Process parent, Action onParentExited)
    {
        try
        {
            using (parent)
            {
                await parent.WaitForExitAsync();
            }

            Log.Warning("Parent process exited; shutting down server");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Parent-process watcher failed; exiting defensively");
        }
        finally
        {
            onParentExited();
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }
}
