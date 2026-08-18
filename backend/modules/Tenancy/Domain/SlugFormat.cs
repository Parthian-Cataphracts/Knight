using System.Text.RegularExpressions;

namespace Tenancy.Domain;

/// <summary>
/// Normalization and validation for tenant slugs. A slug is the stable,
/// human-readable identifier used in places a raw <see cref="Guid"/> would not be
/// (e.g. future default subdomains). Kept as a static helper rather than a full
/// value object since <see cref="Tenant"/> is the only consumer.
/// </summary>
internal static partial class SlugFormat
{
    private const int MaxLength = 63;

    public static string Normalize(string raw) => raw.Trim().ToLowerInvariant();

    public static bool IsValid(string normalized) =>
        normalized.Length is > 0 and <= MaxLength && SlugPattern().IsMatch(normalized);

    // Lowercase alphanumeric segments separated by single hyphens; no leading,
    // trailing, or consecutive hyphens.
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();
}
