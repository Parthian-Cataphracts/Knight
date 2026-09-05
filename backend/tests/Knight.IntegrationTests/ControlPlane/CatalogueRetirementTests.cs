using FeatureRegistry.Domain;
using Knight.Infrastructure.ControlPlane.Seed;
using Knight.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// Phase 28's orphan-identity criterion: an identity a past catalogue seeded and
/// later superseded is retired the next time the catalogue is seeded, rather than
/// left for an operator to remember an API call
/// (docs/phase-28-verification.md §6). A withdrawal is a status change, never a
/// delete, and a no-op on a deployment that never had the identity.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CatalogueRetirementTests
{
    private readonly PostgresApiFixture _fixture;

    public CatalogueRetirementTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReseedingWithdrawsAnOrphanIdentityThatWasOncePublished()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        // `analytics` is a declared orphan (superseded by analytics-core /
        // analytics-reports). A fresh deployment never seeds it, so stand one up
        // the way an old deployment would have: published, and sellable.
        const string slug = "analytics";
        await _fixture.WithControlPlaneScopeAsync(async (context, _) =>
        {
            var existing = await context.Features.FirstOrDefaultAsync(f => f.Slug == slug);
            if (existing is null)
            {
                var orphan = Feature.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, slug, "Analytics (orphan)", "Insight");
                orphan.Publish(DateTimeOffset.UtcNow);
                context.Features.Add(orphan);
                await context.SaveChangesAsync();
            }
        });

        // Re-run the seeder — additive and idempotent — which now retires the
        // declared orphans.
        await _fixture.WithControlPlaneScopeAsync((_, sp) =>
            sp.GetRequiredService<ICommercialCatalogueSeeder>().SeedAsync(CancellationToken.None));

        var status = await _fixture.WithControlPlaneScopeAsync((context, _) =>
            context.Features.Where(f => f.Slug == slug).Select(f => f.Status).FirstAsync());

        Assert.Equal(FeatureStatus.Withdrawn, status);
    }
}
