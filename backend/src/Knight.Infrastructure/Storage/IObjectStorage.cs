namespace Knight.Infrastructure.Storage;

public sealed record StoredObjectReference(string Key, string Uri);

/// <summary>
/// Abstraction over object/file storage. Implementations must be swappable for an
/// S3-compatible provider without changing call sites — callers must never assume
/// a local filesystem layout. Keys must be namespaced per tenant, e.g.
/// "tenants/{tenantId}/...", so tenant assets never overlap.
/// </summary>
public interface IObjectStorage
{
    Task<StoredObjectReference> PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);

    Task<Stream?> GetAsync(string key, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
