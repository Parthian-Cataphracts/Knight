using Catalog.Domain;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;

namespace Catalog;

public sealed class ProductVariantManagementService : IProductVariantManagementService
{
    private const string EntityType = nameof(ProductVariant);

    private readonly IProductVariantRepository _repository;
    private readonly IProductRepository _productRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly CatalogAuditRecorder _audit;

    public ProductVariantManagementService(
        IProductVariantRepository repository,
        IProductRepository productRepository,
        IDateTimeProvider dateTimeProvider,
        CatalogAuditRecorder audit)
    {
        _repository = repository;
        _productRepository = productRepository;
        _dateTimeProvider = dateTimeProvider;
        _audit = audit;
    }

    public async Task<ProductVariant> CreateAsync(Guid tenantId, Guid productId, CreateProductVariantInput input, CancellationToken cancellationToken)
    {
        await RequireProductAsync(tenantId, productId, cancellationToken);

        // A product with no variants gets a sane default automatically; the partial
        // unique index still guarantees only one default can ever exist.
        var existing = await _repository.ListByProductAsync(tenantId, productId, cancellationToken);
        var isDefault = existing.Count == 0;

        var now = _dateTimeProvider.UtcNow;

        var variant = ProductVariant.Create(
            Guid.NewGuid(),
            now,
            tenantId,
            productId,
            input.Name,
            input.Sku,
            input.Price,
            input.CompareAtPrice,
            isDefault,
            input.IsAvailable,
            input.DisplayOrder);

        try
        {
            await _repository.AddAsync(variant, cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            throw new ConflictException($"A product variant with SKU '{input.Sku}' already exists in this tenant.");
        }

        await _audit.RecordAsync("VariantChanged", tenantId, EntityType, variant.Id, cancellationToken, new Dictionary<string, string>
        {
            ["operation"] = "Created",
            ["productId"] = productId.ToString(),
            ["isDefault"] = isDefault.ToString()
        });

        return variant;
    }

    public Task<ProductVariant?> GetAsync(Guid tenantId, Guid productId, Guid variantId, CancellationToken cancellationToken) =>
        _repository.GetByIdAsync(tenantId, productId, variantId, cancellationToken);

    public Task<IReadOnlyCollection<ProductVariant>> ListAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken) =>
        _repository.ListByProductAsync(tenantId, productId, cancellationToken);

    public async Task<ProductVariant> UpdateAsync(Guid tenantId, Guid productId, Guid variantId, UpdateProductVariantInput input, CancellationToken cancellationToken)
    {
        var variant = await RequireAsync(tenantId, productId, variantId, cancellationToken);

        variant.UpdateDetails(
            input.Name,
            input.Sku,
            input.Price,
            input.CompareAtPrice,
            input.IsAvailable,
            input.DisplayOrder,
            _dateTimeProvider.UtcNow);

        try
        {
            await _repository.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            throw new ConflictException($"A product variant with SKU '{input.Sku}' already exists in this tenant.");
        }

        await _audit.RecordAsync("VariantChanged", tenantId, EntityType, variant.Id, cancellationToken, new Dictionary<string, string>
        {
            ["operation"] = "Updated",
            ["productId"] = productId.ToString()
        });

        return variant;
    }

    public async Task SetDefaultAsync(Guid tenantId, Guid productId, Guid variantId, CancellationToken cancellationToken)
    {
        var updated = await _repository.SetDefaultAsync(tenantId, productId, variantId, _dateTimeProvider.UtcNow, cancellationToken);
        if (!updated)
        {
            throw new NotFoundException(nameof(ProductVariant), variantId);
        }

        await _audit.RecordAsync("VariantChanged", tenantId, EntityType, variantId, cancellationToken, new Dictionary<string, string>
        {
            ["operation"] = "DefaultChanged",
            ["productId"] = productId.ToString()
        });
    }

    public async Task<ProductVariant> SetAvailabilityAsync(Guid tenantId, Guid productId, Guid variantId, bool isAvailable, CancellationToken cancellationToken)
    {
        var variant = await RequireAsync(tenantId, productId, variantId, cancellationToken);

        variant.SetAvailability(isAvailable, _dateTimeProvider.UtcNow);
        await _repository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync("VariantChanged", tenantId, EntityType, variant.Id, cancellationToken, new Dictionary<string, string>
        {
            ["operation"] = "AvailabilityChanged",
            ["productId"] = productId.ToString(),
            ["isAvailable"] = isAvailable.ToString()
        });

        return variant;
    }

    public async Task DeactivateAsync(Guid tenantId, Guid productId, Guid variantId, CancellationToken cancellationToken)
    {
        var variant = await RequireAsync(tenantId, productId, variantId, cancellationToken);

        // Deliberately no automatic reassignment of the default flag: leaving the
        // product without a default is an accepted end state, and silently promoting
        // another variant would be a pricing decision the caller never made.
        variant.SetAvailability(false, _dateTimeProvider.UtcNow);
        await _repository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync("VariantChanged", tenantId, EntityType, variant.Id, cancellationToken, new Dictionary<string, string>
        {
            ["operation"] = "Deactivated",
            ["productId"] = productId.ToString(),
            ["wasDefault"] = variant.IsDefault.ToString()
        });
    }

    private async Task<ProductVariant> RequireAsync(Guid tenantId, Guid productId, Guid variantId, CancellationToken cancellationToken) =>
        await _repository.GetByIdAsync(tenantId, productId, variantId, cancellationToken)
            ?? throw new NotFoundException(nameof(ProductVariant), variantId);

    private async Task RequireProductAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken)
    {
        var product = await _productRepository.GetByIdAsync(tenantId, productId, cancellationToken);
        if (product is null)
        {
            throw new NotFoundException(nameof(Product), productId);
        }
    }
}
