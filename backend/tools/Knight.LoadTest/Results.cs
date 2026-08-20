using System.Collections.Concurrent;

namespace Knight.LoadTest;

/// <summary>
/// Collects every response and prints the summary at the end.
///
/// Latencies are kept in full rather than aggregated as they arrive, because
/// percentiles cannot be computed from a running mean. A minute at a few
/// thousand requests per second is a few hundred thousand doubles, which costs
/// less memory than the payloads already in flight.
/// </summary>
internal sealed class Results
{
    private readonly ConcurrentQueue<double> _latencies = new();
    private readonly ConcurrentDictionary<string, int> _byKind = new();
    private readonly ConcurrentDictionary<int, int> _byStatus = new();

    public void Record(string kind, int status, TimeSpan elapsed)
    {
        _latencies.Enqueue(elapsed.TotalMilliseconds);
        _byKind.AddOrUpdate(kind, 1, (_, count) => count + 1);
        _byStatus.AddOrUpdate(status, 1, (_, count) => count + 1);
    }

    public void Report(TimeSpan elapsed)
    {
        var latencies = _latencies.ToArray();
        Array.Sort(latencies);

        var total = latencies.Length;
        if (total == 0)
        {
            Console.WriteLine("No requests completed.");
            return;
        }

        var succeeded = _byStatus.Where(entry => entry.Key is >= 200 and < 300).Sum(entry => entry.Value);
        var throttled = _byStatus.TryGetValue(429, out var rateLimited) ? rateLimited : 0;
        var transport = _byStatus.TryGetValue(0, out var broken) ? broken : 0;

        Console.WriteLine("--- Results -------------------------------------------------");
        Console.WriteLine($"Elapsed          {elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"Requests         {total}");
        Console.WriteLine($"Throughput       {total / elapsed.TotalSeconds:F0} req/s");
        Console.WriteLine($"Accepted (2xx)   {succeeded} ({100.0 * succeeded / total:F1}%)");

        if (throttled > 0)
        {
            // Called out rather than folded into a failure count. A run that was
            // mostly rate-limited measured the limiter, not the write path.
            Console.WriteLine($"Rate-limited     {throttled} ({100.0 * throttled / total:F1}%)");
        }

        if (transport > 0)
        {
            Console.WriteLine($"Transport errors {transport}");
        }

        Console.WriteLine();
        Console.WriteLine("Latency (ms)");
        Console.WriteLine($"  min            {latencies[0]:F1}");
        Console.WriteLine($"  p50            {Percentile(latencies, 50):F1}");
        Console.WriteLine($"  p90            {Percentile(latencies, 90):F1}");
        Console.WriteLine($"  p99            {Percentile(latencies, 99):F1}");
        Console.WriteLine($"  max            {latencies[^1]:F1}");

        Console.WriteLine();
        Console.WriteLine("By endpoint");
        foreach (var entry in _byKind.OrderByDescending(entry => entry.Value))
        {
            Console.WriteLine($"  {entry.Key,-12} {entry.Value}");
        }

        Console.WriteLine();
        Console.WriteLine("By status");
        foreach (var entry in _byStatus.OrderBy(entry => entry.Key))
        {
            var label = entry.Key == 0 ? "transport" : entry.Key.ToString();
            Console.WriteLine($"  {label,-12} {entry.Value}");
        }
    }

    /// <summary>
    /// Nearest-rank on the sorted array. Not interpolated: with this many
    /// samples the difference is noise, and the rank is the number people expect
    /// when they check it by hand.
    /// </summary>
    private static double Percentile(double[] sorted, int percentile)
    {
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}
