namespace Ingestion.Domain;

/// <summary>
/// The severity ladder a log level belongs to, so the stream can be filtered to
/// the lines worth attention — the errors, warnings and alerts — rather than only
/// to one exact level.
///
/// Stores log in whatever vocabulary their framework uses ("WARN", "warning",
/// "ERR", "fatal", …), and the raw token is stored uppercased. A severity is a
/// bucket of those tokens with a rank, so "at or above Warning" means the same
/// thing across stores without KNIGHT having rewritten what anyone logged. A
/// token in no bucket — a level nobody standardised — ranks as Information, the
/// same default the read path normalises it to, so it is treated as noise rather
/// than smuggled into the problem stream.
/// </summary>
public static class LogSeverity
{
    /// <summary>The canonical severities, lowest first. The index is the rank.</summary>
    private static readonly string[] Order = ["DEBUG", "INFORMATION", "WARNING", "ERROR", "CRITICAL"];

    /// <summary>Raw level token (uppercased) → canonical severity.</summary>
    private static readonly IReadOnlyDictionary<string, string> Buckets = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["TRACE"] = "DEBUG",
        ["DEBUG"] = "DEBUG",
        ["INFO"] = "INFORMATION",
        ["INFORMATION"] = "INFORMATION",
        ["NOTICE"] = "INFORMATION",
        ["WARN"] = "WARNING",
        ["WARNING"] = "WARNING",
        ["ERROR"] = "ERROR",
        ["ERR"] = "ERROR",
        ["CRITICAL"] = "CRITICAL",
        ["FATAL"] = "CRITICAL",
        ["ALERT"] = "CRITICAL",
        ["EMERGENCY"] = "CRITICAL",
    };

    /// <summary>
    /// The raw level tokens at or above the given severity, for filtering the
    /// stored <c>Level</c> column, or null when the severity is not recognised
    /// (so the caller can ignore the filter rather than return nothing).
    /// </summary>
    public static IReadOnlyList<string>? TokensAtOrAbove(string? minSeverity)
    {
        if (string.IsNullOrWhiteSpace(minSeverity))
        {
            return null;
        }

        // Accept either a canonical name ("Warning") or any raw token a store
        // might log ("warn", "err"). Anything else is not a severity, so the
        // filter is ignored rather than silently treated as a floor of its own.
        var token = minSeverity.Trim().ToUpperInvariant();
        string canonical;
        if (Array.IndexOf(Order, token) >= 0)
        {
            canonical = token;
        }
        else if (Buckets.TryGetValue(token, out var bucket))
        {
            canonical = bucket;
        }
        else
        {
            return null;
        }

        var minRank = Array.IndexOf(Order, canonical);
        return [.. Buckets.Where(pair => Array.IndexOf(Order, pair.Value) >= minRank).Select(pair => pair.Key)];
    }
}
