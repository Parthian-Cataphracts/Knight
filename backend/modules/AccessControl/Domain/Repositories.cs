namespace AccessControl.Domain;

/// <summary>
/// Persistence contract for control-plane accounts. Implementations apply the
/// caller's customer scope, so a customer-scoped principal cannot read another
/// customer's account even by guessing an id (docs/authorization.md section 4).
/// </summary>
public interface IControlPlaneUserRepository
{
    Task<ControlPlaneUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves an account for authentication. Login happens before any scope is
    /// known, so this one query deliberately ignores the customer filter; the
    /// resulting scope is derived from the account itself.
    /// </summary>
    Task<ControlPlaneUser?> FindForAuthenticationAsync(string normalizedEmail, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves an account by id for authentication. Like the lookup above this
    /// runs before any scope exists — a refresh request carries no established
    /// customer — so it is not customer-filtered. The caller must already hold
    /// proof of identity, such as an unconsumed refresh token.
    /// </summary>
    Task<ControlPlaneUser?> FindForAuthenticationByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves an account from the hash of an activation token. Runs before any
    /// scope exists — whoever follows an invitation link is not signed in — so
    /// it is deliberately not customer-filtered, and it matches on the hash
    /// rather than on anything the caller could enumerate.
    /// </summary>
    Task<ControlPlaneUser?> FindByActivationTokenAsync(string tokenHash, CancellationToken cancellationToken);

    Task<bool> ExistsWithEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<ControlPlaneUser> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        Guid? customerId,
        string? search,
        CancellationToken cancellationToken);

    Task AddAsync(ControlPlaneUser user, CancellationToken cancellationToken);

    void RegisterNewAssignment(UserRoleAssignment assignment);

    void RemoveAssignment(UserRoleAssignment assignment);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IRoleRepository
{
    Task<Role?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<Role?> GetByNameAsync(string normalizedName, RoleScope scope, Guid? customerId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<Role>> ListAsync(RoleScope? scope, CancellationToken cancellationToken);

    /// <summary>Loads the roles held by one account together with their permissions.</summary>
    Task<IReadOnlyCollection<Role>> GetRolesForUserAsync(Guid userId, CancellationToken cancellationToken);

    Task AddAsync(Role role, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IUserSessionRepository
{
    /// <summary>
    /// Looks a session up by token hash. Like authentication itself this runs
    /// before a scope exists, so it is not customer-filtered; the caller must
    /// check the session belongs to the account it is refreshing.
    /// </summary>
    Task<UserSession?> FindByTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken);

    Task<UserSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(UserSession session, CancellationToken cancellationToken);

    /// <summary>Revokes every unexpired token in a family — the response to a replayed refresh token.</summary>
    Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, string reason, CancellationToken cancellationToken);

    Task RevokeAllForUserAsync(Guid userId, DateTimeOffset now, string reason, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed record AuditLogQuery(
    int Page,
    int PageSize,
    Guid? ActorUserId,
    string? TargetType,
    string? Action,
    DateTimeOffset? From,
    DateTimeOffset? To);

public interface IAuditLogRepository
{
    Task AddAsync(AuditLog entry, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<AuditLog> Items, long TotalCount)> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
