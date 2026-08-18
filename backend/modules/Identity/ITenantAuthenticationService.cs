using Identity.Authentication;

namespace Identity;

public interface ITenantAuthenticationService
{
    Task<LoginResult> LoginAsync(Guid tenantId, string email, string password, CancellationToken cancellationToken);

    /// <param name="expectedTenantId">The host-resolved tenant — must match the refresh token's own bound tenant.</param>
    Task<RefreshResult> RefreshAsync(string rawRefreshToken, Guid expectedTenantId, CancellationToken cancellationToken);

    Task LogoutAsync(string rawRefreshToken, CancellationToken cancellationToken);

    Task LogoutAllAsync(Guid userId, CancellationToken cancellationToken);

    Task<ChangePasswordOutcome> ChangePasswordAsync(Guid tenantId, Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken);
}
