using System.Text.RegularExpressions;
using Knight.Domain.Exceptions;

namespace FeatureRegistry.Domain;

/// <summary>
/// A feature's slug is the name its Python package carries, so it is normalised
/// to exactly what a package name may be: lowercase, hyphen-separated, no
/// leading or trailing hyphen. Getting this wrong later means a package nobody
/// can install, so it is refused here rather than corrected silently.
/// </summary>
public static partial class FeatureSlug
{
    public const int MaxLength = 100;

    public static string Normalize(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            throw DomainException.Validation("Feature slug is required.");
        }

        var normalized = slug.Trim().ToLowerInvariant();

        if (normalized.Length > MaxLength)
        {
            throw DomainException.Validation($"Feature slug cannot exceed {MaxLength} characters.");
        }

        if (!Pattern().IsMatch(normalized))
        {
            throw DomainException.Validation(
                "Feature slug must be lowercase letters, digits and single hyphens, starting and ending with a letter or digit.");
        }

        return normalized;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Pattern();
}
