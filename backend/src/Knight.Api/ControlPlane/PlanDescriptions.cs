using Knight.Application.Abstractions.ControlPlane;
using Knight.Contracts.ControlPlane;
using Plans.Domain;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Turns plans into responses.
///
/// A plan lists its features by id, but every reader needs their names, so the
/// slugs are resolved once for the whole set being returned rather than once per
/// row. The included and optional groupings are derived here too, so every
/// client groups them the same way instead of each inventing its own rule.
/// </summary>
internal static class PlanDescriptions
{
    public static async Task<IReadOnlyCollection<PlanResponse>> DescribeAsync(
        IReadOnlyCollection<Plan> plans,
        IFeatureCatalogReader features,
        IPlanSubscriberReader subscribers,
        CancellationToken cancellationToken)
    {
        var counts = await subscribers.CountByPlanAsync(cancellationToken);
        var ids = plans
            .SelectMany(plan => plan.Features.Select(feature => feature.FeatureId))
            .Distinct()
            .ToArray();

        var descriptors = await features.GetManyAsync(ids, cancellationToken);

        return plans.Select(plan => Describe(plan, descriptors, counts.GetValueOrDefault(plan.Id))).ToArray();
    }

    public static async Task<PlanResponse> DescribeAsync(
        Plan plan,
        IFeatureCatalogReader features,
        IPlanSubscriberReader subscribers,
        CancellationToken cancellationToken) =>
        (await DescribeAsync([plan], features, subscribers, cancellationToken)).Single();

    private static PlanResponse Describe(Plan plan, IReadOnlyCollection<FeatureDescriptor> descriptors, int customerCount)
    {
        // A feature the catalogue no longer returns falls back to its id: the
        // plan row still exists and hiding it would be a worse lie than showing
        // an unresolved identifier.
        string SlugOf(Guid featureId) =>
            descriptors.SingleOrDefault(descriptor => descriptor.FeatureId == featureId)?.Slug ?? featureId.ToString();

        var entries = plan.Features
            .Select(feature => new PlanFeatureResponse
            {
                FeatureId = feature.FeatureId,
                FeatureSlug = SlugOf(feature.FeatureId),
                FeatureName = SlugOf(feature.FeatureId),
                IsIncluded = feature.IsIncluded,
                IsCustomerToggleable = feature.IsCustomerToggleable,
                PinnedVersionRange = feature.PinnedVersionRange,
            })
            .ToArray();

        return new PlanResponse
        {
            Id = plan.Id,
            Key = plan.Key,
            Name = plan.Name,
            Description = plan.Description,
            BasePrice = plan.BasePriceAmount,
            Currency = plan.Currency,
            IsActive = plan.IsActive,
            SortOrder = plan.SortOrder,
            Features = entries,
            IncludedFeatures = entries.Where(entry => entry.IsIncluded).Select(entry => entry.FeatureSlug).ToArray(),
            OptionalFeatures = entries
                .Where(entry => entry is { IsIncluded: false, IsCustomerToggleable: true })
                .Select(entry => entry.FeatureSlug)
                .ToArray(),
            CustomerCount = customerCount,
            CreatedAt = plan.CreatedAt,
            UpdatedAt = plan.UpdatedAt,
        };
    }
}
