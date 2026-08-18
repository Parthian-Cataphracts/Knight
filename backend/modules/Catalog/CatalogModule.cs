using Microsoft.Extensions.DependencyInjection;
using Knight.Application.Authorization;

namespace Catalog;

/// <summary>
/// Tenant feature key gating the catalog module. Endpoints (slice 3) check this
/// through <see cref="Knight.Application.Abstractions.Features.IFeatureAccessService"/>;
/// application services deliberately do not re-check it.
/// </summary>
public static class CatalogFeature
{
    public const string Key = "catalog";
}

/// <summary>
/// Machine-readable permissions owned by the Catalog module, covering tenant
/// category, product, variant, modifier, media and availability administration.
/// </summary>
public static class CatalogPermissions
{
    private const string Module = "catalog";

    public static readonly Permission CategoriesView = new("catalog.categories.view", "View catalog categories.", Module);
    public static readonly Permission CategoriesCreate = new("catalog.categories.create", "Create catalog categories.", Module);
    public static readonly Permission CategoriesUpdate = new("catalog.categories.update", "Update catalog categories.", Module);
    public static readonly Permission CategoriesDelete = new("catalog.categories.delete", "Delete catalog categories.", Module);

    public static readonly Permission ProductsView = new("catalog.products.view", "View catalog products.", Module);
    public static readonly Permission ProductsCreate = new("catalog.products.create", "Create catalog products.", Module);
    public static readonly Permission ProductsUpdate = new("catalog.products.update", "Update catalog products.", Module);
    public static readonly Permission ProductsDelete = new("catalog.products.delete", "Archive catalog products.", Module);

    public static readonly Permission VariantsManage = new("catalog.variants.manage", "Manage product variants.", Module);
    public static readonly Permission ModifiersManage = new("catalog.modifiers.manage", "Manage modifier groups and modifiers.", Module);
    public static readonly Permission MediaManage = new("catalog.media.manage", "Manage product media associations.", Module);
    public static readonly Permission AvailabilityManage = new("catalog.availability.manage", "Manage product/variant/modifier availability.", Module);

    public static readonly IReadOnlyCollection<Permission> All =
    [
        CategoriesView,
        CategoriesCreate,
        CategoriesUpdate,
        CategoriesDelete,
        ProductsView,
        ProductsCreate,
        ProductsUpdate,
        ProductsDelete,
        VariantsManage,
        ModifiersManage,
        MediaManage,
        AvailabilityManage
    ];
}

internal sealed class CatalogPermissionProvider : IPermissionProvider
{
    public IReadOnlyCollection<Permission> Permissions => CatalogPermissions.All;
}

public static class CatalogModule
{
    public static IServiceCollection AddCatalogModule(this IServiceCollection services)
    {
        services.AddSingleton<IPermissionProvider, CatalogPermissionProvider>();

        services.AddScoped<CatalogAuditRecorder>();

        services.AddScoped<ICategoryManagementService, CategoryManagementService>();
        services.AddScoped<IProductManagementService, ProductManagementService>();
        services.AddScoped<IProductVariantManagementService, ProductVariantManagementService>();
        services.AddScoped<IModifierGroupManagementService, ModifierGroupManagementService>();
        services.AddScoped<IProductModifierAssignmentService, ProductModifierAssignmentService>();
        services.AddScoped<IProductMediaManagementService, ProductMediaManagementService>();
        services.AddScoped<ICatalogPublicQueryService, CatalogPublicQueryService>();

        return services;
    }
}
