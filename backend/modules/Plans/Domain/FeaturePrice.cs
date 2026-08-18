using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Plans.Domain;

/// <summary>
/// What a feature costs, optionally only within one plan.
///
/// Prices are time-boxed rather than overwritten: an invoice issued last month
/// must still be explicable from the prices that were in force last month, so
/// changing a price closes the old row and opens a new one
/// (docs/domain-model.md section 4).
/// </summary>
public sealed class FeaturePrice : Entity
{
    public Guid FeatureId { get; private set; }

    /// <summary>Null means the price applies on every plan that does not override it.</summary>
    public Guid? PlanId { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; }

    public BillingPeriod BillingPeriod { get; private set; }

    public DateTimeOffset ValidFrom { get; private set; }

    public DateTimeOffset? ValidTo { get; private set; }

    public Money Price => Money.Of(Amount, Currency);

    private FeaturePrice()
    {
        Currency = string.Empty;
    }

    private FeaturePrice(
        Guid id,
        Guid featureId,
        Guid? planId,
        Money price,
        BillingPeriod billingPeriod,
        DateTimeOffset validFrom,
        DateTimeOffset? validTo)
        : base(id)
    {
        FeatureId = featureId;
        PlanId = planId;
        Amount = price.Amount;
        Currency = price.Currency;
        BillingPeriod = billingPeriod;
        ValidFrom = validFrom;
        ValidTo = validTo;
    }

    public static FeaturePrice Create(
        Guid id,
        Guid featureId,
        Guid? planId,
        Money price,
        BillingPeriod billingPeriod,
        DateTimeOffset validFrom,
        DateTimeOffset? validTo = null)
    {
        if (featureId == Guid.Empty)
        {
            throw DomainException.Validation("A price must name a feature.");
        }

        if (validTo is not null && validTo <= validFrom)
        {
            throw DomainException.Validation("A price cannot stop applying before it starts.");
        }

        return new FeaturePrice(id, featureId, planId, price, billingPeriod, validFrom, validTo);
    }

    /// <summary>Closes this price so a replacement can take over from the same moment.</summary>
    public void Close(DateTimeOffset validTo)
    {
        if (validTo <= ValidFrom)
        {
            throw DomainException.Validation("A price cannot stop applying before it starts.");
        }

        if (ValidTo is not null)
        {
            throw DomainException.Conflict("The price is already closed.");
        }

        ValidTo = validTo;
    }

    public bool AppliesAt(DateTimeOffset moment) => ValidFrom <= moment && (ValidTo is null || ValidTo > moment);

    /// <summary>
    /// A price scoped to one plan beats the general one. Used to rank candidates
    /// when both exist for the same feature.
    /// </summary>
    public int Specificity => PlanId is null ? 0 : 1;
}

public enum BillingPeriod
{
    Monthly = 0,
    Yearly = 1,
    OneTime = 2,
}
