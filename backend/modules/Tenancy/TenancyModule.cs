using Microsoft.Extensions.DependencyInjection;
using Knight.Application.Abstractions.Tenancy;

namespace Tenancy;

public static class TenancyModule
{
    public static IServiceCollection AddTenancyModule(this IServiceCollection services)
    {
        services.AddScoped<TenantContextAccessor>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContextAccessor>());
        services.AddScoped<ITenantContextAccessor>(sp => sp.GetRequiredService<TenantContextAccessor>());
        services.AddScoped<ITenantResolver, DomainTenantResolver>();
        services.AddScoped<ITenantManagementService, TenantManagementService>();

        return services;
    }
}
