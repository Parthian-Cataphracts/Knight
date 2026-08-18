using System.Text.RegularExpressions;
using Knight.Domain.Exceptions;

namespace Customer.Domain;

/// <summary>
/// Deterministic normalization and validation helpers for customer contact fields.
/// </summary>
public static class CustomerNormalization
{
    public const int MaxDisplayNameLength = 200;
    public const int MaxEmailLength = 320;
    public const int MaxPhoneLength = 32;
    public const int MinPhoneDigits = 7;
    public const int MaxPhoneDigits = 20;

    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string ValidateDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw DomainException.Validation("Customer display name is required.");
        }

        var trimmed = displayName.Trim();
        if (trimmed.Length > MaxDisplayNameLength)
        {
            throw DomainException.Validation($"Customer display name cannot exceed {MaxDisplayNameLength} characters.");
        }

        return trimmed;
    }

    public static (string? Phone, string? NormalizedPhone) NormalizePhone(string? rawPhone)
    {
        if (string.IsNullOrWhiteSpace(rawPhone))
        {
            return (null, null);
        }

        var trimmed = rawPhone.Trim();
        if (trimmed.Length > MaxPhoneLength)
        {
            throw DomainException.Validation($"Customer phone cannot exceed {MaxPhoneLength} characters.");
        }

        // Check for disallowed characters (letters, symbols other than +, -, ., (, ), /, space)
        var hasLeadingPlus = trimmed.StartsWith('+');
        var rest = hasLeadingPlus ? trimmed[1..] : trimmed;

        if (rest.Contains('+'))
        {
            throw DomainException.Validation("Phone number may only contain a '+' at the beginning.");
        }

        foreach (var c in rest)
        {
            if (!char.IsDigit(c) && c != ' ' && c != '-' && c != '.' && c != '(' && c != ')' && c != '/')
            {
                throw DomainException.Validation($"Phone number contains invalid character '{c}'.");
            }
        }

        // Normalize digits
        var digitsOnly = new string(rest.Where(char.IsDigit).ToArray());
        if (digitsOnly.Length < MinPhoneDigits || digitsOnly.Length > MaxPhoneDigits)
        {
            throw DomainException.Validation($"Phone number must contain between {MinPhoneDigits} and {MaxPhoneDigits} digits.");
        }

        var normalized = hasLeadingPlus ? $"+{digitsOnly}" : digitsOnly;
        return (trimmed, normalized);
    }

    public static (string? Email, string? NormalizedEmail) NormalizeEmail(string? rawEmail)
    {
        if (string.IsNullOrWhiteSpace(rawEmail))
        {
            return (null, null);
        }

        var trimmed = rawEmail.Trim();
        if (trimmed.Length > MaxEmailLength)
        {
            throw DomainException.Validation($"Customer email cannot exceed {MaxEmailLength} characters.");
        }

        if (!EmailRegex.IsMatch(trimmed))
        {
            throw DomainException.Validation($"Customer email '{trimmed}' is not in a valid format.");
        }

        var normalized = trimmed.ToLowerInvariant();
        return (trimmed, normalized);
    }
}
