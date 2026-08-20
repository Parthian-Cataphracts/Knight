using Knight.Application.Abstractions.ControlPlane;
using Knight.Infrastructure.Caching;
using Knight.Infrastructure.ControlPlane.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The entitlement cache, which is the one cache in KNIGHT where being wrong has
/// a commercial consequence: a stale entry lets a store keep using a capability
/// its customer has stopped paying for.
///
/// So these tests are less about the cache working and more about the two things
/// that must hold when it does not: a revocation evicts immediately, and a cache
/// that is unavailable does not stop entitlements resolving at all.
/// </summary>
public sealed class EntitlementCacheTests
{
    private static readonly Guid Customer = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static EntitledFeature Feature(string slug) =>
        new(Guid.NewGuid(), slug, slug, "plan", DateTimeOffset.UnixEpoch, null);

    /// <summary>
    /// An in-memory stand-in for the distributed cache. Simpler to reason about
    /// here than a substitute with argument matchers, because these tests care
    /// about what is *in* the cache across calls.
    /// </summary>
    private sealed class FakeCache : ICacheService
    {
        private readonly Dictionary<string, object?> _entries = new();

        public int Reads { get; private set; }

        public int Writes { get; private set; }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(_entries.TryGetValue(key, out var value) ? (T?)value : default);
        }

        public Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken)
        {
            Writes++;
            _entries[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken)
        {
            _entries.Remove(key);
            return Task.CompletedTask;
        }

        public bool Holds(string key) => _entries.ContainsKey(key);
    }

    private static CachingCustomerEntitlementReader Reader(ICustomerEntitlementReader inner, ICacheService cache) =>
        new(inner, cache, NullLogger<CachingCustomerEntitlementReader>.Instance);

    [Fact]
    public async Task TheSecondReadDoesNotReachTheDatabase()
    {
        var inner = Substitute.For<ICustomerEntitlementReader>();
        inner.ListActiveAsync(Customer, Arg.Any<CancellationToken>())
            .Returns([Feature("log-shipping")]);

        var reader = Reader(inner, new FakeCache());

        await reader.ListActiveAsync(Customer, CancellationToken.None);
        await reader.ListActiveAsync(Customer, CancellationToken.None);

        await inner.Received(1).ListActiveAsync(Customer, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneCustomersEntitlementsAreNeverServedToAnother()
    {
        var other = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var inner = Substitute.For<ICustomerEntitlementReader>();
        inner.ListActiveAsync(Customer, Arg.Any<CancellationToken>()).Returns([Feature("log-shipping")]);
        inner.ListActiveAsync(other, Arg.Any<CancellationToken>()).Returns([Feature("analytics")]);

        var reader = Reader(inner, new FakeCache());

        var first = await reader.ListActiveAsync(Customer, CancellationToken.None);
        var second = await reader.ListActiveAsync(other, CancellationToken.None);

        Assert.Equal("log-shipping", Assert.Single(first).Slug);
        Assert.Equal("analytics", Assert.Single(second).Slug);
    }

    [Fact]
    public async Task ARevocationEvictsTheCachedSetImmediately()
    {
        var cache = new FakeCache();
        var inner = Substitute.For<ICustomerEntitlementReader>();
        inner.ListActiveAsync(Customer, Arg.Any<CancellationToken>()).Returns([Feature("log-shipping")]);

        var reader = Reader(inner, cache);
        await reader.ListActiveAsync(Customer, CancellationToken.None);

        Assert.True(cache.Holds(EntitlementCacheKeys.ActiveFor(Customer)));

        var invalidator = new EntitlementCacheInvalidator(cache, NullLogger<EntitlementCacheInvalidator>.Instance);
        await invalidator.PublishAsync(
            new FeatureEntitlementRevoked(Customer, Guid.NewGuid(), "subscription cancelled", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.False(cache.Holds(EntitlementCacheKeys.ActiveFor(Customer)));

        // And the next read goes back to the database rather than to a set that
        // no longer describes what this customer is owed.
        await reader.ListActiveAsync(Customer, CancellationToken.None);
        await inner.Received(2).ListActiveAsync(Customer, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AGrantEvictsTooSoTheNewCapabilityIsVisibleAtOnce()
    {
        var cache = new FakeCache();
        var inner = Substitute.For<ICustomerEntitlementReader>();
        inner.ListActiveAsync(Customer, Arg.Any<CancellationToken>()).Returns([]);

        var reader = Reader(inner, cache);
        await reader.ListActiveAsync(Customer, CancellationToken.None);

        var invalidator = new EntitlementCacheInvalidator(cache, NullLogger<EntitlementCacheInvalidator>.Instance);
        await invalidator.PublishAsync(
            new FeatureEntitlementGranted(Customer, Guid.NewGuid(), "plan", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.False(cache.Holds(EntitlementCacheKeys.ActiveFor(Customer)));
    }

    [Fact]
    public async Task AnUnavailableCacheStillResolvesEntitlements()
    {
        // A cache outage must not become an entitlement outage: every store in
        // the system asks this question on a timer, and the honest answer is
        // still in the database.
        var cache = Substitute.For<ICacheService>();
        cache.GetAsync<EntitledFeature[]>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("cache is down"));
        cache.SetAsync(Arg.Any<string>(), Arg.Any<EntitledFeature[]>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("cache is down"));

        var inner = Substitute.For<ICustomerEntitlementReader>();
        inner.ListActiveAsync(Customer, Arg.Any<CancellationToken>()).Returns([Feature("log-shipping")]);

        var held = await Reader(inner, cache).ListActiveAsync(Customer, CancellationToken.None);

        Assert.Equal("log-shipping", Assert.Single(held).Slug);
    }

    [Fact]
    public async Task AFailedEvictionDoesNotFailTheEntitlementChange()
    {
        // The grant has already been committed by the time this runs. Throwing
        // here would report a successful commercial change as failed.
        var cache = Substitute.For<ICacheService>();
        cache.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("cache is down"));

        var invalidator = new EntitlementCacheInvalidator(cache, NullLogger<EntitlementCacheInvalidator>.Instance);

        await invalidator.PublishAsync(
            new FeatureEntitlementGranted(Customer, Guid.NewGuid(), "plan", DateTimeOffset.UtcNow),
            CancellationToken.None);
    }

    [Fact]
    public async Task IsEntitledIsAnsweredFromTheSameCachedSet()
    {
        var inner = Substitute.For<ICustomerEntitlementReader>();
        inner.ListActiveAsync(Customer, Arg.Any<CancellationToken>()).Returns([Feature("log-shipping")]);

        var reader = Reader(inner, new FakeCache());

        Assert.True(await reader.IsEntitledAsync(Customer, "log-shipping", CancellationToken.None));
        Assert.True(await reader.IsEntitledAsync(Customer, "LOG-SHIPPING", CancellationToken.None));
        Assert.False(await reader.IsEntitledAsync(Customer, "analytics", CancellationToken.None));

        // One database read for three questions, and no second cache entry to
        // keep in step with the first.
        await inner.Received(1).ListActiveAsync(Customer, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheCacheIsEvictedBeforeDeliveryActsOnTheChange()
    {
        // Ordering matters: delivery reacts to a grant by queueing work, and that
        // work must not read the entitlement set the grant just replaced.
        var cache = new FakeCache();
        cache.SetAsync(EntitlementCacheKeys.ActiveFor(Customer), Array.Empty<EntitledFeature>(), TimeSpan.FromMinutes(1), CancellationToken.None).Wait();

        var observed = new List<string>();

        var delivery = Substitute.For<IEntitlementEventPublisher>();
        delivery.PublishAsync(Arg.Any<FeatureEntitlementGranted>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                observed.Add(cache.Holds(EntitlementCacheKeys.ActiveFor(Customer)) ? "stale" : "evicted");
                return Task.CompletedTask;
            });

        var composite = new CompositeEntitlementEventPublisher(
            new EntitlementCacheInvalidator(cache, NullLogger<EntitlementCacheInvalidator>.Instance),
            delivery);

        await composite.PublishAsync(
            new FeatureEntitlementGranted(Customer, Guid.NewGuid(), "plan", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal("evicted", Assert.Single(observed));
    }
}
