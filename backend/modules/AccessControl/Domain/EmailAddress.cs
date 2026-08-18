using System.Text.RegularExpressions;
using Knight.Domain.Exceptions;

namespace AccessControl.Domain;

/// <summary>
/// Normalization for control-plane account emails. Kept local to this module for
/// the same reason <c>CustomerNormalization</c> is: the control plane must not
/// depend on a frozen store-side module (docs/architecture.md, dependency rules).
/// </summary>
public static partial class EmailAddress
{
    public const int MaxLength = 320;

    /// <summary>The stored, display form: trimmed, case preserved in the local part.</summary>
    public static string Normalize(string? email) => Validate(email).Trimmed;

    /// <summary>The uppercase-invariant form; the only value uniqueness and lookup use.</summary>
    public static string NormalizeForComparison(string? email) => Validate(email).Trimmed.ToUpperInvariant();

    private static (string Trimmed, bool Valid) Validate(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw DomainException.Validation("Email is required.");
        }

        var trimmed = email.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw DomainException.Validation($"Email cannot exceed {MaxLength} characters.");
        }

        if (!Pattern().IsMatch(trimmed))
        {
            throw DomainException.Validation("Email is not a valid address.");
        }

        return (trimmed, true);
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}
