using System.Net;
using System.Net.Http.Json;
using AccessControl.Domain;
using Knight.IntegrationTests.Infrastructure;
using Stores.Domain;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// Phase 9 end to end: a provisioning run that sits on the steps it genuinely
/// cannot finish, a deprovisioning run that will not purge before its retention
/// window closes, and the two permissions that keep the destructive path away
/// from everyday store management.
///
/// The tests assert on *where a run stops*, not just that it started. A
/// provisioning implementation that quietly marched to Active without an agent,
/// a handshake or a health check would pass a weaker suite and would be the
/// exact failure this phase exists to prevent.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProvisioningTests
{
    private const string Password = "correct horse battery staple";

    private readonly PostgresApiFixture _fixture;

    public ProvisioningTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private static string Email() => $"user-{Guid.NewGuid():n}@knight.test";

    private async Task<HttpClient> ClientAsync(string role = SystemRoles.SuperAdmin, Guid? customerId = null)
    {
        var email = Email();
        await _fixture.SeedUserAsync(email, Password, role, customerId);
        return _fixture.CreateClient(await _fixture.SignInAsync(email, Password));
    }

    [Fact]
    public async Task AProvisioningRun_StopsOnTheFirstStepNobodyHasDoneYet()
    {
        if (!_fixture.IsAvailable) return;

        var client = await ClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);

        var response = await client.PostAsJsonAsync($"/api/v1/provisioning/stores/{storeId}", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var job = await response.Content.ReadFromJsonAsync<JobBody>();

        // No machine has been recorded for the store, so the run waits for a
        // person rather than inventing progress.
        Assert.Equal("Provision", job!.Kind);
        Assert.Equal("AwaitingOperator", job.State);
        Assert.Equal("server", job.CurrentStep);
        Assert.True(job.AwaitingOperator);

        var server = job.Steps.Single(step => step.Name == "server");
        Assert.Equal("Manual", server.Mode);
        Assert.False(string.IsNullOrWhiteSpace(server.Detail));
    }

    [Fact]
    public async Task StartingTheSameRunTwice_ReturnsTheRunThatAlreadyExists()
    {
        if (!_fixture.IsAvailable) return;

        var client = await ClientAsync();
        var storeId = await _fixture.SeedStoreAsync(await _fixture.SeedCustomerAsync());

        var first = await (await client.PostAsJsonAsync($"/api/v1/provisioning/stores/{storeId}", new { }))
            .Content.ReadFromJsonAsync<JobBody>();

        var second = await (await client.PostAsJsonAsync($"/api/v1/provisioning/stores/{storeId}", new { }))
            .Content.ReadFromJsonAsync<JobBody>();

        Assert.Equal(first!.Id, second!.Id);
    }

    [Fact]
    public async Task AnOperatorCannotTickOffAStepKnightChecksItself()
    {
        if (!_fixture.IsAvailable) return;

        var client = await ClientAsync();
        var storeId = await _fixture.SeedStoreAsync(await _fixture.SeedCustomerAsync());

        var job = await (await client.PostAsJsonAsync($"/api/v1/provisioning/stores/{storeId}", new { }))
            .Content.ReadFromJsonAsync<JobBody>();

        var refused = await client.PostAsJsonAsync(
            $"/api/v1/provisioning/{job!.Id}/steps",
            new { step = "healthcheck", detail = "It looks fine to me." });

        // A store that never passed a health check must never reach Active, and
        // an operator's confidence is not a health check.
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task TheMachineStepCannotBeTickedOffWhileNoMachineIsRecorded()
    {
        if (!_fixture.IsAvailable) return;

        var client = await ClientAsync();
        var storeId = await _fixture.SeedStoreAsync(await _fixture.SeedCustomerAsync());

        var job = await (await client.PostAsJsonAsync($"/api/v1/provisioning/stores/{storeId}", new { }))
            .Content.ReadFromJsonAsync<JobBody>();

        // An operator asserting "the box exists" while no server is recorded
        // leaves a run that walks on and then stalls at the agent step for a
        // reason nobody can act on.
        var refused = await client.PostAsJsonAsync(
            $"/api/v1/provisioning/{job!.Id}/steps",
            new { step = "server", detail = "Trust me." });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task CompletingTheManualSteps_MovesTheRunOnToWhatKnightIsWaitingFor()
    {
        if (!_fixture.IsAvailable) return;

        var client = await ClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId, hostingModel: HostingModel.DedicatedManaged);

        var server = await (await client.PostAsJsonAsync("/api/v1/servers", new
        {
            name = $"dedicated-{Guid.NewGuid():n}"[..20],
            hostingModel = "DedicatedManaged",
            environment = "Production",
        })).Content.ReadFromJsonAsync<ServerBody>();

        await client.PutAsJsonAsync($"/api/v1/servers/{server!.Id}/dedication", new { customerId });

        await client.PutAsJsonAsync($"/api/v1/stores/{storeId}/server", new { serverId = server.Id });

        var job = await (await client.PostAsJsonAsync($"/api/v1/provisioning/stores/{storeId}", new { }))
            .Content.ReadFromJsonAsync<JobBody>();

        var advanced = await (await client.PostAsJsonAsync(
                $"/api/v1/provisioning/{job!.Id}/steps",
                new { step = "server", detail = "Recorded by hand on rack 3." }))
            .Content.ReadFromJsonAsync<JobBody>();

        // The instance step is the next manual one, and it is still outstanding:
        // the store has never handshaked, so KNIGHT has no evidence it exists.
        Assert.Equal("instance", advanced!.CurrentStep);
        Assert.Equal("Succeeded", advanced.Steps.Single(step => step.Name == "server").Status);
    }

    [Fact]
    public async Task AStoreCannotBePlacedOnAnotherCustomersDedicatedMachine()
    {
        if (!_fixture.IsAvailable) return;

        var client = await ClientAsync();
        var mine = await _fixture.SeedCustomerAsync();
        var theirs = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(mine, hostingModel: HostingModel.DedicatedManaged);

        var server = await (await client.PostAsJsonAsync("/api/v1/servers", new
        {
            name = $"theirs-{Guid.NewGuid():n}"[..20],
            hostingModel = "DedicatedManaged",
            environment = "Production",
        })).Content.ReadFromJsonAsync<ServerBody>();

        await client.PutAsJsonAsync($"/api/v1/servers/{server!.Id}/dedication", new { customerId = theirs });

        var refused = await client.PutAsJsonAsync($"/api/v1/stores/{storeId}/server", new { serverId = server.Id });

        // Dedicated is a promise somebody pays for, not a billing label.
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task NamingAnUnpublishedBaseImage_IsRefused()
    {
        if (!_fixture.IsAvailable) return;

        var client = await ClientAsync();
        var storeId = await _fixture.SeedStoreAsync(await _fixture.SeedCustomerAsync());

        var job = await (await client.PostAsJsonAsync($"/api/v1/provisioning/stores/{storeId}", new { }))
            .Content.ReadFromJsonAsync<JobBody>();

        await client.PostAsJsonAsync($"/api/v1/provisioning/{job!.Id}/steps", new { step = "server" });

        var refused = await client.PostAsJsonAsync(
            $"/api/v1/provisioning/{job.Id}/steps",
            new { step = "instance", baseImageVersion = "9.9.9" });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task ADeprovisioningRun_RevokesAccessAndThenWaitsOutTheRetentionWindow()
    {
        if (!_fixture.IsAvailable) return;

        var client = await ClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);

        await client.PostAsync($"/api/v1/stores/{storeId}/credentials", null);

        var response = await client.PostAsJsonAsync($"/api/v1/provisioning/stores/{storeId}/deprovision", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var job = await response.Content.ReadFromJsonAsync<JobBody>();

        Assert.Equal("Deprovision", job!.Kind);
        Assert.NotNull(job.RetainUntil);

        // Access is gone immediately; the data is not. A retention promise that
        // deletes on the way out is not a retention promise.
        Assert.Equal("retain", job.CurrentStep);
        Assert.Equal("Succeeded", job.Steps.Single(step => step.Name == "revoke-access").Status);
        Assert.Equal("Succeeded", job.Steps.Single(step => step.Name == "stop-ingestion").Status);

        var store = await (await client.GetAsync($"/api/v1/stores/{storeId}")).Content.ReadFromJsonAsync<StoreBody>();
        Assert.Equal("Archived", store!.Status);
        Assert.All(store.Credentials, credential => Assert.Equal("Revoked", credential.State));
    }

    [Fact]
    public async Task PurgingNow_WaivesTheRetentionWindowAndLetsTheRunProceed()
    {
        if (!_fixture.IsAvailable) return;

        var client = await ClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);

        await client.PostAsync($"/api/v1/stores/{storeId}/credentials", null);

        var started = await (await client.PostAsJsonAsync($"/api/v1/provisioning/stores/{storeId}/deprovision", new { }))
            .Content.ReadFromJsonAsync<JobBody>();

        // It is sitting on retain, waiting out the contractual window.
        Assert.Equal("retain", started!.CurrentStep);

        // The customer asks to be purged immediately; an operator honours it.
        var response = await client.PostAsync($"/api/v1/provisioning/{started.Id}/purge-now", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var job = await response.Content.ReadFromJsonAsync<JobBody>();

        // The wait is gone: retain has passed and the run moved on to the export
        // and purge rather than parking on the window.
        Assert.Equal("Succeeded", job!.Steps.Single(step => step.Name == "retain").Status);
        Assert.NotEqual("retain", job.CurrentStep);
        Assert.True(job.RetainUntil <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task PurgingNow_NeedsTheDeprovisionPermission()
    {
        if (!_fixture.IsAvailable) return;

        var platform = await ClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);

        var job = await (await platform.PostAsJsonAsync($"/api/v1/provisioning/stores/{storeId}/deprovision", new { }))
            .Content.ReadFromJsonAsync<JobBody>();

        // An Admin may run the platform but not the path that ends in deleted
        // data — waiving the window is on that path, so it is refused too.
        var admin = await ClientAsync(SystemRoles.Admin);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await admin.PostAsync($"/api/v1/provisioning/{job!.Id}/purge-now", null)).StatusCode);
    }

    [Fact]
    public async Task ACustomerRetentionOverride_DecidesHowLongTheirDataIsKept()
    {
        if (!_fixture.IsAvailable) return;

        var client = await ClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();

        await client.PutAsJsonAsync($"/api/v1/customers/{customerId}/retention", new { days = 1 });

        var storeId = await _fixture.SeedStoreAsync(customerId);
        var job = await (await client.PostAsJsonAsync($"/api/v1/provisioning/stores/{storeId}/deprovision", new { }))
            .Content.ReadFromJsonAsync<JobBody>();

        Assert.NotNull(job!.RetainUntil);
        Assert.True(job.RetainUntil < DateTimeOffset.UtcNow.AddDays(2));
    }

    [Fact]
    public async Task DeprovisioningNeedsItsOwnPermission()
    {
        if (!_fixture.IsAvailable) return;

        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);

        // An Admin runs the platform day to day and may provision; the path that
        // ends in deleted data is deliberately not theirs.
        var admin = await ClientAsync(SystemRoles.Admin);

        Assert.Equal(
            HttpStatusCode.Created,
            (await admin.PostAsJsonAsync($"/api/v1/provisioning/stores/{storeId}", new { })).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await admin.PostAsJsonAsync($"/api/v1/provisioning/stores/{storeId}/deprovision", new { })).StatusCode);
    }

    [Fact]
    public async Task ACustomerSeesTheirOwnRunAndNobodyElses()
    {
        if (!_fixture.IsAvailable) return;

        var platform = await ClientAsync();

        var mine = await _fixture.SeedCustomerAsync();
        var theirs = await _fixture.SeedCustomerAsync();
        var myStore = await _fixture.SeedStoreAsync(mine);
        var theirStore = await _fixture.SeedStoreAsync(theirs);

        var myJob = await (await platform.PostAsJsonAsync($"/api/v1/provisioning/stores/{myStore}", new { }))
            .Content.ReadFromJsonAsync<JobBody>();

        var theirJob = await (await platform.PostAsJsonAsync($"/api/v1/provisioning/stores/{theirStore}", new { }))
            .Content.ReadFromJsonAsync<JobBody>();

        var customer = await ClientAsync(SystemRoles.CustomerOwner, mine);

        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync($"/api/v1/provisioning/{myJob!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync($"/api/v1/provisioning/{theirJob!.Id}")).StatusCode);
    }

    [Fact]
    public async Task AStoreCanReportABackupAndAFailureIsVisibleOnTheStore()
    {
        if (!_fixture.IsAvailable) return;

        var client = await ClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);
        var storeClient = await StoreClientAsync(client, storeId);

        var succeeded = await storeClient.PostAsJsonAsync("/api/v1/ingest/backups", new
        {
            environment = "Production",
            status = "Succeeded",
            kind = "Scheduled",
            startedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            completedAt = DateTimeOffset.UtcNow,
            sizeBytes = 1_048_576,
            location = "s3://knight-backups/acme/latest.dump",
        });

        Assert.Equal(HttpStatusCode.OK, succeeded.StatusCode);

        // A "successful" backup that produced nothing is the classic silent
        // failure and is refused rather than shown as green.
        var empty = await storeClient.PostAsJsonAsync("/api/v1/ingest/backups", new
        {
            environment = "Production",
            status = "Succeeded",
            startedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            completedAt = DateTimeOffset.UtcNow,
            sizeBytes = 0,
        });

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        // Read through the same envelope the dashboard's collection hook needs:
        // a bare array is the one shape it cannot consume.
        var listed = await client.GetFromJsonAsync<BackupListBody>($"/api/v1/stores/{storeId}/backups");
        Assert.Single(listed!.Items);
        Assert.Equal("Succeeded", listed.Items[0].Status);
    }

    [Fact]
    public async Task MutualTlsIsOfferedOnDedicatedInfrastructureAndRefusedOnSharedHosting()
    {
        if (!_fixture.IsAvailable) return;

        var client = await ClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();

        var shared = await _fixture.SeedStoreAsync(customerId);
        var dedicated = await _fixture.SeedStoreAsync(customerId, hostingModel: HostingModel.DedicatedManaged);
        const string thumbprint = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await client.PutAsJsonAsync($"/api/v1/stores/{shared}/mutual-tls", new { thumbprint })).StatusCode);

        var bound = await client.PutAsJsonAsync($"/api/v1/stores/{dedicated}/mutual-tls", new { thumbprint });
        Assert.Equal(HttpStatusCode.OK, bound.StatusCode);

        var store = await bound.Content.ReadFromJsonAsync<StoreBody>();
        Assert.True(store!.RequiresMutualTls);
    }

    /// <summary>
    /// A client authenticated as the store itself: issue a credential, hand it
    /// back through the handshake, and carry the token it mints. The same path a
    /// real store takes, so the environment binding and the mutual-TLS gate are
    /// exercised rather than bypassed.
    /// </summary>
    private async Task<HttpClient> StoreClientAsync(HttpClient operatorClient, Guid storeId)
    {
        var credential = await (await operatorClient.PostAsync($"/api/v1/stores/{storeId}/credentials", null))
            .Content.ReadFromJsonAsync<IssuedCredentialBody>();

        var handshake = await _fixture.Factory.CreateClient().PostAsJsonAsync("/api/v1/ingest/handshake", new
        {
            clientId = credential!.ClientId,
            clientSecret = credential.ClientSecret,
            environment = "Production",
            storeVersion = "1.0.0",
        });

        handshake.EnsureSuccessStatusCode();
        var session = await handshake.Content.ReadFromJsonAsync<HandshakeBody>();

        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session!.AccessToken);

        return client;
    }

    private sealed record IssuedCredentialBody(Guid Id, string ClientId, string ClientSecret);

    private sealed record HandshakeBody(string AccessToken);

    private sealed record JobBody(
        Guid Id,
        Guid StoreId,
        string Kind,
        string State,
        bool AwaitingOperator,
        string? CurrentStep,
        int CompletedStepCount,
        int TotalStepCount,
        DateTimeOffset? RetainUntil,
        IReadOnlyCollection<StepBody> Steps);

    private sealed record StepBody(int Sequence, string Name, string Mode, string Status, string? Detail);

    private sealed record ServerBody(Guid Id, string Name);

    private sealed record StoreBody(
        Guid Id,
        string Status,
        string PrimaryDomain,
        bool RequiresMutualTls,
        IReadOnlyCollection<CredentialBody> Credentials);

    private sealed record CredentialBody(Guid Id, string State);

    private sealed record BackupBody(Guid Id, string Status, string Kind, long? SizeBytes);

    private sealed record BackupListBody(IReadOnlyList<BackupBody> Items);
}
