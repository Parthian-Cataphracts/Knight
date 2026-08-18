using System.Text;
using Knight.Domain.Exceptions;

namespace Catalog.Domain;

/// <summary>
/// Shared slug normalization for catalog entities. Kept in one place so
/// <see cref="Category"/> and <see cref="Product"/> can never drift into two
/// different definitions of "the same slug" — uniqueness indexes are declared
/// on the normalized column, so the normalization itself is the constraint.
/// </summary>
internal static class SlugNormalizer
{
    internal const int MaxLength = 150;

    internal static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw DomainException.Validation("Slug is required.");
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var normalized = builder.ToString().Trim('-');
        if (normalized.Length == 0)
        {
            throw DomainException.Validation("Slug must contain at least one letter or digit.");
        }

        if (normalized.Length > MaxLength)
        {
            normalized = normalized[..MaxLength].Trim('-');
        }

        if (normalized.Length == 0)
        {
            throw DomainException.Validation("Slug must contain at least one letter or digit.");
        }

        return normalized;
    }
}
