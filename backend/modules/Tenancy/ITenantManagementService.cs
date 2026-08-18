using Tenancy.Domain;

namespace Tenancy;

public sealed record CreateTenantInput(string Name, string Slug, string TimeZone, string DefaultCurrency);

public sealed record UpdateTenantInput(string Name, string TimeZone, string DefaultCurrency);

public sealed record AddTenantDomainInput(string Host, TenantDomainType Type, bool MakePrimary);

public sealed record TenantListResult(IReadOnlyCollection<Tenant> Items, long TotalCount, int Page, int PageSize);

/// <summary>
/// Application-facing tenant lifecycle and domain-management use cases. This is
/// the only sanctioned entry point for mutating tenants — it owns audit logging
/// and re-loading the aggregate, so callers (API endpoints, future Super Admin
/// workflows) never touch <see cref="ITenantRepository"/> directly.
/// </summary>
public interface ITenantManagementService
{
    Task<Tenant> CreateAsync(CreateTenantInput input, CancellationToken cancellationToken);

    Task<Tenant?> GetAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<TenantListResult> ListAsync(int page, int pageSize, CancellationToken cancellationToken);

    Task<Tenant> UpdateAsync(Guid tenantId, UpdateTenantInput input, CancellationToken cancellationToken);

    Task<Tenant> ActivateAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<Tenant> SuspendAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<Tenant> ArchiveAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<TenantDomain> AddDomainAsync(Guid tenantId, AddTenantDomainInput input, CancellationToken cancellationToken);

    Task RemoveDomainAsync(Guid tenantId, Guid domainId, CancellationToken cancellationToken);

    Task<TenantDomain> SetPrimaryDomainAsync(Guid tenantId, Guid domainId, CancellationToken cancellationToken);
}
