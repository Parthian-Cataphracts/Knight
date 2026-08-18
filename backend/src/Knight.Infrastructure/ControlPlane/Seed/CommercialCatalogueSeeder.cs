using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FeatureRegistry.Domain;
using Knight.Application.Abstractions.Time;
using Knight.Domain.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Plans.Domain;

namespace Knight.Infrastructure.ControlPlane.Seed;

/// <summary>
/// Loads the commercial catalogue — the features that may be sold, the plans, and
/// their prices — from data rather than from code
/// (docs/domain-model.md section 4).
///
/// It lives in Infrastructure because seeding touches both the feature catalogue
/// and the plan catalogue, and a module may not reach into a sibling to do it.
///
/// Seeding is additive and idempotent: it creates what is missing and updates
/// what has drifted, and it never deletes. An operator who removed a feature from
/// a plan through the dashboard made a deliberate decision, and a redeploy is not
/// the moment to overrule it — so plan entries already present are updated in
/// place, and entries no longer in the file are left alone.
/// </summary>
public interface ICommercialCatalogueSeeder
{
    Task SeedAsync(CancellationToken cancellationToken);
}

internal sealed class CommercialCatalogueSeeder : ICommercialCatalogueSeeder
{
    private const string EmbeddedResourceName = "Knight.Infrastructure.ControlPlane.Seed.commercial-catalogue.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IFeatureRepository _features;
    private readonly IPlanRepository _plans;
    private readonly IFeaturePriceRepository _prices;
    private readonly IDateTimeProvider _clock;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CommercialCatalogueSeeder> _logger;

    public CommercialCatalogueSeeder(
        IFeatureRepository features,
        IPlanRepository plans,
        IFeaturePriceRepository prices,
        IDateTimeProvider clock,
        IConfiguration configuration,
        ILogger<CommercialCatalogueSeeder> logger)
    {
        _features = features;
        _plans = plans;
        _prices = prices;
        _clock = clock;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var catalogue = await LoadAsync(cancellationToken);
        if (catalogue is null)
        {
            return;
        }

        var now = _clock.UtcNow;
        var featureIds = await SeedFeaturesAsync(catalogue, now, cancellationToken);
        await SeedPlansAsync(catalogue, featureIds, now, cancellationToken);

        _logger.LogInformation(
            "Seeded the commercial catalogue: {FeatureCount} features, {PlanCount} plans.",
            catalogue.Features.Count,
            catalogue.Plans.Count);
    }

    private async Task<Dictionary<string, Guid>> SeedFeaturesAsync(
        CatalogueDocument catalogue,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);

        foreach (var definition in catalogue.Features)
        {
            var slug = FeatureSlug.Normalize(definition.Slug);
            var feature = await _features.GetBySlugAsync(slug, cancellationToken);

            if (feature is null)
            {
                feature = Feature.Create(
                    Guid.NewGuid(),
                    now,
                    slug,
                    definition.Name,
                    definition.Category,
                    definition.IsOptional,
                    definition.RequiresDedicatedInfrastructure);

                feature.UpdateMetadata(definition.Name, definition.Description, definition.Category, now);

                if (definition.Publish)
                {
                    feature.Publish(now);
                }

                await _features.AddAsync(feature, cancellationToken);
            }
            else if (feature.Status is not FeatureStatus.Withdrawn)
            {
                // Metadata is safe to refresh; whether the capability needs
                // dedicated infrastructure is not, and the aggregate refuses to
                // change it after publication for exactly that reason.
                feature.UpdateMetadata(definition.Name, definition.Description, definition.Category, now);
            }

            ids[slug] = feature.Id;
        }

        await _features.SaveChangesAsync(cancellationToken);

        foreach (var definition in catalogue.Features.Where(feature => feature.Prices.Count > 0))
        {
            var featureId = ids[FeatureSlug.Normalize(definition.Slug)];
            var existing = await _prices.ListForFeatureAsync(featureId, cancellationToken);

            foreach (var price in definition.Prices)
            {
                // A price already in force is left exactly as it is: rewriting it
                // would change what past periods are explained by.
                if (existing.Any(candidate => candidate.PlanId is null && candidate.AppliesAt(now)))
                {
                    continue;
                }

                await _prices.AddAsync(
                    FeaturePrice.Create(
                        Guid.NewGuid(),
                        featureId,
                        planId: null,
                        Money.Of(price.Amount, catalogue.Currency),
                        Enum.Parse<BillingPeriod>(price.BillingPeriod, ignoreCase: true),
                        now),
                    cancellationToken);
            }
        }

        await _prices.SaveChangesAsync(cancellationToken);
        return ids;
    }

    private async Task SeedPlansAsync(
        CatalogueDocument catalogue,
        IReadOnlyDictionary<string, Guid> featureIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var definition in catalogue.Plans)
        {
            var key = definition.Key.Trim().ToLowerInvariant();
            var plan = await _plans.GetByKeyAsync(key, cancellationToken);

            if (plan is null)
            {
                plan = Plan.Create(
                    Guid.NewGuid(),
                    now,
                    key,
                    definition.Name,
                    Money.Of(definition.BasePrice, catalogue.Currency),
                    definition.SortOrder);

                plan.UpdateMetadata(definition.Name, definition.Description, definition.SortOrder, now);
                await _plans.AddAsync(plan, cancellationToken);
            }

            foreach (var entry in definition.Features)
            {
                var slug = FeatureSlug.Normalize(entry.Slug);
                if (!featureIds.TryGetValue(slug, out var featureId))
                {
                    _logger.LogWarning("Plan '{PlanKey}' references unknown feature '{Slug}'; skipping.", key, slug);
                    continue;
                }

                var existed = plan.Find(featureId) is not null;
                var planFeature = plan.SetFeature(
                    featureId,
                    entry.IsIncluded,
                    entry.IsCustomerToggleable,
                    entry.PinnedVersionRange,
                    now);

                if (!existed)
                {
                    _plans.RegisterNewFeature(planFeature);
                }
            }
        }

        await _plans.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the catalogue from the configured path, falling back to the copy
    /// shipped inside the assembly so a deployment is never left without one.
    /// </summary>
    private async Task<CatalogueDocument?> LoadAsync(CancellationToken cancellationToken)
    {
        var path = _configuration["Catalogue:SeedPath"];

        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"Catalogue:SeedPath points at '{path}', which does not exist.");
            }

            await using var file = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<CatalogueDocument>(file, SerializerOptions, cancellationToken);
        }

        await using var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"The embedded catalogue '{EmbeddedResourceName}' is missing from the assembly.");

        return await JsonSerializer.DeserializeAsync<CatalogueDocument>(embedded, SerializerOptions, cancellationToken);
    }

    private sealed record CatalogueDocument
    {
        public string Currency { get; init; } = "EUR";

        public IReadOnlyCollection<FeatureDefinition> Features { get; init; } = [];

        public IReadOnlyCollection<PlanDefinition> Plans { get; init; } = [];
    }

    private sealed record FeatureDefinition
    {
        public string Slug { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string? Description { get; init; }

        public string Category { get; init; } = "General";

        public bool IsOptional { get; init; } = true;

        public bool RequiresDedicatedInfrastructure { get; init; }

        public bool Publish { get; init; }

        public IReadOnlyCollection<PriceDefinition> Prices { get; init; } = [];
    }

    private sealed record PriceDefinition
    {
        public decimal Amount { get; init; }

        public string BillingPeriod { get; init; } = "Monthly";
    }

    private sealed record PlanDefinition
    {
        public string Key { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string? Description { get; init; }

        public decimal BasePrice { get; init; }

        public int SortOrder { get; init; }

        public IReadOnlyCollection<PlanFeatureDefinition> Features { get; init; } = [];
    }

    private sealed record PlanFeatureDefinition
    {
        public string Slug { get; init; } = string.Empty;

        public bool IsIncluded { get; init; }

        public bool IsCustomerToggleable { get; init; }

        public string? PinnedVersionRange { get; init; }
    }
}
