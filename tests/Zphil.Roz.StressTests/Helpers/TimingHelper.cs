using System.Diagnostics;

namespace Zphil.Roz.StressTests.Helpers;

/// <summary>
///     Wraps async operations with <see cref="Stopwatch" />-based timing,
///     writing elapsed milliseconds to xUnit's <see cref="ITestOutputHelper" />.
///     Persists results to a local cache and flags regressions against historical baselines.
/// </summary>
internal static class TimingHelper
{
    public static async Task<T> TimeAsync<T>(string label, Func<Task<T>> action, ITestOutputHelper output)
    {
        var sw = Stopwatch.StartNew();
        T result = await action();
        sw.Stop();
        await ReportAsync(label, sw.ElapsedMilliseconds, output);
        return result;
    }

    public static async Task TimeAsync(string label, Func<Task> action, ITestOutputHelper output)
    {
        var sw = Stopwatch.StartNew();
        await action();
        sw.Stop();
        await ReportAsync(label, sw.ElapsedMilliseconds, output);
    }

    private static async Task ReportAsync(string label, long elapsedMs, ITestOutputHelper output)
    {
        TimingComparison? comparison = await TimingResultsCache.RecordAsync(label, elapsedMs);

        if (comparison is null)
        {
            output.WriteLine($"[TIMING] {label}: {elapsedMs:N0} ms (first run, no baseline)");
        }
        else
        {
            output.WriteLine($"[TIMING] {label}: {comparison.FormatReport()}");
        }
    }
}
