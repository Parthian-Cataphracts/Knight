using AccessControl;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Knight.Infrastructure.ControlPlane;

/// <summary>
/// Deployment-time work for the control plane: bring the schema up to date,
/// reconcile the system roles, and load the commercial catalogue.
///
/// Deliberately not called from the API host. A running host that migrates its
/// own database turns every restart into a schema change and fails to start at
/// all when the database is briefly unreachable; migration is a deployment step,
/// and Knight.Bootstrap is where it is invoked by hand
/// (docs/deployment.md).
/// </summary>
public static class ControlPlaneStartup
{
    public static async Task MigrateAndSeedControlPlaneAsync(this IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        using var scope = serviceProvider.CreateScope();

        // Seeding has no request and therefore no customer. Without an explicit
        // platform scope the isolation filter would, quite correctly, hide every
        // existing row from the seeder and it would try to insert duplicates.
        scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        await context.Database.MigrateAsync(cancellationToken);

        var access = scope.ServiceProvider.GetRequiredService<IControlPlaneAccessSeeder>();
        await access.SeedAsync(cancellationToken);

        var catalogue = scope.ServiceProvider.GetRequiredService<Seed.ICommercialCatalogueSeeder>();
        await catalogue.SeedAsync(cancellationToken);
    }
}
