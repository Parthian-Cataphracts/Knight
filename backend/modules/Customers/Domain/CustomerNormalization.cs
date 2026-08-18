using System.Text.RegularExpressions;
using Knight.Domain.Exceptions;

namespace Customers.Domain;

/// <summary>
/// Deterministic normalization for control-plane contact fields. Deliberately
/// local to this module: the frozen store-side <c>Customer</c> module has its own
/// rules for end-consumer contact data, and the control plane must not depend on
/// a store module (docs/architecture.md, dependency rules).
/// </summary>
public static partial class CustomerNormalization
{
    public const int MaxEmailLength = 320;
    public const int MaxPhoneLength = 32;

    public static string NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw DomainException.Validation("Contact email is required.");
        }

        var trimmed = email.Trim();
        if (trimmed.Length > MaxEmailLength)
        {
            throw DomainException.Validation($"Contact email cannot exceed {MaxEmailLength} characters.");
        }

        if (!EmailPattern().IsMatch(trimmed))
        {
            throw DomainException.Validation("Contact email is not a valid address.");
        }

        return trimmed.ToLowerInvariant();
    }

    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var trimmed = phone.Trim();
        if (trimmed.Length > MaxPhoneLength)
        {
            throw DomainException.Validation($"Phone cannot exceed {MaxPhoneLength} characters.");
        }

        var digits = trimmed.Count(char.IsDigit);
        if (digits is < 7 or > 20)
        {
            throw DomainException.Validation("Phone must contain between 7 and 20 digits.");
        }

        return trimmed;
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.IgnoreCase)]
    private static partial Regex EmailPattern();
}
