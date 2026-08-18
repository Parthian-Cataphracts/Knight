using System.ComponentModel.DataAnnotations;

namespace Identity.Options;

/// <summary>
/// Bound from configuration (section "PasswordPolicy"). Shared by Platform and
/// Tenant administrative accounts unless a concrete requirement justifies
/// splitting them later.
/// </summary>
public sealed class PasswordPolicyOptions
{
    public const string SectionName = "PasswordPolicy";

    [Range(8, 256)]
    public int MinLength { get; init; } = 10;

    [Range(8, 512)]
    public int MaxLength { get; init; } = 128;
}
