using Microsoft.Extensions.Logging;
using Plans.Domain;
using PlatformBilling;
using Provisioning;
using Stores;
using Stores.Domain;

namespace Knight.Api.ControlPlane;

/// <summary>
/// The automatic wire from a confirmed payment to a provisioned store
/// (docs/self-service-saas-plan.md §7, missing item #3). It is the one place the
/// self-service journey crosses from billing into provisioning, and it does so
/// with no operator in the loop: when <see cref="IPlatformWebhookService"/> has
/// activated a subscription and resolved its entitlements, this creates the store
/// record and starts the ordinary provisioning job for it.
///
/// It runs after the activation is committed and must be idempotent — a
/// redelivered webhook reaches it again — so it reuses an existing store for the
/// customer rather than creating a second, and keys the provisioning job on the
/// subscription so a repeat call finds the job already started.
/// </summary>
internal sealed class ProvisioningActivationListener : ISubscriptionActivatedListener
{
    private readonly IStoreManagementService _stores;
    private readonly IProvisioningService _provisioning;
    private readonly IPlanRepository _plans;
    private readonly ILogger<ProvisioningActivationListener> _logger;

    public ProvisioningActivationListener(
        IStoreManagementService stores,
        IProvisioningService provisioning,
        IPlanRepository plans,
        ILogger<ProvisioningActivationListener> logger)
    {
        _stores = stores;
        _provisioning = provisioning;
        _plans = plans;
        _logger = logger;
    }

    public async Task OnActivatedAsync(SubscriptionActivatedContext context, CancellationToken cancellationToken)
    {
        // Idempotent: a redelivered webhook must not create a second store. A
        // self-service customer gets exactly one store, so an existing one is the
        // store this subscription provisions.
        var existing = await _stores.ListAsync(
            new StoreListQuery(1, 1, context.CustomerId, Environment: null, Status: null),
            cancellationToken);

        var store = existing.Items.FirstOrDefault();

        if (store is null)
        {
            var plan = await _plans.GetByIdAsync(context.PlanId, cancellationToken);

            // A dedicated plan gets a machine of its own; everything else shares
            // managed hosting.
            var hosting = plan is not null && IsDedicated(plan)
                ? HostingModel.DedicatedManaged
                : HostingModel.SharedManaged;

            var slug = $"store-{context.CustomerId:n}"[..Math.Min(40, $"store-{context.CustomerId:n}".Length)];
            var name = plan is null ? "New store" : $"{plan.Name} store";

            store = await _stores.CreateAsync(
                new CreateStoreInput(
                    context.CustomerId,
                    name,
                    slug,
                    $"{slug}.stores.knight.local",
                    StoreEnvironment.Production,
                    hosting),
                cancellationToken);

            _logger.LogInformation(
                "Self-service store {StoreId} created for customer {CustomerId} on subscription {SubscriptionId}.",
                store.Id,
                context.CustomerId,
                context.SubscriptionId);
        }

        // Keyed on the subscription so a repeat activation finds the job already
        // started rather than queuing a second run.
        await _provisioning.StartProvisioningAsync(store.Id, context.SubscriptionId.ToString(), cancellationToken);
    }

    private static bool IsDedicated(Plan plan) =>
        plan.Key.Contains("professional", StringComparison.OrdinalIgnoreCase)
        || plan.Key.Contains("dedicated", StringComparison.OrdinalIgnoreCase);
}
