using Catalog;
using Checkout;
using Customer;
using Delivery;
using FeatureManagement;
using Fulfillment;
using Identity;
using Ordering;
using Payment;
using Knight.Application.Authorization;
using Promotions;
using Tenancy;

namespace Knight.Api.Composition;

/// <summary>
/// Single place where module DI registration is composed. Adding a new module
/// should only require one line here plus its own AddXModule() extension.
/// </summary>
public static class ModuleRegistration
{
    public static IServiceCollection AddPlatformModules(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddTenancyModule();
        services.AddIdentityModule(configuration);
        services.AddFeatureManagementModule();
        services.AddCatalogModule();
        services.AddOrderingModule();
        services.AddCustomerModule();
        services.AddFulfillmentModule();
        services.AddDeliveryModule();
        services.AddCheckoutModule();
        services.AddPaymentModule();
        services.AddPromotionsModule();

        return services;
    }

    /// <summary>
    /// Populates the permission catalog from every registered <see cref="IPermissionProvider"/>.
    /// Must run after the service provider is built.
    /// </summary>
    public static void InitializePermissionCatalog(this IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IPermissionCatalog>();
        var providers = scope.ServiceProvider.GetServices<IPermissionProvider>();

        foreach (var provider in providers)
        {
            catalog.Register(provider.Permissions);
        }
    }
}
