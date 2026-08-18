using FeatureManagement.Domain;
using Microsoft.EntityFrameworkCore;

namespace Knight.Infrastructure.Persistence.Repositories;

internal sealed class FeatureStore : IFeatureStore
{
    private readonly PlatformDbContext _context;

    public FeatureStore(PlatformDbContext context)
    {
        _context = context;
    }

    public Task<FeatureDefinition?> GetDefinitionAsync(string featureKey, CancellationToken cancellationToken) =>
        _context.FeatureDefinitions.FirstOrDefaultAsync(f => f.Key == featureKey, cancellationToken);

    public async Task<IReadOnlyCollection<FeatureDefinition>> GetAllDefinitionsAsync(CancellationToken cancellationToken) =>
        await _context.FeatureDefinitions.AsNoTracking().ToArrayAsync(cancellationToken);

    public Task<TenantFeature?> GetTenantFeatureAsync(Guid tenantId, string featureKey, CancellationToken cancellationToken) =>
        _context.TenantFeatures.FirstOrDefaultAsync(f => f.TenantId == tenantId && f.FeatureKey == featureKey, cancellationToken);

    public async Task<IReadOnlyCollection<TenantFeature>> GetEnabledTenantFeaturesAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await _context.TenantFeatures
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.IsEnabled)
            .ToArrayAsync(cancellationToken);

    public async Task SetTenantFeatureAsync(Guid tenantId, string featureKey, bool isEnabled, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await _context.TenantFeatures
            .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.FeatureKey == featureKey, cancellationToken);

        if (existing is null)
        {
            var feature = TenantFeature.Create(Guid.NewGuid(), tenantId, featureKey, isEnabled, now);
            await _context.TenantFeatures.AddAsync(feature, cancellationToken);
        }
        else if (isEnabled)
        {
            existing.Enable(now);
        }
        else
        {
            existing.Disable(now);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }
}
