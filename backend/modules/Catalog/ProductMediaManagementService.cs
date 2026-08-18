using Catalog.Domain;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;

namespace Catalog;

public sealed class ProductMediaManagementService : IProductMediaManagementService
{
    private const string EntityType = nameof(ProductMedia);

    private readonly IProductMediaRepository _repository;
    private readonly IProductRepository _productRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly CatalogAuditRecorder _audit;

    public ProductMediaManagementService(
        IProductMediaRepository repository,
        IProductRepository productRepository,
        IDateTimeProvider dateTimeProvider,
        CatalogAuditRecorder audit)
    {
        _repository = repository;
        _productRepository = productRepository;
        _dateTimeProvider = dateTimeProvider;
        _audit = audit;
    }

    public async Task<ProductMedia> AddAsync(Guid tenantId, Guid productId, AddProductMediaInput input, CancellationToken cancellationToken)
    {
        await RequireProductAsync(tenantId, productId, cancellationToken);

        var existing = await _repository.ListByProductAsync(tenantId, productId, cancellationToken);
        var shouldBePrimary = input.IsPrimary || existing.Count == 0;

        // Insert as non-primary, then promote through the transactional swap, so the
        // partial unique index never sees two primary rows for the same product.
        var media = ProductMedia.Create(
            Guid.NewGuid(),
            _dateTimeProvider.UtcNow,
            tenantId,
            productId,
            input.StorageKey,
            input.AltText,
            input.DisplayOrder,
            isPrimary: false);

        await _repository.AddAsync(media, cancellationToken);

        if (shouldBePrimary)
        {
            await _repository.SetPrimaryAsync(tenantId, productId, media.Id, cancellationToken);
            media.SetPrimary(true);
        }

        await _audit.RecordAsync("ProductMediaAdded", tenantId, EntityType, media.Id, cancellationToken, new Dictionary<string, string>
        {
            ["productId"] = productId.ToString(),
            ["isPrimary"] = shouldBePrimary.ToString()
        });

        return media;
    }

    public Task<ProductMedia?> GetAsync(Guid tenantId, Guid productId, Guid mediaId, CancellationToken cancellationToken) =>
        _repository.GetByIdAsync(tenantId, productId, mediaId, cancellationToken);

    public Task<IReadOnlyCollection<ProductMedia>> ListAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken) =>
        _repository.ListByProductAsync(tenantId, productId, cancellationToken);

    public async Task SetPrimaryAsync(Guid tenantId, Guid productId, Guid mediaId, CancellationToken cancellationToken)
    {
        var updated = await _repository.SetPrimaryAsync(tenantId, productId, mediaId, cancellationToken);
        if (!updated)
        {
            throw new NotFoundException(nameof(ProductMedia), mediaId);
        }

        await _audit.RecordAsync("ProductMediaPrimaryChanged", tenantId, EntityType, mediaId, cancellationToken, new Dictionary<string, string>
        {
            ["productId"] = productId.ToString()
        });
    }

    public async Task DeleteAsync(Guid tenantId, Guid productId, Guid mediaId, CancellationToken cancellationToken)
    {
        var media = await _repository.GetByIdAsync(tenantId, productId, mediaId, cancellationToken)
            ?? throw new NotFoundException(nameof(ProductMedia), mediaId);

        await _repository.DeleteAsync(media, cancellationToken);

        await _audit.RecordAsync("ProductMediaRemoved", tenantId, EntityType, mediaId, cancellationToken, new Dictionary<string, string>
        {
            ["productId"] = productId.ToString(),
            ["wasPrimary"] = media.IsPrimary.ToString()
        });
    }

    private async Task RequireProductAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken)
    {
        var product = await _productRepository.GetByIdAsync(tenantId, productId, cancellationToken);
        if (product is null)
        {
            throw new NotFoundException(nameof(Product), productId);
        }
    }
}
