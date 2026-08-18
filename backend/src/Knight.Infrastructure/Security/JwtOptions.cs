using System.ComponentModel.DataAnnotations;

namespace Knight.Infrastructure.Security;

/// <summary>
/// Bound from configuration (section "Jwt"). The signing key must be supplied via
/// environment variable / secret store in every environment and never committed.
/// Platform tokens use a deliberately shorter lifetime than Tenant tokens — see
/// docs/architecture/authorization.md ("Platform Admin Security Level").
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>Minimum signing-key length in characters, chosen so the UTF-8 byte count clears 256 bits for HMAC-SHA256.</summary>
    public const int MinimumSigningKeyLength = 32;

    [Required(AllowEmptyStrings = false)]
    public required string Issuer { get; init; }

    [Required(AllowEmptyStrings = false)]
    public required string Audience { get; init; }

    [Required(AllowEmptyStrings = false)]
    [MinLength(MinimumSigningKeyLength)]
    public required string SigningKey { get; init; }

    [Range(1, 60)]
    public int PlatformAccessTokenLifetimeMinutes { get; init; } = 5;

    [Range(1, 120)]
    public int TenantAccessTokenLifetimeMinutes { get; init; } = 10;
}
