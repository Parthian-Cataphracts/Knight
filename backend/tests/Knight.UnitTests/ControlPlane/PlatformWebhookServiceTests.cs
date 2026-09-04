using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PlatformBilling;
using PlatformBilling.Domain;
using PlatformBilling.Payments;
using Subscriptions;
using Subscriptions.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The webhook is the only path that activates a paid subscription
/// (docs/self-service-saas-plan.md §7): it verifies the signature, is idempotent
/// against replay, activates the subscription, reconciles entitlements, and runs
/// the post-activation listeners once — after the activation is committed.
/// </summary>
public sealed class PlatformWebhookServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ICheckoutSessionRepository _sessions = Substitute.For<ICheckoutSessionRepository>();
    private readonly IPlatformBillingTransactionRepository _transactions = Substitute.For<IPlatformBillingTransactionRepository>();
    private readonly ISubscriptionRepository _subscriptions = Substitute.For<ISubscriptionRepository>();
    private readonly IEntitlementService _entitlements = Substitute.For<IEntitlementService>();
    private readonly IActivationOutboxRepository _outbox = Substitute.For<IActivationOutboxRepository>();
    private readonly ICustomerScopeAccessor _scope = Substitute.For<ICustomerScopeAccessor>();
    private readonly IAuditTrail _audit = Substitute.For<IAuditTrail>();
    private readonly IDateTimeProvider _clock = Substitute.For<IDateTimeProvider>();

    private readonly Guid _customerId = Guid.NewGuid();
    private CheckoutSession _session = null!;
    private PlatformBillingTransaction _transaction = null!;
    private Subscription _subscription = null!;

    public PlatformWebhookServiceTests()
    {
        _clock.UtcNow.Returns(Now);
    }

    private PlatformWebhookService Build(string webhookSecret = "")
    {
        var options = Options.Create(new PlatformBillingOptions { WebhookSecret = webhookSecret });
        var registry = new PlatformPaymentProviderRegistry([new SimulatedPaymentProvider(options)]);

        return new PlatformWebhookService(
            registry, _sessions, _transactions, _subscriptions, _entitlements,
            _outbox, _scope, _audit, _clock, NullLogger<PlatformWebhookService>.Instance);
    }

    private void SeedOpenCheckout()
    {
        _subscription = Subscription.StartPending(Guid.NewGuid(), Now, _customerId, Guid.NewGuid(), Now, Now.AddMonths(1));

        var sessionId = Guid.CreateVersion7();
        _session = CheckoutSession.Open(
            sessionId, Now, _customerId, _subscription.PlanId, _subscription.Id,
            BillingInterval.Monthly, [], Knight.Domain.Common.Money.Of(49m, "EUR"), Now.AddHours(1));
        _session.AttachProviderSession("simulated", "sim_sess_1", Now);

        _transaction = PlatformBillingTransaction.Record(
            Guid.CreateVersion7(), Now, _customerId, _subscription.Id, "simulated",
            Knight.Domain.Common.Money.Of(49m, "EUR"), CheckoutService.IdempotencyKeyFor(sessionId));

        _sessions.FindByProviderSessionAsync("simulated", "sim_sess_1", Arg.Any<CancellationToken>()).Returns(_session);
        _transactions.FindByIdempotencyKeyAsync(CheckoutService.IdempotencyKeyFor(sessionId), Arg.Any<CancellationToken>()).Returns(_transaction);
        _subscriptions.GetByIdAsync(_subscription.Id, Arg.Any<CancellationToken>()).Returns(_subscription);
    }

    private static string SuccessPayload(string sessionId = "sim_sess_1", string transactionId = "sim_txn_1") =>
        $$"""{"type":"payment_succeeded","providerSessionId":"{{sessionId}}","providerTransactionId":"{{transactionId}}"}""";

    [Fact]
    public async Task AConfirmedPaymentActivatesTheSubscriptionAndReconcilesEntitlements()
    {
        SeedOpenCheckout();
        var service = Build();

        var result = await service.HandleAsync("simulated", SuccessPayload(), signature: null, CancellationToken.None);

        Assert.Equal(WebhookOutcome.Processed, result.Outcome);
        Assert.Equal(SubscriptionStatus.Active, _subscription.Status);
        Assert.Equal(PlatformBillingTransactionStatus.Succeeded, _transaction.Status);
        Assert.Equal(CheckoutSessionStatus.Completed, _session.Status);
        await _entitlements.Received(1).ReconcileAsync(_customerId, Arg.Any<CancellationToken>());
        _scope.Received().SetPlatformScope();
    }

    [Fact]
    public async Task ARedeliveredWebhookChangesNothing()
    {
        SeedOpenCheckout();
        _transaction.Succeed(Now); // already settled by the first delivery
        var service = Build();

        var result = await service.HandleAsync("simulated", SuccessPayload(), signature: null, CancellationToken.None);

        Assert.Equal(WebhookOutcome.AlreadyProcessed, result.Outcome);
        await _entitlements.DidNotReceive().ReconcileAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivationQueuesExactlyOneOutboxEntryForProvisioning()
    {
        SeedOpenCheckout();
        ActivationOutboxEntry? queued = null;
        await _outbox.AddAsync(Arg.Do<ActivationOutboxEntry>(e => queued = e), Arg.Any<CancellationToken>());
        var service = Build();

        await service.HandleAsync("simulated", SuccessPayload(), signature: null, CancellationToken.None);

        // The webhook does not provision in-process any more — it commits a durable
        // intent in the same unit of work, and the dispatcher acts on it.
        await _outbox.Received(1).AddAsync(Arg.Any<ActivationOutboxEntry>(), Arg.Any<CancellationToken>());
        Assert.NotNull(queued);
        Assert.Equal(_customerId, queued!.CustomerId);
        Assert.Equal(_subscription.Id, queued.SubscriptionId);
        Assert.Equal(ActivationOutboxStatus.Pending, queued.Status);
    }

    [Fact]
    public async Task ARedeliveredWebhookDoesNotQueueASecondOutboxEntry()
    {
        SeedOpenCheckout();
        _transaction.Succeed(Now); // already settled by the first delivery
        var service = Build();

        await service.HandleAsync("simulated", SuccessPayload(), signature: null, CancellationToken.None);

        await _outbox.DidNotReceive().AddAsync(Arg.Any<ActivationOutboxEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AWebhookWithNoSignatureIsRejectedWhenASecretIsConfigured()
    {
        SeedOpenCheckout();
        var service = Build(webhookSecret: "a-configured-webhook-secret");

        var result = await service.HandleAsync("simulated", SuccessPayload(), signature: null, CancellationToken.None);

        Assert.Equal(WebhookOutcome.InvalidSignature, result.Outcome);
        Assert.Equal(SubscriptionStatus.Pending, _subscription.Status);
    }

    [Fact]
    public async Task AWebhookForAnUnknownSessionIsAcknowledgedNotActedOn()
    {
        var service = Build();

        var result = await service.HandleAsync("simulated", SuccessPayload("no_such_session"), signature: null, CancellationToken.None);

        Assert.Equal(WebhookOutcome.UnknownSession, result.Outcome);
    }
}
