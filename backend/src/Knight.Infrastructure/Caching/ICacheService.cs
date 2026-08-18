namespace Knight.Infrastructure.Caching;

/// <summary>
/// Thin abstraction over the distributed cache. Callers are responsible for
/// composing tenant-aware keys (e.g. "tenant:{tenantId}:catalog:...") — this
/// service does not scope keys itself, so sensitive or tenant-owned data must
/// never be cached under a key that omits the tenant identifier.
/// </summary>
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken);

    Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken);

    Task RemoveAsync(string key, CancellationToken cancellationToken);
}
