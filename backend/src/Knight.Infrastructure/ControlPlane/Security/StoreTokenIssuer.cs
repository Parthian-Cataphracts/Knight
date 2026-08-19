using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Infrastructure.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Stores;

namespace Knight.Infrastructure.ControlPlane.Security;

/// <summary>Claims carried only by store tokens.</summary>
public static class StoreClaims
{
    public const string StoreId = "store_id";

    /// <summary>
    /// The environment the <em>store</em> is registered as — distinct from
    /// <see cref="ControlPlaneClaims.Environment"/>, which is the environment the
    /// control plane itself runs in. One KNIGHT legitimately manages development,
    /// staging and production stores, so ingestion has to check the store's, not
    /// the host's.
    /// </summary>
    public const string StoreEnvironment = "store_env";

    public const string ClientId = "client_id";
}

/// <summary>
/// Mints the short-lived token a store uses for ingestion
/// ([`adr/0012`](../../../../docs/adr/0012-store-authentication-mechanism.md)).
///
/// The token says which store, which customer and which environment — and
/// nothing else. It carries no permissions: what a store principal may do is
/// fixed by the endpoints it can reach, not by anything it can present. Half an
/// hour of life is what makes it acceptable that, unlike a client secret, it
/// cannot be revoked.
/// </summary>
public sealed class StoreTokenIssuer : IStoreTokenIssuer
{
    private readonly JwtOptions _jwt;
    private readonly StoreOptions _stores;
    private readonly string _hostEnvironment;

    public StoreTokenIssuer(IOptions<JwtOptions> jwt, IOptions<StoreOptions> stores, IHostEnvironment environment)
    {
        _jwt = jwt.Value;
        _stores = stores.Value;
        _hostEnvironment = environment.EnvironmentName;
    }

    public IssuedStoreToken Issue(Guid storeId, Guid customerId, string environment, string clientId)
    {
        var now = DateTime.UtcNow;
        var expiresAt = now.Add(_stores.TokenLifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, storeId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(PrincipalTypes.ClaimType, PrincipalTypes.Store),
            new(StoreClaims.StoreId, storeId.ToString()),
            new(ControlPlaneClaims.CustomerId, customerId.ToString()),
            new(StoreClaims.StoreEnvironment, environment),
            new(StoreClaims.ClientId, clientId),
            new(ControlPlaneClaims.Environment, _hostEnvironment),
        };

        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims: claims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SigningKey)),
                SecurityAlgorithms.HmacSha256));

        return new IssuedStoreToken(
            new JwtSecurityTokenHandler().WriteToken(token),
            expiresAt,
            _stores.TokenLifetime);
    }
}

/// <summary>
/// Signs what a store has to be able to trust while KNIGHT is unreachable.
///
/// The per-store key is derived rather than stored: HMAC of the store's identity
/// under one master key. Nothing new goes in the database, rotating the master
/// key rotates every store's key at once, and a leaked per-store key is useless
/// against any other store. The store receives its derived key in the handshake
/// response, over TLS, having already proven it is that store.
/// </summary>
public sealed class StorePayloadSigner : IStorePayloadSigner
{
    private readonly byte[] _masterKey;

    public StorePayloadSigner(IOptions<StoreOptions> stores, IOptions<JwtOptions> jwt, IHostEnvironment environment)
    {
        var configured = stores.Value.IntegrationSigningKey;

        if (string.IsNullOrWhiteSpace(configured))
        {
            // Outside Development this is unreachable: StoreSigningKeyValidator
            // fails the host at startup rather than letting it derive store keys
            // from the token-signing key, which would mean one leak compromises
            // both.
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "Stores:IntegrationSigningKey is required outside Development.");
            }

            configured = jwt.Value.SigningKey;
        }

        _masterKey = Encoding.UTF8.GetBytes(configured);
    }

    public string DeriveVerificationKey(Guid storeId, string environment) =>
        Convert.ToBase64String(Derive(storeId, environment));

    public string Sign(Guid storeId, string environment, string canonicalPayload)
    {
        using var hmac = new HMACSHA256(Derive(storeId, environment));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonicalPayload)));
    }

    /// <summary>
    /// The environment is part of the derivation, so a key handed to a staging
    /// store cannot verify a payload signed for the production store of the same
    /// customer.
    /// </summary>
    private byte[] Derive(Guid storeId, string environment) =>
        HMACSHA256.HashData(_masterKey, Encoding.UTF8.GetBytes($"store-payload|{storeId:D}|{environment}"));
}
