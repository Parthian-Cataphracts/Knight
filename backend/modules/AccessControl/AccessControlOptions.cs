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
    /// Bootstrap account created on first start when no platform staff exists.
    /// Without it a fresh deployment has no way to sign in at all. The password
    /// comes from the secret store, never from source, and is left unset in every
    /// configuration file checked into the repository.
    /// </summary>
    public BootstrapAdminOptions? BootstrapAdmin { get; init; }
}

public sealed class BootstrapAdminOptions
{
    [Required]
    public string Email { get; init; } = string.Empty;

    [Required]
    public string Password { get; init; } = string.Empty;

    public string DisplayName { get; init; } = "Platform Administrator";
}
