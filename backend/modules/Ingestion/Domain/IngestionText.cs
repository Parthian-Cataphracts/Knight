using Knight.Domain.Exceptions;

namespace Ingestion.Domain;

/// <summary>
/// Length handling for text that arrives from a store.
///
/// The two rules are deliberately different. A field the record cannot mean
/// anything without — the exception type, the environment — is <em>required</em>
/// and refused when it is missing or absurdly long, because accepting a
/// truncated identity would silently corrupt grouping later. Everything else is
/// <em>clipped</em>: a 4 MB stack trace is still worth the first 20 000
/// characters, and rejecting the batch over it would lose the error entirely.
/// </summary>
internal static class IngestionText
{
    public static string Require(string? value, int maxLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw DomainException.Validation($"'{field}' is required.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw DomainException.Validation($"'{field}' cannot exceed {maxLength} characters.");
        }

        return trimmed;
    }

    public static string? Clip(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
