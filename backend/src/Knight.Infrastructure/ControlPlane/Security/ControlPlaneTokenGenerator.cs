using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AccessControl.Abstractions;
using AccessControl.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Infrastructure.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Knight.Infrastructure.ControlPlane.Security;

/// <summary>
/// Issues dashboard access tokens.
///
/// The token says who the principal is and which session it belongs to; it does
/// not say what they may do. Permissions are resolved per request from the
/// database, so revoking a role takes effect immediately rather than at the next
/// login (docs/authentication.md section 1).
///
/// Every token carries the environment it was minted in, so one obtained from
/// staging cannot be replayed against production even if the signing key were
/// somehow shared.
/// </summary>
public sealed class ControlPlaneTokenGenerator : IControlPlaneTokenGenerator
{
    private readonly JwtOptions _options;
    private readonly string _environment;

    public ControlPlaneTokenGenerator(IOptions<JwtOptions> options, IHostEnvironment environment)
    {
        _options = options.Value;
        _environment = environment.EnvironmentName;
    }

    public IssuedAccessToken Issue(ControlPlaneUser user, UserSession session, IReadOnlyCollection<string> roleNames)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(PrincipalTypes.ClaimType, PrincipalTypes.User),
            new(ControlPlaneClaims.SessionId, session.Id.ToString()),
            new(ControlPlaneClaims.Environment, _environment),

            // Recorded so authorization can refuse everything but enrolment while
            // a required second factor is still outstanding.
            new(ControlPlaneClaims.MfaSatisfied, session.MfaSatisfied ? "mfa" : "pwd"),
        };

        if (user.CustomerId is { } customerId)
        {
            claims.Add(new Claim(ControlPlaneClaims.CustomerId, customerId.ToString()));
        }

        claims.AddRange(roleNames.Select(role => new Claim(ControlPlaneClaims.Role, role)));

        var now = DateTime.UtcNow;
        var expiresAt = now.AddMinutes(_options.PlatformAccessTokenLifetimeMinutes);

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));

        return new IssuedAccessToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
