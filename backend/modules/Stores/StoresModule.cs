using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Stores;

/// <summary>
/// Bound from configuration (section "Stores").
/// </summary>
public sealed class StoreOptions
{
    public const string SectionName = "Stores";

    /// <summary>
    /// How long a rotated credential keeps working. Long enough for a store to
    /// pick the new secret up on its next configuration reload, short enough that
    /// a compromised secret is not usable for a working day (risks.md R8).
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan RotationGracePeriod { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Optional absolute lifetime for newly issued credentials; null means they expire only when rotated or revoked.</summary>
    public TimeSpan? CredentialLifetime { get; init; }

    /// <summary>
    /// How long a store token minted by the handshake stays valid. Short by
    /// design: a leaked token is the one credential in this system that is not
    /// rotatable, so it is instead made not worth stealing
    /// ([`adr/0012`](../../docs/adr/0012-store-authentication-mechanism.md)).
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "12:00:00")]
    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a handshake nonce is remembered. Must comfortably exceed the
    /// clock skew and network delay a legitimate store can accumulate, and
    /// nothing more: every remembered nonce is memory.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
    public TimeSpan HandshakeNonceWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether a store must prove it owns its primary domain before the link is
    /// considered established. On by default: KNIGHT polls that domain, and a
    /// credential says nothing about who answers there
    /// (docs/security-threat-model.md).
    /// </summary>
    public bool RequireDomainVerification { get; init; } = true;

    /// <summary>How often a store is told to check in. Advertised in the handshake response so the interval is KNIGHT's decision, not the store's.</summary>
    [Range(typeof(TimeSpan), "00:00:15", "01:00:00")]
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How often a store is told to re-read its entitlements.</summary>
    [Range(typeof(TimeSpan), "00:00:30", "24:00:00")]
    public TimeSpan FeatureRefreshInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The header a terminating proxy puts a verified client certificate's
    /// SHA-256 thumbprint in.
    ///
    /// TLS almost never terminates in this process, so the certificate itself is
    /// not here to inspect — what arrives is the proxy's word for it. That is
    /// only worth anything because the proxy is inside the trust boundary and
    /// strips the header from anything it did not verify itself; a deployment
    /// where it does not must not switch mutual TLS on.
    /// </summary>
    public string ClientCertificateHeader { get; init; } = "X-Client-Certificate-Sha256";

    /// <summary>
    /// Master key from which each store's payload-signing key is derived. Kept
    /// separate from the token-signing key so that one leak does not compromise
    /// both, and required outside Development — where it falls back to the JWT
    /// key with a startup warning rather than making local development need a
    /// secret store.
    /// </summary>
    [MinLength(32)]
    public string? IntegrationSigningKey { get; init; }
}

public static class StoresModule
{
    public static IServiceCollection AddStoresModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StoreOptions>()
            .Bind(configuration.GetSection(StoreOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<IStoreManagementService, StoreManagementService>();
        services.AddScoped<IStoreIntegrationService, StoreIntegrationService>();

        return services;
    }
}
