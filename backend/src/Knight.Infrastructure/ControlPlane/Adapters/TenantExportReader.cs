using Knight.Application.Abstractions.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Subscriptions.Domain;

namespace Knight.Infrastructure.ControlPlane.Adapters;

/// <summary>
/// Gathers the control plane's record of a customer into an export document
/// (hardening backlog P3). It reads only what KNIGHT holds — store metadata, the
/// subscription, entitlements, provisioning history and telemetry counts — never a
/// store's own business data, which lives in the store's database.
///
/// Called from the customer's own <c>/me</c> surface, so the isolation filter
/// already confines it; it also filters by <paramref name="customerId"/> in each
/// query, because an export is the one place a leak would hand one customer
/// another's record, and one guard is not enough for that.
/// </summary>
internal sealed class TenantExportReader : ITenantExportReader
{
    private readonly ControlPlaneDbContext _context;

    public TenantExportReader(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task<TenantExport> ExportAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var subscription = await _context.Subscriptions
            .AsNoTracking()
            .Include(s => s.Features)
            .Where(s => s.CustomerId == customerId && s.Status != SubscriptionStatus.Cancelled)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var entitlements = await _context.FeatureEntitlements
            .AsNoTracking()
            .Where(e => e.CustomerId == customerId && e.RevokedAt == null)
            .Select(e => new TenantExportEntitlement(e.FeatureId, e.Source.ToString(), e.GrantedAt, e.ExpiresAt))
            .ToListAsync(cancellationToken);

        var stores = await _context.Stores
            .AsNoTracking()
            .Where(s => s.CustomerId == customerId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        var exportedStores = new List<TenantExportStore>(stores.Count);
        foreach (var store in stores)
        {
            var telemetry = new TenantExportTelemetry(
                ErrorGroups: await _context.ErrorGroups.CountAsync(x => x.StoreId == store.Id, cancellationToken),
                ErrorEvents: await _context.StoreErrorEvents.CountAsync(x => x.StoreId == store.Id, cancellationToken),
                LogEntries: await _context.StoreLogEntries.CountAsync(x => x.StoreId == store.Id, cancellationToken),
                Events: await _context.StoreEvents.CountAsync(x => x.StoreId == store.Id, cancellationToken),
                HealthChecks: await _context.StoreHealthChecks.CountAsync(x => x.StoreId == store.Id, cancellationToken),
                Deployments: await _context.StoreDeployments.CountAsync(x => x.StoreId == store.Id, cancellationToken),
                Backups: await _context.StoreBackups.CountAsync(x => x.StoreId == store.Id, cancellationToken));

            var runs = await _context.ProvisioningJobs
                .AsNoTracking()
                .Where(j => j.StoreId == store.Id)
                .OrderBy(j => j.CreatedAt)
                .Select(j => new TenantExportProvisioningRun(j.Kind.ToString(), j.State.ToString(), j.CreatedAt, j.CompletedAt))
                .ToListAsync(cancellationToken);

            exportedStores.Add(new TenantExportStore(
                store.Id,
                store.Name,
                store.Slug,
                store.PrimaryDomain,
                store.Environment.ToString(),
                store.HostingModel.ToString(),
                store.Status.ToString(),
                store.IntegrationStatus.ToString(),
                store.CreatedAt,
                telemetry,
                runs));
        }

        return new TenantExport(
            customerId,
            DateTimeOffset.UtcNow,
            subscription is null
                ? null
                : new TenantExportSubscription(
                    subscription.Id,
                    subscription.PlanId,
                    subscription.Status.ToString(),
                    subscription.CurrentPeriodStart,
                    subscription.CurrentPeriodEnd,
                    subscription.CancelAtPeriodEnd,
                    subscription.EnabledFeatureIds.ToList()),
            entitlements,
            exportedStores);
    }
}
