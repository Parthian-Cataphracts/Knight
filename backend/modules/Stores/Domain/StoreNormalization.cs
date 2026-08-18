using System.Globalization;
using System.Text.RegularExpressions;
using Knight.Domain.Exceptions;

namespace Stores.Domain;

/// <summary>
/// Deterministic normalization for store identity fields. Hosts are lower-cased,
/// stripped of scheme, port, trailing dot and any path, and IDN-encoded, so the
/// same domain written five ways resolves to one stored value — the property the
/// uniqueness index depends on.
/// </summary>
public static partial class StoreNormalization
{
    public const int MaxNameLength = 200;
    public const int MaxSlugLength = 100;
    public const int MaxHostLength = 253;
    public const int MaxVersionLength = 50;

    public static string ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("Store name is required.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw DomainException.Validation($"Store name cannot exceed {MaxNameLength} characters.");
        }

        return trimmed;
    }

    public static string NormalizeSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            throw DomainException.Validation("Store slug is required.");
        }

        var normalized = slug.Trim().ToLowerInvariant();
        if (normalized.Length > MaxSlugLength)
        {
            throw DomainException.Validation($"Store slug cannot exceed {MaxSlugLength} characters.");
        }

        if (!SlugPattern().IsMatch(normalized))
        {
            throw DomainException.Validation(
                "Store slug may contain only lowercase letters, digits and single hyphens, and cannot start or end with a hyphen.");
        }

        return normalized;
    }

    public static string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw DomainException.Validation("Store domain is required.");
        }

        var value = host.Trim();

        // Tolerate a pasted URL rather than rejecting it: operators copy from a browser.
        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                throw DomainException.Validation("Store domain is not a valid host.");
            }

            value = uri.Host;
        }
        else
        {
            var slash = value.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                value = value[..slash];
            }

            var colon = value.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                value = value[..colon];
            }
        }

        value = value.TrimEnd('.').ToLowerInvariant();

        if (value.Length is 0 or > MaxHostLength)
        {
            throw DomainException.Validation("Store domain is not a valid host.");
        }

        try
        {
            value = new IdnMapping().GetAscii(value);
        }
        catch (ArgumentException)
        {
            throw DomainException.Validation("Store domain is not a valid host.");
        }

        if (!HostPattern().IsMatch(value))
        {
            throw DomainException.Validation("Store domain is not a valid host.");
        }

        return value;
    }

    public static string? NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var trimmed = version.Trim();
        if (trimmed.Length > MaxVersionLength)
        {
            throw DomainException.Validation($"Version cannot exceed {MaxVersionLength} characters.");
        }

        return trimmed;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"^(?!-)[a-z0-9-]{1,63}(?<!-)(\.(?!-)[a-z0-9-]{1,63}(?<!-))+$")]
    private static partial Regex HostPattern();
}
