using Microsoft.Extensions.DependencyInjection;
using Knight.Application.Authorization;
using Promotions.Domain;

namespace Promotions;

public static class PromotionsFeature
{
    public const string Key = "promotions";
}

public static class PromotionsPermissions
{
    private const string Module = "promotions";

    public static readonly Permission PromotionsView = new("promotions.view", "View promotions.", Module);
    public static readonly Permission PromotionsCreate = new("promotions.create", "Create promotions.", Module);
    public static readonly Permission PromotionsUpdate = new("promotions.update", "Update promotions.", Module);
    public static readonly Permission PromotionsArchive = new("promotions.archive", "Archive promotions.", Module);

    public static readonly Permission CouponsView = new("coupons.view", "View coupons.", Module);
    public static readonly Permission CouponsCreate = new("coupons.create", "Create coupons.", Module);
    public static readonly Permission CouponsUpdate = new("coupons.update", "Update coupons.", Module);
    public static readonly Permission CouponsArchive = new("coupons.archive", "Archive coupons.", Module);

    public static readonly IReadOnlyCollection<Permission> All =
    [
        PromotionsView,
        PromotionsCreate,
        PromotionsUpdate,
        PromotionsArchive,
        CouponsView,
        CouponsCreate,
        CouponsUpdate,
        CouponsArchive
    ];
}

internal sealed class PromotionsPermissionProvider : IPermissionProvider
{
    public IReadOnlyCollection<Permission> Permissions => PromotionsPermissions.All;
}

public static class PromotionsModule
{
    public static IServiceCollection AddPromotionsModule(this IServiceCollection services)
    {
        services.AddSingleton<IPermissionProvider, PromotionsPermissionProvider>();
        services.AddScoped<PromotionsAuditRecorder>();
        services.AddScoped<IPromotionPricingEvaluator, PromotionEvaluationService>();
        services.AddScoped<IPromotionRedemptionService, PromotionRedemptionService>();
        services.AddScoped<IPromotionManagementService, PromotionManagementService>();
        services.AddScoped<ICouponManagementService, CouponManagementService>();

        return services;
    }
}
