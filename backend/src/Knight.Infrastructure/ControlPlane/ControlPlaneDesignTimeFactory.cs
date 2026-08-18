using Knight.Application.Abstractions.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Knight.Infrastructure.ControlPlane;

/// <summary>
/// Lets "dotnet ef migrations" build the control-plane context without the
/// application host. The connection string here is for migration generation
/// only; the composition root supplies the real one at runtime.
/// </summary>
public sealed class ControlPlaneDesignTimeFactory : IDesignTimeDbContextFactory<ControlPlaneDbContext>
{
    public ControlPlaneDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("CONTROL_PLANE_DB_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=knight;Username=platform;Password=platform";

        var optionsBuilder = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", ControlPlaneDbContext.SchemaName));

        // Migration generation needs the model, not a principal; platform scope
        // keeps the filters from narrowing anything during model building.
        var scope = new CustomerScopeAccessor();
        scope.SetPlatformScope();

        return new ControlPlaneDbContext(optionsBuilder.Options, scope);
    }
}
