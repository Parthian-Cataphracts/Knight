namespace Ordering.Domain;

public sealed record OrderingTenantSnapshot(
    Guid Id,
    string DefaultCurrency,
    string Status);

/// <summary>
/// Cross-module read port enabling Ordering to resolve tenant metadata (currency, status)
/// during order placement without referencing Tenancy EF entities directly.
/// </summary>
public interface IOrderingTenantReader
{
    Task<OrderingTenantSnapshot?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken);
}
