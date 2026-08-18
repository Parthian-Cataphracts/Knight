using Knight.Application.Abstractions.Identity;

namespace Knight.Api.Composition;

/// <summary>
/// Reads the authenticated caller from the current <see cref="HttpContext"/>.
/// The "principal_type" claim (set by <c>JwtAccessTokenGenerator</c>) is what
/// distinguishes a platform admin token from a tenant user token — never the
/// mere presence or absence of a tenant claim.
/// </summary>
internal sealed class HttpContextCurrentUser : ICurrentUser
{
    private const string PrincipalTypeClaim = "principal_type";
    private const string PermissionClaim = "permission";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private System.Security.Claims.ClaimsPrincipal? User => _httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated ?? false;

    public Guid? UserId
    {
        get
        {
            var subject = User?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            return Guid.TryParse(subject, out var id) ? id : null;
        }
    }

    public PrincipalType? PrincipalType
    {
        get
        {
            var value = User?.FindFirst(PrincipalTypeClaim)?.Value;
            return value switch
            {
                "platform_admin" => Application.Abstractions.Identity.PrincipalType.PlatformAdmin,
                "tenant_user" => Application.Abstractions.Identity.PrincipalType.TenantUser,
                _ => null
            };
        }
    }

    public IReadOnlyCollection<string> Permissions =>
        User?.FindAll(PermissionClaim).Select(c => c.Value).ToArray() ?? [];

    public bool HasPermission(string permissionKey) => Permissions.Contains(permissionKey, StringComparer.Ordinal);
}
