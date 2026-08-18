using Microsoft.Extensions.DependencyInjection;
using Knight.Application.Authorization;

namespace Fulfillment;

public static class FulfillmentPermissions
{
    private const string Module = "fulfillment";

    public static readonly Permission SettingsView = new("fulfillment.settings.view", "View tenant fulfillment settings.", Module);
    public static readonly Permission SettingsUpdate = new("fulfillment.settings.update", "Update tenant fulfillment settings.", Module);

    public static readonly IReadOnlyCollection<Permission> All =
    [
        SettingsView,
        SettingsUpdate
    ];
}

internal sealed class FulfillmentPermissionProvider : IPermissionProvider
{
    public IReadOnlyCollection<Permission> Permissions => FulfillmentPermissions.All;
}

public static class FulfillmentModule
{
    public static IServiceCollection AddFulfillmentModule(this IServiceCollection services)
    {
        services.AddSingleton<IPermissionProvider, FulfillmentPermissionProvider>();
        services.AddScoped<FulfillmentAuditRecorder>();
        services.AddScoped<IFulfillmentManagementService, FulfillmentManagementService>();

        return services;
    }
}
