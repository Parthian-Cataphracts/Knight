using Microsoft.Extensions.DependencyInjection;
using Ordering.Domain;
using Knight.Application.Authorization;

namespace Ordering;

/// <summary>
/// Tenant feature key gating the ordering module.
/// </summary>
public static class OrderingFeature
{
    public const string Key = "ordering";
}

/// <summary>
/// Machine-readable permissions owned by the Ordering module.
/// </summary>
public static class OrderingPermissions
{
    private const string Module = "ordering";

    public static readonly Permission OrdersView = new("ordering.orders.view", "View tenant orders.", Module);
    public static readonly Permission OrdersStatusUpdate = new("ordering.orders.status.update", "Update order status.", Module);
    public static readonly Permission OrdersCancel = new("ordering.orders.cancel", "Cancel orders.", Module);

    public static readonly IReadOnlyCollection<Permission> All =
    [
        OrdersView,
        OrdersStatusUpdate,
        OrdersCancel
    ];
}

internal sealed class OrderingPermissionProvider : IPermissionProvider
{
    public IReadOnlyCollection<Permission> Permissions => OrderingPermissions.All;
}

public static class OrderingModule
{
    public static IServiceCollection AddOrderingModule(this IServiceCollection services)
    {
        services.AddSingleton<IPermissionProvider, OrderingPermissionProvider>();

        services.AddScoped<OrderingAuditRecorder>();
        services.AddScoped<IOrderPricingCalculator, OrderPricingCalculator>();
        services.AddScoped<IOrderPlacementService, OrderPlacementService>();
        services.AddScoped<IOrderManagementService, OrderManagementService>();

        return services;
    }
}
