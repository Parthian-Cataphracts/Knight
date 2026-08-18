using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Identity.Domain;

/// <summary>
/// A tenant-scoped, named collection of permissions that can be assigned to
/// tenant users. Belongs to exactly one tenant — never global. The actual
/// permission grants live in <see cref="RolePermission"/> rows managed
/// through <see cref="IRoleRepository"/>, not on this entity, so they can be
/// validated against the permission catalog and protected by real database
/// constraints — see docs/architecture/authorization.md.
/// </summary>
public sealed class Role : AuditableEntity, ITenantScoped
{
    private const int MaxNameLength = 100;

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    /// <summary>Uppercase-invariant form of <see cref="Name"/> — the only column uniqueness/lookup uses.</summary>
    public string NormalizedName { get; private set; }

    private Role()
    {
        Name = string.Empty;
        NormalizedName = string.Empty;
    }

    private Role(Guid id, DateTimeOffset createdAt, Guid tenantId, string name, string normalizedName)
        : base(id, createdAt)
    {
        TenantId = tenantId;
        Name = name;
        NormalizedName = normalizedName;
    }

    public static Role Create(Guid id, DateTimeOffset createdAt, Guid tenantId, string name)
    {
        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("A role must belong to a tenant.");
        }

        var (trimmed, normalized) = ValidateName(name);
        return new Role(id, createdAt, tenantId, trimmed, normalized);
    }

    public void Rename(string name, DateTimeOffset now)
    {
        var (trimmed, normalized) = ValidateName(name);
        Name = trimmed;
        NormalizedName = normalized;
        MarkUpdated(now);
    }

    private static (string Trimmed, string Normalized) ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("Role name is required.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw DomainException.Validation($"Role name cannot exceed {MaxNameLength} characters.");
        }

        return (trimmed, trimmed.ToUpperInvariant());
    }
}
