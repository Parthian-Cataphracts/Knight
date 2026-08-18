namespace Tenancy.Domain;

/// <summary>
/// Persistence contract for tenants. Deliberately specific to <see cref="Tenant"/>
/// rather than a generic repository abstraction. Implementations always load the
/// tenant's <see cref="Tenant.Domains"/> collection, since domain mutation is only
/// ever performed through the aggregate.
/// </summary>
public interface ITenantRepository
{
    Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<Tenant?> GetBySlugAsync(string slug, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the tenant owning the given normalized host, if any. Used during
    /// tenant resolution — must be reachable without any tenant context already
    /// established (see <see cref="TenantDomain"/>).
    /// </summary>
    Task<Tenant?> GetByDomainHostAsync(string normalizedHost, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<Tenant> Items, long TotalCount)> ListAsync(int page, int pageSize, CancellationToken cancellationToken);

    Task AddAsync(Tenant tenant, CancellationToken cancellationToken);

    /// <summary>
    /// Explicitly registers a newly created <see cref="TenantDomain"/> as an
    /// insert. Required in addition to mutating <see cref="Tenant.Domains"/>:
    /// a new child discovered only via graph traversal of an already-tracked
    /// parent's navigation is not reliably classified as an insert by EF Core's
    /// change tracker in this model (it was observed to generate an UPDATE
    /// against a non-existent row instead) — see
    /// Knight.IntegrationTests.Security.PlatformAuthorizationTests.AddDomain_*
    /// and docs/architecture/multi-tenancy.md.
    /// </summary>
    Task RegisterNewDomainAsync(TenantDomain domain, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
