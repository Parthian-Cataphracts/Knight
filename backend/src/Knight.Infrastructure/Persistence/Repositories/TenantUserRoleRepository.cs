using Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Knight.Infrastructure.Persistence.Repositories;

internal sealed class TenantUserRoleRepository : ITenantUserRoleRepository
{
    private readonly PlatformDbContext _context;

    public TenantUserRoleRepository(PlatformDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyCollection<Guid>> GetRoleIdsForUserAsync(Guid tenantId, Guid tenantUserId, CancellationToken cancellationToken) =>
        await _context.TenantUserRoles
            .Where(a => a.TenantId == tenantId && a.TenantUserId == tenantUserId)
            .Select(a => a.RoleId)
            .ToArrayAsync(cancellationToken);

    public async Task ReplaceUserRolesAsync(Guid tenantId, Guid tenantUserId, IReadOnlyCollection<Guid> roleIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        await _context.TenantUserRoles
            .Where(a => a.TenantId == tenantId && a.TenantUserId == tenantUserId)
            .ExecuteDeleteAsync(cancellationToken);

        if (roleIds.Count > 0)
        {
            var rows = roleIds.Select(roleId => TenantUserRole.Create(Guid.NewGuid(), tenantId, tenantUserId, roleId, now));
            await _context.TenantUserRoles.AddRangeAsync(rows, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// One indexed join, not one query per role — see
    /// docs/architecture/authorization.md ("permission query performance").
    /// </summary>
    public async Task<IReadOnlyCollection<string>> GetEffectivePermissionKeysAsync(Guid tenantId, Guid tenantUserId, CancellationToken cancellationToken)
    {
        var roleIds = _context.TenantUserRoles
            .Where(a => a.TenantId == tenantId && a.TenantUserId == tenantUserId)
            .Select(a => a.RoleId);

        return await _context.RolePermissions
            .Where(rp => rp.TenantId == tenantId && roleIds.Contains(rp.RoleId))
            .Select(rp => rp.PermissionKey)
            .Distinct()
            .ToArrayAsync(cancellationToken);
    }
}
