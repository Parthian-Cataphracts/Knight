using Microsoft.Extensions.DependencyInjection;
using Knight.Application.Authorization;

namespace Delivery;

public static class DeliveryFeature
{
    public const string Key = "delivery";
}

public static class DeliveryPermissions
{
    private const string Module = "delivery";

    public static readonly Permission SettingsView = new("delivery.settings.view", "View tenant delivery settings.", Module);
    public static readonly Permission SettingsUpdate = new("delivery.settings.update", "Update tenant delivery settings.", Module);
    public static readonly Permission ZonesView = new("delivery.zones.view", "View tenant delivery zones.", Module);
    public static readonly Permission ZonesCreate = new("delivery.zones.create", "Create tenant delivery zones.", Module);
    public static readonly Permission ZonesUpdate = new("delivery.zones.update", "Update tenant delivery zones.", Module);
    public static readonly Permission ZonesArchive = new("delivery.zones.archive", "Archive tenant delivery zones.", Module);

    public static readonly IReadOnlyCollection<Permission> All =
    [
        SettingsView,
        SettingsUpdate,
        ZonesView,
        ZonesCreate,
        ZonesUpdate,
        ZonesArchive
    ];
}

internal sealed class DeliveryPermissionProvider : IPermissionProvider
{
    public IReadOnlyCollection<Permission> Permissions => DeliveryPermissions.All;
}

public static class DeliveryModule
{
    public static IServiceCollection AddDeliveryModule(this IServiceCollection services)
    {
        services.AddSingleton<IPermissionProvider, DeliveryPermissionProvider>();
        services.AddScoped<DeliveryAuditRecorder>();
        services.AddScoped<IDeliveryQuoteService, DeliveryQuoteService>();
        services.AddScoped<IDeliveryManagementService, DeliveryManagementService>();

        return services;
    }
}
