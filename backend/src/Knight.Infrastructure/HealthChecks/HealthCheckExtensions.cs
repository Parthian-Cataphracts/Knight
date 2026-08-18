using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Knight.Infrastructure.HealthChecks;

public static class HealthCheckExtensions
{
    public static IHealthChecksBuilder AddPlatformHealthChecks(this IServiceCollection services, IConfiguration configuration)
    {
        var builder = services.AddHealthChecks();

        var connectionString = configuration.GetConnectionString("Platform");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            builder.AddNpgSql(connectionString, name: "postgresql", tags: ["ready", "database"]);
        }

        return builder;
    }
}
