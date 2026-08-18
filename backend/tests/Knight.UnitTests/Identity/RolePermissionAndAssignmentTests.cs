using Identity.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.Identity;

public sealed class RolePermissionAndAssignmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RolePermission_Create_WithEmptyPermissionKey_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => RolePermission.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", Now));
    }

    [Fact]
    public void RolePermission_Create_WithEmptyTenantOrRoleId_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => RolePermission.Create(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), "tenant.roles.view", Now));
        Assert.Throws<DomainException>(() => RolePermission.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, "tenant.roles.view", Now));
    }

    [Fact]
    public void TenantUserRole_Create_WithAnyEmptyReference_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => TenantUserRole.Create(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), Now));
        Assert.Throws<DomainException>(() => TenantUserRole.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), Now));
        Assert.Throws<DomainException>(() => TenantUserRole.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Now));
    }

    [Fact]
    public void TenantUserRole_Create_WithValidReferences_Succeeds()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();

        var assignment = TenantUserRole.Create(Guid.NewGuid(), tenantId, userId, roleId, Now);

        Assert.Equal(tenantId, assignment.TenantId);
        Assert.Equal(userId, assignment.TenantUserId);
        Assert.Equal(roleId, assignment.RoleId);
    }
}
