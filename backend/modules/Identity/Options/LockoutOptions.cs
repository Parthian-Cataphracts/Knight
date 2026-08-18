using System.ComponentModel.DataAnnotations;

namespace Identity.Options;

/// <summary>
/// Bound from configuration (section "Lockout"). Shared by PlatformAdmin and
/// TenantUser authentication — see docs/architecture/authorization.md.
/// </summary>
public sealed class LockoutOptions
{
    public const string SectionName = "Lockout";

    [Range(1, 100)]
    public int FailedAttemptThreshold { get; init; } = 5;

    [Range(typeof(TimeSpan), "00:00:30", "7.00:00:00")]
    public TimeSpan LockoutDuration { get; init; } = TimeSpan.FromMinutes(15);
}
