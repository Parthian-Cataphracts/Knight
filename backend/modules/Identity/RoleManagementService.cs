using Identity.Authorization;
using Identity.Domain;
using Knight.Application.Abstractions.Auditing;
using Knight.Application.Abstractions.Identity;
using Knight.Application.Abstractions.Time;
using Knight.Application.Authorization;
using Knight.Application.Exceptions;

namespace Identity;

public sealed class RoleManagementService : IRoleManagementService
{
    private const int MaxPageSize = 100;

    private readonly IRoleRepository _repository;
    private readonly IPermissionCatalog _permissionCatalog;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _auditLogger;

    public RoleManagementService(
        IRoleRepository repository,
        IPermissionCatalog permissionCatalog,
        IDateTimeProvider dateTimeProvider,
        ICurrentUser currentUser,
        IAuditLogger auditLogger)
    {
        _repository = repository;
        _permissionCatalog = permissionCatalog;
        _dateTimeProvider = dateTimeProvider;
        _currentUser = currentUser;
        _auditLogger = auditLogger;
    }

    public async Task<Role> CreateAsync(Guid tenantId, CreateRoleInput input, CancellationToken cancellationToken)
    {
        var normalizedName = input.Name.Trim().ToUpperInvariant();
        var existing = await _repository.GetByNormalizedNameAsync(tenantId, normalizedName, cancellationToken);
        if (existing is not null)
        {
            throw new ConflictException($"A role named '{input.Name}' already exists in this tenant.");
        }

        var permissionKeys = ValidateAgainstCatalog(input.PermissionKeys);
        EnsureDelegated(permissionKeys);

        var now = _dateTimeProvider.UtcNow;
        var role = Role.Create(Guid.NewGuid(), now, tenantId, input.Name);

        await _repository.AddAsync(role, cancellationToken);
        await _repository.ReplacePermissionsAsync(tenantId, role.Id, permissionKeys, now, cancellationToken);

        await AuditAsync("TenantRoleCreated", tenantId, role.Id, cancellationToken, new Dictionary<string, string>
        {
            ["name"] = role.Name,
            ["permissionCount"] = permissionKeys.Count.ToString()
        });

        return role;
    }

    public Task<Role?> GetAsync(Guid tenantId, Guid roleId, CancellationToken cancellationToken) =>
        _repository.GetByIdAsync(tenantId, roleId, cancellationToken);

    public Task<IReadOnlyCollection<string>> GetPermissionKeysAsync(Guid tenantId, Guid roleId, CancellationToken cancellationToken) =>
        _repository.GetPermissionKeysAsync(tenantId, roleId, cancellationToken);

    public async Task<RoleListResult> ListAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var boundedPage = Math.Max(page, 1);
        var boundedPageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var (items, total) = await _repository.ListAsync(tenantId, boundedPage, boundedPageSize, cancellationToken);
        return new RoleListResult(items, total, boundedPage, boundedPageSize);
    }

    public async Task<Role> RenameAsync(Guid tenantId, Guid roleId, string name, CancellationToken cancellationToken)
    {
        var role = await RequireRoleAsync(tenantId, roleId, cancellationToken);

        var normalizedName = name.Trim().ToUpperInvariant();
        if (normalizedName != role.NormalizedName)
        {
            var existing = await _repository.GetByNormalizedNameAsync(tenantId, normalizedName, cancellationToken);
            if (existing is not null)
            {
                throw new ConflictException($"A role named '{name}' already exists in this tenant.");
            }
        }

        role.Rename(name, _dateTimeProvider.UtcNow);
        await _repository.SaveChangesAsync(cancellationToken);

        await AuditAsync("TenantRoleUpdated", tenantId, role.Id, cancellationToken, new Dictionary<string, string> { ["name"] = role.Name });

        return role;
    }

    public async Task<IReadOnlyCollection<string>> SetPermissionsAsync(Guid tenantId, Guid roleId, IReadOnlyCollection<string> permissionKeys, CancellationToken cancellationToken)
    {
        await RequireRoleAsync(tenantId, roleId, cancellationToken);

        var validatedKeys = ValidateAgainstCatalog(permissionKeys);
        EnsureDelegated(validatedKeys);

        var now = _dateTimeProvider.UtcNow;
        await _repository.ReplacePermissionsAsync(tenantId, roleId, validatedKeys, now, cancellationToken);

        await AuditAsync("TenantRolePermissionsChanged", tenantId, roleId, cancellationToken, new Dictionary<string, string>
        {
            ["permissionCount"] = validatedKeys.Count.ToString()
        });

        return validatedKeys;
    }

    public async Task DeleteAsync(Guid tenantId, Guid roleId, CancellationToken cancellationToken)
    {
        var role = await RequireRoleAsync(tenantId, roleId, cancellationToken);

        var assignedCount = await _repository.CountAssignedUsersAsync(tenantId, roleId, cancellationToken);
        if (assignedCount > 0)
        {
            throw new ConflictException($"Role '{role.Name}' is currently assigned to {assignedCount} user(s) and cannot be deleted.");
        }

        await _repository.DeleteAsync(role, cancellationToken);

        await AuditAsync("TenantRoleDeleted", tenantId, roleId, cancellationToken, new Dictionary<string, string> { ["name"] = role.Name });
    }

    private async Task<Role> RequireRoleAsync(Guid tenantId, Guid roleId, CancellationToken cancellationToken) =>
        await _repository.GetByIdAsync(tenantId, roleId, cancellationToken)
            ?? throw new NotFoundException(nameof(Role), roleId);

    /// <summary>Rejects any key the shared platform permission catalog does not recognize — fail closed, never silently valid.</summary>
    private IReadOnlyCollection<string> ValidateAgainstCatalog(IReadOnlyCollection<string> permissionKeys)
    {
        var distinct = permissionKeys.Distinct(StringComparer.Ordinal).ToArray();
        var unknown = distinct.Where(key => !_permissionCatalog.IsRegistered(new Permission(key))).ToArray();

        if (unknown.Length > 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["permissionKeys"] = [$"Unknown permission key(s): {string.Join(", ", unknown)}"]
            });
        }

        return distinct;
    }

    private void EnsureDelegated(IReadOnlyCollection<string> permissionKeys)
    {
        var isPlatformAdmin = _currentUser.PrincipalType == PrincipalType.PlatformAdmin;
        DelegationGuard.EnsureSubset(permissionKeys, _currentUser.Permissions, isPlatformAdmin);
    }

    private async Task AuditAsync(string action, Guid tenantId, Guid entityId, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? metadata = null)
    {
        var isPlatformAdmin = _currentUser.PrincipalType == PrincipalType.PlatformAdmin;

        await _auditLogger.RecordAsync(new AuditEntry
        {
            ActorUserId = _currentUser.UserId,
            ActorType = isPlatformAdmin ? AuditActorType.PlatformAdmin : AuditActorType.TenantUser,
            TenantId = tenantId,
            Action = action,
            EntityType = nameof(Role),
            EntityId = entityId.ToString(),
            Metadata = metadata
        }, cancellationToken);
    }
}
