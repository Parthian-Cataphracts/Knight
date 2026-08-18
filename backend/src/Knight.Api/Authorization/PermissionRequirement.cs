using Microsoft.AspNetCore.Authorization;
using Knight.Application.Abstractions.Identity;

namespace Knight.Api.Authorization;

/// <summary>
/// Authorization requirement satisfied when the current caller's access token
/// carries the given permission claim. Lets endpoints declare
/// <c>.RequireAuthorization(p =&gt; p.RequirePermission("catalog.products.view"))</c>
/// without controller-specific database queries or role switch statements —
/// see docs/architecture/authorization.md.
/// </summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public string Permission { get; }

    public PermissionRequirement(string permission)
    {
        Permission = permission;
    }
}

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly ICurrentUser _currentUser;

    public PermissionAuthorizationHandler(ICurrentUser currentUser)
    {
        _currentUser = currentUser;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (_currentUser.HasPermission(requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class AuthorizationPolicyBuilderExtensions
{
    public static AuthorizationPolicyBuilder RequirePermission(this AuthorizationPolicyBuilder builder, string permission) =>
        builder.AddRequirements(new PermissionRequirement(permission));
}
