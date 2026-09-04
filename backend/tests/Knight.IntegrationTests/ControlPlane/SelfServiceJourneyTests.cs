using System.Net;
using System.Net.Http.Json;
using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Domain.Common;
using Knight.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Onboarding;
using Plans.Domain;
using Provisioning;
using Provisioning.Domain;
using Stores.Domain;
using Subscriptions.Domain;
using Xunit;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// The self-service acceptance test (docs/self-service-saas-plan.md §13): the one
/// test that proves the whole thing. A brand-new anonymous visitor registers,
/// verifies their email, chooses a plan with an optional feature, "pays", and —
/// with <b>no operator step</b> — ends up with a fully provisioned, Active store
/// that has the purchased feature installed.
///
/// Infrastructure is simulated (§11): a simulated payment provider confirms the
/// charge, and a simulated infrastructure adapter produces the machine, agent,
/// credential, domain, handshake and health facts a real cloud and a real agent
/// would, then plays the agent to apply the delivery jobs. Everything else — the
/// billing, the entitlements, the provisioning engine, the delivery pipeline — is
/// the real code that runs in production.
/// </summary>
/// <summary>
/// A fixture of its own — its own database and its own host, with infrastructure
/// simulated — so the always-running simulated worker and the provisioning it
/// drives never touch the rows the shared collection's tests depend on.
/// </summary>
public sealed class SimulatedJourneyFixture : PostgresApiFixture
{
    public CapturingVerificationSender Verification { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Provisioning:SimulateInfrastructure", "true");
        builder.UseSetting("FeatureArtifacts:PublicBaseUrl", "http://localhost/artifacts");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IVerificationEmailSender>();
            services.AddSingleton<IVerificationEmailSender>(Verification);

            // The test drives the outbox, the infrastructure adapter and the
            // provisioning engine itself, inline and in order, precisely so the
            // journey is deterministic. The background workers that also do those
            // things would only race it — two dispatchers starting one store's
            // provisioning is a duplicate-key crash — so they are removed here.
            services.RemoveAll<IHostedService>();
        });
    }

    public sealed class CapturingVerificationSender : IVerificationEmailSender
    {
        public string? LastToken { get; private set; }

        public bool CanSend => true;

        public Task<bool> SendAsync(string email, string displayName, string verificationToken, CancellationToken cancellationToken)
        {
            LastToken = verificationToken;
            return Task.FromResult(true);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SimulatedJourneyCollection : ICollectionFixture<SimulatedJourneyFixture>
{
    public const string Name = "SimulatedJourney";
}

[Collection(SimulatedJourneyCollection.Name)]
public sealed class SelfServiceJourneyTests
{
    private const string Password = "correct horse battery staple";

    private readonly SimulatedJourneyFixture _fixture;

    public SelfServiceJourneyTests(SimulatedJourneyFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AnAnonymousVisitorReachesAProvisionedStoreWithNoOperatorStep()
    {
        if (!_fixture.IsAvailable) return;

        var factory = _fixture.Factory;
        var sender = _fixture.Verification;
        var client = factory.CreateClient();

        // --- Arrange: a publicly purchasable plan offering one installable feature.
        var (planId, featureId, slug) = await SeedInstallablePlanAsync(factory);

        // --- 1. Register.
        var email = $"owner-{Guid.NewGuid():n}@knight.test";
        var registered = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = Password, name = "Owner", companyName = "Acme" });
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);

        // --- 2. Verify email.
        var token = sender.LastToken;
        Assert.False(string.IsNullOrWhiteSpace(token));
        var verified = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new { token });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);

        // --- 3. Sign in as the owner.
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await login.Content.ReadFromJsonAsync<LoginBody>();
        var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session!.AccessToken);

        // --- 4. The plan is on the public price list.
        var plans = await (await client.GetAsync("/api/v1/catalog/plans")).Content.ReadFromJsonAsync<PublicPlanBody[]>();
        Assert.Contains(plans!, plan => plan.Id == planId);

        // --- 5. Checkout: choose the plan plus the optional feature.
        var checkout = await owner.PostAsJsonAsync("/api/v1/billing/checkout", new
        {
            planId,
            billingInterval = "monthly",
            selectedFeatureIds = new[] { featureId },
        });
        Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);
        var checkoutBody = await checkout.Content.ReadFromJsonAsync<CheckoutBody>();
        Assert.True(checkoutBody!.Amount > 0);

        // --- 6. The provider confirms the payment. This webhook is the only thing
        //        that activates anything.
        var payload = $$"""
            {"type":"payment_succeeded","providerSessionId":"sim_sess_{{checkoutBody.CheckoutSessionId:N}}","providerTransactionId":"sim_txn_1"}
            """;
        var webhook = await client.PostAsync(
            "/api/v1/billing/webhooks/simulated",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);

        // The webhook commits the activation and a durable "provision this store"
        // outbox entry; the store is created when the dispatcher drains it. Drive
        // it inline so the assertion is deterministic rather than racing the 5s
        // background sweep.
        await DispatchOutboxAsync(factory);

        // The subscription is Active, and the store record exists — created with
        // no operator ever touching it.
        var storeId = await WaitForStoreAsync(factory, session.User!.CustomerId!.Value);
        Assert.NotNull(storeId);

        // --- 7. Let the simulated infrastructure bring the store up. Drives the
        //        same adapter + engine the background worker uses, but inline so the
        //        test is deterministic rather than timing-dependent.
        await DriveProvisioningToActiveAsync(factory, storeId!.Value);

        // --- 8. The definition of done: an Active store with the purchased feature
        //        installed.
        await _fixture.WithControlPlaneScopeAsync(async (context, _) =>
        {
            var store = await context.Stores.AsNoTracking().FirstAsync(s => s.Id == storeId);
            Assert.Equal(StoreStatus.Active, store.Status);

            var subscription = await context.Subscriptions.AsNoTracking()
                .FirstAsync(s => s.CustomerId == session.User.CustomerId);
            Assert.Equal(SubscriptionStatus.Active, subscription.Status);

            var entitlement = await context.FeatureEntitlements.AsNoTracking()
                .FirstOrDefaultAsync(e => e.CustomerId == session.User.CustomerId && e.FeatureId == featureId);
            Assert.NotNull(entitlement);

            var installation = await context.FeatureInstallations.AsNoTracking()
                .FirstOrDefaultAsync(i => i.StoreId == storeId && i.FeatureId == featureId);
            Assert.NotNull(installation);
            Assert.Equal(FeatureDelivery.Domain.InstallationState.Installed, installation!.State);
        });

        // --- 9. Data portability: the customer exports KNIGHT's record of them.
        var export = await owner.GetFromJsonAsync<ExportBody>("/api/v1/me/export");
        Assert.NotNull(export);
        Assert.Equal(session.User.CustomerId, export!.CustomerId);
        Assert.NotNull(export.Subscription);
        Assert.Contains(export.Stores, s => s.Id == storeId);
        Assert.Contains(export.Entitlements, e => e.FeatureId == featureId);
    }

    /// <summary>
    /// Seeds a published, installable feature and a publicly purchasable plan that
    /// offers it as a priced optional add-on. Written straight through the context
    /// in platform scope, the way the other suites seed their arrangements.
    /// </summary>
    private async Task<(Guid PlanId, Guid FeatureId, string Slug)> SeedInstallablePlanAsync(WebApplicationFactory<Program> factory)
    {
        var slug = $"reviews{Guid.NewGuid():n}"[..12];
        var now = DateTimeOffset.UtcNow;

        var manifestJson = $$"""
            {
              "apiVersion": "knight.dev/v1",
              "slug": "{{slug}}",
              "version": "1.0.0",
              "name": "Reviews",
              "runtime": "django",
              "django": { "app_label": "knight_{{slug}}", "installed_app": "knight_feature_{{slug}}", "urls": { "include": "knight_feature_{{slug}}.urls", "prefix": "{{slug}}/" } },
              "compatibility": { "storeVersion": "*", "python": "*", "django": "*" },
              "migrations": { "required": false, "reversible": true, "estimatedDurationSeconds": 1 },
              "install": { "strategy": "package-install", "healthCheck": "knight_feature_{{slug}}.checks.health" },
              "uninstall": { "strategy": "disable-then-remove", "dataRetentionDays": 30 }
            }
            """;

        Assert.True(FeatureManifest.TryParse(manifestJson, out var manifest, out var errors), string.Join("; ", errors));

        var featureId = Guid.NewGuid();
        var planId = Guid.NewGuid();

        await _fixture.WithControlPlaneScopeAsync(async (context, _) =>
        {
            var feature = Feature.Create(featureId, now, slug, "Reviews", "Engagement");
            feature.Publish(now);
            await context.Features.AddAsync(feature);

            var version = FeatureVersion.Create(
                Guid.NewGuid(), now, featureId, manifest!, manifestJson,
                packageReference: $"{slug}-1.0.0.knight",
                artifactDigest: new string('a', 64),
                artifactSizeBytes: 1024,
                signature: "simulated-signature",
                signingKeyId: "dev",
                releaseNotes: null);
            version.Publish(Guid.NewGuid(), now);
            await context.FeatureVersions.AddAsync(version);

            var plan = Plan.Create(planId, now, $"plan{Guid.NewGuid():n}"[..12], "Reviews Plan", Money.Of(49m, "EUR"), 1);
            plan.SetPubliclyPurchasable(true, now);
            plan.SetFeature(featureId, isIncluded: false, isCustomerToggleable: true, pinnedVersionRange: null, now);
            await context.Plans.AddAsync(plan);

            await context.FeaturePrices.AddAsync(
                FeaturePrice.Create(Guid.NewGuid(), featureId, planId, Money.Of(29m, "EUR"), BillingPeriod.Monthly, now));

            await context.SaveChangesAsync();
        });

        return (planId, featureId, slug);
    }

    private async Task<Guid?> WaitForStoreAsync(WebApplicationFactory<Program> factory, Guid customerId)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var storeId = await _fixture.WithControlPlaneScopeAsync(async (context, _) =>
                await context.Stores.AsNoTracking()
                    .Where(s => s.CustomerId == customerId)
                    .Select(s => (Guid?)s.Id)
                    .FirstOrDefaultAsync());

            if (storeId is not null)
            {
                return storeId;
            }

            await Task.Delay(100);
        }

        return null;
    }

    /// <summary>
    /// Runs the simulated infrastructure adapter and the provisioning engine in a
    /// loop — exactly the pair the background worker runs — until the store's run
    /// finishes, so the assertion below is not racing a timer.
    /// </summary>
    /// <summary>
    /// Drains the activation outbox inline — the durable webhook → provisioning
    /// handoff — so the store is created deterministically rather than when the
    /// background sweep next fires.
    /// </summary>
    private static async Task DispatchOutboxAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();
        await scope.ServiceProvider
            .GetRequiredService<PlatformBilling.IActivationOutboxDispatcher>()
            .DispatchDueAsync(50, CancellationToken.None);
    }

    private static async Task DriveProvisioningToActiveAsync(WebApplicationFactory<Program> factory, Guid storeId)
    {
        for (var pass = 0; pass < 25; pass++)
        {
            using var scope = factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

            var adapter = scope.ServiceProvider.GetRequiredService<IInfrastructureAdapter>();
            Assert.True(adapter.IsAutomated, "Infrastructure should be simulated for this test.");

            var provisioning = scope.ServiceProvider.GetRequiredService<IProvisioningService>();

            var job = (await provisioning.ListAsync(
                    new ProvisioningJobQuery(1, 1, storeId, CustomerId: null, State: null),
                    CancellationToken.None))
                .Items.FirstOrDefault();

            if (job is null)
            {
                await Task.Delay(100);
                continue;
            }

            if (job.State is ProvisioningState.Succeeded)
            {
                return;
            }

            await adapter.EnsureAsync(storeId, CancellationToken.None);
            await provisioning.AdvanceAsync(job.Id, CancellationToken.None);
        }

        Assert.Fail("The store's provisioning run did not reach Succeeded.");
    }

    private sealed record LoginBody(string Status, string? AccessToken, UserBody? User);

    private sealed record UserBody(Guid Id, string Email, Guid? CustomerId);

    private sealed record PublicPlanBody(Guid Id, string Key, string Name, decimal BasePrice);

    private sealed record CheckoutBody(Guid CheckoutSessionId, Guid SubscriptionId, string CheckoutUrl, decimal Amount, string Currency);

    private sealed record ExportBody(Guid CustomerId, object? Subscription, IReadOnlyList<ExportStore> Stores, IReadOnlyList<ExportEntitlement> Entitlements);

    private sealed record ExportStore(Guid Id, string Name);

    private sealed record ExportEntitlement(Guid FeatureId, string Source);
}
