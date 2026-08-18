using Catalog.Domain;

namespace Catalog;

public sealed record AddProductMediaInput(string StorageKey, string? AltText, int DisplayOrder, bool IsPrimary);

/// <summary>
/// Product media associations. This service only records which stored objects
/// belong to a product; uploading and storing bytes is the object storage layer's job.
/// </summary>
public interface IProductMediaManagementService
{
    /// <summary>
    /// Associates a stored object with the product. The first media row for a product,
    /// or an explicit <c>IsPrimary</c> request, atomically becomes the primary image.
    /// </summary>
    Task<ProductMedia> AddAsync(Guid tenantId, Guid productId, AddProductMediaInput input, CancellationToken cancellationToken);

    Task<ProductMedia?> GetAsync(Guid tenantId, Guid productId, Guid mediaId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ProductMedia>> ListAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken);

    Task SetPrimaryAsync(Guid tenantId, Guid productId, Guid mediaId, CancellationToken cancellationToken);

    Task DeleteAsync(Guid tenantId, Guid productId, Guid mediaId, CancellationToken cancellationToken);
}
