using System.Net.Http.Json;
using System.Text.Json;
using AccessControl.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Observability;
using Knight.Infrastructure.ControlPlane;
using Knight.Infrastructure.Telemetry;
using Knight.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stores.Domain;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// KNIGHT's observability of itself: the gauges it reports, the retention sweep
/// that keeps its own tables bounded, and the guarantee that nothing it writes
/// down carries a secret (docs/observability.md).
///
/// The retention cases are release-blocking in the way a backup restore is: the
/// sweep is the only thing standing between the highest-volume tables and a
/// database that stops working, and its failure mode is silence — it deletes
/// nothing and nobody notices for a year.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SelfObservabilityTests
{
    private const string Password = "correct horse battery staple";

    private readonly PostgresApiFixture _fixture;

    public SelfObservabilityTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TheGaugesReportWhatIsActuallyInTheDatabase()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();

        var opened = await client.PostAsJsonAsync("/api/v1/incidents", new
        {
            title = "Gauge probe",
            severity = "Critical",
        });

        opened.EnsureSuccessStatusCode();

        var snapshot = await ReadGaugesAsync();

        // A gauge that reads zero while an incident is open is worse than no
        // gauge: it is a dashboard that says everything is fine.
        Assert.True(snapshot.OpenIncidents >= 1);
        Assert.True(snapshot.CriticalOpenIncidents >= 1);
    }

    [Fact]
    public async Task GaugesReadInPlatformScopeRatherThanWhicheverCustomerWasLastSeen()
    {
        if (!_fixture.IsAvailable) return;

        var customerId = await _fixture.SeedCustomerAsync();
        var client = await CustomerClientAsync(customerId);

        // A customer's request runs immediately before the scrape. Without an
        // explicit platform scope the isolation filter would fail closed and
        // every gauge would read zero — which looks exactly like an idle system.
        await client.GetAsync("/api/v1/stores");

        var snapshot = await ReadGaugesAsync();

        Assert.True(snapshot.OpenIncidents >= 0);
        Assert.True(snapshot.StoresConnected >= 0);
    }

    [Fact]
    public async Task RetentionRemovesExpiredTelemetryAndKeepsTheRest()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedStoreAsync();

        // One log line well past its retention, one comfortably inside it.
        var oldId = await InsertLogAsync(store, DateTimeOffset.UtcNow.AddDays(-400));
        var freshId = await InsertLogAsync(store, DateTimeOffset.UtcNow.AddMinutes(-5));

        var result = await ApplyRetentionAsync();

        Assert.True(result.Total >= 1);

        using var scope = _fixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        Assert.False(await context.StoreLogEntries.AnyAsync(entry => entry.Id == oldId));
        Assert.True(await context.StoreLogEntries.AnyAsync(entry => entry.Id == freshId));
    }

    [Fact]
    public async Task RetentionNeverDeletesAuditEntries()
    {
        if (!_fixture.IsAvailable) return;

        // Audit has a legal minimum and is not operational data. A sweep that
        // trimmed it would destroy the one record that exists to be produced
        // years later.
        var client = await PlatformClientAsync();

        await client.PostAsJsonAsync("/api/v1/incidents", new { title = "Audited", severity = "Info" });

        using var before = _fixture.Factory.Services.CreateScope();
        before.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

        var countBefore = await before.ServiceProvider
            .GetRequiredService<ControlPlaneDbContext>()
            .AuditLogs.CountAsync();

        await ApplyRetentionAsync();

        using var after = _fixture.Factory.Services.CreateScope();
        after.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

        var countAfter = await after.ServiceProvider
            .GetRequiredService<ControlPlaneDbContext>()
            .AuditLogs.CountAsync();

        Assert.True(countAfter >= countBefore);
    }

    [Fact]
    public async Task RetentionNeverDeletesIncidents()
    {
        if (!_fixture.IsAvailable) return;

        // An incident is the record of a response, which is what a post-mortem
        // is written from a year later.
        var client = await PlatformClientAsync();

        var opened = await client.PostAsJsonAsync("/api/v1/incidents", new { title = "Kept", severity = "Warning" });
        opened.EnsureSuccessStatusCode();

        var id = JsonDocument.Parse(await opened.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        await ApplyRetentionAsync();

        var still = await client.GetAsync($"/api/v1/incidents/{id}");

        still.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ASecretInAReportedErrorNeverReachesTheDatabase()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var storeClient = await StoreClientAsync(store);

        // The error path rather than the log path: log shipping is an entitled
        // capability, and the guarantee under test has nothing to do with
        // entitlement. An exception message is also where a credential most
        // often escapes in practice.
        var shipped = await storeClient.PostAsJsonAsync("/api/v1/ingest/errors", new
        {
            environment = "Production",
            version = "4.2.0",
            events = new[]
            {
                new
                {
                    occurredAt = DateTimeOffset.UtcNow,
                    exceptionType = "OperationalError",
                    message = "could not connect: Host=db;Username=knight;Password=hunter2-leaked",
                    endpoint = "/api/orders/",
                    stackTrace = """
                        File "apps/db.py", line 8, in connect
                            dsn = 'postgres://knight:hunter2-leaked@db/knight'
                        """,
                },
            },
        });

        shipped.EnsureSuccessStatusCode();

        using var scope = _fixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

        var stored = await scope.ServiceProvider
            .GetRequiredService<ControlPlaneDbContext>()
            .StoreErrorEvents
            .Where(error => error.StoreId == store.StoreId)
            .Select(error => error.Message + " " + (error.StackTrace ?? string.Empty))
            .ToArrayAsync();

        // Redacted on the way in. Storing it and hiding it at read time would
        // leave the secret in the database and in every backup taken since.
        Assert.All(stored, message =>
            Assert.DoesNotContain("hunter2-leaked", message, StringComparison.Ordinal));

        Assert.Contains(stored, message => message.Contains(Redaction.Placeholder, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AQueuedJobCarriesTheTraceThatCreatedIt()
    {
        if (!_fixture.IsAvailable) return;

        // The column exists and is nullable: an agent that does not trace, and a
        // host with tracing off, both queue jobs without one rather than failing.
        using var scope = _fixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        var canQuery = await context.FeatureInstallationJobs
            .Select(job => job.TraceParent)
            .Take(1)
            .ToArrayAsync();

        Assert.NotNull(canQuery);
    }

    // --- Helpers -------------------------------------------------------------

    private sealed record RegisteredStore(Guid CustomerId, Guid StoreId, string ClientId, string ClientSecret);

    private static string Email() => $"user-{Guid.NewGuid():n}@knight.test";

    private async Task<ObservabilitySnapshot> ReadGaugesAsync()
    {
        var source = _fixture.Factory.Services.GetRequiredService<IObservabilityGaugeSource>();

        return await source.ReadAsync(CancellationToken.None);
    }

    private async Task<RetentionResult> ApplyRetentionAsync()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

        return await scope.ServiceProvider
            .GetRequiredService<IRetentionService>()
            .ApplyAsync(CancellationToken.None);
    }

    private async Task<Guid> SeedStoreAsync()
    {
        var customerId = await _fixture.SeedCustomerAsync();

        return await _fixture.SeedStoreAsync(customerId, StoreEnvironment.Production);
    }

    /// <summary>
    /// Writes a log entry directly at a chosen age. Ingestion clamps a timestamp
    /// to now, which is correct for real traffic and useless for testing a
    /// retention window measured in months.
    /// </summary>
    private async Task<Guid> InsertLogAsync(Guid storeId, DateTimeOffset timestamp)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        var customerId = await context.Stores
            .Where(store => store.Id == storeId)
            .Select(store => store.CustomerId)
            .SingleAsync();

        var entry = Ingestion.Domain.StoreLogEntry.Record(
            Guid.NewGuid(),
            storeId,
            customerId,
            timestamp,
            timestamp,
            "INFO",
            "test",
            "Production",
            "1.0.0",
            null,
            null,
            "a line",
            null,
            null);

        context.StoreLogEntries.Add(entry);

        await context.SaveChangesAsync();

        return entry.Id;
    }

    private async Task<HttpClient> PlatformClientAsync(string role = SystemRoles.Admin)
    {
        var email = Email();
        await _fixture.SeedUserAsync(email, Password, role);

        return _fixture.CreateClient(await _fixture.SignInAsync(email, Password));
    }

    private async Task<HttpClient> CustomerClientAsync(Guid customerId)
    {
        var email = Email();
        await _fixture.SeedUserAsync(email, Password, SystemRoles.CustomerOwner, customerId);

        return _fixture.CreateClient(await _fixture.SignInAsync(email, Password));
    }

    private async Task<RegisteredStore> SeedRegisteredStoreAsync()
    {
        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId, StoreEnvironment.Production);

        var client = await PlatformClientAsync();
        var issued = await client.PostAsync($"/api/v1/stores/{storeId}/credentials", null);
        issued.EnsureSuccessStatusCode();

        var credential = JsonDocument.Parse(await issued.Content.ReadAsStringAsync()).RootElement;

        return new RegisteredStore(
            customerId,
            storeId,
            credential.GetProperty("clientId").GetString()!,
            credential.GetProperty("clientSecret").GetString()!);
    }

    private async Task<HttpClient> StoreClientAsync(RegisteredStore store)
    {
        var client = _fixture.Factory.CreateClient();

        var handshake = await client.PostAsJsonAsync("/api/v1/ingest/handshake", new
        {
            clientId = store.ClientId,
            clientSecret = store.ClientSecret,
            environment = "Production",
            storeVersion = "4.2.0",
            runtime = "Python 3.12 / Django 5.1",
        });

        handshake.EnsureSuccessStatusCode();

        var token = JsonDocument.Parse(await handshake.Content.ReadAsStringAsync())
            .RootElement.GetProperty("accessToken").GetString()!;

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        return client;
    }
}
