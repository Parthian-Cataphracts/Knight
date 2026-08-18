using FeatureManagement;
using FeatureManagement.Domain;
using NSubstitute;
using Xunit;

namespace Knight.UnitTests.FeatureManagement;

public sealed class FeatureAccessServiceTests
{
    [Fact]
    public async Task IsEnabledAsync_WhenNoTenantFeatureRecordExists_ReturnsFalse()
    {
        var store = Substitute.For<IFeatureStore>();
        store.GetTenantFeatureAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((TenantFeature?)null);

        var service = new FeatureAccessService(store);

        var result = await service.IsEnabledAsync(Guid.NewGuid(), "online-ordering", CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsEnabledAsync_WhenTenantFeatureIsEnabled_ReturnsTrue()
    {
        var tenantId = Guid.NewGuid();
        var feature = TenantFeature.Create(Guid.NewGuid(), tenantId, "online-ordering", isEnabled: true, DateTimeOffset.UtcNow);

        var store = Substitute.For<IFeatureStore>();
        store.GetTenantFeatureAsync(tenantId, "online-ordering", Arg.Any<CancellationToken>())
            .Returns(feature);

        var service = new FeatureAccessService(store);

        var result = await service.IsEnabledAsync(tenantId, "online-ordering", CancellationToken.None);

        Assert.True(result);
    }
}
