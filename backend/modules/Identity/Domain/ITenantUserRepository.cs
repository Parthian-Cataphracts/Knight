namespace Identity.Domain;

public sealed record TenantUserListItem
{
    public required TenantUser User { get; init; }
    public required IReadOnlyCollection<Guid> RoleIds { get; init; }
}

public interface ITenantUserRepository
{
    Task<TenantUser?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    /// <param name="normalizedEmail">Must already be normalized via <c>EmailFormat.NormalizeForComparison</c>.</param>
    Task<TenantUser?> GetByNormalizedEmailAsync(Guid tenantId, string normalizedEmail, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<TenantUserListItem> Items, long TotalCount)> ListAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken);

    Task AddAsync(TenantUser user, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically persists a new user together with its initial role
    /// assignments in one transaction — if either half fails, neither is
    /// committed. See docs/architecture/authorization.md ("staff creation").
    /// </summary>
    Task AddWithRoleAssignmentsAsync(TenantUser user, IReadOnlyCollection<TenantUserRole> roleAssignments, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
