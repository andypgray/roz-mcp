using System.Diagnostics;

namespace Zphil.Roz.StressTests.Helpers;

/// <summary>
///     Captures managed heap, working set, allocation, and GC counters at a single instant.
///     Used by memory-profiling stress tests to detect growth across repeated tool calls.
/// </summary>
internal readonly record struct MemorySnapshot(
    int Iteration,
    long ManagedHeapBytes,
    long WorkingSetBytes,
    long PrivateBytes,
    long TotalAllocatedBytes,
    int Gen0Count,
    int Gen1Count,
    int Gen2Count)
{
    /// <summary>
    ///     Captures a snapshot. Forces a full GC first so the managed-heap reading reflects
    ///     retained memory rather than uncollected garbage — that's what we care about for
    ///     leak detection.
    /// </summary>
    public static MemorySnapshot Capture(int iteration)
    {
        long managed = GC.GetTotalMemory(true);
        long allocated = GC.GetTotalAllocatedBytes();
        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);

        var proc = Process.GetCurrentProcess();
        long workingSet = proc.WorkingSet64;
        long privateBytes = proc.PrivateMemorySize64;

        return new MemorySnapshot(iteration, managed, workingSet, privateBytes, allocated, gen0, gen1, gen2);
    }

    public static string FormatHeader() =>
        $"{"Iter",6} | {"Heap MB",8} | {"WSet MB",8} | {"Priv MB",8} | {"Allocd MB",10} | {"G0",4} {"G1",4} {"G2",4}";

    public string FormatRow(MemorySnapshot baseline) =>
        $"{Iteration,6} | {ManagedHeapBytes / 1024 / 1024,8:N0} | " +
        $"{WorkingSetBytes / 1024 / 1024,8:N0} | {PrivateBytes / 1024 / 1024,8:N0} | " +
        $"{(TotalAllocatedBytes - baseline.TotalAllocatedBytes) / 1024 / 1024,10:N0} | " +
        $"{Gen0Count - baseline.Gen0Count,4} {Gen1Count - baseline.Gen1Count,4} {Gen2Count - baseline.Gen2Count,4}";

    public string FormatGrowthSummary(MemorySnapshot baseline)
    {
        long heapDelta = (ManagedHeapBytes - baseline.ManagedHeapBytes) / 1024 / 1024;
        long wsDelta = (WorkingSetBytes - baseline.WorkingSetBytes) / 1024 / 1024;
        long privDelta = (PrivateBytes - baseline.PrivateBytes) / 1024 / 1024;
        long totalAllocMb = (TotalAllocatedBytes - baseline.TotalAllocatedBytes) / 1024 / 1024;

        return $"Managed heap: {heapDelta:+#;-#;0} MB | Working set: {wsDelta:+#;-#;0} MB | " +
               $"Private: {privDelta:+#;-#;0} MB | Total allocated: {totalAllocMb:N0} MB | " +
               $"GCs: Gen0={Gen0Count - baseline.Gen0Count} Gen1={Gen1Count - baseline.Gen1Count} Gen2={Gen2Count - baseline.Gen2Count}";
    }
}
