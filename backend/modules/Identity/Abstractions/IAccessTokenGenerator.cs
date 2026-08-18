using Identity.Domain;

namespace Identity.Abstractions;

public sealed record AccessTokenResult(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues short-lived access tokens for an authenticated subject. Refresh tokens
/// are issued and persisted separately by application-level use cases.
/// </summary>
public interface IAccessTokenGenerator
{
    AccessTokenResult GenerateForPlatformAdmin(PlatformAdmin admin);

    AccessTokenResult GenerateForTenantUser(TenantUser user, IReadOnlyCollection<string> permissionKeys);
}
