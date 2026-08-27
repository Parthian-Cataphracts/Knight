using System.Net.Http.Headers;
using System.Net.Http.Json;
using AccessControl.Abstractions;
using AccessControl.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Infrastructure.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stores.Domain;
using ControlPlaneCustomer = Customers.Domain.Customer;

namespace Knight.IntegrationTests.Infrastructure;

/// <summary>
/// Seeding and sign-in helpers for the control-plane suites. Seeding writes
/// directly through the context in platform scope: these are arrangements, and
/// making them go through the API would make every test depend on the endpoints
/// it is not testing.
/// </summary>
public static class ControlPlaneFixtureExtensions
{
    public static async Task<TResult> WithControlPlaneScopeAsync<TResult>(
        this PostgresApiFixture fixture,
        Func<ControlPlaneDbContext, IServiceProvider, Task<TResult>> action,
        Guid? customerId = null)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>();

        if (customerId is { } value)
        {
            accessor.SetCustomer(value);
        }
        else
        {
            accessor.SetPlatformScope();
        }

        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        return await action(context, scope.ServiceProvider);
    }

    public static Task WithControlPlaneScopeAsync(
        this PostgresApiFixture fixture,
        Func<ControlPlaneDbContext, IServiceProvider, Task> action,
        Guid? customerId = null) =>
        fixture.WithControlPlaneScopeAsync(async (context, sp) =>
        {
            await action(context, sp);
            return true;
        }, customerId);

    /// <summary>Creates an active customer.</summary>
    public static async Task<Guid> SeedCustomerAsync(this PostgresApiFixture fixture, string? contactEmail = null)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var email = contactEmail ?? $"owner-{Guid.NewGuid():n}@example.test";

        await fixture.WithControlPlaneScopeAsync(async (context, _) =>
        {
            var customer = ControlPlaneCustomer.Create(id, now, $"Customer {id:n}"[..20], email);
            customer.Activate(now);
            await context.Customers.AddAsync(customer);
            await context.SaveChangesAsync();
        });

        return id;
    }

    /// <summary>Creates an active store for a customer, in Production on shared hosting unless told otherwise.</summary>
    public static async Task<Guid> SeedStoreAsync(
        this PostgresApiFixture fixture,
        Guid customerId,
        StoreEnvironment environment = StoreEnvironment.Production,
        HostingModel hostingModel = HostingModel.SharedManaged)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("n")[..8];

        await fixture.WithControlPlaneScopeAsync(async (context, _) =>
        {
            var store = Store.Create(
                id,
                now,
                customerId,
                $"Store {suffix}",
                $"store-{suffix}",
                $"{suffix}.example.test",
                environment,
                hostingModel);

            store.Activate(now);
            await context.Stores.AddAsync(store);
            await context.SaveChangesAsync();
        });

        return id;
    }

    /// <summary>
    /// Records the heartbeat a connected store sends, so the store can be
    /// planned against.
    ///
    /// A store with no health check has reported no runtime, and since phase 20
    /// a store that has not said which runtime it runs cannot be planned against
    /// at all — which is deliberate, and which makes this the difference between
    /// a fixture that models a connected store and one that models a store
    /// nobody has ever heard from.
    /// </summary>
    public static async Task SeedHeartbeatAsync(
        this PostgresApiFixture fixture,
        Guid storeId,
        string runtime = "django",
        string? python = "3.12.10",
        string? django = "5.1.15",
        string? node = null,
        string database = "postgresql",
        string storeVersion = "1.0.0")
    {
        var block = new Dictionary<string, string?>
        {
            ["name"] = runtime,
            ["python"] = python,
            ["django"] = django,
            ["node"] = node,
            ["database"] = database,
        };

        var dependencies = System.Text.Json.JsonSerializer.Serialize(new
        {
            runtime = block.Where(entry => entry.Value is not null).ToDictionary(entry => entry.Key, entry => entry.Value),
        });

        await fixture.WithControlPlaneScopeAsync(async (context, _) =>
        {
            var customerId = await context.Stores
                .Where(store => store.Id == storeId)
                .Select(store => store.CustomerId)
                .FirstAsync();

            context.StoreHealthChecks.Add(StoreHealthCheck.Record(
                Guid.NewGuid(),
                storeId,
                customerId,
                DateTimeOffset.UtcNow,
                StoreHealthStatus.Healthy,
                HealthCheckSource.Heartbeat,
                responseTimeMs: 5,
                reportedVersion: storeVersion,
                dependencies: dependencies));

            await context.SaveChangesAsync();
        });
    }

    /// <summary>
    /// Creates an active account holding one seeded system role. MFA is enrolled
    /// and confirmed up front when the role requires it, so tests that are not
    /// about MFA do not have to walk the enrolment flow.
    /// </summary>
    public static async Task<Guid> SeedUserAsync(
        this PostgresApiFixture fixture,
        string email,
        string password,
        string roleName,
        Guid? customerId = null,
        bool enrolMfa = true)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await fixture.WithControlPlaneScopeAsync(async (context, sp) =>
        {
            var hasher = sp.GetRequiredService<IControlPlanePasswordHasher>();
            var totp = sp.GetRequiredService<ITotpService>();

            var user = customerId is { } value
                ? ControlPlaneUser.CreateCustomerUser(id, now, value, email, "Test User", hasher.Hash(password))
                : ControlPlaneUser.CreatePlatformStaff(id, now, email, "Test User", hasher.Hash(password));

            user.Activate(now);

            var role = await context.Roles
                .IgnoreQueryFilters()
                .FirstAsync(r => r.NormalizedName == roleName.ToUpperInvariant());

            var assignment = user.AssignRole(Guid.NewGuid(), role, now);

            if (enrolMfa && SystemRoles.RequiresMfa([roleName]))
            {
                user.BeginMfaEnrollment(totp.GenerateSecret(), now);
                user.ConfirmMfa(now);
            }

            await context.Users.AddAsync(user);
            context.Entry(assignment).State = EntityState.Added;
            await context.SaveChangesAsync();
        });

        return id;
    }

    public static async Task<string?> ReadMfaSecretAsync(this PostgresApiFixture fixture, Guid userId) =>
        await fixture.WithControlPlaneScopeAsync(async (context, _) =>
            await context.Users.IgnoreQueryFilters().Where(u => u.Id == userId).Select(u => u.MfaSecret).FirstAsync());

    /// <summary>Signs in and returns the access token, supplying a current TOTP code when the account has MFA enabled.</summary>
    public static async Task<string> SignInAsync(this PostgresApiFixture fixture, string email, string password)
    {
        var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var body = await response.Content.ReadFromJsonAsync<LoginPayload>();

        if (body?.Status == "mfa_required")
        {
            var userId = await fixture.WithControlPlaneScopeAsync(async (context, _) =>
                await context.Users.IgnoreQueryFilters()
                    .Where(u => u.NormalizedEmail == email.ToUpperInvariant())
                    .Select(u => u.Id)
                    .FirstAsync());

            var code = await fixture.CurrentCodeAsync(userId);
            response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password, mfaCode = code });
            body = await response.Content.ReadFromJsonAsync<LoginPayload>();
        }

        return body?.AccessToken
            ?? throw new InvalidOperationException($"Sign-in for '{email}' produced no access token ({response.StatusCode}).");
    }

    /// <summary>
    /// Produces the code the account's authenticator would currently show.
    /// Computed here from RFC 6238 directly rather than borrowed from the
    /// service under test, so a broken production implementation cannot make its
    /// own tests pass; the unit suite pins that implementation to the standard's
    /// published vectors.
    /// </summary>
    public static async Task<string> CurrentCodeAsync(this PostgresApiFixture fixture, Guid userId)
    {
        var secret = await fixture.ReadMfaSecretAsync(userId)
            ?? throw new InvalidOperationException("The account has no MFA secret.");

        var key = Base32Decode(secret);
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        var hash = System.Security.Cryptography.HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string value)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var bits = 0;
        var accumulator = 0;
        var output = new List<byte>();

        foreach (var character in value.TrimEnd('=').ToUpperInvariant())
        {
            accumulator = (accumulator << 5) | Alphabet.IndexOf(character);
            bits += 5;

            if (bits < 8)
            {
                continue;
            }

            bits -= 8;
            output.Add((byte)((accumulator >> bits) & 0xFF));
        }

        return [.. output];
    }

    public static HttpClient CreateClient(this PostgresApiFixture fixture, string accessToken)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private sealed record LoginPayload(string Status, string? AccessToken, string? RefreshToken);
}
