using AccessControl;
using Microsoft.AspNetCore.Authorization;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Requires a named control-plane permission. The permission is resolved from
/// the database for the current account on every request, never read out of the
/// token, so removing a role takes effect immediately
/// (docs/authorization.md section 6).
/// </summary>
public sealed class ControlPlanePermissionRequirement : IAuthorizationRequirement
{
    public ControlPlanePermissionRequirement(string permission)
    {
        Permission = permission;
    }

    public string Permission { get; }
}

public sealed class ControlPlanePermissionHandler : AuthorizationHandler<ControlPlanePermissionRequirement>
{
    private readonly IControlPlanePrincipal _principal;
    private readonly IEffectivePermissionResolver _permissions;

    public ControlPlanePermissionHandler(IControlPlanePrincipal principal, IEffectivePermissionResolver permissions)
    {
        _principal = principal;
        _permissions = permissions;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ControlPlanePermissionRequirement requirement)
    {
        // Cross-principal access is refused here, before any handler runs: a
        // store or agent token must never satisfy a dashboard permission
        // (docs/authentication.md section 4).
        if (!_principal.IsControlPlaneUser || _principal.UserId is not { } userId)
        {
            return;
        }

        // An outstanding second factor blocks everything that needs a
        // permission. Enrolment endpoints require none, so the account can still
        // finish setting MFA up and nothing else.
        if (!_principal.MfaSatisfied)
        {
            return;
        }

        if (await _permissions.HasPermissionAsync(userId, requirement.Permission, CancellationToken.None))
        {
            context.Succeed(requirement);
        }
    }
}

public static class ControlPlaneAuthorizationExtensions
{
    /// <summary>The policy every dashboard endpoint sits behind: an authenticated control-plane user.</summary>
    public const string UserPolicy = "ControlPlaneUser";

    public static AuthorizationPolicyBuilder RequireControlPlanePermission(
        this AuthorizationPolicyBuilder builder,
        string permission) =>
        builder
            .RequireAuthenticatedUser()
            .AddRequirements(new ControlPlanePermissionRequirement(permission));

    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission) =>
        builder.RequireAuthorization(policy => policy.RequireControlPlanePermission(permission));
}
