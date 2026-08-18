using Identity.Abstractions;
using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Knight.Application.Abstractions.Tenancy;
using Knight.Infrastructure.Persistence;
using Knight.Infrastructure.Security;

// Manual, explicit administrative bootstrap for the very first PlatformAdmin.
// Deliberately NOT part of the API host — there is no public registration
// endpoint and there must never be one; see docs/security/README.md. Run this
// once, by hand, against the target database:
//
//   dotnet run --project tools/Knight.Bootstrap -- --email admin@example.com
//
// The password is never accepted as a command-line argument (that would leak
// into shell history) — it is always read interactively, masked, and confirmed.

var email = ParseEmailArgument(args);
if (email is null)
{
    Console.Error.WriteLine("Usage: Knight.Bootstrap [--control-plane] --email <email>");
    return 1;
}

// The control plane has its own schema, its own account model and its own first
// administrator; the legacy path below stays until the store-side modules are
// removed in phase 8.
var isControlPlane = args.Contains("--control-plane", StringComparer.Ordinal);

if (isControlPlane)
{
    var controlPlanePassword = ReadPasswordWithConfirmation();
    if (controlPlanePassword is null)
    {
        Console.Error.WriteLine("Passwords did not match. Aborting.");
        return 1;
    }

    if (controlPlanePassword.Length is < 10 or > 128)
    {
        Console.Error.WriteLine("Password must be between 10 and 128 characters.");
        return 1;
    }

    return await Knight.Bootstrap.ControlPlaneBootstrap.RunAsync(email, controlPlanePassword);
}

var connectionString = Environment.GetEnvironmentVariable("PLATFORM_DB_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("PLATFORM_DB_CONNECTION_STRING must be set to the target database's connection string.");
    return 1;
}

var password = ReadPasswordWithConfirmation();
if (password is null)
{
    Console.Error.WriteLine("Passwords did not match. Aborting.");
    return 1;
}

if (password.Length < 10 || password.Length > 128)
{
    Console.Error.WriteLine("Password must be between 10 and 128 characters — see the platform's PasswordPolicy configuration.");
    return 1;
}

var optionsBuilder = new DbContextOptionsBuilder<PlatformDbContext>();
optionsBuilder.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "platform"));

await using var context = new PlatformDbContext(optionsBuilder.Options, new PlatformOnlyTenantContext());

var normalizedEmail = email.Trim().ToUpperInvariant();
var existing = await context.PlatformAdmins.FirstOrDefaultAsync(a => a.NormalizedEmail == normalizedEmail);
if (existing is not null)
{
    Console.WriteLine($"A platform admin with email '{email}' already exists (Id: {existing.Id}). No changes made.");
    return 0;
}

IPasswordHasher hasher = new Pbkdf2PasswordHasher();
var now = DateTimeOffset.UtcNow;
var admin = PlatformAdmin.Create(Guid.NewGuid(), now, email, hasher.Hash(password), displayName: "Platform Administrator");
admin.Activate(now);

await context.PlatformAdmins.AddAsync(admin);
await context.SaveChangesAsync();

Console.WriteLine($"Platform admin created: {admin.Email} (Id: {admin.Id}).");
return 0;

static string? ParseEmailArgument(string[] arguments)
{
    for (var i = 0; i < arguments.Length - 1; i++)
    {
        if (arguments[i] is "--email" or "-e")
        {
            return arguments[i + 1];
        }
    }

    return null;
}

static string? ReadPasswordWithConfirmation()
{
    var first = ReadHiddenLine("Password: ");
    var second = ReadHiddenLine("Confirm password: ");

    return string.Equals(first, second, StringComparison.Ordinal) ? first : null;
}

static string ReadHiddenLine(string prompt)
{
    Console.Write(prompt);

    // Console.ReadKey requires an interactive console; fall back to a plain
    // (unmasked) read when input is redirected — e.g. scripted/CI usage. The
    // masked interactive path remains the norm for a human running this by hand.
    if (Console.IsInputRedirected)
    {
        return Console.ReadLine() ?? string.Empty;
    }

    var buffer = new System.Text.StringBuilder();

    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            break;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (buffer.Length > 0)
            {
                buffer.Length--;
            }

            continue;
        }

        if (!char.IsControl(key.KeyChar))
        {
            buffer.Append(key.KeyChar);
        }
    }

    return buffer.ToString();
}

/// <summary>Bootstrapping never operates within a tenant — it always writes platform-level data.</summary>
internal sealed class PlatformOnlyTenantContext : ITenantContext
{
    public Guid? TenantId => null;
    public bool HasTenant => false;
    public bool IsPlatformContext => true;
}
