using Customers.Domain;
using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Stores.Domain;
using Microsoft.EntityFrameworkCore;

namespace Knight.Infrastructure.ControlPlane.Repositories;

/// <summary>
/// Whether a customer's stores may still talk to KNIGHT.
///
/// Read without the isolation filter for the same reason the credential lookup
/// is: it runs during a handshake, before any scope exists, and it answers one
/// boolean about a customer the caller has already proven it belongs to.
/// </summary>
internal sealed class CustomerStatusReader : ICustomerStatusReader
{
    private readonly ControlPlaneDbContext _context;

    public CustomerStatusReader(ControlPlaneDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Only an active customer is operable. A prospect has not started, a
    /// suspended one has been stopped deliberately, and an archived one is gone —
    /// none of the three should have a store reporting into the control plane.
    /// </summary>
    public Task<bool> IsOperableAsync(Guid customerId, CancellationToken cancellationToken) =>
        _context.Customers
            .IgnoreQueryFilters()
            .AnyAsync(customer => customer.Id == customerId && customer.Status == CustomerStatus.Active, cancellationToken);
}

/// <summary>
/// Reads the entitlement set that has already been resolved, joined to the
/// feature catalogue so callers get the slug their code actually asks about.
///
/// Deliberately a read model, not a second opinion: it never decides what a
/// customer is owed, it reports what the entitlement records say. Resolution and
/// reconciliation live in the subscriptions module and stay there
/// ([`adr/0019`](../../../../docs/adr/0019-entitlement-as-an-explicit-record.md)).
/// </summary>
internal sealed class CustomerEntitlementReader : ICustomerEntitlementReader
{
    private static readonly FeatureStatus[] EntitlingStatuses =
    [
        FeatureStatus.Published,
        FeatureStatus.Deprecated,
    ];

    private readonly ControlPlaneDbContext _context;
    private readonly IDateTimeProvider _clock;

    public CustomerEntitlementReader(ControlPlaneDbContext context, IDateTimeProvider clock)
    {
        _context = context;
        _clock = clock;
    }

    public async Task<IReadOnlyCollection<EntitledFeature>> ListActiveAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        // Projected to plain columns and ordered in SQL, then shaped in memory.
        // Constructing the record inside the query would make the ordering and
        // the enum's ToString client-side, which EF refuses to translate at all.
        var rows = await ActiveEntitlements(customerId, now)
            .Join(
                _context.Features.Where(feature => EntitlingStatuses.Contains(feature.Status)),
                entitlement => entitlement.FeatureId,
                feature => feature.Id,
                (entitlement, feature) => new
                {
                    feature.Id,
                    feature.Slug,
                    feature.Name,
                    entitlement.Source,
                    entitlement.GrantedAt,
                    entitlement.ExpiresAt,
                })
            .OrderBy(row => row.Slug)
            .ToArrayAsync(cancellationToken);

        return rows
            .Select(row => new EntitledFeature(
                row.Id,
                row.Slug,
                row.Name,
                row.Source.ToString(),
                row.GrantedAt,
                row.ExpiresAt))
            .ToArray();
    }

    public async Task<bool> IsEntitledAsync(Guid customerId, string featureSlug, CancellationToken cancellationToken)
    {
        var slug = FeatureSlug.Normalize(featureSlug);
        var now = _clock.UtcNow;

        return await ActiveEntitlements(customerId, now)
            .Join(
                _context.Features.Where(feature => feature.Slug == slug && EntitlingStatuses.Contains(feature.Status)),
                entitlement => entitlement.FeatureId,
                feature => feature.Id,
                (_, feature) => feature.Id)
            .AnyAsync(cancellationToken);
    }

    /// <summary>
    /// Read without the isolation filter and filtered by customer explicitly.
    /// Both callers are machine principals whose customer is fixed by the token
    /// they presented, and the ingestion pipeline resolves a store's customer
    /// before this is ever reached.
    /// </summary>
    private IQueryable<Subscriptions.Domain.FeatureEntitlement> ActiveEntitlements(Guid customerId, DateTimeOffset now) =>
        _context.FeatureEntitlements
            .IgnoreQueryFilters()
            .Where(entitlement =>
                entitlement.CustomerId == customerId
                && entitlement.RevokedAt == null
                && entitlement.GrantedAt <= now
                && (entitlement.ExpiresAt == null || entitlement.ExpiresAt > now));
}

/// <summary>
/// Finds stores nobody has reported a successful backup for.
///
/// Archived stores are excluded — a store that was deprovisioned is meant to
/// have stopped backing anything up — and so are stores that were never
/// registered, which have nothing to back up yet. What is left is the set where
/// silence is a problem rather than an expectation.
/// </summary>
internal sealed class BackupHealthReader : IBackupHealthReader
{
    private readonly ControlPlaneDbContext _context;

    public BackupHealthReader(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyCollection<StoreWithoutBackup>> ListStoresWithoutRecentBackupAsync(
        DateTimeOffset olderThan,
        CancellationToken cancellationToken)
    {
        var stores = await _context.Stores
            .AsNoTracking()
            .Where(store =>
                store.Status != StoreStatus.Archived &&
                store.Status != StoreStatus.Provisioning &&
                store.IntegrationStatus != IntegrationStatus.NotRegistered)
            .Select(store => new
            {
                store.Id,
                store.CustomerId,
                store.Name,
                LastBackupAt = _context.StoreBackups
                    .Where(backup => backup.StoreId == store.Id && backup.Status == BackupStatus.Succeeded)
                    .Max(backup => (DateTimeOffset?)backup.StartedAt),
            })
            .Where(store => store.LastBackupAt == null || store.LastBackupAt < olderThan)
            .ToListAsync(cancellationToken);

        return [.. stores.Select(store => new StoreWithoutBackup(store.Id, store.CustomerId, store.Name, store.LastBackupAt))];
    }
}

/// <summary>
/// Where a machine sits, so the Stores module can refuse a placement without
/// referencing the module that owns servers.
/// </summary>
internal sealed class ServerPlacementReader : IServerPlacementReader
{
    private readonly ControlPlaneDbContext _context;

    public ServerPlacementReader(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task<ServerPlacement?> GetAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var server = await _context.Servers
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == serverId, cancellationToken);

        return server is null
            ? null
            : new ServerPlacement(
                server.Id,
                server.HostingModel.ToString(),
                server.Environment.ToString(),
                server.DedicatedCustomerId,
                server.DecommissionedAt is not null);
    }
}
