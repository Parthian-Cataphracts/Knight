using AccessControl.Domain;
using Knight.Application.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Knight.Infrastructure.ControlPlane.Repositories;

internal sealed class ControlPlaneUserRepository : IControlPlaneUserRepository
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly ControlPlaneDbContext _context;

    public ControlPlaneUserRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<ControlPlaneUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Users.Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    /// <summary>
    /// Authentication happens before any scope exists, so this one query
    /// deliberately ignores the isolation filter: the scope is derived from the
    /// account it finds, not the other way round. Nothing about the account is
    /// returned to the caller unless the credentials check out.
    /// </summary>
    public Task<ControlPlaneUser?> FindForAuthenticationAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        _context.Users
            .IgnoreQueryFilters()
            .Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

    public Task<bool> ExistsWithEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        _context.Users.IgnoreQueryFilters().AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

    public async Task<(IReadOnlyCollection<ControlPlaneUser> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        Guid? customerId,
        string? search,
        CancellationToken cancellationToken)
    {
        var query = _context.Users.Include(u => u.Roles).AsQueryable();

        if (customerId is not null)
        {
            query = query.Where(u => u.CustomerId == customerId);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(u => EF.Functions.ILike(u.Email, term) || EF.Functions.ILike(u.DisplayName, term));
        }

        var ordered = query.OrderBy(u => u.Email).ThenBy(u => u.Id);

        var totalCount = await ordered.LongCountAsync(cancellationToken);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddAsync(ControlPlaneUser user, CancellationToken cancellationToken) =>
        await _context.Users.AddAsync(user, cancellationToken);

    public void RegisterNewAssignment(UserRoleAssignment assignment) =>
        _context.Entry(assignment).State = EntityState.Added;

    public void RemoveAssignment(UserRoleAssignment assignment) =>
        _context.Entry(assignment).State = EntityState.Deleted;

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState })
        {
            throw new UniqueConstraintViolationException("The account conflicts with an existing one.", ex);
        }
    }
}

internal sealed class ControlPlaneRoleRepository : IRoleRepository
{
    private readonly ControlPlaneDbContext _context;

    public ControlPlaneRoleRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<Role?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    /// <summary>
    /// Used by seeding and by uniqueness checks, both of which run before or
    /// outside a customer scope, so the filter is bypassed here and the caller
    /// states the owner explicitly instead.
    /// </summary>
    public Task<Role?> GetByNameAsync(string normalizedName, RoleScope scope, Guid? customerId, CancellationToken cancellationToken) =>
        _context.Roles
            .IgnoreQueryFilters()
            .Include(r => r.Permissions)
            .FirstOrDefaultAsync(
                r => r.NormalizedName == normalizedName && r.Scope == scope && r.CustomerId == customerId,
                cancellationToken);

    public async Task<IReadOnlyCollection<Role>> ListAsync(RoleScope? scope, CancellationToken cancellationToken)
    {
        var query = _context.Roles.Include(r => r.Permissions).AsQueryable();

        if (scope is not null)
        {
            query = query.Where(r => r.Scope == scope);
        }

        return await query.OrderBy(r => r.Name).ToArrayAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves the roles behind one account's permissions. It bypasses the
    /// isolation filter on purpose: a customer user's own roles include
    /// platform-owned system roles such as CustomerOwner, which carry no
    /// customer id and would otherwise be filtered out of their own permission
    /// set. The account id constrains the result to that one user's rows.
    /// </summary>
    public async Task<IReadOnlyCollection<Role>> GetRolesForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var roleIds = await _context.UserRoleAssignments
            .IgnoreQueryFilters()
            .Where(a => a.UserId == userId)
            .Select(a => a.RoleId)
            .ToArrayAsync(cancellationToken);

        if (roleIds.Length == 0)
        {
            return [];
        }

        return await _context.Roles
            .IgnoreQueryFilters()
            .Include(r => r.Permissions)
            .Where(r => roleIds.Contains(r.Id))
            .ToArrayAsync(cancellationToken);
    }

    public async Task AddAsync(Role role, CancellationToken cancellationToken) =>
        await _context.Roles.AddAsync(role, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}

internal sealed class UserSessionRepository : IUserSessionRepository
{
    private readonly ControlPlaneDbContext _context;

    public UserSessionRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Refresh happens with no established scope, so the filter is bypassed; the
    /// token hash is the credential, and only the holder of the raw token can
    /// produce it.
    /// </summary>
    public Task<UserSession?> FindByTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken) =>
        _context.UserSessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.RefreshTokenHash == refreshTokenHash, cancellationToken);

    public Task<UserSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.UserSessions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task AddAsync(UserSession session, CancellationToken cancellationToken) =>
        await _context.UserSessions.AddAsync(session, cancellationToken);

    public async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        var sessions = await _context.UserSessions
            .IgnoreQueryFilters()
            .Where(s => s.FamilyId == familyId && s.RevokedAt == null)
            .ToArrayAsync(cancellationToken);

        foreach (var session in sessions)
        {
            session.Revoke(now, reason);
        }
    }

    public async Task RevokeAllForUserAsync(Guid userId, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        var sessions = await _context.UserSessions
            .IgnoreQueryFilters()
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .ToArrayAsync(cancellationToken);

        foreach (var session in sessions)
        {
            session.Revoke(now, reason);
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}

internal sealed class AuditLogRepository : IAuditLogRepository
{
    private readonly ControlPlaneDbContext _context;

    public AuditLogRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(AuditLog entry, CancellationToken cancellationToken) =>
        await _context.AuditLogs.AddAsync(entry, cancellationToken);

    public async Task<(IReadOnlyCollection<AuditLog> Items, long TotalCount)> QueryAsync(
        AuditLogQuery query,
        CancellationToken cancellationToken)
    {
        var entries = _context.AuditLogs.AsQueryable();

        if (query.ActorUserId is not null)
        {
            entries = entries.Where(a => a.ActorUserId == query.ActorUserId);
        }

        if (!string.IsNullOrWhiteSpace(query.TargetType))
        {
            entries = entries.Where(a => a.TargetType == query.TargetType);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            entries = entries.Where(a => a.Action == query.Action);
        }

        if (query.From is not null)
        {
            entries = entries.Where(a => a.OccurredAt >= query.From);
        }

        if (query.To is not null)
        {
            entries = entries.Where(a => a.OccurredAt <= query.To);
        }

        var ordered = entries.OrderByDescending(a => a.OccurredAt).ThenBy(a => a.Id);

        var totalCount = await ordered.LongCountAsync(cancellationToken);
        var items = await ordered
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToArrayAsync(cancellationToken);

        return (items, totalCount);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}
