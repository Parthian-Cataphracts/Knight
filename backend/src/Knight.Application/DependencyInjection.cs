using Microsoft.Extensions.DependencyInjection;
using Knight.Application.Authorization;

namespace Knight.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddPlatformApplication(this IServiceCollection services)
    {
        services.AddSingleton<IPermissionCatalog, PermissionCatalog>();

        return services;
    }
}
