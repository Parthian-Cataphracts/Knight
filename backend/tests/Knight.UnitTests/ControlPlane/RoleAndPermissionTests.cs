using AccessControl.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

public sealed class RoleAndPermissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SystemRole_CannotBeModified()
    {
        var role = Role.CreateSystem(Guid.NewGuid(), Now, SystemRoles.Admin, RoleScope.Platform);

        Assert.Throws<DomainException>(() => role.Grant(ControlPlanePermissions.StoreView, Now));
        Assert.Throws<DomainException>(() => role.Describe("Renamed", null, Now));
    }

    [Fact]
    public void CustomerScopedRole_MustBelongToACustomer()
    {
        var exception = Assert.Throws<DomainException>(() =>
            Role.CreateCustom(Guid.NewGuid(), Now, "Analyst", RoleScope.Customer, customerId: null));

        Assert.Equal(DomainErrorCategory.Validation, exception.Category);
    }

    [Fact]
    public void PlatformScopedRole_CannotBelongToACustomer()
    {
        Assert.Throws<DomainException>(() =>
            Role.CreateCustom(Guid.NewGuid(), Now, "Analyst", RoleScope.Platform, customerId: Guid.NewGuid()));
    }

    [Fact]
    public void UnknownPermission_IsRefused()
    {
        var role = Role.CreateCustom(Guid.NewGuid(), Now, "Analyst", RoleScope.Platform, null);

        var exception = Assert.Throws<DomainException>(() => role.Grant("store.destroy", Now));

        Assert.Equal(DomainErrorCategory.Validation, exception.Category);
    }

    [Fact]
    public void MachinePermission_CannotBeGrantedToARole()
    {
        var role = Role.CreateCustom(Guid.NewGuid(), Now, "Ingest", RoleScope.Platform, null);

        var exception = Assert.Throws<DomainException>(() => role.Grant(ControlPlanePermissions.IngestWrite, Now));

        Assert.Equal(DomainErrorCategory.Conflict, exception.Category);
    }

    [Fact]
    public void CustomerRole_CannotHoldAPlatformOnlyPermission()
    {
        var role = Role.CreateCustom(Guid.NewGuid(), Now, "Analyst", RoleScope.Customer, Guid.NewGuid());

        // Publishing ships executable code to every entitled store; a customer
        // may never hold it (docs/authorization.md section 2).
        Assert.Throws<DomainException>(() => role.Grant(ControlPlanePermissions.FeaturePublish, Now));
        Assert.Throws<DomainException>(() => role.Grant(ControlPlanePermissions.InstallationUninstall, Now));
        Assert.Throws<DomainException>(() => role.Grant(ControlPlanePermissions.PlanManage, Now));
    }

    [Fact]
    public void CustomerRole_MayHoldItsOwnOperationalPermissions()
    {
        var role = Role.CreateCustom(Guid.NewGuid(), Now, "Analyst", RoleScope.Customer, Guid.NewGuid());

        role.Grant(ControlPlanePermissions.StoreView, Now);
        role.Grant(ControlPlanePermissions.ErrorsView, Now);

        Assert.True(role.HasPermission(ControlPlanePermissions.StoreView));
        Assert.True(role.HasPermission(ControlPlanePermissions.ErrorsView));
    }

    [Fact]
    public void GrantingTheSamePermissionTwice_IsIdempotent()
    {
        var role = Role.CreateCustom(Guid.NewGuid(), Now, "Analyst", RoleScope.Platform, null);

        role.Grant(ControlPlanePermissions.StoreView, Now);
        role.Grant(ControlPlanePermissions.StoreView, Now);

        Assert.Single(role.Permissions);
    }

    [Fact]
    public void ReplacePermissions_SetsExactlyTheRequestedSet()
    {
        var role = Role.CreateCustom(Guid.NewGuid(), Now, "Analyst", RoleScope.Platform, null);
        role.Grant(ControlPlanePermissions.StoreView, Now);
        role.Grant(ControlPlanePermissions.CustomerView, Now);

        role.ReplacePermissions([ControlPlanePermissions.CustomerView, ControlPlanePermissions.AuditView], Now);

        Assert.Equal(2, role.Permissions.Count);
        Assert.True(role.HasPermission(ControlPlanePermissions.CustomerView));
        Assert.True(role.HasPermission(ControlPlanePermissions.AuditView));
        Assert.False(role.HasPermission(ControlPlanePermissions.StoreView));
    }

    [Fact]
    public void EverySeededRoleDefinitionUsesKnownPermissions()
    {
        foreach (var definition in SystemRoles.All)
        {
            foreach (var permission in definition.Permissions)
            {
                Assert.True(
                    ControlPlanePermissions.Exists(permission),
                    $"Role '{definition.Name}' references unknown permission '{permission}'.");
            }
        }
    }

    [Fact]
    public void SeededCustomerRoles_OnlyCarryCustomerAssignablePermissions()
    {
        foreach (var definition in SystemRoles.All.Where(d => d.Scope is RoleScope.Customer))
        {
            foreach (var permission in definition.Permissions)
            {
                Assert.True(
                    ControlPlanePermissions.IsCustomerAssignable(permission),
                    $"Customer role '{definition.Name}' must not carry '{permission}'.");
            }
        }
    }

    [Fact]
    public void OnlySuperAdminAndAdminRequireASecondFactor()
    {
        Assert.True(SystemRoles.RequiresMfa([SystemRoles.SuperAdmin]));
        Assert.True(SystemRoles.RequiresMfa([SystemRoles.Support, SystemRoles.Admin]));
        Assert.False(SystemRoles.RequiresMfa([SystemRoles.Support, SystemRoles.Developer]));
        Assert.False(SystemRoles.RequiresMfa([SystemRoles.CustomerOwner]));
    }

    [Fact]
    public void MachinePermissionsAreNotAssignableToAnyRole()
    {
        Assert.DoesNotContain(ControlPlanePermissions.IngestWrite, ControlPlanePermissions.AssignableToRoles);
        Assert.DoesNotContain(ControlPlanePermissions.AgentReport, ControlPlanePermissions.AssignableToRoles);
        Assert.DoesNotContain(ControlPlanePermissions.AgentExecuteJob, ControlPlanePermissions.AssignableToRoles);
    }
}
