using System.Text.RegularExpressions;

namespace Identity.Domain;

/// <summary>
/// Normalization and validation for account email addresses. <c>Email</c> stores
/// the trimmed, lowercased value; <c>NormalizedEmail</c> stores an
/// uppercase-invariant form used exclusively for uniqueness and lookup, so
/// PostgreSQL's default case-sensitive string comparison cannot let two
/// case-variant emails collide or bypass uniqueness.
/// </summary>
internal static partial class EmailFormat
{
    private const int MaxLength = 320;

    public static string Normalize(string raw) => raw.Trim().ToLowerInvariant();

    public static string NormalizeForComparison(string raw) => raw.Trim().ToUpperInvariant();

    public static bool IsValid(string normalized) =>
        normalized.Length is > 0 and <= MaxLength && EmailPattern().IsMatch(normalized);

    [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]+$")]
    private static partial Regex EmailPattern();
}
