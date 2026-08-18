namespace Customers.Domain;

/// <summary>
/// Commercial lifecycle of a control-plane customer. <see cref="Archived"/> is terminal.
/// </summary>
public enum CustomerStatus
{
    Prospect = 0,
    Active = 1,
    Suspended = 2,
    Archived = 3,
}
