using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace AccessControl.Domain;

/// <summary>
/// A named bundle of permissions. Roles are data, not code: the seeded system
/// roles are rows like any other, and operators may add their own
/// (docs/authorization.md section 1).
///
/// A custom role belongs to whoever may manage it — a platform role has no
/// customer, a customer's own role carries its <see cref="CustomerId"/> so the
/// isolation filter keeps it invisible to everyone else.
/// </summary>
public sealed class Role : AuditableEntity, ICustomerScoped
{
    public string Name { get; private set; }

    /// <summary>Uppercase-invariant form of <see cref="Name"/>; uniqueness is checked against this.</summary>
    public string NormalizedName { get; private set; }

    public string? Description { get; private set; }

    public RoleScope Scope { get; private set; }

    /// <summary>Seeded roles cannot be deleted; removing one would strip access from every account holding it.</summary>
    public bool IsSystem { get; private set; }

    public Guid? CustomerId { get; private set; }

    private readonly List<RolePermission> _permissions = [];

    public IReadOnlyCollection<RolePermission> Permissions => _permissions.AsReadOnly();

    private Role()
    {
        Name = string.Empty;
        NormalizedName = string.Empty;
    }

    private Role(
        Guid id,
        DateTimeOffset createdAt,
        string name,
        string? description,
        RoleScope scope,
        bool isSystem,
        Guid? customerId)
        : base(id, createdAt)
    {
        Name = name;
        NormalizedName = name.ToUpperInvariant();
        Description = description;
        Scope = scope;
        IsSystem = isSystem;
        CustomerId = customerId;
    }

    public static Role CreateSystem(Guid id, DateTimeOffset createdAt, string name, RoleScope scope, string? description = null)
        => new(id, createdAt, ValidateName(name), Trim(description), scope, isSystem: true, customerId: null);

    public static Role CreateCustom(
        Guid id,
        DateTimeOffset createdAt,
        string name,
        RoleScope scope,
        Guid? customerId,
        string? description = null)
    {
        if (scope is RoleScope.Customer && customerId is null)
        {
            throw DomainException.Validation("A customer-scoped role must belong to a customer.");
        }

        if (scope is RoleScope.Platform && customerId is not null)
        {
            throw DomainException.Validation("A platform-scoped role cannot belong to a customer.");
        }

        return new Role(id, createdAt, ValidateName(name), Trim(description), scope, isSystem: false, customerId);
    }

    public void Describe(string name, string? description, DateTimeOffset now)
    {
        EnsureMutable();
        Name = ValidateName(name);
        NormalizedName = Name.ToUpperInvariant();
        Description = Trim(description);
        MarkUpdated(now);
    }

    public void Grant(string permissionKey, DateTimeOffset now)
    {
        EnsureMutable();

        var key = ControlPlanePermissions.Require(permissionKey);

        if (Scope is RoleScope.Customer && !ControlPlanePermissions.IsCustomerAssignable(key))
        {
            throw DomainException.Conflict($"Permission '{key}' cannot be granted to a customer-scoped role.");
        }

        if (_permissions.Any(p => p.PermissionKey == key))
        {
            return;
        }

        _permissions.Add(RolePermission.Create(Id, key));
        MarkUpdated(now);
    }

    public void Revoke(string permissionKey, DateTimeOffset now)
    {
        EnsureMutable();

        var existing = _permissions.SingleOrDefault(p => p.PermissionKey == permissionKey);
        if (existing is null)
        {
            return;
        }

        _permissions.Remove(existing);
        MarkUpdated(now);
    }

    /// <summary>
    /// Replaces the whole permission set in one operation, which is how the
    /// dashboard edits a role: the caller sends the intended final set rather
    /// than a diff it would have to compute correctly.
    /// </summary>
    public void ReplacePermissions(IEnumerable<string> permissionKeys, DateTimeOffset now)
    {
        EnsureMutable();

        var keys = permissionKeys.Select(ControlPlanePermissions.Require).Distinct().ToArray();

        foreach (var key in keys)
        {
            if (Scope is RoleScope.Customer && !ControlPlanePermissions.IsCustomerAssignable(key))
            {
                throw DomainException.Conflict($"Permission '{key}' cannot be granted to a customer-scoped role.");
            }
        }

        _permissions.RemoveAll(p => !keys.Contains(p.PermissionKey));

        foreach (var key in keys.Where(key => _permissions.All(p => p.PermissionKey != key)))
        {
            _permissions.Add(RolePermission.Create(Id, key));
        }

        MarkUpdated(now);
    }

    /// <summary>
    /// Seeding is the one path allowed to write a system role's permissions:
    /// the definition lives in code, so a redeploy must be able to reconcile it.
    /// </summary>
    internal void SeedPermissions(IEnumerable<string> permissionKeys, DateTimeOffset now)
    {
        var keys = permissionKeys.Select(ControlPlanePermissions.Require).Distinct().ToArray();

        _permissions.RemoveAll(p => !keys.Contains(p.PermissionKey));

        foreach (var key in keys.Where(key => _permissions.All(p => p.PermissionKey != key)))
        {
            _permissions.Add(RolePermission.Create(Id, key));
        }

        MarkUpdated(now);
    }

    public bool HasPermission(string permissionKey) => _permissions.Any(p => p.PermissionKey == permissionKey);

    private void EnsureMutable()
    {
        if (IsSystem)
        {
            throw DomainException.Conflict("A system role cannot be modified.");
        }
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("Role name is required.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > 100)
        {
            throw DomainException.Validation("Role name must be 100 characters or fewer.");
        }

        return trimmed;
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
