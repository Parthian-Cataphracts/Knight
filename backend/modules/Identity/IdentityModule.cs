using Identity.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Knight.Application.Authorization;

namespace Identity;

/// <summary>
/// Machine-readable permissions owned by the Identity module, covering tenant
/// staff and role/permission self-administration — see
/// docs/architecture/authorization.md.
/// </summary>
public static class IdentityPermissions
{
    private const string Module = "identity";

    public static readonly Permission StaffView = new("tenant.users.view", "View tenant staff accounts.", Module);
    public static readonly Permission StaffCreate = new("tenant.users.create", "Create tenant staff accounts.", Module);
    public static readonly Permission StaffUpdate = new("tenant.users.update", "Update tenant staff accounts (including unlock).", Module);
    public static readonly Permission StaffEnable = new("tenant.users.enable", "Re-enable a disabled tenant staff account.", Module);
    public static readonly Permission StaffDisable = new("tenant.users.disable", "Disable a tenant staff account.", Module);
    public static readonly Permission StaffAssignRoles = new("tenant.users.roles.assign", "Assign roles to tenant staff accounts.", Module);
    public static readonly Permission StaffRevokeSessions = new("tenant.users.sessions.revoke", "Revoke a tenant staff account's active sessions.", Module);

    public static readonly Permission RolesView = new("tenant.roles.view", "View tenant roles.", Module);
    public static readonly Permission RolesCreate = new("tenant.roles.create", "Create tenant roles.", Module);
    public static readonly Permission RolesUpdate = new("tenant.roles.update", "Rename tenant roles.", Module);
    public static readonly Permission RolesDelete = new("tenant.roles.delete", "Delete unassigned tenant roles.", Module);
    public static readonly Permission RolesAssignPermissions = new("tenant.roles.permissions.assign", "Change the permissions granted by a tenant role.", Module);

    public static readonly IReadOnlyCollection<Permission> All =
    [
        StaffView,
        StaffCreate,
        StaffUpdate,
        StaffEnable,
        StaffDisable,
        StaffAssignRoles,
        StaffRevokeSessions,
        RolesView,
        RolesCreate,
        RolesUpdate,
        RolesDelete,
        RolesAssignPermissions
    ];
}

internal sealed class IdentityPermissionProvider : IPermissionProvider
{
    public IReadOnlyCollection<Permission> Permissions => IdentityPermissions.All;
}

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IPermissionProvider, IdentityPermissionProvider>();

        services.AddOptions<LockoutOptions>()
            .Bind(configuration.GetSection(LockoutOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<PasswordPolicyOptions>()
            .Bind(configuration.GetSection(PasswordPolicyOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RefreshTokenOptions>()
            .Bind(configuration.GetSection(RefreshTokenOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddScoped<IPlatformAuthenticationService, PlatformAuthenticationService>();
        services.AddScoped<ITenantAuthenticationService, TenantAuthenticationService>();
        services.AddScoped<IEffectivePermissionService, EffectivePermissionService>();
        services.AddScoped<IRoleManagementService, RoleManagementService>();
        services.AddScoped<IStaffManagementService, StaffManagementService>();

        return services;
    }
}
