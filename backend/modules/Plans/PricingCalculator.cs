using Knight.Domain.Common;
using Knight.Domain.Exceptions;
using Plans.Domain;

namespace Plans;

public sealed record PriceLine(string Description, Guid? FeatureId, int Quantity, Money UnitPrice, Money Total);

public sealed record PriceQuote(Money Subtotal, IReadOnlyCollection<PriceLine> Lines)
{
    public string Currency => Subtotal.Currency;
}

public sealed record QuoteRequest(Plan Plan, IReadOnlyCollection<Guid> SelectedFeatureIds, DateTimeOffset Moment);

/// <summary>
/// The single place a price is computed. Every total the platform quotes,
/// previews or invoices comes from here, derived from the plan's base price and
/// the feature prices in force at the moment being priced — never from a
/// constant in a service or a component (docs/domain-model.md section 4).
/// </summary>
public interface IPricingCalculator
{
    /// <summary>
    /// Prices a plan plus the features chosen on top of it. Features the plan
    /// already includes cost nothing extra; a selected feature with no price in
    /// force is refused rather than quoted as free, because free is a decision
    /// someone has to have made.
    /// </summary>
    Task<PriceQuote> QuoteAsync(QuoteRequest request, CancellationToken cancellationToken);
}

internal sealed class PricingCalculator : IPricingCalculator
{
    private readonly IFeaturePriceRepository _prices;

    public PricingCalculator(IFeaturePriceRepository prices)
    {
        _prices = prices;
    }

    public async Task<PriceQuote> QuoteAsync(QuoteRequest request, CancellationToken cancellationToken)
    {
        var plan = request.Plan;
        var lines = new List<PriceLine> { new($"{plan.Name} plan", null, 1, plan.BasePrice, plan.BasePrice) };

        // Anything the plan already includes is paid for by the base price.
        var billable = request.SelectedFeatureIds
            .Distinct()
            .Where(featureId => plan.Find(featureId)?.IsIncluded != true)
            .ToArray();

        if (billable.Length > 0)
        {
            var applicable = await _prices.GetApplicableAsync(billable, plan.Id, request.Moment, cancellationToken);

            foreach (var featureId in billable)
            {
                // A price scoped to this plan wins over the general one; two
                // prices at the same specificity would be a data error, so the
                // most recently opened one is taken and the ambiguity is not
                // resolved silently by ordering luck.
                var price = applicable
                    .Where(candidate => candidate.FeatureId == featureId)
                    .OrderByDescending(candidate => candidate.Specificity)
                    .ThenByDescending(candidate => candidate.ValidFrom)
                    .FirstOrDefault();

                if (price is null)
                {
                    throw DomainException.Conflict(
                        $"Feature '{featureId}' has no price in force on plan '{plan.Key}'; it cannot be quoted.");
                }

                var amount = price.Price;
                lines.Add(new PriceLine($"Feature {featureId}", featureId, 1, amount, amount));
            }
        }

        var subtotal = lines.Aggregate(Money.Zero(plan.Currency), (running, line) => running.Add(line.Total));

        return new PriceQuote(subtotal, lines);
    }
}
