namespace PlatformBilling;

public sealed record PublicFeatureSummary(Guid FeatureId, string Slug, string Name, string? Description);

public sealed record PublicOptionalFeature(Guid FeatureId, string Slug, string Name, string? Description, decimal? Price, string Currency);

public sealed record PublicPlan(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    decimal BasePrice,
    string Currency,
    IReadOnlyCollection<PublicFeatureSummary> IncludedFeatures,
    IReadOnlyCollection<PublicOptionalFeature> OptionalFeatures);

/// <summary>
/// The public price list: the plans an anonymous visitor may buy, with what each
/// includes and what its optional add-ons cost (docs/self-service-saas-plan.md §6).
/// A plan that is not <see cref="Plans.Domain.Plan.IsPubliclyPurchasable"/> never
/// appears here, however active it is for the customers already on it.
/// </summary>
public interface IPublicPlanCatalog
{
    Task<IReadOnlyCollection<PublicPlan>> ListAsync(CancellationToken cancellationToken);
}
