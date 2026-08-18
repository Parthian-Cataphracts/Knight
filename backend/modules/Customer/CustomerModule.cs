using Microsoft.Extensions.DependencyInjection;
using Knight.Application.Authorization;

namespace Customer;

/// <summary>
/// Tenant feature key gating persistent Customer / CRM functionality.
/// </summary>
public static class CustomerFeature
{
    public const string Key = "customers";
}

/// <summary>
/// Machine-readable permissions owned by the Customer module.
/// </summary>
public static class CustomerPermissions
{
    private const string Module = "customers";

    public static readonly Permission CustomersView = new("customers.view", "View tenant customers.", Module);
    public static readonly Permission CustomersCreate = new("customers.create", "Create tenant customers.", Module);
    public static readonly Permission CustomersUpdate = new("customers.update", "Update tenant customers.", Module);
    public static readonly Permission CustomersArchive = new("customers.archive", "Archive tenant customers.", Module);
    public static readonly Permission CustomersRestore = new("customers.restore", "Restore archived tenant customers.", Module);

    public static readonly IReadOnlyCollection<Permission> All =
    [
        CustomersView,
        CustomersCreate,
        CustomersUpdate,
        CustomersArchive,
        CustomersRestore
    ];
}

internal sealed class CustomerPermissionProvider : IPermissionProvider
{
    public IReadOnlyCollection<Permission> Permissions => CustomerPermissions.All;
}

public static class CustomerModule
{
    public static IServiceCollection AddCustomerModule(this IServiceCollection services)
    {
        services.AddSingleton<IPermissionProvider, CustomerPermissionProvider>();

        services.AddScoped<CustomerAuditRecorder>();
        services.AddScoped<ICustomerManagementService, CustomerManagementService>();

        return services;
    }
}
