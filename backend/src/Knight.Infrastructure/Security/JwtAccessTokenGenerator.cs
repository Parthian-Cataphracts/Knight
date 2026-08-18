using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Identity.Abstractions;
using Identity.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Tenancy;

namespace Knight.Infrastructure.Security;

/// <summary>
/// Issues signed JWT access tokens. Platform admin tokens never carry a tenant_id
/// claim; tenant user tokens always do — this is what downstream tenant
/// resolution and authorization rely on to tell the two contexts apart.
/// </summary>
public sealed class JwtAccessTokenGenerator : IAccessTokenGenerator
{
    private readonly JwtOptions _options;

    public JwtAccessTokenGenerator(IOptions<JwtOptions> options)
    {
        _options = options.Value;
    }

    public AccessTokenResult GenerateForPlatformAdmin(PlatformAdmin admin)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, admin.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, admin.Email),
            new("principal_type", "platform_admin")
        };

        return Generate(claims, TimeSpan.FromMinutes(_options.PlatformAccessTokenLifetimeMinutes));
    }

    public AccessTokenResult GenerateForTenantUser(TenantUser user, IReadOnlyCollection<string> permissionKeys)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("principal_type", "tenant_user"),
            new(DomainTenantResolver.TenantIdClaimType, user.TenantId.ToString())
        };

        claims.AddRange(permissionKeys.Select(key => new Claim("permission", key)));

        return Generate(claims, TimeSpan.FromMinutes(_options.TenantAccessTokenLifetimeMinutes));
    }

    private AccessTokenResult Generate(List<Claim> claims, TimeSpan lifetime)
    {
        claims.Add(new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()));

        var now = DateTime.UtcNow;
        var expiresAt = now.Add(lifetime);

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: credentials);

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        return new AccessTokenResult(tokenString, expiresAt);
    }
}
