using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;

namespace FeatureRegistry;

/// <summary>
/// Feature catalogue administration.
///
/// Withdrawing a feature is audited like everything else, but it is worth naming
/// what it means: existing entitlements stop being honoured, so the delivery
/// engine will disable the capability wherever it is installed. Deprecating is
/// the gentler step — no new entitlements, existing ones untouched.
/// </summary>
internal sealed class FeatureCatalogService : IFeatureCatalogService
{
    private const int MaxPageSize = 100;

    private readonly IFeatureRepository _features;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;

    public FeatureCatalogService(IFeatureRepository features, IAuditTrail audit, IDateTimeProvider clock)
    {
        _features = features;
        _audit = audit;
        _clock = clock;
    }

    public async Task<FeaturePage> ListAsync(FeatureListQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > MaxPageSize ? 25 : query.PageSize;

        var (items, total) = await _features.ListAsync(page, pageSize, query.Status, query.Category, query.Search, cancellationToken);
        return new FeaturePage(items, page, pageSize, total);
    }

    public Task<Feature?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _features.GetByIdAsync(id, cancellationToken);

    public async Task<Feature> CreateAsync(CreateFeatureInput input, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var slug = FeatureSlug.Normalize(input.Slug);

        if (await _features.GetBySlugAsync(slug, cancellationToken) is not null)
        {
            throw new ConflictException($"A feature with slug '{slug}' already exists.");
        }

        var feature = Feature.Create(
            Guid.NewGuid(),
            now,
            slug,
            input.Name,
            input.Category,
            input.IsOptional,
            input.RequiresDedicatedInfrastructure);

        feature.UpdateMetadata(input.Name, input.Description, input.Category, now);

        await _features.AddAsync(feature, cancellationToken);
        await _features.SaveChangesAsync(cancellationToken);

        await AuditAsync("feature.created", feature, cancellationToken);
        return feature;
    }

    public async Task<Feature> UpdateAsync(Guid id, UpdateFeatureInput input, CancellationToken cancellationToken)
    {
        var feature = await RequireAsync(id, cancellationToken);
        var before = Snapshot(feature);

        feature.UpdateMetadata(input.Name, input.Description, input.Category, _clock.UtcNow);
        await _features.SaveChangesAsync(cancellationToken);

        await AuditAsync("feature.updated", feature, cancellationToken, before);
        return feature;
    }

    public Task<Feature> PublishAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, "feature.published", (feature, now) => feature.Publish(now), cancellationToken);

    public Task<Feature> DeprecateAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, "feature.deprecated", (feature, now) => feature.Deprecate(now), cancellationToken);

    public Task<Feature> WithdrawAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, "feature.withdrawn", (feature, now) => feature.Withdraw(now), cancellationToken);

    private async Task<Feature> TransitionAsync(
        Guid id,
        string action,
        Action<Feature, DateTimeOffset> transition,
        CancellationToken cancellationToken)
    {
        var feature = await RequireAsync(id, cancellationToken);
        var before = Snapshot(feature);

        transition(feature, _clock.UtcNow);
        await _features.SaveChangesAsync(cancellationToken);

        await AuditAsync(action, feature, cancellationToken, before);
        return feature;
    }

    private async Task<Feature> RequireAsync(Guid id, CancellationToken cancellationToken) =>
        await _features.GetByIdAsync(id, cancellationToken)
        ?? throw new NotFoundException($"Feature '{id}' was not found.");

    private Task AuditAsync(string action, Feature feature, CancellationToken cancellationToken, object? before = null) =>
        _audit.RecordAsync(
            action,
            nameof(Feature),
            feature.Id.ToString(),

            // The catalogue is platform-wide; an entry about it belongs to no
            // single customer.
            customerId: null,
            cancellationToken,
            before,
            Snapshot(feature));

    private static object Snapshot(Feature feature) => new
    {
        feature.Slug,
        feature.Name,
        feature.Description,
        feature.Category,
        feature.IsOptional,
        feature.RequiresDedicatedInfrastructure,
        Status = feature.Status.ToString(),
    };
}
