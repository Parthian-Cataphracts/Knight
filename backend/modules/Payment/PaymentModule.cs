using Microsoft.Extensions.DependencyInjection;
using Payment.Domain;
using Knight.Application.Authorization;

namespace Payment;

public static class PaymentFeature
{
    public const string Key = "payments";
}

public static class PaymentPermissions
{
    private const string Module = "payment";

    public static readonly Permission View = new("payments.view", "View tenant payments.", Module);
    public static readonly Permission StatusManage = new("payments.status.manage", "Manage payment status.", Module);

    public static readonly IReadOnlyCollection<Permission> All =
    [
        View,
        StatusManage
    ];
}

internal sealed class PaymentPermissionProvider : IPermissionProvider
{
    public IReadOnlyCollection<Permission> Permissions => PaymentPermissions.All;
}

internal sealed class PaymentProviderResolver : IPaymentProviderResolver
{
    private readonly IEnumerable<IPaymentProvider> _providers;

    public PaymentProviderResolver(IEnumerable<IPaymentProvider> providers)
    {
        _providers = providers;
    }

    public IPaymentProvider? Resolve(string? providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return _providers.FirstOrDefault();
        }

        return _providers.FirstOrDefault(p =>
            string.Equals(p.ProviderKey, providerKey.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}

public static class PaymentModule
{
    public static IServiceCollection AddPaymentModule(this IServiceCollection services)
    {
        services.AddSingleton<IPermissionProvider, PaymentPermissionProvider>();
        services.AddScoped<IPaymentProviderResolver, PaymentProviderResolver>();
        services.AddScoped<PaymentAuditRecorder>();
        services.AddScoped<IPaymentManagementService, PaymentManagementService>();

        return services;
    }
}
