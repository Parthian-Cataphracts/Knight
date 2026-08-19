using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccessControl.Domain;
using Json.Schema;
using Knight.IntegrationTests.Infrastructure;
using Stores.Domain;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// The KNIGHT half of the store contract, end to end against a real database:
/// the handshake, everything a store may push afterwards, and — at least as
/// important — everything it may not.
///
/// The negative cases are the point of this file. A store that can be tricked
/// into a neighbour's data, or a credential that keeps working after it is
/// revoked, is a security failure rather than a bug, so these are
/// release-blocking (docs/authorization.md §6).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StoreIngestionTests
{
    private const string Password = "correct horse battery staple";

    private readonly PostgresApiFixture _fixture;

    public StoreIngestionTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    // --- The handshake ------------------------------------------------------

    [Fact]
    public async Task AStoreWithAValidCredential_ReceivesAToken()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();

        var (status, body) = await HandshakeAsync(store.ClientId, store.ClientSecret, "Production");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(string.IsNullOrWhiteSpace(body!.RootElement.GetProperty("accessToken").GetString()));
        Assert.Equal("Bearer", body.RootElement.GetProperty("tokenType").GetString());
        Assert.Equal(store.StoreId, body.RootElement.GetProperty("storeId").GetGuid());
    }

    /// <summary>
    /// The response is checked against the schema both sides test against, so a
    /// field renamed here fails before it reaches a store
    /// (docs/contracts/store-integration.schema.json).
    /// </summary>
    [Fact]
    public async Task TheHandshakeResponse_MatchesTheSharedContract()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var (_, body) = await HandshakeAsync(store.ClientId, store.ClientSecret, "Production");

        AssertMatchesContract("handshakeResponse", body!.RootElement);
    }

    [Fact]
    public async Task AWrongSecret_IsRefusedIdenticallyToAnUnknownClientId()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();

        var (wrongSecret, wrongSecretBody) = await HandshakeAsync(store.ClientId, "not-the-secret", "Production");
        var (unknown, unknownBody) = await HandshakeAsync("knight-nobody-000000000000", "not-the-secret", "Production");

        // Identical answers on purpose: telling a caller which half of the
        // credential was wrong tells an attacker which half to keep working on.
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown);
        Assert.Equal(
            wrongSecretBody!.RootElement.GetProperty("title").GetString(),
            unknownBody!.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task AStoreReportingTheWrongEnvironment_IsRefused()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();

        var (status, _) = await HandshakeAsync(store.ClientId, store.ClientSecret, "Staging");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task AnUnrecognisedEnvironment_IsAValidationError()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();

        // Nothing was concealed by refusing: no credential was considered.
        var (status, _) = await HandshakeAsync(store.ClientId, store.ClientSecret, "Prod");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task ARevokedCredential_IsRefused()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        await RevokeCredentialAsync(store.StoreId, store.CredentialId);

        var (status, _) = await HandshakeAsync(store.ClientId, store.ClientSecret, "Production");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    /// <summary>
    /// Rotation is what keeps a running store working while it picks up a new
    /// secret, so the previous one must survive its grace window.
    /// </summary>
    [Fact]
    public async Task ARotatedCredential_KeepsWorkingDuringItsGraceWindow()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var client = await PlatformClientAsync();

        var rotated = await client.PostAsync(
            $"/api/v1/stores/{store.StoreId}/credentials/{store.CredentialId}/rotate",
            null);
        rotated.EnsureSuccessStatusCode();

        var replacement = await rotated.Content.ReadFromJsonAsync<JsonDocument>();

        var (oldStatus, _) = await HandshakeAsync(store.ClientId, store.ClientSecret, "Production");
        var (newStatus, _) = await HandshakeAsync(
            replacement!.RootElement.GetProperty("clientId").GetString()!,
            replacement.RootElement.GetProperty("clientSecret").GetString()!,
            "Production");

        Assert.Equal(HttpStatusCode.OK, oldStatus);
        Assert.Equal(HttpStatusCode.OK, newStatus);
    }

    [Fact]
    public async Task AStoreWhoseCustomerIsSuspended_IsRefused()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var client = await PlatformClientAsync();

        var suspended = await client.PostAsync($"/api/v1/customers/{store.CustomerId}/suspend", null);
        suspended.EnsureSuccessStatusCode();

        var (status, _) = await HandshakeAsync(store.ClientId, store.ClientSecret, "Production");

        // Commercial suspension has to reach ingestion immediately.
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task AReplayedNonce_IsRefused()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var nonce = Guid.NewGuid().ToString("n");

        var (first, _) = await HandshakeAsync(store.ClientId, store.ClientSecret, "Production", nonce);
        var (second, _) = await HandshakeAsync(store.ClientId, store.ClientSecret, "Production", nonce);

        Assert.Equal(HttpStatusCode.OK, first);
        Assert.Equal(HttpStatusCode.Unauthorized, second);
    }

    // --- Ingestion ----------------------------------------------------------

    [Fact]
    public async Task AStoreCanReportErrorsHeartbeatsAndEvents()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var client = await StoreClientAsync(store);

        var heartbeat = await client.PostAsJsonAsync("/api/v1/ingest/heartbeat", new
        {
            environment = "Production",
            status = "healthy",
            storeVersion = "2.0.0",
            dependencies = new { database = new { status = "healthy", latencyMs = 3 } },
            features = new[] { "storefront" },
        });

        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        var heartbeatBody = await heartbeat.Content.ReadFromJsonAsync<JsonDocument>();
        AssertMatchesContract("heartbeatResponse", heartbeatBody!.RootElement);

        var errors = await client.PostAsJsonAsync("/api/v1/ingest/errors", new
        {
            environment = "Production",
            version = "2.0.0",
            events = new[]
            {
                new
                {
                    occurredAt = DateTimeOffset.UtcNow,
                    exceptionType = "IntegrityError",
                    message = "duplicate key value violates unique constraint",
                    endpoint = "/api/orders/",
                    httpMethod = "POST",
                    statusCode = 500,
                },
            },
        });

        Assert.Equal(HttpStatusCode.Accepted, errors.StatusCode);
        var receipt = await errors.Content.ReadFromJsonAsync<JsonDocument>();
        AssertMatchesContract("ingestReceipt", receipt!.RootElement);
        Assert.Equal(1, receipt.RootElement.GetProperty("accepted").GetInt32());

        var events = await client.PostAsJsonAsync("/api/v1/ingest/events", new
        {
            environment = "Production",
            events = new[]
            {
                new
                {
                    occurredAt = DateTimeOffset.UtcNow,
                    type = "backup.completed",
                    severity = "Info",
                    summary = "Nightly backup finished.",
                },
            },
        });

        Assert.Equal(HttpStatusCode.Accepted, events.StatusCode);
    }

    /// <summary>
    /// The store's environment comes from the token. A payload claiming a
    /// different one is a misconfigured store or a token being used by something
    /// else, and both are refused.
    /// </summary>
    [Fact]
    public async Task AnErrorBatchFromAnotherEnvironment_IsRefused()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var client = await StoreClientAsync(store);

        var response = await client.PostAsJsonAsync("/api/v1/ingest/errors", new
        {
            environment = "Development",
            events = new[] { new { occurredAt = DateTimeOffset.UtcNow, exceptionType = "X", message = "y" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ARepeatedBatch_IsAcknowledgedWithoutBeingWrittenTwice()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var client = await StoreClientAsync(store);
        var key = Guid.NewGuid().ToString("n");

        var payload = new
        {
            environment = "Production",
            events = new[] { new { occurredAt = DateTimeOffset.UtcNow, exceptionType = "X", message = "y" } },
        };

        var first = await PostWithIdempotencyKeyAsync(client, "/api/v1/ingest/errors", payload, key);
        var second = await PostWithIdempotencyKeyAsync(client, "/api/v1/ingest/errors", payload, key);

        Assert.Equal(1, first.RootElement.GetProperty("accepted").GetInt32());
        Assert.True(second.RootElement.GetProperty("duplicate").GetBoolean());
        Assert.Equal(0, second.RootElement.GetProperty("accepted").GetInt32());
    }

    [Fact]
    public async Task LogShipping_WithoutTheEntitlement_IsRefused()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var client = await StoreClientAsync(store);

        var response = await client.PostAsJsonAsync("/api/v1/ingest/logs", new
        {
            environment = "Production",
            entries = new[] { new { timestamp = DateTimeOffset.UtcNow, level = "INFO", message = "hello" } },
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheEntitlementSet_IsSignedAndMatchesTheContract()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var client = await StoreClientAsync(store);

        var response = await client.GetAsync("/api/v1/ingest/features");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        AssertMatchesContract("entitlementSet", body!.RootElement);

        Assert.Equal(store.StoreId, body.RootElement.GetProperty("storeId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("signature").GetString()));
        Assert.Equal("1", body.RootElement.GetProperty("signatureVersion").GetString());
    }

    // --- Principals and isolation -------------------------------------------

    [Fact]
    public async Task ADashboardToken_CannotIngest()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/ingest/heartbeat", new
        {
            environment = "Production",
            status = "healthy",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AStoreToken_CannotReachTheDashboardApi()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var client = await StoreClientAsync(store);

        var stores = await client.GetAsync("/api/v1/stores");
        var customers = await client.GetAsync("/api/v1/customers");

        Assert.Equal(HttpStatusCode.Forbidden, stores.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, customers.StatusCode);
    }

    [Fact]
    public async Task ATamperedToken_IsRefused()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var token = await StoreTokenAsync(store);

        // Same shape, one character different in the signature.
        var tampered = token[..^2] + (token[^2] == 'a' ? 'b' : 'a') + token[^1];

        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tampered);

        var response = await client.PostAsJsonAsync("/api/v1/ingest/heartbeat", new { environment = "Production", status = "healthy" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A store writes into its own customer's rows and nowhere else. The store id
    /// is never read from a payload, so the way to prove isolation is to show
    /// that what one store ingested is invisible to another customer's operator.
    /// </summary>
    [Fact]
    public async Task OneCustomersErrorsAreInvisibleToAnother()
    {
        if (!_fixture.IsAvailable) return;

        var storeA = await SeedRegisteredStoreAsync();
        var storeB = await SeedRegisteredStoreAsync();

        var clientA = await StoreClientAsync(storeA);
        var ingested = await clientA.PostAsJsonAsync("/api/v1/ingest/errors", new
        {
            environment = "Production",
            events = new[] { new { occurredAt = DateTimeOffset.UtcNow, exceptionType = "SecretError", message = "customer A only" } },
        });
        ingested.EnsureSuccessStatusCode();

        // An operator confined to customer B asks for customer A's store.
        var operatorB = await CustomerClientAsync(storeB.CustomerId);
        var response = await operatorB.GetAsync($"/api/v1/stores/{storeA.StoreId}/errors");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("customer A only", body);
    }

    // --- Domain verification -------------------------------------------------

    /// <summary>
    /// Credentials prove possession of a secret and say nothing about who answers
    /// on the domain KNIGHT will poll, so a store with an unproven domain stops
    /// at Pending.
    /// </summary>
    [Fact]
    public async Task AStoreWithAnUnverifiedDomain_StaysPending()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();

        var (_, body) = await HandshakeAsync(store.ClientId, store.ClientSecret, "Production");

        Assert.Equal("Pending", body!.RootElement.GetProperty("integrationStatus").GetString());
        Assert.True(body.RootElement.GetProperty("domainVerificationOutstanding").GetBoolean());
    }

    [Fact]
    public async Task DomainVerification_IssuesATokenAndAPlaceToPublishIt()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var client = await PlatformClientAsync();

        var started = await client.PostAsync($"/api/v1/stores/{store.StoreId}/domain-verification", null);
        started.EnsureSuccessStatusCode();

        var challenge = await started.Content.ReadFromJsonAsync<JsonDocument>();

        Assert.StartsWith("knight-verify-", challenge!.RootElement.GetProperty("token").GetString());
        Assert.Equal("/.well-known/knight-domain-verification", challenge.RootElement.GetProperty("httpPath").GetString());
        Assert.True(challenge.RootElement.GetProperty("verifiedAt").ValueKind is JsonValueKind.Null);
    }

    /// <summary>
    /// The domain in a test resolves to nothing, so verification fails — which is
    /// the case worth asserting: an unreachable domain must be a clean failure
    /// with a reason, not an exception or a silent success.
    /// </summary>
    [Fact]
    public async Task VerifyingADomainThatPublishesNothing_FailsWithAReason()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var client = await PlatformClientAsync();

        await client.PostAsync($"/api/v1/stores/{store.StoreId}/domain-verification", null);
        var verified = await client.PostAsync($"/api/v1/stores/{store.StoreId}/domain-verification/verify", null);

        verified.EnsureSuccessStatusCode();
        var body = await verified.Content.ReadFromJsonAsync<JsonDocument>();

        Assert.False(body!.RootElement.GetProperty("verified").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task VerifyingBeforeStarting_IsAConflict()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var client = await PlatformClientAsync();

        var response = await client.PostAsync($"/api/v1/stores/{store.StoreId}/domain-verification/verify", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // --- Health and deployments ---------------------------------------------

    [Fact]
    public async Task AHeartbeatIsVisibleAsHealthHistoryAndANewVersionAsADeployment()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var storeClient = await StoreClientAsync(store);

        var beat = await storeClient.PostAsJsonAsync("/api/v1/ingest/heartbeat", new
        {
            environment = "Production",
            status = "degraded",
            storeVersion = "3.1.0",
            detail = "queue backlog",
        });
        beat.EnsureSuccessStatusCode();

        var client = await PlatformClientAsync();

        var health = await client.GetFromJsonAsync<JsonDocument>($"/api/v1/stores/{store.StoreId}/health");
        var latest = health!.RootElement.GetProperty("latest");

        Assert.Equal("Degraded", latest.GetProperty("status").GetString());
        Assert.Equal("Heartbeat", latest.GetProperty("source").GetString());
        Assert.Equal("3.1.0", latest.GetProperty("reportedVersion").GetString());

        var deployments = await client.GetFromJsonAsync<JsonDocument>($"/api/v1/stores/{store.StoreId}/deployments");
        var first = deployments!.RootElement.GetProperty("items").EnumerateArray().First();

        // The handshake reported the version first, so the deployment is already
        // there before the heartbeat arrives.
        Assert.Equal("VersionChange", first.GetProperty("source").GetString());
    }

    /// <summary>
    /// A reported deployment and the version change it causes are one deployment,
    /// so the history shows one row rather than two.
    /// </summary>
    [Fact]
    public async Task AReportedDeployment_ConfirmsTheDetectedOneRatherThanDuplicatingIt()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var storeClient = await StoreClientAsync(store);

        await storeClient.PostAsJsonAsync("/api/v1/ingest/heartbeat", new
        {
            environment = "Production",
            status = "healthy",
            storeVersion = "4.0.0",
        });

        var reported = await storeClient.PostAsJsonAsync("/api/v1/ingest/events", new
        {
            environment = "Production",
            events = new[]
            {
                new
                {
                    occurredAt = DateTimeOffset.UtcNow,
                    type = "deployment.completed",
                    severity = "Info",
                    summary = "Deployed 4.0.0",
                    payload = new { version = "4.0.0", previousVersion = "1.0.0" },
                },
            },
        });
        reported.EnsureSuccessStatusCode();

        var client = await PlatformClientAsync();
        var deployments = await client.GetFromJsonAsync<JsonDocument>($"/api/v1/stores/{store.StoreId}/deployments");

        var forVersion = deployments!.RootElement.GetProperty("items").EnumerateArray()
            .Where(deployment => deployment.GetProperty("version").GetString() == "4.0.0")
            .ToArray();

        Assert.Single(forVersion);
        Assert.Equal("StoreReported", forVersion[0].GetProperty("source").GetString());
        Assert.Equal("Succeeded", forVersion[0].GetProperty("status").GetString());
    }

    // --- Helpers -------------------------------------------------------------

    private sealed record RegisteredStore(Guid CustomerId, Guid StoreId, Guid CredentialId, string ClientId, string ClientSecret);

    private static string Email() => $"user-{Guid.NewGuid():n}@knight.test";

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

    /// <summary>An active customer with an active Production store and one usable credential.</summary>
    private async Task<RegisteredStore> SeedRegisteredStoreAsync()
    {
        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId, StoreEnvironment.Production);

        var client = await PlatformClientAsync();
        var issued = await client.PostAsync($"/api/v1/stores/{storeId}/credentials", null);
        issued.EnsureSuccessStatusCode();

        var credential = await issued.Content.ReadFromJsonAsync<JsonDocument>();

        return new RegisteredStore(
            customerId,
            storeId,
            credential!.RootElement.GetProperty("id").GetGuid(),
            credential.RootElement.GetProperty("clientId").GetString()!,
            credential.RootElement.GetProperty("clientSecret").GetString()!);
    }

    private async Task RevokeCredentialAsync(Guid storeId, Guid credentialId)
    {
        var client = await PlatformClientAsync();
        var response = await client.DeleteAsync($"/api/v1/stores/{storeId}/credentials/{credentialId}");
        response.EnsureSuccessStatusCode();
    }

    private async Task<(HttpStatusCode Status, JsonDocument? Body)> HandshakeAsync(
        string clientId,
        string clientSecret,
        string environment,
        string? nonce = null)
    {
        var client = _fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/ingest/handshake", new
        {
            clientId,
            clientSecret,
            environment,
            storeVersion = "1.0.0",
            runtime = "Python 3.12 / Django 5.1",
            nonce,
        });

        var payload = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(payload) ? null : JsonDocument.Parse(payload));
    }

    private async Task<string> StoreTokenAsync(RegisteredStore store)
    {
        var (status, body) = await HandshakeAsync(store.ClientId, store.ClientSecret, "Production");
        Assert.Equal(HttpStatusCode.OK, status);

        return body!.RootElement.GetProperty("accessToken").GetString()!;
    }

    private async Task<HttpClient> StoreClientAsync(RegisteredStore store)
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await StoreTokenAsync(store));

        return client;
    }

    private static async Task<JsonDocument> PostWithIdempotencyKeyAsync(HttpClient client, string path, object payload, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("Idempotency-Key", key);

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Validates a payload against a definition in the contract both sides test
    /// against. The schema lives in docs/ rather than in either codebase
    /// precisely so that neither can quietly change it.
    /// </summary>
    private static void AssertMatchesContract(string definition, JsonElement payload)
    {
        var schema = StoreContractSchema.Definition(definition);
        var result = schema.Evaluate(payload, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(
            result.IsValid,
            $"The payload does not match '{definition}' in the shared store contract: " +
            string.Join("; ", result.Details.Where(detail => detail.HasErrors).SelectMany(detail =>
                detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}"))));
    }
}

/// <summary>
/// The two strings KNIGHT signs, checked against the worked examples in
/// docs/contracts/store-integration.samples.json.
///
/// This is the test a schema cannot replace. Both sides can agree on every field
/// of a payload and still produce different bytes to sign — over date
/// formatting, property order, or how an absent value is rendered — and the
/// result is a signature that verifies in testing and fails in production. The
/// reference store asserts the same examples from Python.
/// </summary>
public sealed class StoreSignatureContractTests
{
    [Fact]
    public void TheEntitlementCanonicalForm_MatchesTheSharedExample()
    {
        var sample = StoreContractSchema.Sample("entitlementCanonicalForm");
        var payload = sample.GetProperty("payload").Deserialize<Knight.Contracts.Ingest.EntitlementSetResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(
            sample.GetProperty("expected").GetString(),
            Knight.Api.Ingest.EntitlementSignature.Canonicalise(payload));
    }

    /// <summary>
    /// The order features arrive in must not change what is signed, or a store
    /// and KNIGHT would disagree whenever the database returned a different row
    /// order.
    /// </summary>
    [Fact]
    public void TheEntitlementCanonicalForm_DoesNotDependOnTheOrderReceived()
    {
        var sample = StoreContractSchema.Sample("entitlementCanonicalForm");
        var payload = sample.GetProperty("payload").Deserialize<Knight.Contracts.Ingest.EntitlementSetResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var reversed = payload with { Features = payload.Features.Reverse().ToArray() };

        Assert.Equal(
            sample.GetProperty("expected").GetString(),
            Knight.Api.Ingest.EntitlementSignature.Canonicalise(reversed));
    }

    [Fact]
    public void TheRequestCanonicalForm_MatchesTheSharedExample()
    {
        var sample = StoreContractSchema.Sample("requestCanonicalForm");
        var payload = sample.GetProperty("payload");

        Assert.Equal(
            sample.GetProperty("expected").GetString(),
            Knight.Infrastructure.ControlPlane.Integration.StoreRequestSignature.Canonicalise(
                payload.GetProperty("method").GetString()!,
                payload.GetProperty("path").GetString()!,
                payload.GetProperty("timestamp").GetString()!,
                payload.GetProperty("nonce").GetString()!));
    }
}

/// <summary>
/// Loads the shared KNIGHT↔Store contract from docs/, so the schema the
/// reference store validates against is byte-for-byte the one KNIGHT is tested
/// against. Located by walking up from the test binary rather than by a relative
/// path, which would depend on the build layout.
/// </summary>
internal static class StoreContractSchema
{
    private const string RelativePath = "docs/contracts/store-integration.schema.json";

    private const string SamplesRelativePath = "docs/contracts/store-integration.samples.json";

    private static readonly Lazy<JsonDocument> Samples = new(() =>
        JsonDocument.Parse(File.ReadAllText(Locate(SamplesRelativePath))));

    public static JsonElement Sample(string name) => Samples.Value.RootElement.GetProperty(name);

    private static readonly Lazy<string> Definitions = new(ReadDefinitions);

    /// <summary>
    /// One definition as a standalone schema, carrying every sibling definition
    /// so the $refs between them keep resolving.
    /// </summary>
    public static JsonSchema Definition(string name) => JsonSchema.FromText($$"""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$ref": "#/$defs/{{name}}",
          "$defs": {{Definitions.Value}}
        }
        """);

    private static string ReadDefinitions()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Locate()));
        return document.RootElement.GetProperty("$defs").GetRawText();
    }

    private static string Locate(string relativePath = RelativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find '{relativePath}' above {AppContext.BaseDirectory}.");
    }
}
