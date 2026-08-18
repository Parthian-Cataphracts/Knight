using Catalog;
using Catalog.Domain;
using Knight.Api.Authorization;
using Knight.Application.Abstractions.Tenancy;
using Knight.Contracts.Catalog;
using Knight.Contracts.Common;
using Tenancy;

namespace Knight.Api.Endpoints;

/// <summary>
/// Anonymous storefront catalog surface. No authorization policy applies: the
/// tenant is resolved from the request host by
/// <see cref="Middleware.TenantResolutionMiddleware"/>, which runs before
/// authorization and populates <see cref="ITenantContext"/> even for
/// unauthenticated requests. A request that arrives on a host with no resolvable
/// tenant fails closed with the same 403 that every other tenant-requiring route
/// produces — the endpoint never falls back to a default tenant.
///
/// Responses use dedicated public shapes so that no lifecycle, audit or internal
/// field can ever leak onto an anonymous route.
/// </summary>
public static class PublicCatalogEndpoints
{
    public static void MapPublicCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/catalog")
            .RequireFeature(CatalogFeature.Key)
            .RequireRateLimiting("tenant-public")
            .WithTags("Public Catalog");

        group.MapGet("/categories", async (
            int? page,
            int? pageSize,
            ITenantContext tenantContext,
            ICatalogPublicQueryService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var result = await service.ListCategoriesAsync(tenantId, page ?? 1, pageSize ?? 20, cancellationToken);
            var items = result.Items.Select(ToPublicResponse).ToArray();
            return Results.Ok(PagedResponse<PublicCategoryResponse>.Create(items, result.Page, result.PageSize, result.TotalCount));
        });

        group.MapGet("/categories/{slug}", async (
            string slug,
            ITenantContext tenantContext,
            ICatalogPublicQueryService service,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var category = await service.GetCategoryBySlugAsync(tenantId, slug, cancellationToken);
            return category is null ? Results.NotFound() : Results.Ok(ToPublicResponse(category));
        });

        group.MapGet("/products", async (
            int? page,
            int? pageSize,
            Guid? categoryId,
            string? search,
            ITenantContext tenantContext,
            ICatalogPublicQueryService service,
            IProductVariantManagementService variantService,
            IProductMediaManagementService mediaService,
            ITenantManagementService tenantManagementService,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var currency = await GetCurrencyAsync(tenantManagementService, tenantId, cancellationToken);
            var result = await service.ListProductsAsync(tenantId, page ?? 1, pageSize ?? 20, categoryId, search, cancellationToken);

            var items = new List<PublicProductSummaryResponse>(result.Items.Count);
            foreach (var product in result.Items)
            {
                var variants = await variantService.ListAsync(tenantId, product.Id, cancellationToken);
                var media = await mediaService.ListAsync(tenantId, product.Id, cancellationToken);
                items.Add(ToSummaryResponse(product, variants.Count > 0, media, currency));
            }

            return Results.Ok(PagedResponse<PublicProductSummaryResponse>.Create(items, result.Page, result.PageSize, result.TotalCount));
        });

        group.MapGet("/products/{slug}", async (
            string slug,
            ITenantContext tenantContext,
            ICatalogPublicQueryService service,
            ITenantManagementService tenantManagementService,
            CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);

            var detail = await service.GetProductBySlugAsync(tenantId, slug, cancellationToken);
            if (detail is null)
            {
                return Results.NotFound();
            }

            var currency = await GetCurrencyAsync(tenantManagementService, tenantId, cancellationToken);
            return Results.Ok(ToDetailResponse(detail, currency));
        });
    }

    private static async Task<string> GetCurrencyAsync(
        ITenantManagementService tenantManagementService,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var tenant = await tenantManagementService.GetAsync(tenantId, cancellationToken);
        return tenant?.DefaultCurrency ?? string.Empty;
    }

    private static PublicCategoryResponse ToPublicResponse(Category category) => new()
    {
        Id = category.Id,
        Name = category.Name,
        Slug = category.Slug,
        Description = category.Description,
        DisplayOrder = category.DisplayOrder
    };

    private static PublicMediaResponse ToPublicResponse(ProductMedia media) => new()
    {
        StorageKey = media.StorageKey,
        AltText = media.AltText,
        IsPrimary = media.IsPrimary
    };

    private static PublicVariantResponse ToPublicResponse(ProductVariant variant) => new()
    {
        Id = variant.Id,
        Name = variant.Name,
        Price = variant.Price,
        CompareAtPrice = variant.CompareAtPrice,
        IsAvailable = variant.IsAvailable
    };

    private static PublicModifierGroupResponse ToPublicResponse(ProductModifierGroupDetail detail) => new()
    {
        Id = detail.Group.Id,
        Name = detail.Group.Name,
        IsRequired = detail.Group.IsRequired,
        MinSelections = detail.Group.MinSelections,
        MaxSelections = detail.Group.MaxSelections,
        Modifiers = detail.Modifiers
            .OrderBy(m => m.DisplayOrder)
            .Select(m => new PublicModifierResponse
            {
                Id = m.Id,
                Name = m.Name,
                PriceDelta = m.PriceDelta,
                IsAvailable = m.IsAvailable
            })
            .ToArray()
    };

    private static PublicProductSummaryResponse ToSummaryResponse(
        Product product,
        bool hasVariants,
        IReadOnlyCollection<ProductMedia> media,
        string currency)
    {
        var primary = media.FirstOrDefault(m => m.IsPrimary);

        return new PublicProductSummaryResponse
        {
            Id = product.Id,
            Name = product.Name,
            Slug = product.Slug,
            Description = product.Description,
            CategoryId = product.CategoryId,
            HasVariants = hasVariants,
            // With variants present the effective price comes from the variants,
            // so the base price would be misleading and is suppressed.
            BasePrice = hasVariants ? null : product.BasePrice,
            Currency = currency,
            IsAvailable = product.IsAvailable,
            PrimaryMedia = primary is null ? null : ToPublicResponse(primary)
        };
    }

    private static PublicProductDetailResponse ToDetailResponse(ProductDetail detail, string currency)
    {
        var hasVariants = detail.Variants.Count > 0;

        return new PublicProductDetailResponse
        {
            Id = detail.Product.Id,
            Name = detail.Product.Name,
            Slug = detail.Product.Slug,
            Description = detail.Product.Description,
            CategoryId = detail.Product.CategoryId,
            HasVariants = hasVariants,
            BasePrice = hasVariants ? null : detail.Product.BasePrice,
            Currency = currency,
            IsAvailable = detail.Product.IsAvailable,
            Variants = detail.Variants.OrderBy(v => v.DisplayOrder).Select(ToPublicResponse).ToArray(),
            ModifierGroups = detail.ModifierGroups.OrderBy(g => g.DisplayOrder).Select(ToPublicResponse).ToArray(),
            Media = detail.Media.OrderBy(m => m.DisplayOrder).Select(ToPublicResponse).ToArray()
        };
    }
}
