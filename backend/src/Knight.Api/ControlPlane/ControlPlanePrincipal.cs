using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Knight.Application.Abstractions.ControlPlane;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Reads the control-plane claims off the current request. Nothing here decides
/// what the caller may do — that is resolved from the database per request — it
/// only says who the caller is.
/// </summary>
public interface IControlPlanePrincipal
{
    bool IsControlPlaneUser { get; }

    Guid? UserId { get; }

    Guid? CustomerId { get; }

    Guid? SessionId { get; }

    bool IsPlatformStaff { get; }

    /// <summary>False while a required second factor is still outstanding for this session.</summary>
    bool MfaSatisfied { get; }

    string? Email { get; }

    IReadOnlyCollection<string> Roles { get; }
}

internal sealed class HttpControlPlanePrincipal : IControlPlanePrincipal
{
    private readonly ClaimsPrincipal? _user;

    public HttpControlPlanePrincipal(IHttpContextAccessor accessor)
    {
        _user = accessor.HttpContext?.User;
    }

    public bool IsControlPlaneUser =>
        _user?.FindFirstValue(PrincipalTypes.ClaimType) == PrincipalTypes.User;

    public Guid? UserId => ParseGuid(_user?.FindFirstValue(JwtRegisteredClaimNames.Sub));

    public Guid? CustomerId => ParseGuid(_user?.FindFirstValue(ControlPlaneClaims.CustomerId));

    public Guid? SessionId => ParseGuid(_user?.FindFirstValue(ControlPlaneClaims.SessionId));

    public bool IsPlatformStaff => IsControlPlaneUser && CustomerId is null;

    public bool MfaSatisfied => _user?.FindFirstValue(ControlPlaneClaims.MfaSatisfied) == "mfa";

    public string? Email => _user?.FindFirstValue(JwtRegisteredClaimNames.Email);

    public IReadOnlyCollection<string> Roles =>
        _user?.FindAll(ControlPlaneClaims.Role).Select(claim => claim.Value).ToArray() ?? [];

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var parsed) ? parsed : null;
}
