using System.Net;
using System.Net.Http.Json;
using AccessControl.Domain;
using Knight.IntegrationTests.Infrastructure;
using Stores.Domain;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// Phase 2's exit criteria end to end: a subscription can be priced from data,
/// and entitlements are computable, queryable, and clearly distinct from
/// installations.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ControlPlaneCommerceTests
{
    private const string Password = "correct horse battery staple";

    private readonly PostgresApiFixture _fixture;

    public ControlPlaneCommerceTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private static string Email() => $"user-{Guid.NewGuid():n}@knight.test";

    private async Task<HttpClient> PlatformClientAsync(string role = SystemRoles.SuperAdmin)
    {
        var email = Email();
        await _fixture.SeedUserAsync(email, Password, role);
        return _fixture.CreateClient(await _fixture.SignInAsync(email, Password));
    }

    /// <summary>Creates a published feature, optionally one that cannot share a host.</summary>
    private static async Task<Guid> CreateFeatureAsync(HttpClient client, bool dedicated = false)
    {
        var slug = $"feature-{Guid.NewGuid():n}"[..24];

        var created = await client.PostAsJsonAsync("/api/v1/features", new
        {
            slug,
            name = $"Feature {slug}",
            category = "Test",
            isOptional = true,
            requiresDedicatedInfrastructure = dedicated,
        });

        var feature = await created.Content.ReadFromJsonAsync<FeatureBody>();
        await client.PostAsync($"/api/v1/features/{feature!.Id}/publish", null);
        return feature.Id;
    }

    private static async Task<Guid> CreatePlanAsync(HttpClient client, decimal basePrice = 49m)
    {
        var created = await client.PostAsJsonAsync("/api/v1/plans", new
        {
            key = $"plan-{Guid.NewGuid():n}"[..20],
            name = "Test plan",
            basePrice,
            currency = "EUR",
            sortOrder = 1,
        });

        return (await created.Content.ReadFromJsonAsync<PlanBody>())!.Id;
    }

    private static Task OfferFeatureAsync(HttpClient client, Guid planId, Guid featureId, bool included, bool toggleable) =>
        client.PutAsJsonAsync($"/api/v1/plans/{planId}/features", new
        {
            featureId,
            isIncluded = included,
            isCustomerToggleable = toggleable,
        });

    private static Task PriceFeatureAsync(HttpClient client, Guid featureId, decimal amount, Guid? planId = null) =>
        client.PutAsJsonAsync("/api/v1/plans/prices", new
        {
            featureId,
            planId,
            amount,
            currency = "EUR",
            billingPeriod = "Monthly",
        });

    [Fact]
    public async Task TheSeededCatalogueIsPresent()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var plans = await (await client.GetAsync("/api/v1/plans")).Content.ReadFromJsonAsync<PagedBody<PlanBody>>();

        // Basic, Custom and Professional are seeded from data, not code.
        Assert.Contains(plans!.Items, plan => plan.Key == "basic");
        Assert.Contains(plans!.Items, plan => plan.Key == "custom");
        Assert.Contains(plans!.Items, plan => plan.Key == "professional");
    }

    [Fact]
    public async Task AQuoteIsThePlanPlusThePricedFeaturesChosen()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var planId = await CreatePlanAsync(client, 49m);
        var featureId = await CreateFeatureAsync(client);

        await OfferFeatureAsync(client, planId, featureId, included: false, toggleable: true);
        await PriceFeatureAsync(client, featureId, 29m);

        var quote = await (await client.PostAsJsonAsync("/api/v1/subscriptions/quote", new { planId, featureIds = new[] { featureId } }))
            .Content.ReadFromJsonAsync<QuoteBody>();

        Assert.Equal("EUR", quote!.Currency);
        Assert.Equal(78m, quote.Subtotal);
        Assert.Equal(2, quote.Lines.Length);
    }

    [Fact]
    public async Task AQuoteHasNoSideEffects()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var planId = await CreatePlanAsync(client);

        await client.PostAsJsonAsync("/api/v1/subscriptions/quote", new { planId, featureIds = Array.Empty<Guid>() });

        var subscriptions = await (await client.GetAsync($"/api/v1/subscriptions?customerId={customerId}"))
            .Content.ReadFromJsonAsync<PagedBody<SubscriptionBody>>();

        Assert.Equal(0, subscriptions!.TotalCount);
    }

    [Fact]
    public async Task StartingASubscriptionEntitlesWhatThePlanIncludes()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var planId = await CreatePlanAsync(client);
        var featureId = await CreateFeatureAsync(client);

        await OfferFeatureAsync(client, planId, featureId, included: true, toggleable: false);

        var created = await client.PostAsJsonAsync("/api/v1/subscriptions", new { customerId, planId });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var entitlements = await (await client.GetAsync($"/api/v1/customers/{customerId}/entitlements"))
            .Content.ReadFromJsonAsync<PagedBody<EntitlementBody>>();

        var entitlement = Assert.Single(entitlements!.Items);
        Assert.Equal(featureId, entitlement.FeatureId);
        Assert.Equal("Plan", entitlement.Source);
        Assert.True(entitlement.IsActive);
    }

    [Fact]
    public async Task ACustomerCannotSelectAFeatureThePlanDoesNotLetThemToggle()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var planId = await CreatePlanAsync(client);
        var featureId = await CreateFeatureAsync(client);

        await OfferFeatureAsync(client, planId, featureId, included: false, toggleable: false);

        var created = await client.PostAsJsonAsync("/api/v1/subscriptions", new
        {
            customerId,
            planId,
            featureIds = new[] { featureId },
        });

        Assert.Equal(HttpStatusCode.Conflict, created.StatusCode);
    }

    [Fact]
    public async Task ADedicatedInfrastructureFeatureIsRefusedOnSharedHosting()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        await _fixture.SeedStoreAsync(customerId);

        var planId = await CreatePlanAsync(client);
        var featureId = await CreateFeatureAsync(client, dedicated: true);
        await OfferFeatureAsync(client, planId, featureId, included: false, toggleable: true);
        await PriceFeatureAsync(client, featureId, 149m);

        var created = await client.PostAsJsonAsync("/api/v1/subscriptions", new
        {
            customerId,
            planId,
            featureIds = new[] { featureId },
        });

        Assert.Equal(HttpStatusCode.Conflict, created.StatusCode);

        var check = await (await client.GetAsync($"/api/v1/customers/{customerId}/entitlements/{featureId}/check"))
            .Content.ReadFromJsonAsync<CheckBody>();

        Assert.False(check!.IsAllowed);
        Assert.Equal("RequiresDedicatedInfrastructure", check.Refusal);
    }

    [Fact]
    public async Task ADedicatedInfrastructureFeatureIsAllowedOnceTheCustomerHasCapacity()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        await _fixture.SeedStoreAsync(customerId, hostingModel: HostingModel.DedicatedManaged);

        var planId = await CreatePlanAsync(client);
        var featureId = await CreateFeatureAsync(client, dedicated: true);
        await OfferFeatureAsync(client, planId, featureId, included: false, toggleable: true);
        await PriceFeatureAsync(client, featureId, 149m);

        var created = await client.PostAsJsonAsync("/api/v1/subscriptions", new
        {
            customerId,
            planId,
            featureIds = new[] { featureId },
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task ChangingPlanRevokesWhatTheOldPlanGranted()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();

        var firstPlan = await CreatePlanAsync(client);
        var secondPlan = await CreatePlanAsync(client);
        var featureId = await CreateFeatureAsync(client);
        await OfferFeatureAsync(client, firstPlan, featureId, included: true, toggleable: false);

        var subscription = await (await client.PostAsJsonAsync("/api/v1/subscriptions", new { customerId, planId = firstPlan }))
            .Content.ReadFromJsonAsync<SubscriptionBody>();

        await client.PatchAsJsonAsync($"/api/v1/subscriptions/{subscription!.Id}", new { planId = secondPlan });

        var active = await (await client.GetAsync($"/api/v1/customers/{customerId}/entitlements"))
            .Content.ReadFromJsonAsync<PagedBody<EntitlementBody>>();

        Assert.Empty(active!.Items);
    }

    [Fact]
    public async Task CancellingASubscriptionRevokesItsEntitlements()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var planId = await CreatePlanAsync(client);
        var featureId = await CreateFeatureAsync(client);
        await OfferFeatureAsync(client, planId, featureId, included: true, toggleable: false);

        var subscription = await (await client.PostAsJsonAsync("/api/v1/subscriptions", new { customerId, planId }))
            .Content.ReadFromJsonAsync<SubscriptionBody>();

        await client.PostAsync($"/api/v1/subscriptions/{subscription!.Id}/cancel", null);

        var active = await (await client.GetAsync($"/api/v1/customers/{customerId}/entitlements"))
            .Content.ReadFromJsonAsync<PagedBody<EntitlementBody>>();

        Assert.Empty(active!.Items);

        // The record survives revocation: what was held and when is billing evidence.
        var all = await (await client.GetAsync($"/api/v1/customers/{customerId}/entitlements?includeInactive=true"))
            .Content.ReadFromJsonAsync<PagedBody<EntitlementBody>>();

        Assert.Single(all!.Items);
    }

    [Fact]
    public async Task SuspendingStopsEntitlingAndActivatingRestoresIt()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var planId = await CreatePlanAsync(client);
        var featureId = await CreateFeatureAsync(client);
        await OfferFeatureAsync(client, planId, featureId, included: true, toggleable: false);

        var subscription = await (await client.PostAsJsonAsync("/api/v1/subscriptions", new { customerId, planId }))
            .Content.ReadFromJsonAsync<SubscriptionBody>();

        await client.PostAsync($"/api/v1/subscriptions/{subscription!.Id}/suspend", null);
        var suspended = await (await client.GetAsync($"/api/v1/customers/{customerId}/entitlements"))
            .Content.ReadFromJsonAsync<PagedBody<EntitlementBody>>();
        Assert.Empty(suspended!.Items);

        await client.PostAsync($"/api/v1/subscriptions/{subscription.Id}/activate", null);
        var restored = await (await client.GetAsync($"/api/v1/customers/{customerId}/entitlements"))
            .Content.ReadFromJsonAsync<PagedBody<EntitlementBody>>();
        Assert.Single(restored!.Items);
    }

    [Fact]
    public async Task AManualGrantSurvivesAPlanChange()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var planId = await CreatePlanAsync(client);
        var otherPlan = await CreatePlanAsync(client);
        var featureId = await CreateFeatureAsync(client);

        var subscription = await (await client.PostAsJsonAsync("/api/v1/subscriptions", new { customerId, planId }))
            .Content.ReadFromJsonAsync<SubscriptionBody>();

        await client.PostAsJsonAsync($"/api/v1/customers/{customerId}/entitlements", new { featureId });
        await client.PatchAsJsonAsync($"/api/v1/subscriptions/{subscription!.Id}", new { planId = otherPlan });

        var active = await (await client.GetAsync($"/api/v1/customers/{customerId}/entitlements"))
            .Content.ReadFromJsonAsync<PagedBody<EntitlementBody>>();

        var entitlement = Assert.Single(active!.Items);
        Assert.Equal("Grant", entitlement.Source);
    }

    [Fact]
    public async Task OneSubscriptionPerCustomer()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var planId = await CreatePlanAsync(client);

        await client.PostAsJsonAsync("/api/v1/subscriptions", new { customerId, planId });
        var second = await client.PostAsJsonAsync("/api/v1/subscriptions", new { customerId, planId });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task AnInvoiceIsBuiltFromTheSamePricesAsTheQuote()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var planId = await CreatePlanAsync(client, 49m);
        var featureId = await CreateFeatureAsync(client);

        await OfferFeatureAsync(client, planId, featureId, included: false, toggleable: true);
        await PriceFeatureAsync(client, featureId, 29m);

        var subscription = await (await client.PostAsJsonAsync("/api/v1/subscriptions", new
        {
            customerId,
            planId,
            featureIds = new[] { featureId },
        })).Content.ReadFromJsonAsync<SubscriptionBody>();

        var quote = await (await client.PostAsJsonAsync("/api/v1/subscriptions/quote", new { planId, featureIds = new[] { featureId } }))
            .Content.ReadFromJsonAsync<QuoteBody>();

        var invoice = await (await client.PostAsync($"/api/v1/invoices/prepare/{subscription!.Id}", null))
            .Content.ReadFromJsonAsync<InvoiceBody>();

        Assert.Equal("Draft", invoice!.Status);
        Assert.Equal(quote!.Subtotal, invoice.Total);
    }

    [Fact]
    public async Task PreparingTwiceRebuildsRatherThanChargingTwice()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var planId = await CreatePlanAsync(client, 49m);

        var subscription = await (await client.PostAsJsonAsync("/api/v1/subscriptions", new { customerId, planId }))
            .Content.ReadFromJsonAsync<SubscriptionBody>();

        await client.PostAsync($"/api/v1/invoices/prepare/{subscription!.Id}", null);
        var second = await (await client.PostAsync($"/api/v1/invoices/prepare/{subscription.Id}", null))
            .Content.ReadFromJsonAsync<InvoiceBody>();

        Assert.Equal(49m, second!.Total);
        Assert.Single(second.Lines);
    }

    [Fact]
    public async Task IssuingNumbersTheInvoiceAndFreezesIt()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var planId = await CreatePlanAsync(client, 49m);

        var subscription = await (await client.PostAsJsonAsync("/api/v1/subscriptions", new { customerId, planId }))
            .Content.ReadFromJsonAsync<SubscriptionBody>();

        var draft = await (await client.PostAsync($"/api/v1/invoices/prepare/{subscription!.Id}", null))
            .Content.ReadFromJsonAsync<InvoiceBody>();

        var issued = await (await client.PostAsync($"/api/v1/invoices/{draft!.Id}/issue", null))
            .Content.ReadFromJsonAsync<InvoiceBody>();

        Assert.Equal("Issued", issued!.Status);
        Assert.False(string.IsNullOrWhiteSpace(issued.Number));
        Assert.StartsWith(DateTimeOffset.UtcNow.Year.ToString(), issued.Number!);

        // Preparing again must not touch an issued invoice.
        var rebuilt = await (await client.PostAsync($"/api/v1/invoices/prepare/{subscription.Id}", null))
            .Content.ReadFromJsonAsync<InvoiceBody>();

        Assert.NotEqual(issued.Id, rebuilt!.Id);
    }

    [Fact]
    public async Task RecordingPaymentSettlesTheInvoice()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var planId = await CreatePlanAsync(client, 49m);

        var subscription = await (await client.PostAsJsonAsync("/api/v1/subscriptions", new { customerId, planId }))
            .Content.ReadFromJsonAsync<SubscriptionBody>();

        var draft = await (await client.PostAsync($"/api/v1/invoices/prepare/{subscription!.Id}", null))
            .Content.ReadFromJsonAsync<InvoiceBody>();
        var issued = await (await client.PostAsync($"/api/v1/invoices/{draft!.Id}/issue", null))
            .Content.ReadFromJsonAsync<InvoiceBody>();

        var paid = await (await client.PostAsJsonAsync($"/api/v1/invoices/{issued!.Id}/payments", new
        {
            amount = 49m,
            currency = "EUR",
            method = "BankTransfer",
            reference = "REF-1",
        })).Content.ReadFromJsonAsync<InvoiceBody>();

        Assert.Equal("Paid", paid!.Status);
        Assert.Equal(0m, paid.Outstanding);
    }

    [Fact]
    public async Task InvoiceNumbersAreUniqueAcrossConcurrentIssues()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var planId = await CreatePlanAsync(client, 10m);

        var drafts = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            var customerId = await _fixture.SeedCustomerAsync();
            var subscription = await (await client.PostAsJsonAsync("/api/v1/subscriptions", new { customerId, planId }))
                .Content.ReadFromJsonAsync<SubscriptionBody>();

            var draft = await (await client.PostAsync($"/api/v1/invoices/prepare/{subscription!.Id}", null))
                .Content.ReadFromJsonAsync<InvoiceBody>();

            drafts.Add(draft!.Id);
        }

        var issued = await Task.WhenAll(drafts.Select(async id =>
            (await (await client.PostAsync($"/api/v1/invoices/{id}/issue", null)).Content.ReadFromJsonAsync<InvoiceBody>())!.Number));

        Assert.Equal(issued.Length, issued.Distinct().Count());
    }

    [Fact]
    public async Task ACustomerSeesOnlyTheirOwnSubscriptionsInvoicesAndEntitlements()
    {
        if (!_fixture.IsAvailable) return;

        var platform = await PlatformClientAsync();
        var planId = await CreatePlanAsync(platform, 49m);

        var customerId = await _fixture.SeedCustomerAsync();
        var otherCustomerId = await _fixture.SeedCustomerAsync();

        foreach (var id in new[] { customerId, otherCustomerId })
        {
            var subscription = await (await platform.PostAsJsonAsync("/api/v1/subscriptions", new { customerId = id, planId }))
                .Content.ReadFromJsonAsync<SubscriptionBody>();

            await platform.PostAsync($"/api/v1/invoices/prepare/{subscription!.Id}", null);
        }

        var email = Email();
        await _fixture.SeedUserAsync(email, Password, SystemRoles.CustomerOwner, customerId);
        var customer = _fixture.CreateClient(await _fixture.SignInAsync(email, Password));

        var subscriptions = await (await customer.GetAsync("/api/v1/subscriptions"))
            .Content.ReadFromJsonAsync<PagedBody<SubscriptionBody>>();
        Assert.Equal(1, subscriptions!.TotalCount);
        Assert.Equal(customerId, subscriptions.Items.Single().CustomerId);

        var invoices = await (await customer.GetAsync("/api/v1/invoices"))
            .Content.ReadFromJsonAsync<PagedBody<InvoiceBody>>();
        Assert.Equal(1, invoices!.TotalCount);

        // Asking for the other customer explicitly must not widen the scope.
        var foreign = await (await customer.GetAsync($"/api/v1/subscriptions?customerId={otherCustomerId}"))
            .Content.ReadFromJsonAsync<PagedBody<SubscriptionBody>>();
        Assert.Equal(0, foreign!.TotalCount);
    }

    [Fact]
    public async Task ACustomerCannotDefinePlansOrGrantThemselvesFeatures()
    {
        if (!_fixture.IsAvailable) return;

        var customerId = await _fixture.SeedCustomerAsync();
        var email = Email();
        await _fixture.SeedUserAsync(email, Password, SystemRoles.CustomerOwner, customerId);
        var customer = _fixture.CreateClient(await _fixture.SignInAsync(email, Password));

        // Defining what a plan contains, and granting outside one, are platform
        // business — no customer-scoped role holds plan.manage.
        var plan = await customer.PostAsJsonAsync("/api/v1/plans", new
        {
            key = $"p-{Guid.NewGuid():n}"[..12],
            name = "Mine",
            basePrice = 0m,
            currency = "EUR",
        });
        Assert.Equal(HttpStatusCode.Forbidden, plan.StatusCode);

        var grant = await customer.PostAsJsonAsync($"/api/v1/customers/{customerId}/entitlements", new { featureId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, grant.StatusCode);

        // Reading the price list is fine: they have to see what they can buy.
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/v1/plans")).StatusCode);
    }

    [Fact]
    public async Task ACustomerCannotIssueOrPayInvoices()
    {
        if (!_fixture.IsAvailable) return;

        var platform = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var planId = await CreatePlanAsync(platform, 49m);

        var subscription = await (await platform.PostAsJsonAsync("/api/v1/subscriptions", new { customerId, planId }))
            .Content.ReadFromJsonAsync<SubscriptionBody>();
        var draft = await (await platform.PostAsync($"/api/v1/invoices/prepare/{subscription!.Id}", null))
            .Content.ReadFromJsonAsync<InvoiceBody>();

        var email = Email();
        await _fixture.SeedUserAsync(email, Password, SystemRoles.CustomerOwner, customerId);
        var customer = _fixture.CreateClient(await _fixture.SignInAsync(email, Password));

        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsync($"/api/v1/invoices/{draft!.Id}/issue", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync($"/api/v1/invoices/{draft.Id}")).StatusCode);
    }

    [Fact]
    public async Task EveryCommercialMutationIsAudited()
    {
        if (!_fixture.IsAvailable) return;

        var client = await PlatformClientAsync();
        var customerId = await _fixture.SeedCustomerAsync();
        var planId = await CreatePlanAsync(client);
        var featureId = await CreateFeatureAsync(client);
        await OfferFeatureAsync(client, planId, featureId, included: true, toggleable: false);
        await client.PostAsJsonAsync("/api/v1/subscriptions", new { customerId, planId });

        var audit = await (await client.GetAsync("/api/v1/audit-logs?pageSize=200")).Content.ReadAsStringAsync();

        Assert.Contains("plan.created", audit);
        Assert.Contains("feature.published", audit);
        Assert.Contains("subscription.started", audit);
        Assert.Contains("entitlement.granted", audit);
    }

    private sealed record PagedBody<T>(IReadOnlyCollection<T> Items, long TotalCount);

    private sealed record FeatureBody(Guid Id, string Slug, string Status, bool RequiresDedicatedInfrastructure);

    private sealed record PlanBody(Guid Id, string Key, decimal BasePrice, string Currency);

    private sealed record SubscriptionBody(Guid Id, Guid CustomerId, Guid PlanId, string Status);

    private sealed record QuoteBody(string Currency, decimal Subtotal, QuoteLineBody[] Lines);

    private sealed record QuoteLineBody(string Description, Guid? FeatureId, decimal Total);

    private sealed record EntitlementBody(Guid FeatureId, string Source, bool IsActive);

    private sealed record CheckBody(bool IsAllowed, string Refusal, string? Detail);

    private sealed record InvoiceBody(
        Guid Id,
        string? Number,
        string Status,
        decimal Total,
        decimal Outstanding,
        InvoiceLineBody[] Lines);

    private sealed record InvoiceLineBody(Guid Id, string Description, decimal Total);
}
