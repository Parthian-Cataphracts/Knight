using FeatureDelivery;
using FeatureDelivery.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Provisioning;
using Servers.Domain;
using Stores;
using Stores.Domain;
using Subscriptions.Domain;

namespace Knight.Infrastructure.ControlPlane.Adapters;

/// <summary>
/// The store facts a provisioning run reads, and the two store transitions it
/// makes. Everything here is a decision the Stores module already owns; the
/// adapter only saves the provisioning module from referencing it.
/// </summary>
internal sealed class StoreProvisioningPort : IStoreProvisioningPort
{
    private readonly ControlPlaneDbContext _context;
    private readonly IStoreManagementService _stores;

    public StoreProvisioningPort(ControlPlaneDbContext context, IStoreManagementService stores)
    {
        _context = context;
        _stores = stores;
    }

    public async Task<ProvisioningStoreSnapshot?> GetAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var store = await _context.Stores
            .AsNoTracking()
            .Include(item => item.Credentials)
            .FirstOrDefaultAsync(item => item.Id == storeId, cancellationToken);

        if (store is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;

        // Health is read from the link state rather than from the newest health
        // row: Connected already means the store answered, its environment
        // matched and its domain was proven. A raw "the last poll said healthy"
        // would let a store that has since been disconnected pass the step.
        var isHealthy = store.IntegrationStatus is IntegrationStatus.Connected;

        return new ProvisioningStoreSnapshot(
            store.Id,
            store.CustomerId,
            store.Name,
            store.Slug,
            store.PrimaryDomain,
            store.Environment.ToString(),
            store.HostingModel.ToString(),
            store.Status.ToString(),
            store.IntegrationStatus.ToString(),
            store.ServerId,
            store.IsDomainVerified,
            store.Credentials.Any(credential => credential.IsUsable(now)),
            store.LastSeenAt is not null,
            isHealthy);
    }

    public Task ActivateAsync(Guid storeId, CancellationToken cancellationToken) =>
        _stores.ActivateAsync(storeId, cancellationToken);

    public Task ArchiveAsync(Guid storeId, CancellationToken cancellationToken) =>
        _stores.ArchiveAsync(storeId, cancellationToken);
}

/// <summary>
/// Whether the machine under a store has a live agent, and how to take its
/// agents away when the machine is handed back.
/// </summary>
internal sealed class ServerProvisioningPort : IServerProvisioningPort
{
    private readonly ControlPlaneDbContext _context;
    private readonly ILogger<ServerProvisioningPort> _logger;

    public ServerProvisioningPort(ControlPlaneDbContext context, ILogger<ServerProvisioningPort> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// An agent counts as enrolled once it has a credential, whether or not it
    /// is reporting this minute. Offline is included deliberately: an agent that
    /// enrolled and is briefly quiet has still been provisioned, and failing the
    /// step for it would restart a run over a missed heartbeat.
    /// </summary>
    public Task<bool> HasEnrolledAgentAsync(Guid serverId, CancellationToken cancellationToken) =>
        _context.Agents
            .AsNoTracking()
            .AnyAsync(
                agent => agent.ServerId == serverId &&
                         (agent.Status == AgentStatus.Online || agent.Status == AgentStatus.Offline),
                cancellationToken);

    public async Task<int> RevokeAgentsAsync(Guid serverId, string reason, CancellationToken cancellationToken)
    {
        var agents = await _context.Agents
            .Where(agent => agent.ServerId == serverId && agent.Status != AgentStatus.Revoked)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;

        foreach (var agent in agents)
        {
            agent.Revoke(reason, now);
        }

        if (agents.Count > 0)
        {
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Revoked {Count} agent(s) on server {ServerId}: {Reason}",
                agents.Count,
                serverId,
                reason);
        }

        return agents.Count;
    }
}

/// <summary>
/// Installs what a newly provisioned store is entitled to, using the ordinary
/// feature-delivery pipeline.
///
/// Provisioning decides nothing about which Features a store should run. The
/// customer's entitlements are the answer, and they were computed from the plan
/// when the subscription was created — so this reads them and asks delivery for
/// an install of each, with <see cref="JobTrigger.Provisioning"/> so an incident
/// can tell an automatic install from one an operator asked for.
/// </summary>
internal sealed class BaseFeatureInstaller : IBaseFeatureInstaller
{
    private readonly ControlPlaneDbContext _context;
    private readonly IFeatureDeliveryService _delivery;
    private readonly ICustomerEntitlementReader _entitlements;
    private readonly ILogger<BaseFeatureInstaller> _logger;

    public BaseFeatureInstaller(
        ControlPlaneDbContext context,
        IFeatureDeliveryService delivery,
        ICustomerEntitlementReader entitlements,
        ILogger<BaseFeatureInstaller> logger)
    {
        _context = context;
        _delivery = delivery;
        _entitlements = entitlements;
        _logger = logger;
    }

    public async Task<BaseFeatureProgress> EnsureBaseFeaturesAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var store = await _context.Stores
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == storeId, cancellationToken);

        if (store is null)
        {
            return new BaseFeatureProgress(0, 0, 0, "The store no longer exists.");
        }

        var entitled = await _entitlements.ListActiveAsync(store.CustomerId, cancellationToken);

        // A customer with no entitlements is a finished step, not a stuck one.
        // Plenty of stores are provisioned bare and have Features added later.
        if (entitled.Count == 0)
        {
            return new BaseFeatureProgress(0, 0, 0, "The customer holds no entitlements yet.");
        }

        var pinned = await ResolvePinnedRangesAsync(store.CustomerId, cancellationToken);

        var completed = 0;
        var failed = 0;
        var details = new List<string>();

        foreach (var feature in entitled)
        {
            var installation = await _context.FeatureInstallations
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    item => item.StoreId == storeId && item.FeatureId == feature.FeatureId,
                    cancellationToken);

            if (installation?.State is InstallationState.Installed or InstallationState.Disabled)
            {
                completed++;
                continue;
            }

            // The idempotency key is derived from the store and the Feature, so
            // the coordinator asking again on its next pass finds the job it
            // already queued instead of queuing a second install.
            var result = await _delivery.InstallAsync(
                new InstallFeatureInput(
                    storeId,
                    feature.Slug,
                    pinned.GetValueOrDefault(feature.FeatureId),
                    $"provisioning:{storeId}:{feature.Slug}",
                    JobTrigger.Provisioning),
                cancellationToken);

            if (!result.Plan.IsSuccessful)
            {
                failed++;
                details.Add($"{feature.Slug}: {result.Plan.DescribeFailures()}");
                continue;
            }

            if (result.Installation.State is InstallationState.Failed)
            {
                failed++;
                details.Add($"{feature.Slug}: {result.Installation.FailureMessage ?? "the installation failed."}");
            }
        }

        _logger.LogInformation(
            "Provisioning store {StoreId}: {Completed} of {Total} entitled Features installed, {Failed} failed.",
            storeId,
            completed,
            entitled.Count,
            failed);

        return new BaseFeatureProgress(
            entitled.Count,
            completed,
            failed,
            details.Count == 0 ? null : string.Join(" ", details));
    }

    public async Task<int> DisableAllAsync(Guid storeId, string reason, CancellationToken cancellationToken)
    {
        var installed = await _context.FeatureInstallations
            .AsNoTracking()
            .Where(item => item.StoreId == storeId && item.State == InstallationState.Installed)
            .Select(item => item.FeatureId)
            .ToListAsync(cancellationToken);

        var disabled = 0;

        foreach (var featureId in installed)
        {
            try
            {
                await _delivery.DisableAsync(storeId, featureId, reason, cancellationToken);
                disabled++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A Feature that cannot be switched off does not stop the store
                // being deprovisioned: revoking its credentials in the next step
                // takes the store off the network regardless.
                _logger.LogWarning(
                    exception,
                    "Feature {FeatureId} on store {StoreId} could not be disabled during deprovisioning.",
                    featureId,
                    storeId);
            }
        }

        return disabled;
    }

    /// <summary>
    /// The version ranges the customer's plan pins, by feature.
    ///
    /// A plan may pin a Feature to a range, and a provisioning install must
    /// honour that rather than taking the newest published version — otherwise a
    /// store comes up on a version the customer's plan never promised.
    /// </summary>
    private async Task<Dictionary<Guid, string?>> ResolvePinnedRangesAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var planId = await _context.Subscriptions
            .AsNoTracking()
            .Where(subscription =>
                subscription.CustomerId == customerId &&
                (subscription.Status == SubscriptionStatus.Active || subscription.Status == SubscriptionStatus.Trial))
            .OrderByDescending(subscription => subscription.CreatedAt)
            .Select(subscription => (Guid?)subscription.PlanId)
            .FirstOrDefaultAsync(cancellationToken);

        if (planId is not { } plan)
        {
            return [];
        }

        var entries = await _context.Plans
            .AsNoTracking()
            .Where(item => item.Id == plan)
            .SelectMany(item => item.Features)
            .Select(feature => new { feature.FeatureId, feature.PinnedVersionRange })
            .ToListAsync(cancellationToken);

        return entries.ToDictionary(entry => entry.FeatureId, entry => entry.PinnedVersionRange);
    }
}

/// <summary>
/// Deletes everything KNIGHT holds about a store once the retention window has
/// closed.
///
/// Deliberately a set of bulk deletes rather than a cascade from the store row:
/// the store record itself stays, archived, so that invoices, audit entries and
/// incidents that reference it keep making sense. What goes is the operational
/// data — errors, logs, events, health, deployments, backups — which is the data
/// the retention promise was about (docs/store-provisioning.md §5).
/// </summary>
internal sealed class StoreDataPurger : IStoreDataPurger
{
    private readonly ControlPlaneDbContext _context;
    private readonly ILogger<StoreDataPurger> _logger;

    public StoreDataPurger(ControlPlaneDbContext context, ILogger<StoreDataPurger> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<PurgeSummary> PurgeAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var errors = await _context.StoreErrorEvents.Where(item => item.StoreId == storeId)
            .ExecuteDeleteAsync(cancellationToken);

        var logs = await _context.StoreLogEntries.Where(item => item.StoreId == storeId)
            .ExecuteDeleteAsync(cancellationToken);

        var events = await _context.StoreEvents.Where(item => item.StoreId == storeId)
            .ExecuteDeleteAsync(cancellationToken);

        var health = await _context.StoreHealthChecks.Where(item => item.StoreId == storeId)
            .ExecuteDeleteAsync(cancellationToken);

        var deployments = await _context.StoreDeployments.Where(item => item.StoreId == storeId)
            .ExecuteDeleteAsync(cancellationToken);

        var backups = await _context.StoreBackups.Where(item => item.StoreId == storeId)
            .ExecuteDeleteAsync(cancellationToken);

        var summary = new PurgeSummary(errors, logs, events, health, deployments, backups);

        _logger.LogWarning(
            "Purged {Total} operational records for store {StoreId} after its retention window closed.",
            summary.Total,
            storeId);

        return summary;
    }
}

/// <summary>
/// Resolves how long a customer's data is kept: their negotiated override, else
/// their plan's promise, else the deployment default.
/// </summary>
internal sealed class RetentionPolicyReader : IRetentionPolicyReader
{
    private readonly ControlPlaneDbContext _context;
    private readonly ProvisioningOptions _options;

    public RetentionPolicyReader(ControlPlaneDbContext context, IOptions<ProvisioningOptions> options)
    {
        _context = context;
        _options = options.Value;
    }

    public async Task<TimeSpan> ResolveAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var overrideDays = await _context.Customers
            .AsNoTracking()
            .Where(customer => customer.Id == customerId)
            .Select(customer => customer.DataRetentionOverrideDays)
            .FirstOrDefaultAsync(cancellationToken);

        if (overrideDays is { } negotiated)
        {
            return TimeSpan.FromDays(negotiated);
        }

        var planDays = await _context.Subscriptions
            .AsNoTracking()
            .Where(subscription => subscription.CustomerId == customerId)
            .OrderByDescending(subscription => subscription.CreatedAt)
            .Join(
                _context.Plans.AsNoTracking(),
                subscription => subscription.PlanId,
                plan => plan.Id,
                (_, plan) => plan.DataRetentionDays)
            .FirstOrDefaultAsync(cancellationToken);

        return planDays is { } days ? TimeSpan.FromDays(days) : _options.DefaultRetention;
    }
}
