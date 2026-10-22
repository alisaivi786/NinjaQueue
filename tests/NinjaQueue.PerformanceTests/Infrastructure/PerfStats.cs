namespace NinjaQueue.PerformanceTests.Infrastructure;

/// <summary>Percentiles over a set of samples (nearest-rank).</summary>
public sealed class Percentiles
{
    private readonly double[] _sorted;

    public Percentiles(IEnumerable<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        _sorted = [.. samples.Order()];
    }

    public int Count => _sorted.Length;

    public double P50 => At(50);

    public double P95 => At(95);

    public double P99 => At(99);

    public double Max => _sorted.Length == 0 ? 0 : _sorted[^1];

    public double Mean => _sorted.Length == 0 ? 0 : _sorted.Average();

    public double At(double percentile)
    {
        if (_sorted.Length == 0)
        {
            return 0;
        }

        int rank = (int)Math.Ceiling(percentile / 100.0 * _sorted.Length);
        return _sorted[Math.Clamp(rank - 1, 0, _sorted.Length - 1)];
    }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"n={Count} p50={P50:F1} p95={P95:F1} p99={P99:F1} max={Max:F1} mean={Mean:F1}");
}

/// <summary>
/// Writes each measurement to the test output and appends it to <c>perf-results.txt</c> (next to the test assembly, or the
/// path in <c>NINJA_JOBS_PERF_RESULTS</c>), so a run leaves one file with every number measured.
/// </summary>
public static class PerfReport
{
    private static readonly Lock Gate = new();

    public static string ResultsPath => Environment.GetEnvironmentVariable("NINJA_JOBS_PERF_RESULTS") is { Length: > 0 } path
        ? path
        : Path.Combine(AppContext.BaseDirectory, "perf-results.txt");

    public static void Write(ITestOutputHelper output, DatabaseKind kind, string metric, string measured, string target, bool pass)
    {
        ArgumentNullException.ThrowIfNull(output);
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ} | {kind,-16} | {metric} | measured {measured} | target {target} | {(pass ? "PASS" : "FAIL")}");
        output.WriteLine(line);
        lock (Gate)
        {
            File.AppendAllLines(ResultsPath, [line]);
        }
    }

    public static void Note(ITestOutputHelper output, DatabaseKind kind, string text)
    {
        ArgumentNullException.ThrowIfNull(output);
        string line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ} | {kind,-16} | note | {text}");
        output.WriteLine(line);
        lock (Gate)
        {
            File.AppendAllLines(ResultsPath, [line]);
        }
    }
}

/// <summary>Suite sizes, overridable for a nightly run on production-sized hardware.</summary>
public static class PerfScale
{
    /// <summary>Jobs per throughput run (<c>NINJA_JOBS_PERF_JOBS</c>, default 20 000).</summary>
    public static int Jobs => Read("NINJA_JOBS_PERF_JOBS", 20_000);

    /// <summary>The largest backlog for the claim-latency test (<c>NINJA_JOBS_PERF_MAX_BACKLOG</c>, default 1 000 000).</summary>
    public static int MaxBacklog => Read("NINJA_JOBS_PERF_MAX_BACKLOG", 1_000_000);

    /// <summary>Terminal jobs seeded for the purge test (<c>NINJA_JOBS_PERF_PURGE_JOBS</c>, default 100 000).</summary>
    public static int PurgeJobs => Read("NINJA_JOBS_PERF_PURGE_JOBS", 100_000);

    private static int Read(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : fallback;
}

/// <summary>Polling helper.</summary>
public static class Wait
{
    public static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan timeout, int pollMs = 100)
    {
        ArgumentNullException.ThrowIfNull(condition);
        Stopwatch watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.Elapsed > timeout)
            {
                throw new TimeoutException($"Condition not met within {timeout}.");
            }

            await Task.Delay(pollMs);
        }
    }
}