using Identity.Authorization;
using Knight.Application.Exceptions;
using Xunit;

namespace Knight.UnitTests.Identity;

public sealed class DelegationGuardTests
{
    [Fact]
    public void EnsureSubset_WhenRequestedIsSubsetOfCaller_DoesNotThrow()
    {
        DelegationGuard.EnsureSubset(
            requestedPermissionKeys: ["tenant.roles.view", "tenant.roles.update"],
            callerEffectivePermissionKeys: ["tenant.roles.view", "tenant.roles.update", "tenant.users.disable"],
            callerIsPlatformAdmin: false);
    }

    [Fact]
    public void EnsureSubset_WhenRequestedExceedsCaller_ThrowsForbiddenException()
    {
        Assert.Throws<ForbiddenException>(() => DelegationGuard.EnsureSubset(
            requestedPermissionKeys: ["tenant.roles.view", "tenant.users.disable"],
            callerEffectivePermissionKeys: ["tenant.roles.view", "tenant.roles.update"],
            callerIsPlatformAdmin: false));
    }

    [Fact]
    public void EnsureSubset_ForPlatformAdmin_NeverThrowsRegardlessOfCallerPermissions()
    {
        DelegationGuard.EnsureSubset(
            requestedPermissionKeys: ["tenant.users.disable", "tenant.roles.delete"],
            callerEffectivePermissionKeys: [],
            callerIsPlatformAdmin: true);
    }

    [Fact]
    public void EnsureSubset_WithEmptyRequestedSet_NeverThrows()
    {
        DelegationGuard.EnsureSubset(
            requestedPermissionKeys: [],
            callerEffectivePermissionKeys: [],
            callerIsPlatformAdmin: false);
    }
}
