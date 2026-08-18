using Knight.Domain.Exceptions;

namespace Knight.Domain.Common;

/// <summary>
/// An amount and the currency it is in, kept together so the two can never drift
/// apart in a calculation. Arithmetic across currencies is refused rather than
/// silently performed on the numbers — that is how a customer ends up invoiced
/// for the wrong sum in a plausible-looking amount.
///
/// Defined here, as a domain primitive, rather than borrowed from the frozen
/// Payment module: the control plane may not depend on a store-side module, and
/// plans, subscriptions and billing all need the same type.
/// </summary>
public sealed class Money : ValueObject
{
    /// <summary>Two decimal places, which is what every currency this platform bills in uses.</summary>
    public const int Scale = 2;

    public decimal Amount { get; }

    /// <summary>ISO 4217, uppercase.</summary>
    public string Currency { get; }

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money Of(decimal amount, string currency)
    {
        if (amount < 0)
        {
            throw DomainException.Validation("An amount cannot be negative.");
        }

        return new Money(Round(amount), NormalizeCurrency(currency));
    }

    public static Money Zero(string currency) => new(0m, NormalizeCurrency(currency));

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Round(Amount + other.Amount), Currency);
    }

    public Money Multiply(int quantity)
    {
        if (quantity < 0)
        {
            throw DomainException.Validation("A quantity cannot be negative.");
        }

        return new Money(Round(Amount * quantity), Currency);
    }

    /// <summary>
    /// Prorates an amount across a period. Used when a plan changes mid-period:
    /// the customer pays for what they actually had.
    /// </summary>
    public Money Prorate(int elapsedDays, int totalDays)
    {
        if (totalDays <= 0)
        {
            throw DomainException.Validation("A billing period must span at least one day.");
        }

        if (elapsedDays < 0 || elapsedDays > totalDays)
        {
            throw DomainException.Validation("The elapsed portion must fall inside the period.");
        }

        return new Money(Round(Amount * elapsedDays / totalDays), Currency);
    }

    public bool IsZero => Amount == 0m;

    private void EnsureSameCurrency(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw DomainException.Conflict($"Cannot combine amounts in '{Currency}' and '{other.Currency}'.");
        }
    }

    private static decimal Round(decimal amount) => Math.Round(amount, Scale, MidpointRounding.ToEven);

    private static string NormalizeCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            throw DomainException.Validation("A currency is required.");
        }

        var normalized = currency.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || !normalized.All(char.IsAsciiLetterUpper))
        {
            throw DomainException.Validation("Currency must be a three-letter ISO 4217 code.");
        }

        return normalized;
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Amount;
        yield return Currency;
    }

    public override string ToString() => $"{Amount.ToString($"F{Scale}")} {Currency}";
}
