namespace Ordering.Domain;

/// <summary>
/// Projection of tenant customer data required for creating an immutable order party snapshot.
/// </summary>
public sealed record CustomerOrderingSnapshot(
    Guid CustomerId,
    string DisplayName,
    string? Phone,
    string? Email,
    bool IsActive);

/// <summary>
/// Cross-module read port enabling Ordering to resolve customer snapshot data without direct Customer domain coupling.
/// </summary>
public interface ICustomerOrderingReader
{
    Task<CustomerOrderingSnapshot?> GetCustomerSnapshotAsync(
        Guid tenantId,
        Guid customerId,
        CancellationToken cancellationToken);
}
