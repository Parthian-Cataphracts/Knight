using System.ComponentModel.DataAnnotations;

namespace Identity.Options;

/// <summary>
/// Bound from configuration (section "RefreshToken"). Governs the absolute
/// (non-extendable) lifetime of a refresh-token family — see
/// docs/architecture/authorization.md.
/// </summary>
public sealed class RefreshTokenOptions
{
    public const string SectionName = "RefreshToken";

    [Range(typeof(TimeSpan), "00:15:00", "7.00:00:00")]
    public TimeSpan PlatformFamilyLifetime { get; init; } = TimeSpan.FromHours(12);

    [Range(typeof(TimeSpan), "00:15:00", "90.00:00:00")]
    public TimeSpan TenantFamilyLifetime { get; init; } = TimeSpan.FromDays(30);
}
