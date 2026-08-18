using Identity.Authentication;

namespace Identity;

public interface IPlatformAuthenticationService
{
    Task<LoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken);

    Task<RefreshResult> RefreshAsync(string rawRefreshToken, CancellationToken cancellationToken);

    Task LogoutAsync(string rawRefreshToken, CancellationToken cancellationToken);

    Task LogoutAllAsync(Guid adminId, CancellationToken cancellationToken);

    Task<ChangePasswordOutcome> ChangePasswordAsync(Guid adminId, string currentPassword, string newPassword, CancellationToken cancellationToken);
}
