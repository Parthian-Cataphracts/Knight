using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AccessControl;

/// <summary>
/// DI registration for the control-plane access module. Repositories, the
/// password hasher, token minting and TOTP are supplied by Infrastructure; this
/// module only owns the rules.
/// </summary>
public static class AccessControlModule
{
    public static IServiceCollection AddAccessControlModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AccessControlOptions>()
            .Bind(configuration.GetSection(AccessControlOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<IEffectivePermissionResolver, EffectivePermissionResolver>();
        services.AddScoped<IControlPlaneAuthenticationService, ControlPlaneAuthenticationService>();
        services.AddScoped<Knight.Application.Abstractions.ControlPlane.IAuditTrail, AuditTrail>();
        services.AddScoped<IAuditLogQueryService, AuditLogQueryService>();
        services.AddScoped<IAccessDirectory, AccessDirectory>();
        services.AddScoped<IControlPlaneAccessSeeder, ControlPlaneAccessSeeder>();

        return services;
    }
}
