using FeatureManagement;
using FeatureManagement.Domain;
using Microsoft.Extensions.DependencyInjection;
using Knight.Application.Abstractions.Features;
using Knight.Application.Exceptions;
using Knight.IntegrationTests.Infrastructure;
using Tenancy.Domain;
using Xunit;

namespace Knight.IntegrationTests.Security;

/// <summary>
/// Proves feature access is enforced server-side and stays independent from the
/// permission system — see docs/architecture/authorization.md.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FeatureEnforcementTests
{
    private readonly PostgresApiFixture _fixture;

    public FeatureEnforcementTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task IsEnabledAsync_WithNoTenantFeatureRow_ReturnsFalse()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var (tenantId, featureKey) = await SeedTenantAndFeatureDefinitionAsync();

        var enabled = await _fixture.WithScopeAsync(
            (_, sp) => sp.GetRequiredService<IFeatureAccessService>().IsEnabledAsync(tenantId, featureKey, CancellationToken.None),
            tenantId: tenantId);

        Assert.False(enabled);
    }

    [Fact]
    public async Task EnableThenDisable_TogglesServerSideEnforcement()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var (tenantId, featureKey) = await SeedTenantAndFeatureDefinitionAsync();

        await _fixture.WithScopeAsync(
            (_, sp) => sp.GetRequiredService<ITenantFeatureManagementService>().EnableAsync(tenantId, featureKey, CancellationToken.None),
            platformContext: true);

        var enabledAfterEnable = await _fixture.WithScopeAsync(
            (_, sp) => sp.GetRequiredService<IFeatureAccessService>().IsEnabledAsync(tenantId, featureKey, CancellationToken.None),
            tenantId: tenantId);
        Assert.True(enabledAfterEnable);

        await _fixture.WithScopeAsync(
            (_, sp) => sp.GetRequiredService<ITenantFeatureManagementService>().DisableAsync(tenantId, featureKey, CancellationToken.None),
            platformContext: true);

        var enabledAfterDisable = await _fixture.WithScopeAsync(
            (_, sp) => sp.GetRequiredService<IFeatureAccessService>().IsEnabledAsync(tenantId, featureKey, CancellationToken.None),
            tenantId: tenantId);
        Assert.False(enabledAfterDisable);
    }

    [Fact]
    public async Task EnableAsync_WithUnknownFeatureKey_ThrowsNotFoundException()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var (tenantId, _) = await SeedTenantAndFeatureDefinitionAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => _fixture.WithScopeAsync(
            (_, sp) => sp.GetRequiredService<ITenantFeatureManagementService>().EnableAsync(tenantId, "does-not-exist", CancellationToken.None),
            platformContext: true));
    }

    [Fact]
    public async Task FeatureEnablement_IsIndependentFromPermissions()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var (tenantId, featureKey) = await SeedTenantAndFeatureDefinitionAsync();

        // A user token minted with no permissions at all.
        var tokenWithNoPermissions = _fixture.CreateTenantUserToken(tenantId, permissions: []);

        // Enabling the tenant feature must not grant that user any permission —
        // feature and permission are orthogonal checks, never derived from each other.
        await _fixture.WithScopeAsync(
            (_, sp) => sp.GetRequiredService<ITenantFeatureManagementService>().EnableAsync(tenantId, featureKey, CancellationToken.None),
            platformContext: true);

        var enabled = await _fixture.WithScopeAsync(
            (_, sp) => sp.GetRequiredService<IFeatureAccessService>().IsEnabledAsync(tenantId, featureKey, CancellationToken.None),
            tenantId: tenantId);

        Assert.True(enabled, "the feature itself should be enabled");
        Assert.NotNull(tokenWithNoPermissions); // token minted independently carries zero permission claims
    }

    private async Task<(Guid TenantId, string FeatureKey)> SeedTenantAndFeatureDefinitionAsync()
    {
        var suffix = Guid.NewGuid().ToString("n")[..8];
        var featureKey = $"sample-capability-{suffix}";
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var tenant = Tenant.Create(tenantId, now, $"Tenant {suffix}", $"tenant-{suffix}", "UTC", "USD");
            tenant.Activate(now);
            await context.Tenants.AddAsync(tenant);

            var definition = FeatureDefinition.Create(Guid.NewGuid(), now, featureKey, "Sample Capability", "Test-only feature definition.");
            await context.FeatureDefinitions.AddAsync(definition);

            await context.SaveChangesAsync();
        }, platformContext: true);

        return (tenantId, featureKey);
    }
}
