using Catalog.Domain;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;

namespace Catalog;

public sealed class CategoryManagementService : ICategoryManagementService
{
    private const string EntityType = nameof(Category);

    private readonly ICategoryRepository _repository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly CatalogAuditRecorder _audit;

    public CategoryManagementService(
        ICategoryRepository repository,
        IDateTimeProvider dateTimeProvider,
        CatalogAuditRecorder audit)
    {
        _repository = repository;
        _dateTimeProvider = dateTimeProvider;
        _audit = audit;
    }

    public async Task<Category> CreateAsync(Guid tenantId, CreateCategoryInput input, CancellationToken cancellationToken)
    {
        var now = _dateTimeProvider.UtcNow;

        var category = Category.Create(
            Guid.NewGuid(),
            now,
            tenantId,
            input.Name,
            input.Slug,
            input.Description,
            input.IsVisible,
            input.DisplayOrder);

        await EnsureSlugAvailableAsync(tenantId, category.Slug, currentCategoryId: null, cancellationToken);

        try
        {
            await _repository.AddAsync(category, cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            throw new ConflictException($"A category with slug '{category.Slug}' already exists in this tenant.");
        }

        await _audit.RecordAsync("CategoryCreated", tenantId, EntityType, category.Id, cancellationToken, new Dictionary<string, string>
        {
            ["name"] = category.Name,
            ["slug"] = category.Slug
        });

        return category;
    }

    public Task<Category?> GetAsync(Guid tenantId, Guid categoryId, CancellationToken cancellationToken) =>
        _repository.GetByIdAsync(tenantId, categoryId, cancellationToken);

    public async Task<CategoryListResult> ListAsync(Guid tenantId, int page, int pageSize, string? search, bool? isVisible, CancellationToken cancellationToken)
    {
        var (boundedPage, boundedPageSize) = CatalogPaging.Bound(page, pageSize);

        var (items, total) = await _repository.ListAsync(tenantId, boundedPage, boundedPageSize, search, isVisible, cancellationToken);
        return new CategoryListResult(items, total, boundedPage, boundedPageSize);
    }

    public async Task<Category> UpdateAsync(Guid tenantId, Guid categoryId, UpdateCategoryInput input, CancellationToken cancellationToken)
    {
        var category = await RequireAsync(tenantId, categoryId, cancellationToken);

        category.UpdateDetails(
            input.Name,
            input.Slug,
            input.Description,
            input.IsVisible,
            input.DisplayOrder,
            _dateTimeProvider.UtcNow);

        await EnsureSlugAvailableAsync(tenantId, category.Slug, categoryId, cancellationToken);

        try
        {
            await _repository.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            throw new ConflictException($"A category with slug '{category.Slug}' already exists in this tenant.");
        }

        await _audit.RecordAsync("CategoryUpdated", tenantId, EntityType, category.Id, cancellationToken, new Dictionary<string, string>
        {
            ["name"] = category.Name,
            ["slug"] = category.Slug
        });

        return category;
    }

    public async Task<Category> SetVisibilityAsync(Guid tenantId, Guid categoryId, bool isVisible, CancellationToken cancellationToken)
    {
        var category = await RequireAsync(tenantId, categoryId, cancellationToken);

        category.SetVisibility(isVisible, _dateTimeProvider.UtcNow);
        await _repository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync("CategoryUpdated", tenantId, EntityType, category.Id, cancellationToken, new Dictionary<string, string>
        {
            ["isVisible"] = isVisible.ToString()
        });

        return category;
    }

    public async Task DeleteAsync(Guid tenantId, Guid categoryId, CancellationToken cancellationToken)
    {
        var category = await RequireAsync(tenantId, categoryId, cancellationToken);

        if (await _repository.HasProductsAsync(tenantId, categoryId, cancellationToken))
        {
            throw new ConflictException($"Category '{category.Name}' still contains products and cannot be deleted.");
        }

        await _repository.DeleteAsync(category, cancellationToken);

        await _audit.RecordAsync("CategoryDeleted", tenantId, EntityType, categoryId, cancellationToken, new Dictionary<string, string>
        {
            ["name"] = category.Name
        });
    }

    private async Task<Category> RequireAsync(Guid tenantId, Guid categoryId, CancellationToken cancellationToken) =>
        await _repository.GetByIdAsync(tenantId, categoryId, cancellationToken)
            ?? throw new NotFoundException(nameof(Category), categoryId);

    private async Task EnsureSlugAvailableAsync(Guid tenantId, string normalizedSlug, Guid? currentCategoryId, CancellationToken cancellationToken)
    {
        var existing = await _repository.GetBySlugAsync(tenantId, normalizedSlug, cancellationToken);
        if (existing is not null && existing.Id != currentCategoryId)
        {
            throw new ConflictException($"A category with slug '{normalizedSlug}' already exists in this tenant.");
        }
    }
}
