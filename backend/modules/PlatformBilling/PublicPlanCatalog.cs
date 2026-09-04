using FeatureRegistry.Domain;
using Knight.Application.Abstractions.Time;
using Plans.Domain;

namespace PlatformBilling;

/// <summary>
/// Projects the plan aggregates into the public price list. Prices for optional
/// features come from the same <see cref="IFeaturePriceRepository"/> the checkout
/// prices against, at the current moment, so the figure a visitor sees on the
/// catalogue is the figure they are charged.
/// </summary>
internal sealed class PublicPlanCatalog : IPublicPlanCatalog
{
    private readonly IPlanRepository _plans;
    private readonly IFeatureRepository _features;
    private readonly IFeaturePriceRepository _prices;
    private readonly IDateTimeProvider _clock;

    public PublicPlanCatalog(
        IPlanRepository plans,
        IFeatureRepository features,
        IFeaturePriceRepository prices,
        IDateTimeProvider clock)
    {
        _plans = plans;
        _features = features;
        _prices = prices;
        _clock = clock;
    }

    public async Task<IReadOnlyCollection<PublicPlan>> ListAsync(CancellationToken cancellationToken)
    {
        var plans = await _plans.ListAsync(includeInactive: false, cancellationToken);
        var purchasable = plans.Where(plan => plan.IsPubliclyPurchasable).OrderBy(plan => plan.SortOrder).ToArray();

        if (purchasable.Length == 0)
        {
            return [];
        }

        var now = _clock.UtcNow;
        var result = new List<PublicPlan>(purchasable.Length);

        foreach (var plan in purchasable)
        {
            var included = new List<PublicFeatureSummary>();
            foreach (var featureId in plan.IncludedFeatureIds)
            {
                if (await _features.GetByIdAsync(featureId, cancellationToken) is { } feature)
                {
                    included.Add(new PublicFeatureSummary(feature.Id, feature.Slug, feature.Name, feature.Description));
                }
            }

            var selectableIds = plan.SelectableFeatureIds;
            var optional = new List<PublicOptionalFeature>();
            var applicable = selectableIds.Count > 0
                ? await _prices.GetApplicableAsync(selectableIds, plan.Id, now, cancellationToken)
                : [];

            foreach (var featureId in selectableIds)
            {
                var feature = await _features.GetByIdAsync(featureId, cancellationToken);
                if (feature is null || !feature.CanBeEntitled)
                {
                    continue;
                }

                var price = applicable
                    .Where(candidate => candidate.FeatureId == featureId)
                    .OrderByDescending(candidate => candidate.Specificity)
                    .ThenByDescending(candidate => candidate.ValidFrom)
                    .FirstOrDefault();

                optional.Add(new PublicOptionalFeature(
                    feature.Id,
                    feature.Slug,
                    feature.Name,
                    feature.Description,
                    price?.Price.Amount,
                    price?.Price.Currency ?? plan.Currency));
            }

            result.Add(new PublicPlan(
                plan.Id,
                plan.Key,
                plan.Name,
                plan.Description,
                plan.BasePrice.Amount,
                plan.Currency,
                included,
                optional));
        }

        return result;
    }
}
