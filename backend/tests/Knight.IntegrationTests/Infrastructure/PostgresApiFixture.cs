using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Knight.Infrastructure.ControlPlane;
using Testcontainers.PostgreSql;
using Xunit;

namespace Knight.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the full API host against a real PostgreSQL database, migrated.
///
/// The database comes from one of two places. If <c>KNIGHT_TEST_POSTGRES</c>
/// holds a connection string, the fixture creates a uniquely named database on
/// that server and drops it afterwards — this is how the suite runs without a
/// container runtime, against a local PostgreSQL an operator started themselves.
/// Otherwise it falls back to an ephemeral Testcontainers instance, which needs
/// a Docker-compatible daemon.
///
/// If neither is available, <see cref="IsAvailable"/> is false and dependent
/// tests skip themselves rather than fail the whole run, so ordinary local
/// development degrades gracefully. Setting <c>REQUIRE_POSTGRES_TESTS</c> to
/// <c>1</c>/<c>true</c> switches to mandatory mode: the failure is rethrown
/// instead of swallowed, failing the suite loudly. Use this for CI and for
/// explicit security-validation runs, where a silently skipped PostgreSQL suite
/// must never be mistaken for a passing one — see
/// docs/adr/0005-postgresql-integration-testing.md.
/// </summary>
public sealed class PostgresApiFixture : IAsyncLifetime
{
    private const string RequireEnvironmentVariable = "REQUIRE_POSTGRES_TESTS";

    /// <summary>A connection string to a PostgreSQL server the suite may create databases on.</summary>
    private const string ExternalServerEnvironmentVariable = "KNIGHT_TEST_POSTGRES";

    /// <summary>
    /// The host's token signing parameters, exposed so that a test needing a
    /// token the API will actually accept mints it from the same values rather
    /// than from a copy that can quietly drift out of step.
    /// </summary>
    public const string TestSigningKey = "integration-test-signing-key-at-least-32-characters-long";

    public const string TestIssuer = "platform-api";

    public const string TestAudience = "platform-clients";

    private PostgreSqlContainer? _container;
    private string? _externalAdminConnectionString;
    private string? _externalDatabaseName;

    public bool IsAvailable { get; private set; }

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var required = IsTruthy(Environment.GetEnvironmentVariable(RequireEnvironmentVariable));
        string connectionString;

        try
        {
            connectionString = Environment.GetEnvironmentVariable(ExternalServerEnvironmentVariable) is { Length: > 0 } external
                ? await CreateExternalDatabaseAsync(external)
                : await StartContainerAsync();

            IsAvailable = true;
        }
        catch when (!required)
        {
            IsAvailable = false;
            return;
        }
        catch (Exception ex) when (required)
        {
            throw new InvalidOperationException(
                $"{RequireEnvironmentVariable} is set, so PostgreSQL-backed integration tests are mandatory, but no " +
                $"database could be prepared. Either point {ExternalServerEnvironmentVariable} at a running PostgreSQL " +
                "server, or make a Docker-compatible daemon available for Testcontainers. Validation cannot proceed " +
                "with a skipped suite.",
                ex);
        }

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Platform", connectionString);

            // The control plane has its own schema in the same database; naming
            // it explicitly keeps the fixture honest about which context is
            // being pointed where.
            builder.UseSetting("ConnectionStrings:ControlPlane", connectionString);

            // Empty on purpose: the suite runs on the in-process replay guard and
            // memory cache, so it needs no Redis. Pointing at a Redis that is not
            // there would fail every cached path instead of exercising it.
            builder.UseSetting("ConnectionStrings:Redis", string.Empty);

            // The poller would otherwise reach out to whatever a test's store
            // domain happens to resolve to. Tests that need an observation record
            // one directly.
            builder.UseSetting("Stores:Probe:PollingEnabled", "false");
            builder.UseSetting("Jwt:SigningKey", TestSigningKey);
            builder.UseSetting("Jwt:Issuer", TestIssuer);
            builder.UseSetting("Jwt:Audience", TestAudience);

            // Every test in this collection shares one WebApplicationFactory and
            // therefore one apparent client IP (TestServer has no real network
            // identity), so the per-IP auth rate limits would otherwise collapse
            // unrelated tests into one shared bucket. Raise them here; a
            // dedicated low-limit variant is built specifically to test 429
            // behavior — see LoginRateLimitTests.
            builder.UseSetting("RateLimiting:ControlPlaneLoginPermitLimit", "10000");
            builder.UseSetting("RateLimiting:ControlPlanePermitLimit", "1000000");

            // Same reasoning for the two shared non-auth policies: the whole
            // collection looks like a single client to the fixed-window limiters,
            // so the production defaults (100/300 per minute) would otherwise make
            // unrelated tests fail with 429 purely because of suite size.

            // The store handshake partitions by client IP too, and every store in
            // the suite presents the same one. Its production default is a
            // deliberately tight 30 per minute, which the ingestion and
            // observability suites together exceed on suite size alone.
            builder.UseSetting("RateLimiting:IngestHandshakePermitLimit", "1000000");
            builder.UseSetting("RateLimiting:IngestPermitLimit", "1000000");
        });

        // The host does not migrate itself — that is a deployment step — so the
        // control-plane schema and its system roles are brought up here, the
        // same way Knight.Bootstrap does it against a real database.
        await Factory.Services.MigrateAndSeedControlPlaneAsync();
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        await DropExternalDatabaseAsync();
    }

    private async Task<string> StartContainerAsync()
    {
        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("platform_test")
            .WithUsername("platform")
            .WithPassword("platform")
            .Build();

        await _container.StartAsync();
        return _container.GetConnectionString();
    }

    /// <summary>
    /// Creates a database of its own on an operator-supplied server, so a run
    /// never sees another run's rows and never leaves anything behind. The name
    /// carries a random suffix rather than a fixed one so two runs — a developer's
    /// and a watch process, say — cannot collide.
    /// </summary>
    private async Task<string> CreateExternalDatabaseAsync(string serverConnectionString)
    {
        _externalAdminConnectionString = serverConnectionString;
        _externalDatabaseName = $"knight_test_{Guid.NewGuid():n}"[..30];

        await using (var connection = new Npgsql.NpgsqlConnection(serverConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{_externalDatabaseName}\"";
            await command.ExecuteNonQueryAsync();
        }

        return new Npgsql.NpgsqlConnectionStringBuilder(serverConnectionString)
        {
            Database = _externalDatabaseName,
        }.ConnectionString;
    }

    private async Task DropExternalDatabaseAsync()
    {
        if (_externalAdminConnectionString is null || _externalDatabaseName is null)
        {
            return;
        }

        try
        {
            // Pooled connections to the test database outlive the host, and
            // PostgreSQL refuses to drop a database anything is connected to.
            Npgsql.NpgsqlConnection.ClearAllPools();

            await using var connection = new Npgsql.NpgsqlConnection(_externalAdminConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS \"{_externalDatabaseName}\" WITH (FORCE)";
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception exception)
        {
            // A leftover test database is untidy, not a test failure — and
            // throwing here would replace a real result with a cleanup error.
            Console.WriteLine($"Could not drop the test database '{_externalDatabaseName}': {exception.Message}");
        }
    }

    private static bool IsTruthy(string? value) =>
        value is not null && (value.Equals("1", StringComparison.Ordinal) || value.Equals("true", StringComparison.OrdinalIgnoreCase));
}

/// <summary>A seeded tenant plus a ready-to-use bearer token, for catalog tests.</summary>
/// <summary>A seeded tenant plus a ready-to-use bearer token, for ordering tests.</summary>
/// <summary>A seeded tenant plus a ready-to-use bearer token, for customer tests.</summary>
/// <summary>A seeded tenant plus a ready-to-use bearer token, for delivery tests.</summary>
/// <summary>A seeded tenant plus a ready-to-use bearer token, for payment tests.</summary>
/// <summary>A seeded tenant plus a ready-to-use bearer token, for promotions tests.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresApiFixture>
{
    public const string Name = "Postgres";
}
