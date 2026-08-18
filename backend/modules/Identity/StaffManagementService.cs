using Identity.Abstractions;
using Identity.Authorization;
using Identity.Domain;
using Identity.Options;
using Microsoft.Extensions.Options;
using Knight.Application.Abstractions.Auditing;
using Knight.Application.Abstractions.Identity;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;

namespace Identity;

public sealed class StaffManagementService : IStaffManagementService
{
    private const int MaxPageSize = 100;

    private readonly ITenantUserRepository _userRepository;
    private readonly IRoleRepository _roleRepository;
    private readonly ITenantUserRoleRepository _userRoleRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _auditLogger;
    private readonly PasswordPolicyOptions _passwordPolicy;

    public StaffManagementService(
        ITenantUserRepository userRepository,
        IRoleRepository roleRepository,
        ITenantUserRoleRepository userRoleRepository,
        IPasswordHasher passwordHasher,
        IRefreshTokenService refreshTokenService,
        IDateTimeProvider dateTimeProvider,
        ICurrentUser currentUser,
        IAuditLogger auditLogger,
        IOptions<PasswordPolicyOptions> passwordPolicyOptions)
    {
        _userRepository = userRepository;
        _roleRepository = roleRepository;
        _userRoleRepository = userRoleRepository;
        _passwordHasher = passwordHasher;
        _refreshTokenService = refreshTokenService;
        _dateTimeProvider = dateTimeProvider;
        _currentUser = currentUser;
        _auditLogger = auditLogger;
        _passwordPolicy = passwordPolicyOptions.Value;
    }

    public async Task<TenantUser> CreateAsync(Guid tenantId, CreateStaffInput input, CancellationToken cancellationToken)
    {
        if (input.InitialPassword.Length < _passwordPolicy.MinLength || input.InitialPassword.Length > _passwordPolicy.MaxLength)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["initialPassword"] = ["The initial password does not meet the platform's password policy."]
            });
        }

        var normalizedEmail = input.Email.Trim().ToUpperInvariant();
        var existing = await _userRepository.GetByNormalizedEmailAsync(tenantId, normalizedEmail, cancellationToken);
        if (existing is not null)
        {
            throw new ConflictException($"A staff account with email '{input.Email}' already exists in this tenant.");
        }

        var roleIds = input.RoleIds.Distinct().ToArray();
        await EnsureRolesAssignableAsync(tenantId, roleIds, cancellationToken);

        var now = _dateTimeProvider.UtcNow;
        var user = TenantUser.Create(Guid.NewGuid(), now, tenantId, input.Email, _passwordHasher.Hash(input.InitialPassword), input.DisplayName);
        user.Activate(now);

        var assignments = roleIds.Select(roleId => TenantUserRole.Create(Guid.NewGuid(), tenantId, user.Id, roleId, now)).ToArray();

        try
        {
            await _userRepository.AddWithRoleAssignmentsAsync(user, assignments, cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            throw new ConflictException($"A staff account with email '{input.Email}' already exists in this tenant.");
        }

        await AuditAsync("TenantStaffCreated", tenantId, user.Id, cancellationToken, new Dictionary<string, string>
        {
            ["email"] = user.Email,
            ["roleCount"] = roleIds.Length.ToString()
        });

        return user;
    }

    public Task<TenantUser?> GetAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        _userRepository.GetByIdAsync(tenantId, userId, cancellationToken);

    public async Task<StaffListResult> ListAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var boundedPage = Math.Max(page, 1);
        var boundedPageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var (items, total) = await _userRepository.ListAsync(tenantId, boundedPage, boundedPageSize, cancellationToken);
        var mapped = items.Select(i => new StaffListItem { User = i.User, RoleIds = i.RoleIds }).ToArray();

        return new StaffListResult(mapped, total, boundedPage, boundedPageSize);
    }

    public async Task<TenantUser> EnableAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var user = await RequireUserAsync(tenantId, userId, cancellationToken);

        // Re-enabling never restores previously revoked sessions — a fresh
        // login is always required. See docs/architecture/authorization.md.
        user.Activate(_dateTimeProvider.UtcNow);
        await _userRepository.SaveChangesAsync(cancellationToken);

        await AuditAsync("TenantStaffEnabled", tenantId, userId, cancellationToken);
        return user;
    }

    public async Task<TenantUser> DisableAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var user = await RequireUserAsync(tenantId, userId, cancellationToken);

        user.Disable(_dateTimeProvider.UtcNow);
        await _userRepository.SaveChangesAsync(cancellationToken);
        await _refreshTokenService.RevokeAllForSubjectAsync(SubjectType.TenantUser, userId, "staff_disabled", cancellationToken);

        await AuditAsync("TenantStaffDisabled", tenantId, userId, cancellationToken);
        return user;
    }

    public async Task<TenantUser> UnlockAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var user = await RequireUserAsync(tenantId, userId, cancellationToken);

        user.Unlock(_dateTimeProvider.UtcNow);
        await _userRepository.SaveChangesAsync(cancellationToken);

        await AuditAsync("TenantStaffUnlocked", tenantId, userId, cancellationToken);
        return user;
    }

    public async Task<IReadOnlyCollection<Guid>> ReplaceRolesAsync(Guid tenantId, Guid userId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken)
    {
        await RequireUserAsync(tenantId, userId, cancellationToken);

        var distinctRoleIds = roleIds.Distinct().ToArray();
        await EnsureRolesAssignableAsync(tenantId, distinctRoleIds, cancellationToken);

        var now = _dateTimeProvider.UtcNow;
        await _userRoleRepository.ReplaceUserRolesAsync(tenantId, userId, distinctRoleIds, now, cancellationToken);

        await AuditAsync("TenantStaffRolesChanged", tenantId, userId, cancellationToken, new Dictionary<string, string>
        {
            ["roleCount"] = distinctRoleIds.Length.ToString()
        });

        return distinctRoleIds;
    }

    public async Task RevokeSessionsAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        await RequireUserAsync(tenantId, userId, cancellationToken);

        await _refreshTokenService.RevokeAllForSubjectAsync(SubjectType.TenantUser, userId, "administrative_revoke", cancellationToken);

        await AuditAsync("TenantStaffSessionsRevoked", tenantId, userId, cancellationToken);
    }

    private async Task<TenantUser> RequireUserAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        await _userRepository.GetByIdAsync(tenantId, userId, cancellationToken)
            ?? throw new NotFoundException(nameof(TenantUser), userId);

    /// <summary>
    /// Every requested role must exist within the tenant, and — unless the
    /// caller is a PlatformAdmin — the union of permissions those roles grant
    /// must be a subset of the caller's own effective permissions. See
    /// docs/architecture/authorization.md ("role assignment delegation").
    /// </summary>
    private async Task EnsureRolesAssignableAsync(Guid tenantId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken)
    {
        if (roleIds.Count == 0)
        {
            return;
        }

        var grantedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var roleId in roleIds)
        {
            var role = await _roleRepository.GetByIdAsync(tenantId, roleId, cancellationToken)
                ?? throw new NotFoundException(nameof(Role), roleId);

            var permissionKeys = await _roleRepository.GetPermissionKeysAsync(tenantId, role.Id, cancellationToken);
            grantedKeys.UnionWith(permissionKeys);
        }

        var isPlatformAdmin = _currentUser.PrincipalType == PrincipalType.PlatformAdmin;
        DelegationGuard.EnsureSubset(grantedKeys, _currentUser.Permissions, isPlatformAdmin);
    }

    private async Task AuditAsync(string action, Guid tenantId, Guid userId, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? metadata = null)
    {
        var isPlatformAdmin = _currentUser.PrincipalType == PrincipalType.PlatformAdmin;

        await _auditLogger.RecordAsync(new AuditEntry
        {
            ActorUserId = _currentUser.UserId,
            ActorType = isPlatformAdmin ? AuditActorType.PlatformAdmin : AuditActorType.TenantUser,
            TenantId = tenantId,
            Action = action,
            EntityType = nameof(TenantUser),
            EntityId = userId.ToString(),
            Metadata = metadata
        }, cancellationToken);
    }
}
