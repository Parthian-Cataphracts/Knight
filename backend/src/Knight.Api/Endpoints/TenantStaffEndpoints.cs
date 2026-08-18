using Knight.Api.Authorization;
using Identity;
using Identity.Domain;
using Knight.Application.Abstractions.Tenancy;
using Knight.Contracts.AccessControl;
using Knight.Contracts.Common;

namespace Knight.Api.Endpoints;

/// <summary>
/// Tenant self-administration of staff accounts. Always operates on
/// <see cref="ITenantContext.TenantId"/> — never accepts a tenant selector
/// from the request. See docs/architecture/authorization.md.
/// </summary>
public static class TenantStaffEndpoints
{
    public static void MapTenantStaffEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenant/staff")
            .RequireAuthorization("TenantUserOnly")
            .RequireRateLimiting("platform")
            .WithTags("Tenant Staff");

        group.MapGet("/", async (int? page, int? pageSize, ITenantContext tenantContext, IStaffManagementService service, CancellationToken cancellationToken) =>
        {
            var tenantId = RequireTenant(tenantContext);
            var result = await service.ListAsync(tenantId, page ?? 1, pageSize ?? 20, cancellationToken);
            return Results.Ok(PagedResponse<StaffResponse>.Create(result.Items.Select(ToResponse).ToArray(), result.Page, result.PageSize, result.TotalCount));
        }).RequireAuthorization(p => p.RequirePermission(IdentityPermissions.StaffView));

        group.MapGet("/{id:guid}", async (Guid id, ITenantContext tenantContext, IStaffManagementService service, ITenantUserRoleRepository roleRepository, CancellationToken cancellationToken) =>
        {
            var tenantId = RequireTenant(tenantContext);
            var user = await service.GetAsync(tenantId, id, cancellationToken);
            if (user is null)
            {
                return Results.NotFound();
            }

            var roleIds = await roleRepository.GetRoleIdsForUserAsync(tenantId, id, cancellationToken);
            return Results.Ok(ToResponse(new StaffListItem { User = user, RoleIds = roleIds }));
        }).RequireAuthorization(p => p.RequirePermission(IdentityPermissions.StaffView));

        group.MapPost("/", async (CreateStaffRequest request, ITenantContext tenantContext, IStaffManagementService service, CancellationToken cancellationToken) =>
        {
            var tenantId = RequireTenant(tenantContext);
            var user = await service.CreateAsync(tenantId, new CreateStaffInput(request.Email, request.DisplayName, request.InitialPassword, request.RoleIds), cancellationToken);
            return Results.Created($"/api/tenant/staff/{user.Id}", ToResponse(new StaffListItem { User = user, RoleIds = request.RoleIds }));
        }).RequireAuthorization(p => p.RequirePermission(IdentityPermissions.StaffCreate));

        group.MapPost("/{id:guid}/enable", async (Guid id, ITenantContext tenantContext, IStaffManagementService service, CancellationToken cancellationToken) =>
        {
            var tenantId = RequireTenant(tenantContext);
            var user = await service.EnableAsync(tenantId, id, cancellationToken);
            return Results.Ok(ToResponse(new StaffListItem { User = user, RoleIds = [] }));
        }).RequireAuthorization(p => p.RequirePermission(IdentityPermissions.StaffEnable));

        group.MapPost("/{id:guid}/disable", async (Guid id, ITenantContext tenantContext, IStaffManagementService service, CancellationToken cancellationToken) =>
        {
            var tenantId = RequireTenant(tenantContext);
            var user = await service.DisableAsync(tenantId, id, cancellationToken);
            return Results.Ok(ToResponse(new StaffListItem { User = user, RoleIds = [] }));
        }).RequireAuthorization(p => p.RequirePermission(IdentityPermissions.StaffDisable));

        group.MapPost("/{id:guid}/unlock", async (Guid id, ITenantContext tenantContext, IStaffManagementService service, CancellationToken cancellationToken) =>
        {
            var tenantId = RequireTenant(tenantContext);
            var user = await service.UnlockAsync(tenantId, id, cancellationToken);
            return Results.Ok(ToResponse(new StaffListItem { User = user, RoleIds = [] }));
        }).RequireAuthorization(p => p.RequirePermission(IdentityPermissions.StaffUpdate));

        group.MapPut("/{id:guid}/roles", async (Guid id, ReplaceStaffRolesRequest request, ITenantContext tenantContext, IStaffManagementService service, CancellationToken cancellationToken) =>
        {
            var tenantId = RequireTenant(tenantContext);
            var roleIds = await service.ReplaceRolesAsync(tenantId, id, request.RoleIds, cancellationToken);
            var user = await service.GetAsync(tenantId, id, cancellationToken);
            return Results.Ok(ToResponse(new StaffListItem { User = user!, RoleIds = roleIds }));
        }).RequireAuthorization(p => p.RequirePermission(IdentityPermissions.StaffAssignRoles));

        group.MapPost("/{id:guid}/sessions/revoke", async (Guid id, ITenantContext tenantContext, IStaffManagementService service, CancellationToken cancellationToken) =>
        {
            var tenantId = RequireTenant(tenantContext);
            await service.RevokeSessionsAsync(tenantId, id, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequirePermission(IdentityPermissions.StaffRevokeSessions));
    }

    internal static Guid RequireTenant(ITenantContext tenantContext) =>
        tenantContext.HasTenant ? tenantContext.TenantId!.Value : throw new Knight.Application.Exceptions.ForbiddenException("A tenant context is required.");

    internal static StaffResponse ToResponse(StaffListItem item) => new()
    {
        Id = item.User.Id,
        Email = item.User.Email,
        DisplayName = item.User.DisplayName,
        IsEnabled = item.User.Status == UserStatus.Active,
        IsLocked = item.User.IsLocked(DateTimeOffset.UtcNow),
        RoleIds = item.RoleIds,
        CreatedAt = item.User.CreatedAt,
        LastLoginAt = item.User.LastLoginAt
    };
}
