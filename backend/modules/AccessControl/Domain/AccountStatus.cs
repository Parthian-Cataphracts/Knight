namespace AccessControl.Domain;

/// <summary>
/// Lifecycle of a control-plane account. Only <see cref="Active"/> may
/// authenticate; the other states fail closed before any credential is checked.
/// </summary>
public enum AccountStatus
{
    Invited = 0,
    Active = 1,
    Suspended = 2,
    Disabled = 3,
}
