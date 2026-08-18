using AccessControl;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Knight.Infrastructure.ControlPlane;

/// <summary>
/// Startup work for the control plane: bring the schema up to date, then seed
/// the system roles and — on a database with no platform account at all — the
/// bootstrap administrator.
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

        var seeder = scope.ServiceProvider.GetRequiredService<IControlPlaneAccessSeeder>();
        await seeder.SeedAsync(cancellationToken);
    }
}
