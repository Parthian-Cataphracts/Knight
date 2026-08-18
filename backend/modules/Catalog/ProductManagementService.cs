using Catalog.Domain;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;

namespace Catalog;

public sealed class ProductManagementService : IProductManagementService
{
    private const string EntityType = nameof(Product);

    private readonly IProductRepository _repository;
    private readonly ICategoryRepository _categoryRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly CatalogAuditRecorder _audit;

    public ProductManagementService(
        IProductRepository repository,
        ICategoryRepository categoryRepository,
        IDateTimeProvider dateTimeProvider,
        CatalogAuditRecorder audit)
    {
        _repository = repository;
        _categoryRepository = categoryRepository;
        _dateTimeProvider = dateTimeProvider;
        _audit = audit;
    }

    public async Task<Product> CreateAsync(Guid tenantId, CreateProductInput input, CancellationToken cancellationToken)
    {
        // The composite FK is the real enforcement, but resolving the category first
        // gives the caller a clean 404 instead of a database constraint failure.
        await RequireCategoryAsync(tenantId, input.CategoryId, cancellationToken);

        var now = _dateTimeProvider.UtcNow;

        var product = Product.Create(
            Guid.NewGuid(),
            now,
            tenantId,
            input.CategoryId,
            input.Name,
            input.Slug,
            input.Description,
            input.Status,
            input.BasePrice,
            input.IsVisible,
            input.IsAvailable,
            input.DisplayOrder);

        await EnsureSlugAvailableAsync(tenantId, product.Slug, currentProductId: null, cancellationToken);

        try
        {
            await _repository.AddAsync(product, cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            throw new ConflictException($"A product with slug '{product.Slug}' already exists in this tenant.");
        }

        await _audit.RecordAsync("ProductCreated", tenantId, EntityType, product.Id, cancellationToken, new Dictionary<string, string>
        {
            ["name"] = product.Name,
            ["slug"] = product.Slug,
            ["categoryId"] = product.CategoryId.ToString()
        });

        return product;
    }

    public Task<Product?> GetAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken) =>
        _repository.GetByIdAsync(tenantId, productId, cancellationToken);

    public async Task<ProductListResult> ListAsync(Guid tenantId, int page, int pageSize, ProductListFilter filter, CancellationToken cancellationToken)
    {
        var (boundedPage, boundedPageSize) = CatalogPaging.Bound(page, pageSize);

        var (items, total) = await _repository.ListAsync(
            tenantId,
            boundedPage,
            boundedPageSize,
            filter.CategoryId,
            filter.Status,
            filter.IsVisible,
            filter.IsAvailable,
            filter.Search,
            cancellationToken);

        return new ProductListResult(items, total, boundedPage, boundedPageSize);
    }

    public async Task<Product> UpdateAsync(Guid tenantId, Guid productId, UpdateProductInput input, CancellationToken cancellationToken)
    {
        var product = await RequireAsync(tenantId, productId, cancellationToken);

        product.UpdateDetails(
            input.Name,
            input.Slug,
            input.Description,
            input.BasePrice,
            input.IsVisible,
            input.IsAvailable,
            input.DisplayOrder,
            _dateTimeProvider.UtcNow);

        await EnsureSlugAvailableAsync(tenantId, product.Slug, productId, cancellationToken);

        try
        {
            await _repository.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            throw new ConflictException($"A product with slug '{product.Slug}' already exists in this tenant.");
        }

        await _audit.RecordAsync("ProductUpdated", tenantId, EntityType, product.Id, cancellationToken, new Dictionary<string, string>
        {
            ["name"] = product.Name,
            ["slug"] = product.Slug
        });

        return product;
    }

    public async Task<Product> ChangeCategoryAsync(Guid tenantId, Guid productId, Guid categoryId, CancellationToken cancellationToken)
    {
        var product = await RequireAsync(tenantId, productId, cancellationToken);
        await RequireCategoryAsync(tenantId, categoryId, cancellationToken);

        product.ChangeCategory(categoryId, _dateTimeProvider.UtcNow);
        await _repository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync("ProductUpdated", tenantId, EntityType, product.Id, cancellationToken, new Dictionary<string, string>
        {
            ["categoryId"] = categoryId.ToString()
        });

        return product;
    }

    public async Task<Product> ActivateAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken)
    {
        var product = await RequireAsync(tenantId, productId, cancellationToken);

        product.Activate(_dateTimeProvider.UtcNow);
        await _repository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync("ProductUpdated", tenantId, EntityType, product.Id, cancellationToken, new Dictionary<string, string>
        {
            ["status"] = product.Status.ToString()
        });

        return product;
    }

    public async Task<Product> ArchiveAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken)
    {
        var product = await RequireAsync(tenantId, productId, cancellationToken);

        product.Archive(_dateTimeProvider.UtcNow);
        await _repository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync("ProductArchived", tenantId, EntityType, product.Id, cancellationToken, new Dictionary<string, string>
        {
            ["name"] = product.Name
        });

        return product;
    }

    public async Task<Product> SetVisibilityAsync(Guid tenantId, Guid productId, bool isVisible, CancellationToken cancellationToken)
    {
        var product = await RequireAsync(tenantId, productId, cancellationToken);

        product.SetVisibility(isVisible, _dateTimeProvider.UtcNow);
        await _repository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync("ProductUpdated", tenantId, EntityType, product.Id, cancellationToken, new Dictionary<string, string>
        {
            ["isVisible"] = isVisible.ToString()
        });

        return product;
    }

    public async Task<Product> SetAvailabilityAsync(Guid tenantId, Guid productId, bool isAvailable, CancellationToken cancellationToken)
    {
        var product = await RequireAsync(tenantId, productId, cancellationToken);

        product.SetAvailability(isAvailable, _dateTimeProvider.UtcNow);
        await _repository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync("ProductAvailabilityChanged", tenantId, EntityType, product.Id, cancellationToken, new Dictionary<string, string>
        {
            ["isAvailable"] = isAvailable.ToString()
        });

        return product;
    }

    private async Task<Product> RequireAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken) =>
        await _repository.GetByIdAsync(tenantId, productId, cancellationToken)
            ?? throw new NotFoundException(nameof(Product), productId);

    private async Task RequireCategoryAsync(Guid tenantId, Guid categoryId, CancellationToken cancellationToken)
    {
        var category = await _categoryRepository.GetByIdAsync(tenantId, categoryId, cancellationToken);
        if (category is null)
        {
            throw new NotFoundException(nameof(Category), categoryId);
        }
    }

    private async Task EnsureSlugAvailableAsync(Guid tenantId, string normalizedSlug, Guid? currentProductId, CancellationToken cancellationToken)
    {
        var existing = await _repository.GetBySlugAsync(tenantId, normalizedSlug, cancellationToken);
        if (existing is not null && existing.Id != currentProductId)
        {
            throw new ConflictException($"A product with slug '{normalizedSlug}' already exists in this tenant.");
        }
    }
}
