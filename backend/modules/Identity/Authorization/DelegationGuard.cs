using Knight.Application.Exceptions;

namespace Identity.Authorization;

/// <summary>
/// Enforces the privilege-delegation rule: a TenantUser can never grant (via role
/// creation, role permission changes, or role assignment) a permission they do
/// not themselves currently hold. PlatformAdmin is explicitly exempt — Platform
/// context carries its own, separately authorized global authority. See
/// docs/architecture/authorization.md ("Privilege delegation").
/// </summary>
internal static class DelegationGuard
{
    public static void EnsureSubset(IReadOnlyCollection<string> requestedPermissionKeys, IReadOnlyCollection<string> callerEffectivePermissionKeys, bool callerIsPlatformAdmin)
    {
        if (callerIsPlatformAdmin)
        {
            return;
        }

        var callerSet = new HashSet<string>(callerEffectivePermissionKeys, StringComparer.Ordinal);
        var exceeded = requestedPermissionKeys.Where(key => !callerSet.Contains(key)).ToArray();

        if (exceeded.Length > 0)
        {
            throw new ForbiddenException(
                "Cannot grant permissions beyond the caller's own effective permissions: " + string.Join(", ", exceeded));
        }
    }
}
