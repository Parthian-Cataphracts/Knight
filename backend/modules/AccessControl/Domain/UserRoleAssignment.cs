using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace AccessControl.Domain;

/// <summary>
/// Grants one role to one account. The customer is denormalised onto the
/// assignment so the isolation filter can reject a foreign row without first
/// joining back to the account (docs/domain-model.md section 1).
/// </summary>
public sealed class UserRoleAssignment : Entity, ICustomerScoped
{
    public Guid UserId { get; private set; }

    public Guid RoleId { get; private set; }

    public Guid? CustomerId { get; private set; }

    public DateTimeOffset AssignedAt { get; private set; }

    private UserRoleAssignment()
    {
    }

    private UserRoleAssignment(Guid id, Guid userId, Guid roleId, Guid? customerId, DateTimeOffset assignedAt)
        : base(id)
    {
        UserId = userId;
        RoleId = roleId;
        CustomerId = customerId;
        AssignedAt = assignedAt;
    }

    internal static UserRoleAssignment Create(Guid id, Guid userId, Guid roleId, Guid? customerId, DateTimeOffset assignedAt)
    {
        if (userId == Guid.Empty || roleId == Guid.Empty)
        {
            throw DomainException.Validation("A role assignment requires both an account and a role.");
        }

        return new UserRoleAssignment(id, userId, roleId, customerId, assignedAt);
    }
}
