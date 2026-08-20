using System.Text.Json;
using AccessControl;
using Customers;
using FeatureDelivery;
using FeatureRegistry;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Infrastructure.ControlPlane;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Plans;
using Stores;
using Stores.Domain;
using Subscriptions;

namespace Knight.LoadTest;

/// <summary>
/// Creates the customers, stores and credentials the load run drives traffic
/// through, and writes their secrets to a fixture file.
///
/// It goes through the domain services rather than the HTTP API for the same
/// reason `Knight.Bootstrap` does: there is no registration endpoint, and the
/// dashboard API requires a second factor that a script has no business holding.
/// Writing rows directly into the tables was the other option and is worse — it
/// would seed a shape the application never produces, and the load run would
/// then measure a database the product cannot create.
/// </summary>
internal static class Seeder
{
    /// <summary>
    /// The environment every seeded store is created in. Handshake refuses a
    /// mismatch, so the driver has to report the same one.
    /// </summary>
    public const string Environment = "Staging";

    public static async Task<int> RunAsync(string[] args)
    {
        var count = Arguments.Number(args, "--stores", 25);
        var fixturePath = Arguments.FixturePath(args);

        var connectionString =
            System.Environment.GetEnvironmentVariable("CONTROL_PLANE_DB_CONNECTION_STRING")
            ?? System.Environment.GetEnvironmentVariable("PLATFORM_DB_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("CONTROL_PLANE_DB_CONNECTION_STRING must be set to the target database.");
            return 1;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ControlPlane"] = connectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IDateTimeProvider, LoadTestClock>();

        // The API's IAuditContext reads the HTTP request, and there is no request
        // here. Recorded as System rather than as a fabricated user: these rows
        // were created by a tool, and the audit trail should say so.
        services.AddScoped<IAuditContext, LoadTestAuditContext>();

        // Same story as the audit context: the delivery module expects a signed-in
        // principal from the request pipeline, and this tool has no request.
        services.AddScoped<Knight.Application.Abstractions.Identity.ICurrentUser, LoadTestCurrentUser>();
        services.AddControlPlaneInfrastructure(configuration);

        // AccessControl first: it supplies IAuditTrail, and every mutation in the
        // two modules below writes an audit entry. Seeding through the real
        // services means the audit trail is written too, which is correct — these
        // stores did come into existence.
        services.AddAccessControlModule(configuration);
        services.AddCustomersModule();
        services.AddStoresModule(configuration);

        // Needed to put the fixture customer on a plan. Log shipping is an
        // entitled feature, so without a subscription every /ingest/logs call is
        // correctly refused with 403 and the highest-volume write path in the
        // system goes unmeasured.
        services.AddPlansModule();
        services.AddSubscriptionsModule(configuration);

        // Granting an entitlement publishes a delivery event, so the delivery
        // side has to be composed even though this tool never queues a job.
        services.AddFeatureRegistryModule();
        services.AddFeatureDeliveryModule(configuration);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Seeding has no request and therefore no customer. Without an explicit
        // platform scope the isolation filter fails closed, which is correct
        // behaviour and would make every write here invisible to itself.
        scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

        var customers = scope.ServiceProvider.GetRequiredService<ICustomerManagementService>();
        var stores = scope.ServiceProvider.GetRequiredService<IStoreManagementService>();

        // A run tag keeps repeated seedings from colliding on the unique slug,
        // domain and contact-email indexes.
        var tag = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var customer = await customers.CreateAsync(
            new CreateCustomerInput(
                $"Load test {tag}",
                $"Load test {tag} Ltd",
                $"load-{tag}@loadtest.invalid",
                null,
                "Created by tools/Knight.LoadTest. Safe to delete."),
            CancellationToken.None);

        // A customer is created as a prospect. Handshake refuses every store
        // belonging to one that is not operable, so without this the load run
        // measures nothing but 401s.
        await customers.ActivateAsync(customer.Id, CancellationToken.None);

        Console.WriteLine($"Customer {customer.Id} created and activated.");

        await SubscribeAsync(scope.ServiceProvider, customer.Id);

        var fixtures = new List<StoreFixture>(count);

        for (var i = 0; i < count; i++)
        {
            var slug = $"loadtest-{tag}-{i:D3}";

            var store = await stores.CreateAsync(
                new CreateStoreInput(
                    customer.Id,
                    $"Load test store {i:D3}",
                    slug,
                    $"{slug}.loadtest.invalid",
                    StoreEnvironment.Staging,
                    HostingModel.SharedManaged),
                CancellationToken.None);

            // A store that is not active is refused at the handshake, so the
            // load run would measure nothing but rejections.
            await stores.ActivateAsync(store.Id, CancellationToken.None);

            var credential = await stores.IssueCredentialAsync(store.Id, CancellationToken.None);

            fixtures.Add(new StoreFixture(slug, credential.ClientId, credential.ClientSecret, Environment));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixturePath))!);
        await File.WriteAllTextAsync(
            fixturePath,
            JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"{fixtures.Count} stores seeded.");
        Console.WriteLine($"Credentials written to {fixturePath}");
        Console.WriteLine("That file holds live store secrets. It is under artifacts/, which is gitignored.");
        return 0;
    }

    /// <summary>
    /// Puts the fixture customer on the professional plan with every feature the
    /// plan allows, log shipping among them.
    ///
    /// Not fatal when it fails. A run without the entitlement still measures
    /// heartbeats, events and errors; it just reports 403 for logs, and that is a
    /// more useful outcome than refusing to seed at all.
    /// </summary>
    private static async Task SubscribeAsync(IServiceProvider services, Guid customerId)
    {
        try
        {
            var plans = services.GetRequiredService<IPlanService>();
            var subscriptions = services.GetRequiredService<ISubscriptionService>();

            var plan = (await plans.ListAsync(true, CancellationToken.None))
                .FirstOrDefault(candidate => candidate.Key == "professional");

            if (plan is null)
            {
                Console.WriteLine("No professional plan found; logs will be refused with 403.");
                return;
            }

            // No optional extras. Log shipping is *included* in the professional
            // plan, so the plan alone grants it; the toggleable extras on this
            // plan need dedicated infrastructure that a fixture customer on
            // shared hosting correctly does not have.
            var featureIds = Array.Empty<Guid>();

            // Starts Active already; activating again is refused by the state
            // machine, and rightly so.
            await subscriptions.StartAsync(
                new StartSubscriptionInput(customerId, plan.Id, featureIds, AsTrial: false),
                CancellationToken.None);

            Console.WriteLine($"Subscribed to '{plan.Key}'; log shipping is included in it.");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Could not subscribe the fixture customer ({exception.Message}). Logs will be refused with 403.");
        }
    }

    private sealed class LoadTestClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// A platform principal holding every permission. Safe only because this tool
    /// is run by hand against a development database — it is why the seeder is a
    /// separate program from the API and not a hidden endpoint on it.
    /// </summary>
    private sealed class LoadTestCurrentUser : Knight.Application.Abstractions.Identity.ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId => null;

        public Knight.Application.Abstractions.Identity.PrincipalType? PrincipalType =>
            Knight.Application.Abstractions.Identity.PrincipalType.PlatformAdmin;

        public IReadOnlyCollection<string> Permissions => [];

        public bool HasPermission(string permissionKey) => true;
    }

    private sealed class LoadTestAuditContext : IAuditContext
    {
        public AuditActorType ActorType => AuditActorType.System;

        public Guid? ActorUserId => null;

        public string? ActorDisplay => "tools/Knight.LoadTest";

        public string? CorrelationId => null;

        public string? IpAddress => null;
    }
}
