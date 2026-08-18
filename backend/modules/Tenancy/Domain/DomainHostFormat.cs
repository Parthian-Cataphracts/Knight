using System.Text.RegularExpressions;

namespace Tenancy.Domain;

/// <summary>
/// Normalization and validation for tenant domain hosts. Comparison of hosts must
/// be deterministic: no scheme, no path, no port, lowercase, trailing dot removed.
/// </summary>
internal static partial class DomainHostFormat
{
    private const int MaxLength = 255;

    /// <summary>
    /// Normalizes a raw host value. Throws <see cref="FormatException"/> if the
    /// input is not a bare host (contains a scheme, path, query, or port) or does
    /// not otherwise look like a valid hostname — callers should translate that
    /// into a domain-level validation failure.
    /// </summary>
    public static string Normalize(string raw)
    {
        var value = raw.Trim();

        if (value.Contains("://", StringComparison.Ordinal))
        {
            throw new FormatException("Host must not include a URI scheme.");
        }

        if (value.Contains('/', StringComparison.Ordinal) || value.Contains('?', StringComparison.Ordinal))
        {
            throw new FormatException("Host must not include a path or query string.");
        }

        if (value.Contains(':', StringComparison.Ordinal))
        {
            throw new FormatException("Host must not include a port.");
        }

        value = value.ToLowerInvariant();

        if (value.EndsWith('.'))
        {
            value = value[..^1];
        }

        if (!IsValid(value))
        {
            throw new FormatException($"'{raw}' is not a valid host.");
        }

        return value;
    }

    private static bool IsValid(string normalized) =>
        normalized.Length is > 0 and <= MaxLength && HostPattern().IsMatch(normalized);

    // Dot-separated labels of letters, digits, and hyphens; no leading/trailing
    // hyphen per label; at least two labels (rejects bare single-word hosts).
    [GeneratedRegex(@"^(?!-)[a-z0-9-]{1,63}(?<!-)(\.(?!-)[a-z0-9-]{1,63}(?<!-))+$")]
    private static partial Regex HostPattern();
}
