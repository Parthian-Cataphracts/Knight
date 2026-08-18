using AccessControl.Abstractions;
using AccessControl.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Infrastructure.ControlPlane.Repositories;
using Knight.Infrastructure.ControlPlane.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Knight.Infrastructure.ControlPlane;

/// <summary>
/// Wires the control-plane persistence and security services. Deliberately
/// separate from <c>AddPlatformInfrastructure</c>: the two schemas are
/// independent, and the legacy registration will be deleted wholesale in
/// phase 8 without disturbing anything here.
/// </summary>
public static class ControlPlaneInfrastructure
{
    public static IServiceCollection AddControlPlaneInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("ControlPlane")
            ?? configuration.GetConnectionString("Platform")
            ?? throw new InvalidOperationException(
                "Missing connection string 'ControlPlane'. Set it via configuration or the CONTROL_PLANE_DB_CONNECTION_STRING environment variable.");

        // AddDbContext, never pooled: the customer-isolation filter closes over
        // the request's scope, and a pooled instance could carry one request's
        // customer into another's.
        services.AddDbContext<ControlPlaneDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", ControlPlaneDbContext.SchemaName)));

        services.AddScoped<CustomerScopeAccessor>();
        services.AddScoped<ICustomerScope>(sp => sp.GetRequiredService<CustomerScopeAccessor>());
        services.AddScoped<ICustomerScopeAccessor>(sp => sp.GetRequiredService<CustomerScopeAccessor>());

        services.AddScoped<Customers.Domain.ICustomerRepository, ControlPlaneCustomerRepository>();
        services.AddScoped<Stores.Domain.IStoreRepository, ControlPlaneStoreRepository>();
        services.AddScoped<IControlPlaneUserRepository, ControlPlaneUserRepository>();
        services.AddScoped<IRoleRepository, ControlPlaneRoleRepository>();
        services.AddScoped<IUserSessionRepository, UserSessionRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();

        services.AddSingleton<IControlPlanePasswordHasher, ControlPlanePasswordHasher>();
        services.AddSingleton<ISecureTokenFactory, SecureTokenFactory>();
        services.AddSingleton<IControlPlaneTokenGenerator, ControlPlaneTokenGenerator>();
        services.AddSingleton<ITotpService, TotpService>();

        return services;
    }
}
