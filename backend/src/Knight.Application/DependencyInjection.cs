using Knight.Application.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Knight.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddPlatformApplication(this IServiceCollection services)
    {
        services.AddSingleton<IPermissionCatalog, PermissionCatalog>();

        return services;
    }
}
