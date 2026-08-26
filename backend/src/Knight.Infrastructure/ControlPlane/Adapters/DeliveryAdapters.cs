using FeatureDelivery;
using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stores.Domain;

namespace Knight.Infrastructure.ControlPlane.Adapters;

/// <summary>
/// Reads the facts about a store that delivery needs, without the delivery
/// engine having to know the Stores module exists.
/// </summary>
internal sealed class StoreDeliveryReader : IStoreDeliveryReader
{
    private readonly ControlPlaneDbContext _context;

    public StoreDeliveryReader(ControlPlaneDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Candidate stores for a rollout, across every customer.
    ///
    /// <c>IgnoreQueryFilters</c> is deliberate and is the only place in delivery
    /// that uses it: the customer-isolation filter exists so a customer-scoped
    /// principal cannot see another customer's rows, and a fleet-wide rollout is
    /// by definition not a customer-scoped operation. The endpoints that reach
    /// this are platform-only, which is what makes the bypass safe.
    /// </summary>
    public async Task<IReadOnlyCollection<RolloutCandidateStore>> ListRolloutCandidatesAsync(
        string featureSlug,
        string targetVersion,
        CancellationToken cancellationToken)
    {
        var rows = await _context.FeatureInstallations
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(installation =>
                installation.FeatureSlug == featureSlug &&
                installation.InstalledVersion != null &&
                installation.InstalledVersion != targetVersion)
            .Join(
                _context.Stores.AsNoTracking().IgnoreQueryFilters(),
                installation => installation.StoreId,
                store => store.Id,
                (installation, store) => new
                {
                    installation.FeatureId,
                    store.Id,
                    store.CustomerId,
                    store.Name,
                    store.Environment,
                    store.Status,
                    installation.InstalledVersion,
                })
            // A suspended or archived store must not be rolled out to. It is not
            // supposed to be serving at all, and queueing work for it would leave
            // a job nobody will ever run holding up the rollout.
            .Where(row => row.Status == StoreStatus.Active)
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new RolloutCandidateStore(
                row.FeatureId,
                row.Id,
                row.CustomerId,
                row.Name,
                row.Environment.ToString(),
                row.Environment is StoreEnvironment.Production,
                row.InstalledVersion))
            .ToArray();
    }

    public async Task<FeaturePlanContext?> GetPlanContextAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var store = await _context.Stores
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == storeId, cancellationToken);

        if (store is null)
        {
            return null;
        }

        // The installed set comes from KNIGHT's own installation records rather
        // than from what the store last reported. The two can disagree — that
        // disagreement is drift, and phase 5 alerts on it — but a plan must be
        // built from the record the control plane is willing to be held to.
        var installed = await _context.FeatureInstallations
            .AsNoTracking()
            .Where(installation =>
                installation.StoreId == storeId &&
                installation.InstalledVersion != null)
            .Select(installation => new { installation.FeatureSlug, installation.InstalledVersion })
            .ToListAsync(cancellationToken);

        var runtime = await ReadRuntimeAsync(storeId, cancellationToken);

        return new FeaturePlanContext(
            storeId,
            store.ApplicationVersion,
            runtime.Python,
            runtime.Django,
            store.HostingModel is not HostingModel.SharedManaged,
            installed.ToDictionary(
                entry => entry.FeatureSlug,
                entry => entry.InstalledVersion!,
                StringComparer.Ordinal));
    }

    public async Task<Guid?> GetOwningCustomerAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var owner = await _context.Stores
            .AsNoTracking()
            .Where(store => store.Id == storeId)
            .Select(store => (Guid?)store.CustomerId)
            .FirstOrDefaultAsync(cancellationToken);

        return owner;
    }

    /// <summary>
    /// Digs the Python and Django versions out of the most recent health check.
    ///
    /// A store reports its runtime as part of its health payload, so the newest
    /// check is the freshest answer available. When it has never reported one, the
    /// answer is null — and the resolver treats null as "cannot certify" rather
    /// than "no objection", which is the whole reason this returns null instead of
    /// a guess.
    /// </summary>
    private async Task<(string? Python, string? Django)> ReadRuntimeAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var latest = await _context.StoreHealthChecks
            .AsNoTracking()
            .Where(check => check.StoreId == storeId && check.Dependencies != null)
            .OrderByDescending(check => check.CheckedAt)
            .Select(check => check.Dependencies)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(latest))
        {
            return (null, null);
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(latest);
            var root = document.RootElement;

            return (
                root.TryGetProperty("python", out var python) ? python.GetString() : null,
                root.TryGetProperty("django", out var django) ? django.GetString() : null);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, null);
        }
    }
}

/// <summary>
/// Hands delivery the few facts about a published version it needs to send an
/// agent to fetch and verify one.
///
/// It lives in the infrastructure layer rather than in either module because it
/// is exactly a join between them, and the two modules are not allowed to know
/// about each other.
/// </summary>
internal sealed class FeatureVersionReader : IFeatureVersionReader
{
    private readonly ControlPlaneDbContext _context;

    public FeatureVersionReader(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task<DeliverableVersion?> GetForDeliveryAsync(Guid versionId, CancellationToken cancellationToken)
    {
        var version = await _context.FeatureVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == versionId, cancellationToken);

        if (version is null)
        {
            return null;
        }

        var slug = await _context.Features
            .AsNoTracking()
            .Where(feature => feature.Id == version.FeatureId)
            .Select(feature => feature.Slug)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        // The migration policy is read out of the stored manifest rather than
        // duplicated into a column: the manifest is what the author signed, and a
        // column could drift from it.
        var migrations = MigrationPolicy.None;
        var retentionDays = 30;

        // The names the store needs to load the package. Read from the manifest
        // for the same reason as the migration policy, and defaulted to the slug
        // only so a version stored before this was carried does not fail to
        // describe itself at all - the default is a guess, and it is wrong for
        // every Feature whose distribution name is longer than its slug.
        var appLabel = slug.Replace("-", "_");
        var installedApp = appLabel;
        string? urlInclude = null;
        string? urlPrefix = null;
        IReadOnlyList<AgentWorker> workers = [];

        if (FeatureManifest.TryParse(version.ManifestJson, out var manifest, out _))
        {
            migrations = manifest.Migrations;
            retentionDays = manifest.Uninstall.DataRetentionDays;

            appLabel = manifest.Django.AppLabel;
            installedApp = manifest.Django.InstalledApp;
            urlInclude = manifest.Django.UrlInclude;
            urlPrefix = manifest.Django.UrlPrefix;

            workers = [.. manifest.Workers.Select(worker => new AgentWorker(
                worker.Name,
                worker.Entrypoint,
                worker.Schedule.ToString().ToLowerInvariant()))];
        }

        return new DeliverableVersion(
            version.Id,
            slug,
            version.Version,
            version.PackageReference,
            version.ArtifactDigest,
            version.ArtifactSizeBytes,
            version.Signature,
            version.SigningKeyId,
            migrations.Required,
            migrations.Reversible,
            migrations.RequiresMaintenanceWindow,
            retentionDays,
            appLabel,
            installedApp,
            urlInclude,
            urlPrefix,
            workers,
            migrations.Extensions);
    }
}

/// <summary>
/// Connects entitlement changes to delivery — the seam phase 2 left open on
/// purpose (docs/feature-delivery.md §2).
///
/// It still logs, because the log line is what makes the commercial decision
/// legible on its own, and it now also acts. The two halves are kept in that
/// order deliberately: an entitlement change is a commercial fact that has
/// already happened, so failing to act on it must never look like it did not
/// happen.
///
/// A delivery failure here is logged rather than thrown. The alternative is that
/// a subscription renewal fails because one of a customer's six stores is mid-job
/// — which would make the billing path depend on the health of the delivery path,
/// exactly the coupling that keeping entitlement and installation separate exists
/// to prevent. Reconciliation catches whatever this missed.
/// </summary>
internal sealed class DeliveryEntitlementEventPublisher : IEntitlementEventPublisher
{
    private readonly IFeatureDeliveryService _delivery;
    private readonly ILogger<DeliveryEntitlementEventPublisher> _logger;

    public DeliveryEntitlementEventPublisher(
        IFeatureDeliveryService delivery,
        ILogger<DeliveryEntitlementEventPublisher> logger)
    {
        _delivery = delivery;
        _logger = logger;
    }

    public async Task PublishAsync(FeatureEntitlementGranted @event, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Entitlement granted: customer {CustomerId} feature {FeatureId} source {Source} at {OccurredAt}",
            @event.CustomerId,
            @event.FeatureId,
            @event.Source,
            @event.OccurredAt);

        await ApplyAsync(@event.CustomerId, @event.FeatureId, entitled: true, @event.Source, cancellationToken);
    }

    public async Task PublishAsync(FeatureEntitlementRevoked @event, CancellationToken cancellationToken)
    {
        // Disable, never uninstall, and never delete data (docs/adr/0016).
        _logger.LogInformation(
            "Entitlement revoked: customer {CustomerId} feature {FeatureId} reason {Reason} at {OccurredAt}",
            @event.CustomerId,
            @event.FeatureId,
            @event.Reason,
            @event.OccurredAt);

        await ApplyAsync(@event.CustomerId, @event.FeatureId, entitled: false, @event.Reason, cancellationToken);
    }

    private async Task ApplyAsync(
        Guid customerId,
        Guid featureId,
        bool entitled,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await _delivery.ApplyEntitlementChangeAsync(customerId, featureId, entitled, reason, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Entitlement change for customer {CustomerId} feature {FeatureId} was recorded but delivery could not act on it. Reconciliation will retry.",
                customerId,
                featureId);
        }
    }
}
