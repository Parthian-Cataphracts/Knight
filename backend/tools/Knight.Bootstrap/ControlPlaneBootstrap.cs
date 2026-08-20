using AccessControl;
using AccessControl.Abstractions;
using AccessControl.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Infrastructure.ControlPlane;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Knight.Bootstrap;

/// <summary>
/// Brings a control-plane database up: applies its migrations, reconciles the
/// system roles, and creates the first SuperAdmin.
///
/// Run by hand, once, against the target database. There is no registration
/// endpoint, the API host does not migrate itself, and no credential is ever
/// read from configuration — the password is typed in, masked
/// (docs/security/README.md).
///
///   dotnet run --project tools/Knight.Bootstrap -- --control-plane --email admin@example.com
///
/// The account is created without a second factor. It holds SuperAdmin, so its
/// first sign-in can do nothing but enrol one.
/// </summary>
internal static class ControlPlaneBootstrap
{
    /// <summary>
    /// Applies the migrations and seeds the system roles and commercial
    /// catalogue, without creating an account and without prompting.
    ///
    /// This is the step a deployment runs (docs/deployment.md section 7): the API
    /// host deliberately does not migrate itself, because a rolling deploy of
    /// several instances would then race to apply the same migration. It is also
    /// what CI runs before the restore drill, so the drill exercises a database
    /// with the seeded catalogue in it rather than an empty schema.
    ///
    /// Idempotent, because it will be run on every deploy and most of those
    /// deploys will have nothing to apply.
    /// </summary>
    public static async Task<int> MigrateOnlyAsync()
    {
        var provider = BuildProvider(out var failure);
        if (provider is null)
        {
            Console.Error.WriteLine(failure);
            return 1;
        }

        await using (provider)
        {
            await provider.MigrateAndSeedControlPlaneAsync();
        }

        Console.WriteLine("Control-plane migrations applied and seed data reconciled.");
        return 0;
    }

    public static async Task<int> RunAsync(string email, string password)
    {
        var provider = BuildProvider(out var failure);
        if (provider is null)
        {
            Console.Error.WriteLine(failure);
            return 1;
        }

        await using var _ = provider;

        await provider.MigrateAndSeedControlPlaneAsync();

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

        var users = scope.ServiceProvider.GetRequiredService<IControlPlaneUserRepository>();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        var hasher = scope.ServiceProvider.GetRequiredService<IControlPlanePasswordHasher>();

        var normalizedEmail = EmailAddress.NormalizeForComparison(email);
        if (await users.ExistsWithEmailAsync(normalizedEmail, CancellationToken.None))
        {
            Console.WriteLine($"A control-plane account with email '{email}' already exists. No changes made.");
            return 0;
        }

        var superAdmin = await roles.GetByNameAsync(
            SystemRoles.SuperAdmin.ToUpperInvariant(),
            RoleScope.Platform,
            customerId: null,
            CancellationToken.None);

        if (superAdmin is null)
        {
            Console.Error.WriteLine("The SuperAdmin role is missing after seeding. Aborting.");
            return 1;
        }

        var now = DateTimeOffset.UtcNow;
        var user = ControlPlaneUser.CreatePlatformStaff(
            Guid.NewGuid(),
            now,
            email,
            "Platform Administrator",
            hasher.Hash(password));

        user.Activate(now);
        var assignment = user.AssignRole(Guid.NewGuid(), superAdmin, now);

        await users.AddAsync(user, CancellationToken.None);
        users.RegisterNewAssignment(assignment);
        await users.SaveChangesAsync(CancellationToken.None);

        Console.WriteLine($"Control-plane SuperAdmin created: {user.Email} (Id: {user.Id}).");
        Console.WriteLine("It must enrol MFA on first sign-in before it can do anything else.");
        return 0;
    }

    /// <summary>
    /// The service provider both entry points need. Returns null with a reason
    /// rather than throwing, because the one likely failure — an unset
    /// connection string — deserves a sentence an operator can act on and not a
    /// stack trace.
    /// </summary>
    private static ServiceProvider? BuildProvider(out string failure)
    {
        failure = string.Empty;

        var connectionString =
            Environment.GetEnvironmentVariable("CONTROL_PLANE_DB_CONNECTION_STRING")
            ?? Environment.GetEnvironmentVariable("PLATFORM_DB_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            failure = "CONTROL_PLANE_DB_CONNECTION_STRING (or PLATFORM_DB_CONNECTION_STRING) must be set to the target database.";
            return null;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ControlPlane"] = connectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // The catalogue seeder reads Catalogue:SeedPath, so the container needs
        // the configuration itself, not just the values already read from it.
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IDateTimeProvider, BootstrapClock>();
        services.AddControlPlaneInfrastructure(configuration);
        services.AddAccessControlModule(configuration);

        return services.BuildServiceProvider();
    }

    private sealed class BootstrapClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
