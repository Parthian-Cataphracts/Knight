using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Knight.Application.Abstractions.Tenancy;

namespace Knight.Infrastructure.Persistence;

/// <summary>
/// Enables "dotnet ef migrations" tooling to construct <see cref="PlatformDbContext"/>
/// at design time without running the full application host. The connection string
/// here is used for migration generation only and is overridden at runtime by the
/// Knight.Api composition root — see docs/architecture/repository-structure.md.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PlatformDbContext>();
        var connectionString = Environment.GetEnvironmentVariable("PLATFORM_DB_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=platform;Username=platform;Password=platform";

        optionsBuilder.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "platform"));

        return new PlatformDbContext(optionsBuilder.Options, new DesignTimeTenantContext());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public bool HasTenant => false;
        public bool IsPlatformContext => true;
    }
}
