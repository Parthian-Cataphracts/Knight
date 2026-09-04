namespace Knight.Application.Abstractions.ControlPlane;

/// <summary>
/// A customer's self-serve export of everything the control plane holds about them
/// (hardening backlog P3). Data portability: a merchant can take away KNIGHT's
/// record of their store — its metadata, subscription, entitlements, provisioning
/// history and a summary of the operational telemetry — without asking an operator.
///
/// This is deliberately KNIGHT's record, not the store's business data: the shop's
/// catalogue, orders and customers live in the store's own database, which KNIGHT
/// never reads ([`adr/0023`](../../../docs/adr/0023-single-tenant-store.md)). A
/// full backup of that is the store's own export, handed over at deprovisioning.
/// </summary>
public interface ITenantExportReader
{
    Task<TenantExport> ExportAsync(Guid customerId, CancellationToken cancellationToken);
}

public sealed record TenantExport(
    Guid CustomerId,
    DateTimeOffset ExportedAt,
    TenantExportSubscription? Subscription,
    IReadOnlyList<TenantExportEntitlement> Entitlements,
    IReadOnlyList<TenantExportStore> Stores);

public sealed record TenantExportSubscription(
    Guid Id,
    Guid PlanId,
    string Status,
    DateTimeOffset CurrentPeriodStart,
    DateTimeOffset CurrentPeriodEnd,
    bool CancelAtPeriodEnd,
    IReadOnlyList<Guid> FeatureIds);

public sealed record TenantExportEntitlement(Guid FeatureId, string Source, DateTimeOffset GrantedAt, DateTimeOffset? ExpiresAt);

public sealed record TenantExportStore(
    Guid Id,
    string Name,
    string Slug,
    string PrimaryDomain,
    string Environment,
    string HostingModel,
    string Status,
    string IntegrationStatus,
    DateTimeOffset CreatedAt,
    TenantExportTelemetry Telemetry,
    IReadOnlyList<TenantExportProvisioningRun> ProvisioningRuns);

/// <summary>Counts, not the rows themselves — a summary of what KNIGHT recorded, which is what portability is about here.</summary>
public sealed record TenantExportTelemetry(
    int ErrorGroups,
    int ErrorEvents,
    int LogEntries,
    int Events,
    int HealthChecks,
    int Deployments,
    int Backups);

public sealed record TenantExportProvisioningRun(string Kind, string State, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);
