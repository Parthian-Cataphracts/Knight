using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Tenancy.Domain;

/// <summary>
/// A host mapped to a tenant. Owned by the <see cref="Tenant"/> aggregate — always
/// created, promoted, and removed through <see cref="Tenant"/> methods so the
/// "at most one primary domain per type" invariant can be enforced in one place.
/// Deliberately does NOT implement <see cref="ITenantScoped"/>: host-based tenant
/// resolution must be able to query domains before any tenant context exists, and
/// the automatic tenant query filter would otherwise make that lookup always
/// return nothing (see docs/architecture/multi-tenancy.md).
/// </summary>
public sealed class TenantDomain : Entity
{
    public Guid TenantId { get; private set; }

    public string Host { get; private set; }

    public TenantDomainType Type { get; private set; }

    public bool IsPrimary { get; private set; }

    public TenantDomainVerificationStatus VerificationStatus { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? VerifiedAt { get; private set; }

    private TenantDomain()
    {
        Host = string.Empty;
    }

    internal TenantDomain(Guid id, Guid tenantId, string normalizedHost, TenantDomainType type, bool isPrimary, DateTimeOffset createdAt)
        : base(id)
    {
        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("A tenant domain must belong to a tenant.");
        }

        TenantId = tenantId;
        Host = normalizedHost;
        Type = type;
        IsPrimary = isPrimary;
        VerificationStatus = TenantDomainVerificationStatus.Pending;
        CreatedAt = createdAt;
    }

    internal void MarkPrimary() => IsPrimary = true;

    internal void ClearPrimary() => IsPrimary = false;

    internal void MarkVerified(DateTimeOffset now)
    {
        VerificationStatus = TenantDomainVerificationStatus.Verified;
        VerifiedAt = now;
    }
}
