using System.ComponentModel.DataAnnotations;

namespace AccessControl;

/// <summary>
/// Bound from configuration (section "ControlPlaneAccess"). Everything here is a
/// policy decision an operator may need to tune per environment; none of it is
/// hard-coded into the services that enforce it.
/// </summary>
public sealed class AccessControlOptions
{
    public const string SectionName = "ControlPlaneAccess";

    [Range(1, 100)]
    public int LockoutThreshold { get; init; } = 5;

    [Range(typeof(TimeSpan), "00:00:30", "7.00:00:00")]
    public TimeSpan LockoutDuration { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Absolute lifetime of a login. Rotation never extends it.</summary>
    [Range(typeof(TimeSpan), "00:15:00", "30.00:00:00")]
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(12);

    /// <summary>Shown in the authenticator app during enrolment.</summary>
    public string MfaIssuer { get; init; } = "KNIGHT";

    /// <summary>
    /// How long an emailed invitation stays usable. Two days by default: long
    /// enough to survive a weekend, short enough that a forwarded mail sitting
    /// in an old inbox is not a way into an account nobody has claimed yet.
    /// </summary>
    [Range(typeof(TimeSpan), "01:00:00", "14.00:00:00")]
    public TimeSpan InvitationLifetime { get; init; } = TimeSpan.FromDays(2);
}
