namespace AccessControl.Domain;

/// <summary>
/// Whether a role grants across the platform or only inside one customer. The
/// scope is a property of the role itself, so a customer-scoped assignment can
/// never widen into platform access (docs/authorization.md §1).
/// </summary>
public enum RoleScope
{
    Platform = 0,
    Customer = 1,
}
