using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zphil.Roz.StressTests.Helpers;

/// <summary>
///     Persists stress test timing results to a local JSON file
///     at <c>%LOCALAPPDATA%\Zphil.Roz\TimingResults\timings.json</c>.
///     Compares new runs against historical baselines and flags regressions.
/// </summary>
internal sealed class TimingResultsCache
{
    private const int MaxRunsPerTest = 10;
    private const double DefaultRegressionThresholdPercent = 20.0;

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Zphil.Roz", "TimingResults");

    private static readonly string CacheFile = Path.Combine(CacheDir, "timings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    /// <summary>
    ///     Records a timing result and returns a comparison against the historical baseline.
    ///     Returns null if there is no prior history to compare against.
    /// </summary>
    public static async Task<TimingComparison?> RecordAsync(string testName, long elapsedMs)
    {
        await FileLock.WaitAsync();
        try
        {
            TimingData data = await LoadAsync();
            TimingTestResult testResult = data.GetOrCreate(testName);

            TimingComparison? comparison = testResult.Runs.Count > 0
                ? CompareAgainstBaseline(testName, elapsedMs, testResult)
                : null;

            testResult.Runs.Insert(0, new TimingRun
            {
                Timestamp = DateTime.UtcNow,
                ElapsedMs = elapsedMs
            });

            // Trim old runs
            while (testResult.Runs.Count > MaxRunsPerTest)
            {
                testResult.Runs.RemoveAt(testResult.Runs.Count - 1);
            }

            await SaveAsync(data);
            return comparison;
        }
        finally
        {
            FileLock.Release();
        }
    }

    private static async Task<TimingData> LoadAsync()
    {
        try
        {
            await using FileStream stream = File.OpenRead(CacheFile);
            return await JsonSerializer.DeserializeAsync<TimingData>(stream, JsonOptions)
                   ?? new TimingData();
        }
        catch (FileNotFoundException)
        {
            return new TimingData();
        }
    }

    private static async Task SaveAsync(TimingData data)
    {
        data.NopCommitSha = NopPaths.CommitSha;

        Directory.CreateDirectory(CacheDir);
        await using FileStream stream = File.Create(CacheFile);
        await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
    }

    private static TimingComparison CompareAgainstBaseline(
        string testName, long currentMs, TimingTestResult baseline)
    {
        double avgMs = baseline.Runs.Average(r => r.ElapsedMs);
        double changePercent = avgMs > 0 ? (currentMs - avgMs) / avgMs * 100.0 : 0;
        bool isRegression = changePercent > DefaultRegressionThresholdPercent;

        return new TimingComparison(testName, currentMs, avgMs, changePercent, isRegression, baseline.Runs.Count);
    }
}

internal sealed class TimingData
{
    public string? NopCommitSha { get; set; }
    public List<TimingTestResult> Results { get; set; } = [];

    public TimingTestResult GetOrCreate(string testName)
    {
        TimingTestResult? existing = Results.Find(r =>
            String.Equals(r.TestName, testName, StringComparison.Ordinal));

        if (existing is not null)
        {
            return existing;
        }

        var result = new TimingTestResult { TestName = testName };
        Results.Add(result);
        return result;
    }
}

internal sealed class TimingTestResult
{
    public string TestName { get; set; } = "";
    public List<TimingRun> Runs { get; set; } = [];
}

internal sealed class TimingRun
{
    public DateTime Timestamp { get; set; }
    public long ElapsedMs { get; set; }
}

/// <summary>
///     The result of comparing a new timing against historical baselines.
/// </summary>
internal sealed record TimingComparison(
    string TestName,
    long CurrentMs,
    double BaselineAvgMs,
    double ChangePercent,
    bool IsRegression,
    int BaselineRunCount)
{
    public string FormatReport()
    {
        string direction = ChangePercent >= 0 ? "slower" : "faster";
        string flag = IsRegression ? " ** REGRESSION **" : "";
        return $"{CurrentMs:N0} ms (baseline avg: {BaselineAvgMs:N0} ms, {Math.Abs(ChangePercent):F1}% {direction}, {BaselineRunCount} prior runs){flag}";
    }
}
