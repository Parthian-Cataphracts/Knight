using Knight.Application.Abstractions.ControlPlane;
using Knight.Infrastructure.Caching;
using Microsoft.Extensions.Logging;

namespace Knight.Infrastructure.ControlPlane.Caching;

/// <summary>
/// Cache keys for a customer's entitlement set, in one place so the reader and
/// the invalidator cannot disagree about them.
///
/// The key is scoped by customer, which <see cref="ICacheService"/> requires of
/// anything tenant-owned: a key that omitted the customer id would serve one
/// customer's entitlements to another, and entitlements are exactly the fact
/// that decides what a store may run.
/// </summary>
internal static class EntitlementCacheKeys
{
    public static string ActiveFor(Guid customerId) => $"customer:{customerId}:entitlements:active";
}

/// <summary>
/// Caches the active entitlement set for a customer.
///
/// Worth caching because of who asks and how often: every store polls
/// <c>/api/v1/ingest/features</c> on a timer, all of a customer's stores get the
/// same answer, and the underlying query joins entitlements to features. The set
/// itself changes rarely — a subscription change, a plan change, an expiry.
///
/// Two things keep it honest:
///
/// - The TTL is short. Entitlement is the decision that says whether a customer
///   may use a capability they may no longer be paying for, so a stale answer has
///   a commercial cost and the window for one is kept small.
/// - A grant or revocation drops the entry immediately
///   (<see cref="EntitlementCacheInvalidator"/>), so the TTL is the bound on how
///   wrong things can get if that path is missed, not the normal latency of a
///   change.
///
/// <see cref="ICustomerEntitlementReader.IsEntitledAsync"/> is answered from the
/// same cached set rather than with its own entry. It is a question about one
/// slug within the set this class already holds, and a second entry would be a
/// second thing to invalidate.
/// </summary>
internal sealed class CachingCustomerEntitlementReader : ICustomerEntitlementReader
{
    /// <summary>
    /// Deliberately short. See the class summary: this is the ceiling on how long
    /// a revoked entitlement could still be served if invalidation is missed.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly ICustomerEntitlementReader _inner;
    private readonly ICacheService _cache;
    private readonly ILogger<CachingCustomerEntitlementReader> _logger;

    public CachingCustomerEntitlementReader(
        ICustomerEntitlementReader inner,
        ICacheService cache,
        ILogger<CachingCustomerEntitlementReader> logger)
    {
        _inner = inner;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<EntitledFeature>> ListActiveAsync(
        Guid customerId,
        CancellationToken cancellationToken)
    {
        var key = EntitlementCacheKeys.ActiveFor(customerId);

        try
        {
            var cached = await _cache.GetAsync<EntitledFeature[]>(key, cancellationToken);
            if (cached is not null)
            {
                return cached;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A cache that is down must not take entitlement resolution with it.
            // Falling through to the database is slower and correct; failing here
            // would stop every store in the system from learning what it may run.
            _logger.LogWarning(exception, "Could not read entitlements from the cache; falling back to the database.");
        }

        var fresh = await _inner.ListActiveAsync(customerId, cancellationToken);

        try
        {
            await _cache.SetAsync(key, fresh.ToArray(), Ttl, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not cache the entitlement set for customer {CustomerId}.", customerId);
        }

        return fresh;
    }

    public async Task<bool> IsEntitledAsync(Guid customerId, string featureSlug, CancellationToken cancellationToken)
    {
        var held = await ListActiveAsync(customerId, cancellationToken);

        return held.Any(feature => string.Equals(feature.Slug, featureSlug, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Drops a customer's cached entitlement set the moment one of their
/// entitlements changes.
///
/// Implemented as an <see cref="IEntitlementEventPublisher"/> so it sits on the
/// path every grant and revocation already takes, rather than as a call the
/// entitlement service has to remember to make. The publisher that queues
/// delivery work runs alongside it, composed by
/// <see cref="CompositeEntitlementEventPublisher"/>.
/// </summary>
internal sealed class EntitlementCacheInvalidator : IEntitlementEventPublisher
{
    private readonly ICacheService _cache;
    private readonly ILogger<EntitlementCacheInvalidator> _logger;

    public EntitlementCacheInvalidator(ICacheService cache, ILogger<EntitlementCacheInvalidator> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public Task PublishAsync(FeatureEntitlementGranted @event, CancellationToken cancellationToken) =>
        EvictAsync(@event.CustomerId, cancellationToken);

    public Task PublishAsync(FeatureEntitlementRevoked @event, CancellationToken cancellationToken) =>
        EvictAsync(@event.CustomerId, cancellationToken);

    private async Task EvictAsync(Guid customerId, CancellationToken cancellationToken)
    {
        try
        {
            await _cache.RemoveAsync(EntitlementCacheKeys.ActiveFor(customerId), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not fatal, and deliberately not rethrown: the entitlement change
            // itself has already been committed, and failing here would report a
            // grant as failed when it succeeded. The TTL closes the gap.
            _logger.LogWarning(
                exception,
                "Could not evict the cached entitlement set for customer {CustomerId}; it will expire within {Seconds}s.",
                customerId,
                CachingCustomerEntitlementReader.Ttl.TotalSeconds);
        }
    }
}

/// <summary>
/// Fans one entitlement event out to several publishers.
///
/// Order matters and is not alphabetical: the cache is evicted first, so that
/// anything the delivery publisher does next — and anything it triggers — reads
/// the new entitlement set rather than the one being replaced.
/// </summary>
internal sealed class CompositeEntitlementEventPublisher : IEntitlementEventPublisher
{
    private readonly IReadOnlyList<IEntitlementEventPublisher> _publishers;

    public CompositeEntitlementEventPublisher(params IEntitlementEventPublisher[] publishers)
    {
        _publishers = publishers;
    }

    public async Task PublishAsync(FeatureEntitlementGranted @event, CancellationToken cancellationToken)
    {
        foreach (var publisher in _publishers)
        {
            await publisher.PublishAsync(@event, cancellationToken);
        }
    }

    public async Task PublishAsync(FeatureEntitlementRevoked @event, CancellationToken cancellationToken)
    {
        foreach (var publisher in _publishers)
        {
            await publisher.PublishAsync(@event, cancellationToken);
        }
    }
}
