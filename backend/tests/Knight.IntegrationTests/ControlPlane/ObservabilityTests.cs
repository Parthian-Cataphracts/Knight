using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccessControl.Domain;
using Knight.IntegrationTests.Infrastructure;
using Stores.Domain;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// Error grouping, incidents and notifications end to end against a real
/// database.
///
/// The cases that matter most here are the ones a unit test cannot reach: that
/// grouping actually happens inside the ingestion request, that the unique index
/// on a fingerprint holds when the same problem arrives twice, and that a
/// customer cannot read another customer's errors or incidents. The last of those
/// is release-blocking (docs/authorization.md §6).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ObservabilityTests
{
    private const string Password = "correct horse battery staple";

    private const string Trace = """
        File "/srv/app/apps/orders/views.py", line 142, in create
            order = Order.objects.create(**payload)
        File "/usr/lib/python3.12/site-packages/django/db/models/query.py", line 671, in create
            obj.save(force_insert=True, using=self.db)
        """;

    private readonly PostgresApiFixture _fixture;

    public ObservabilityTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    // --- Grouping ------------------------------------------------------------

    [Fact]
    public async Task IdenticalErrors_CollapseIntoOneGroupWithACount()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var storeClient = await StoreClientAsync(store);

        await IngestAsync(storeClient, Enumerable.Range(0, 5).Select(_ => Error()).ToArray());

        var groups = await ListGroupsAsync(store.StoreId);

        var group = Assert.Single(groups);
        Assert.Equal(5, group.GetProperty("occurrenceCount").GetInt64());
        Assert.Equal("New", group.GetProperty("status").GetString());
        Assert.Equal("IntegrityError", group.GetProperty("exceptionType").GetString());
    }

    [Fact]
    public async Task ErrorsDifferingOnlyByLineNumberAndId_AreOneGroup()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var storeClient = await StoreClientAsync(store);

        // The same problem, seen across a deployment that shifted every line
        // number, on two different orders.
        await IngestAsync(storeClient,
        [
            Error(endpoint: "/api/orders/5182/items", stackTrace: Trace),
            Error(endpoint: "/api/orders/5183/items", stackTrace: Trace.Replace("line 142", "line 205")),
        ]);

        var group = Assert.Single(await ListGroupsAsync(store.StoreId));

        Assert.Equal(2, group.GetProperty("occurrenceCount").GetInt64());
        Assert.Equal("/api/orders/{id}/items", group.GetProperty("endpoint").GetString());
    }

    [Fact]
    public async Task DifferentProblems_StayDifferentGroups()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var storeClient = await StoreClientAsync(store);

        await IngestAsync(storeClient,
        [
            Error(exceptionType: "IntegrityError"),
            Error(exceptionType: "ValueError"),
        ]);

        Assert.Equal(2, (await ListGroupsAsync(store.StoreId)).Length);
    }

    [Fact]
    public async Task AGroupSurvivesADeployment()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var storeClient = await StoreClientAsync(store);

        await IngestAsync(storeClient, [Error()], storeVersion: "4.2.0");
        await IngestAsync(storeClient, [Error()], storeVersion: "4.3.0");

        var group = Assert.Single(await ListGroupsAsync(store.StoreId));

        // One problem, first seen in the old release and still happening in the
        // new one — which is exactly the fact an operator needs.
        Assert.Equal(2, group.GetProperty("occurrenceCount").GetInt64());
        Assert.Equal("4.2.0", group.GetProperty("firstSeenVersion").GetString());
        Assert.Equal("4.3.0", group.GetProperty("lastSeenVersion").GetString());
    }

    [Fact]
    public async Task SamplesCarryTheStackTraceForTheGroup()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var storeClient = await StoreClientAsync(store);

        await IngestAsync(storeClient, [Error()]);

        var group = Assert.Single(await ListGroupsAsync(store.StoreId));
        var client = await PlatformClientAsync();

        var response = await client.GetAsync($"/api/v1/errors/groups/{group.GetProperty("id").GetGuid()}/events");
        response.EnsureSuccessStatusCode();

        // The same paged envelope every other collection endpoint returns; a
        // bare array here would be a second, undocumented shape for the client.
        var samples = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("items");

        var sample = Assert.Single(samples.EnumerateArray().ToArray());

        Assert.Contains("views.py", sample.GetProperty("stackTrace").GetString()!, StringComparison.Ordinal);
    }

    // --- Lifecycle -----------------------------------------------------------

    [Fact]
    public async Task AGroupCanBeAcknowledgedResolvedAndIgnored()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var storeClient = await StoreClientAsync(store);
        await IngestAsync(storeClient, [Error()]);

        var groupId = (await ListGroupsAsync(store.StoreId))[0].GetProperty("id").GetGuid();
        var client = await PlatformClientAsync();

        Assert.Equal("Acknowledged", await ActAsync(client, groupId, "acknowledge"));
        Assert.Equal("Resolved", await ActAsync(client, groupId, "resolve"));
        Assert.Equal("Ignored", await ActAsync(client, groupId, "ignore"));
        Assert.Equal("New", await ActAsync(client, groupId, "reopen"));
    }

    [Fact]
    public async Task AResolvedGroupThatRecurs_IsReportedAsARegression()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var storeClient = await StoreClientAsync(store);

        await IngestAsync(storeClient, [Error()]);

        var groupId = (await ListGroupsAsync(store.StoreId))[0].GetProperty("id").GetGuid();
        var client = await PlatformClientAsync();

        await ActAsync(client, groupId, "resolve");

        // The problem comes back. A fix that did not hold must not keep showing
        // "Resolved" while the counter climbs.
        await IngestAsync(storeClient, [Error()]);

        var group = (await ListGroupsAsync(store.StoreId))[0];

        Assert.True(group.GetProperty("isRegression").GetBoolean());
        Assert.Equal("New", group.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DeclaringAnErrorResolved_NeedsMoreThanPermissionToSeeIt()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        await IngestAsync(await StoreClientAsync(store), [Error()]);

        var groupId = (await ListGroupsAsync(store.StoreId))[0].GetProperty("id").GetGuid();

        // Support holds errors.view but not errors.manage. Marking a problem
        // fixed is a claim other people act on, and being allowed to see that
        // something is broken is a long way from being allowed to close it.
        var support = await PlatformClientAsync(SystemRoles.Support);

        var read = await support.GetAsync($"/api/v1/errors/groups/{groupId}");
        read.EnsureSuccessStatusCode();

        var resolve = await support.PostAsJsonAsync($"/api/v1/errors/groups/{groupId}/resolve", new { });

        Assert.Equal(HttpStatusCode.Forbidden, resolve.StatusCode);
    }

    [Fact]
    public async Task ACustomerCannotSeeAnotherCustomersErrorGroups()
    {
        if (!_fixture.IsAvailable) return;

        var storeA = await SeedRegisteredStoreAsync();
        var storeB = await SeedRegisteredStoreAsync();

        await IngestAsync(await StoreClientAsync(storeA), [Error()]);
        await IngestAsync(await StoreClientAsync(storeB), [Error()]);

        var clientA = await CustomerClientAsync(storeA.CustomerId);
        var response = await clientA.GetAsync("/api/v1/errors/groups");
        response.EnsureSuccessStatusCode();

        var items = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("items");

        // Their own, and only their own.
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(storeA.StoreId, items[0].GetProperty("storeId").GetGuid());
    }

    [Fact]
    public async Task ACustomerCannotReadAnotherCustomersGroupById()
    {
        if (!_fixture.IsAvailable) return;

        var storeA = await SeedRegisteredStoreAsync();
        var storeB = await SeedRegisteredStoreAsync();

        await IngestAsync(await StoreClientAsync(storeB), [Error()]);

        var groupId = (await ListGroupsAsync(storeB.StoreId))[0].GetProperty("id").GetGuid();
        var clientA = await CustomerClientAsync(storeA.CustomerId);

        // Not found rather than forbidden: confirming the id exists would itself
        // leak that another customer has that problem.
        var response = await clientA.GetAsync($"/api/v1/errors/groups/{groupId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Incidents -----------------------------------------------------------

    [Fact]
    public async Task AnIncidentRunsItsFullLifecycleAndKeepsItsTimeline()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SeedRegisteredStoreAsync();
        var client = await PlatformClientAsync();

        var opened = await client.PostAsJsonAsync("/api/v1/incidents", new
        {
            title = "Checkout is failing",
            severity = "Critical",
            summary = "Orders cannot be created.",
            storeId = store.StoreId,
        });

        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);

        var incident = JsonDocument.Parse(await opened.Content.ReadAsStringAsync()).RootElement;
        var id = incident.GetProperty("id").GetGuid();

        Assert.StartsWith("INC-", incident.GetProperty("reference").GetString()!, StringComparison.Ordinal);
        Assert.Equal("Open", incident.GetProperty("status").GetString());

        await PostAsync(client, $"/api/v1/incidents/{id}/acknowledge", new { message = "On it." });
        await PostAsync(client, $"/api/v1/incidents/{id}/notes", new { message = "Migration 0003 looks wrong." });
        await PostAsync(client, $"/api/v1/incidents/{id}/mitigate", new { message = "Rolled back." });

        var resolved = await PostAsync(client, $"/api/v1/incidents/{id}/resolve", new { rootCause = "Irreversible migration." });

        Assert.Equal("Resolved", resolved.GetProperty("status").GetString());
        Assert.Equal("Irreversible migration.", resolved.GetProperty("rootCause").GetString());

        var timeline = await client.GetAsync($"/api/v1/incidents/{id}/events");
        timeline.EnsureSuccessStatusCode();

        var entries = JsonDocument.Parse(await timeline.Content.ReadAsStringAsync())
            .RootElement.GetProperty("items");

        Assert.Equal(5, entries.GetArrayLength());
        Assert.Equal("Opened", entries[0].GetProperty("type").GetString());
        Assert.Equal("Resolved", entries[4].GetProperty("type").GetString());
    }

    [Fact]
    public async Task IncidentReferencesAreUniqueUnderConcurrency()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();

        // Several rules firing at once during one outage is the normal case, and
        // two incidents sharing a reference would make them impossible to tell
        // apart in the chat window where people are discussing them.
        var opened = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            client.PostAsJsonAsync("/api/v1/incidents", new
            {
                title = $"Concurrent {index}",
                severity = "Warning",
            })));

        var references = new List<string>();

        foreach (var response in opened)
        {
            response.EnsureSuccessStatusCode();

            references.Add(JsonDocument.Parse(await response.Content.ReadAsStringAsync())
                .RootElement.GetProperty("reference").GetString()!);
        }

        Assert.Equal(references.Count, references.Distinct().Count());
    }

    [Fact]
    public async Task AResolvedIncidentCannotBeWorkedUntilItIsReopened()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var id = await OpenIncidentAsync(client);

        await PostAsync(client, $"/api/v1/incidents/{id}/resolve", new { });

        var refused = await client.PostAsJsonAsync($"/api/v1/incidents/{id}/mitigate", new { message = "too late" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var reopened = await PostAsync(client, $"/api/v1/incidents/{id}/reopen", new { reason = "It came back." });
        Assert.Equal("Investigating", reopened.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ACustomerCannotSeeAnotherCustomersIncidents()
    {
        if (!_fixture.IsAvailable) return;

        var storeA = await SeedRegisteredStoreAsync();
        var storeB = await SeedRegisteredStoreAsync();

        var platform = await PlatformClientAsync();

        await platform.PostAsJsonAsync("/api/v1/incidents", new
        {
            title = "B is broken",
            severity = "Critical",
            customerId = storeB.CustomerId,
            storeId = storeB.StoreId,
        });

        var clientA = await CustomerClientAsync(storeA.CustomerId);
        var response = await clientA.GetAsync("/api/v1/incidents");
        response.EnsureSuccessStatusCode();

        var items = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("items");

        Assert.Equal(0, items.GetArrayLength());
    }

    // --- Notifications -------------------------------------------------------

    [Fact]
    public async Task AChannelCanBeCreatedListedAndDisabled()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();

        var created = await client.PostAsJsonAsync("/api/v1/notifications/channels", new
        {
            name = "On call",
            kind = "Webhook",
            endpoint = "https://hooks.example.com/knight",
            minimumSeverity = "Critical",
            ruleFilter = new[] { "server.offline", "feature.drift" },
            secret = "s3cret-signing-key",
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var channel = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var id = channel.GetProperty("id").GetGuid();

        // The secret is never returned — not to the person who set it, not once.
        Assert.True(channel.GetProperty("hasSecret").GetBoolean());
        Assert.DoesNotContain(
            channel.EnumerateObject(),
            property => property.Name.Equals("secret", StringComparison.OrdinalIgnoreCase));

        var raw = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain("s3cret-signing-key", raw, StringComparison.Ordinal);

        var disabled = await PostAsync(client, $"/api/v1/notifications/channels/{id}/disable", new { });
        Assert.False(disabled.GetProperty("isEnabled").GetBoolean());

        var enabled = await PostAsync(client, $"/api/v1/notifications/channels/{id}/enable", new { });
        Assert.True(enabled.GetProperty("isEnabled").GetBoolean());
    }

    [Fact]
    public async Task AChannelFilteringOnAnUnknownRule_IsRefused()
    {
        if (!_fixture.IsAvailable) return;

        // A filter naming a rule that does not exist silently matches nothing,
        // which looks exactly like a channel that works and is never used.
        var client = await PlatformClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/notifications/channels", new
        {
            name = "Typo",
            kind = "InApp",
            minimumSeverity = "Info",
            ruleFilter = new[] { "server.offlne" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AWebhookChannelNeedsAnAbsoluteUrl()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/notifications/channels", new
        {
            name = "Bad",
            kind = "Webhook",
            endpoint = "example.com/hook",
            minimumSeverity = "Info",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheRuleCatalogueIsServed()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();

        var response = await client.GetAsync("/api/v1/notifications/rules");
        response.EnsureSuccessStatusCode();

        var rules = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("items").EnumerateArray().Select(rule => rule.GetString()).ToArray();

        Assert.Contains("feature.entitled_not_installed", rules);
        Assert.Contains("feature.drift", rules);
        Assert.Contains("job.stuck", rules);
        Assert.Contains("errors.spike", rules);
    }

    [Fact]
    public async Task AnInAppChannelReceivesAnAlertAndTheDeliveryIsRecorded()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();

        var created = await client.PostAsJsonAsync("/api/v1/notifications/channels", new
        {
            name = "Notification centre",
            kind = "InApp",
            minimumSeverity = "Info",
        });

        created.EnsureSuccessStatusCode();

        var channelId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // A test send is the one path that exercises the transport
        // synchronously, so it can be asserted on without waiting for the
        // dispatcher's timer.
        var tested = await PostAsync(client, $"/api/v1/notifications/channels/{channelId}/test", new { });
        Assert.True(tested.GetProperty("succeeded").GetBoolean());

        var deliveries = await client.GetAsync($"/api/v1/notifications?channelId={channelId}");
        deliveries.EnsureSuccessStatusCode();

        var items = JsonDocument.Parse(await deliveries.Content.ReadAsStringAsync())
            .RootElement.GetProperty("items");

        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("Delivered", items[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnEmailChannelReportsHonestlyThatItCannotDeliver()
    {
        if (!_fixture.IsAvailable) return;

        // No mail transport is configured on this host. Reporting success would
        // make the delivery log a lie exactly where it matters most.
        var client = await PlatformClientAsync();

        var created = await client.PostAsJsonAsync("/api/v1/notifications/channels", new
        {
            name = "Ops mail",
            kind = "Email",
            endpoint = "ops@knight.test",
            minimumSeverity = "Info",
        });

        created.EnsureSuccessStatusCode();

        var channelId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var tested = await PostAsync(client, $"/api/v1/notifications/channels/{channelId}/test", new { });

        Assert.False(tested.GetProperty("succeeded").GetBoolean());
        Assert.Contains("mail transport", tested.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManagingChannels_RequiresThePermission()
    {
        if (!_fixture.IsAvailable) return;

        // Support can read incidents and errors but holds no notification.manage:
        // deciding who gets paged is a different authority from reading what
        // broke.
        var client = await PlatformClientAsync(SystemRoles.Support);

        var response = await client.GetAsync("/api/v1/notifications/channels");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- Helpers -------------------------------------------------------------

    private sealed record RegisteredStore(Guid CustomerId, Guid StoreId, string ClientId, string ClientSecret);

    private static string Email() => $"user-{Guid.NewGuid():n}@knight.test";

    private static object Error(
        string exceptionType = "IntegrityError",
        string message = "duplicate key value violates unique constraint",
        string endpoint = "/api/orders/5182/items",
        string? stackTrace = Trace) => new
        {
            occurredAt = DateTimeOffset.UtcNow,
            exceptionType,
            message,
            endpoint,
            httpMethod = "POST",
            statusCode = 500,
            stackTrace,
            requestId = Guid.NewGuid().ToString("n")[..10],
            traceId = Guid.NewGuid().ToString("n")[..16],
        };

    private static async Task IngestAsync(HttpClient storeClient, object[] errors, string storeVersion = "4.2.0")
    {
        var response = await storeClient.PostAsJsonAsync("/api/v1/ingest/errors", new
        {
            environment = "Production",
            version = storeVersion,
            events = errors,
        });

        response.EnsureSuccessStatusCode();
    }

    private async Task<JsonElement[]> ListGroupsAsync(Guid storeId)
    {
        var client = await PlatformClientAsync();

        var response = await client.GetAsync($"/api/v1/errors/groups?storeId={storeId}");
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("items").EnumerateArray().ToArray();
    }

    private static async Task<string> ActAsync(HttpClient client, Guid groupId, string action)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/errors/groups/{groupId}/{action}", new { });
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("status").GetString()!;
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string path, object payload)
    {
        var response = await client.PostAsJsonAsync(path, payload);
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task<Guid> OpenIncidentAsync(HttpClient client)
    {
        var opened = await client.PostAsJsonAsync("/api/v1/incidents", new
        {
            title = "Something is wrong",
            severity = "Warning",
        });

        opened.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await opened.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
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
