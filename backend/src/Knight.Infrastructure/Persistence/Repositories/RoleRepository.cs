using Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Knight.Infrastructure.Persistence.Repositories;

internal sealed class RoleRepository : IRoleRepository
{
    private readonly PlatformDbContext _context;

    public RoleRepository(PlatformDbContext context)
    {
        _context = context;
    }

    public Task<Role?> GetByIdAsync(Guid tenantId, Guid roleId, CancellationToken cancellationToken) =>
        _context.Roles.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == roleId, cancellationToken);

    public Task<Role?> GetByNormalizedNameAsync(Guid tenantId, string normalizedName, CancellationToken cancellationToken) =>
        _context.Roles.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.NormalizedName == normalizedName, cancellationToken);

    public async Task<(IReadOnlyCollection<RoleListItem> Items, long TotalCount)> ListAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = _context.Roles.Where(r => r.TenantId == tenantId).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id);

        var totalCount = await query.LongCountAsync(cancellationToken);
        var roles = await query.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);

        if (roles.Length == 0)
        {
            return (Array.Empty<RoleListItem>(), totalCount);
        }

        var roleIds = roles.Select(r => r.Id).ToArray();

        var permissionCounts = await _context.RolePermissions
            .Where(rp => rp.TenantId == tenantId && roleIds.Contains(rp.RoleId))
            .GroupBy(rp => rp.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToArrayAsync(cancellationToken);

        var userCounts = await _context.TenantUserRoles
            .Where(a => a.TenantId == tenantId && roleIds.Contains(a.RoleId))
            .GroupBy(a => a.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToArrayAsync(cancellationToken);

        var items = roles
            .Select(r => new RoleListItem
            {
                Role = r,
                PermissionCount = permissionCounts.FirstOrDefault(p => p.RoleId == r.Id)?.Count ?? 0,
                AssignedUserCount = userCounts.FirstOrDefault(u => u.RoleId == r.Id)?.Count ?? 0
            })
            .ToArray();

        return (items, totalCount);
    }

    public async Task AddAsync(Role role, CancellationToken cancellationToken)
    {
        await _context.Roles.AddAsync(role, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Role role, CancellationToken cancellationToken)
    {
        _context.Roles.Remove(role);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);

    public async Task<IReadOnlyCollection<string>> GetPermissionKeysAsync(Guid tenantId, Guid roleId, CancellationToken cancellationToken) =>
        await _context.RolePermissions
            .Where(rp => rp.TenantId == tenantId && rp.RoleId == roleId)
            .Select(rp => rp.PermissionKey)
            .ToArrayAsync(cancellationToken);

    public async Task ReplacePermissionsAsync(Guid tenantId, Guid roleId, IReadOnlyCollection<string> permissionKeys, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        await _context.RolePermissions
            .Where(rp => rp.TenantId == tenantId && rp.RoleId == roleId)
            .ExecuteDeleteAsync(cancellationToken);

        if (permissionKeys.Count > 0)
        {
            var rows = permissionKeys.Select(key => RolePermission.Create(Guid.NewGuid(), tenantId, roleId, key, now));
            await _context.RolePermissions.AddRangeAsync(rows, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public Task<int> CountAssignedUsersAsync(Guid tenantId, Guid roleId, CancellationToken cancellationToken) =>
        _context.TenantUserRoles.CountAsync(a => a.TenantId == tenantId && a.RoleId == roleId, cancellationToken);
}
